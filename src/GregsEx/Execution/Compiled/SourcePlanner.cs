#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Contracts.Pcre2;
using GregsEx.Contracts.Xml;
using GregsEx.Compilation.Core;
using GregsEx.Execution.Accelerated.Literals;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Tables;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Parsing;
using GregsEx.Flavors.Xsd;
using GregsEx.IR;
using GregsEx.Flavors.Pcre2.Runtime.Composition;

namespace GregsEx.Execution.Compiled;

// Executor-free planning front end. It parses a pattern to its flavor program and applies the same eligibility and benefit rules
// as the runtime Compile() path, but it never builds an interpreter or workspace. It is the only planning surface the build-time
// source generator uses, so this file and everything it reaches also compile as netstandard2.0.
internal static class SourcePlanner
{
    // Parses and plans. Returns false only when the pattern does not parse (diagnostic is set). On success, plan is null with
    // reason set when the pattern is not eligible for a generated kernel.
    internal static bool TryPlan(string pattern, RegexProfile profile, out CoreKernelPlan? plan, out string reason,
        out RegexDiagnostic diagnostic)
    {
        plan = null;
        reason = string.Empty;
        diagnostic = default;
        var options = new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto);

        _ = new SourceRange(0, pattern.Length, RegexOffsetUnit.Utf16CodeUnits);
        switch (profile)
        {
            case PosixProfile:
                reason = PosixReason;
                return true;
            case OnigurumaProfile oniguruma:
                return OnigKernelPlan.TryPlan(pattern, oniguruma, options.Limits, LoweringLimits.Default, out plan, out reason, out diagnostic);
            case Pcre2Profile pcre:
                {
                    if (!pcre.Supports(options.InputKind))
                    {
                        diagnostic = new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Utf16CodeUnits);
                        return false;
                    }
                    var budget = new CompileBudget(options.Limits);
                    if (!new Pcre2Compiler().TryCompile(pattern.AsSpan(), pcre, Pcre2Data.Instance,
                            Pcre2ProjectionPolicy.WholeSubjectValidated, ref budget, out var program, out var failure))
                    { diagnostic = failure.Diagnostic; return false; }
                    PlanPcre2(program!, out plan, out reason);
                    return true;
                }
            case EcmaScriptProfile ecma:
                {
                    if (!ecma.Supports(options.InputKind))
                    {
                        diagnostic = new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Utf16CodeUnits);
                        return false;
                    }
                    if (pattern.Length > options.Limits.MaxPatternUnits)
                    {
                        diagnostic = new(RegexDiagnosticCode.PatternLimitExceeded, 0, pattern.Length, RegexOffsetUnit.Utf16CodeUnits);
                        return false;
                    }
                    var budget = new CompileBudget(options.Limits);
                    if (!EcmaUnicodeData.TryCreate(ecma.UnicodeVersion, ref budget, out var unicode, out diagnostic))
                    {
                        return false;
                    }

                    if (!new EcmaCompiler().TryCompile(pattern.AsSpan(), ecma, unicode!, ref budget, out var program, out var failure))
                    { diagnostic = failure.Diagnostic; return false; }
                    PlanEcma(program!, out plan, out reason);
                    return true;
                }
            case XsdProfile or XPathProfile:
                {
                    if (pattern.Length > options.Limits.MaxPatternUnits)
                    {
                        diagnostic = new(RegexDiagnosticCode.PatternLimitExceeded, 0, pattern.Length, RegexOffsetUnit.Utf16CodeUnits);
                        return false;
                    }
                    var budget = new CompileBudget(options.Limits);
                    var version = profile is XsdProfile xsd ? xsd.UnicodeVersion : ((XPathProfile)profile).UnicodeVersion;
                    if (!XmlUnicodeData.TryCreate(version, ref budget, out var data, out diagnostic))
                    {
                        return false;
                    }

                    XmlProgram? program;
                    if (profile is XPathProfile xpath)
                    {
                        if (!new XPathCompiler().TryCompile(pattern.AsSpan(), xpath, options.InputKind, data!, new XmlClassAlgebra(),
                                ref budget, out program, out var failure))
                        { diagnostic = failure.Diagnostic; return false; }
                    }
                    else if (!new XsdCompiler().TryCompile(pattern.AsSpan(), profile, options.InputKind, data!, new XmlClassAlgebra(),
                                 ref budget, out program, out var failure))
                    { diagnostic = failure.Diagnostic; return false; }
                    PlanXml(program!, out plan, out reason);
                    return true;
                }
            default:
                {
                    if (!ProfileDescriptor.TryCreate(profile, options.InputKind, out var descriptor, out diagnostic))
                    {
                        return false;
                    }

                    var budget = new CompileBudget(options.Limits);
                    if (!new CoreCompiler().TryCompile(pattern.AsSpan(), descriptor!, ref budget, out var program, out diagnostic))
                    {
                        return false;
                    }

                    PlanCore(program!, StartScan.TryCreate(program!, AcceleratorSwitches.Semantic)?.HasFastPath == true,
                        out plan, out reason);
                    return true;
                }
        }
    }

    internal const string PosixReason =
        "POSIX patterns are not compiled: leftmost-longest semantics are not modelled by the compiled kernel.";

    internal static bool PlanCore(SemanticProgram program, bool hasFastPath, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        if (hasFastPath)
        {
            reason = "A hand-written accelerated path already serves this pattern faster than a compiled kernel.";
            return false;
        }
        return CoreKernelPlan.TryCreate(program, LoweringLimits.Default, out plan, out reason);
    }

    internal static bool PlanEcma(EcmaProgram program, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        var budget = new CompileBudget(new RegexCompileLimits());
        if (!EcmaUnicodeData.TryCreate(program.Profile.UnicodeVersion, ref budget, out var unicode, out _))
        {
            reason = "The ECMAScript case tables are unavailable.";
            return false;
        }
        // Whole-pattern direct plans (literal, alternation, set repeat, atomic sequence) already beat the generic kernel.
        var accelerator = new EcmaAccelerator(program, unicode!.CaseTables);
        if (accelerator.HasDirectPlan)
        {
            reason = "The interpreter's accelerated direct plan already serves this pattern; a compiled kernel shows no benefit.";
            return false;
        }
        // The kernel keeps the interpreter's search-level prefilters: a required literal, else the required ASCII units.
        char[]? required = null;
        if (accelerator.RequiredLiteral is null
            && EcmaUnitFilters.BuildRequiredUnits(program, program.Nodes, program.Root, unicode.CaseTables, accelerator.FastSets) is { } units)
        {
            var chars = new List<char>();
            for (var c = 0; c < 128; c++)
            {
                if ((c < 64 ? units.Low >> c & 1 : units.High >> (c - 64) & 1) != 0)
                {
                    chars.Add((char)c);
                }
            }

            required = chars.ToArray();
        }
        return EcmaKernelPlan.TryCreate(program, unicode.CaseTables, LoweringLimits.Default, out plan, out reason, required,
            accelerator.RequiredLiteral, accelerator.RequiredLiteralMinOffset);
    }

    internal static bool PlanXml(XmlProgram program, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        System.ReadOnlySpan<XmlCaseMapping> cases = default;
        if (program.Profile is XPathProfile { Flags: var flags } && (flags & XPathFlags.IgnoreCase) != 0)
        {
            var budget = new CompileBudget(new RegexCompileLimits());
            if (!XmlUnicodeData.TryCreate(((XPathProfile)program.Profile).UnicodeVersion, ref budget, out var data, out _))
            {
                reason = "The XML case tables are unavailable.";
                return false;
            }
            cases = data!.CaseMappings;
        }
        return XmlKernelPlan.TryCreate(program, cases, LoweringLimits.Default, out plan, out reason);
    }

    internal static bool PlanPcre2(Pcre2Program program, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        // Whole-pattern direct plans (literal, alternation, set repeat, atomic sequence) already beat the generic kernel.
        if (new Pcre2Accelerator(program, Pcre2Data.Instance.Bind(program.Profile.CharacterTables)).HasDirectPlan)
        {
            reason = "The interpreter's accelerated direct plan already serves this pattern; a compiled kernel shows no benefit.";
            return false;
        }
        return Pcre2KernelPlan.TryCreate(program, LoweringLimits.Default, out plan, out reason);
    }
}
