#nullable enable
using GregsEx.Contracts;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
using GregsEx.IR;

namespace GregsEx.Flavors.EcmaScript.Integration;

internal static class EcmaPatternFactory
{
    // Public pattern, bridge and policy references. VM/program/data have their own charges.
    private const long WrapperBytes = 320;
    internal static bool TryParse(ReadOnlySpan<char> source, EcmaScriptProfile profile,
        RegexCompileOptions options, out RegexPattern? pattern, out EcmaScriptDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        pattern = null;
        var range = new SourceRange(0, source.Length, RegexOffsetUnit.Utf16CodeUnits);
        if (!profile.Supports(options.InputKind))
        {
            diagnostic = new(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, range.Unit),
                EcmaScriptDiagnosticLocation.Pattern, "ES-020");
            return false;
        }
        if (source.Length > options.Limits.MaxPatternUnits)
        {
            diagnostic = new(new(RegexDiagnosticCode.PatternLimitExceeded, 0, source.Length, range.Unit),
                EcmaScriptDiagnosticLocation.Pattern, "ES-020");
            return false;
        }
        var budget = new CompileBudget(options.Limits);
        if (!EcmaUnicodeData.TryCreate(profile.UnicodeVersion, ref budget, out var unicode, out var failure))
        {
            return Failed(failure, out diagnostic);
        }

        if (!new EcmaCompiler().TryCompile(source, profile, unicode!, ref budget, out var program, out diagnostic))
        {
            EnsureExpected(diagnostic.Diagnostic);
            return false;
        }
        if (!new EcmaExecutorFactory().TryCreate(program!, unicode!, ref budget, out var executor, out failure))
        {
            return Failed(failure, out diagnostic);
        }
        // All temporary reservations have been released. The cumulative live charge includes
        // data once on cold creation, its reference on every plan, program and VM instructions.
        if (budget.ReservedBytes > options.Limits.MaxPlanBytes - WrapperBytes)
        {
            return Failed(new(RegexDiagnosticCode.PlanLimitExceeded, 0, source.Length, range.Unit), out diagnostic);
        }

        if (!budget.TryCharge(1, range, out failure) ||
            !budget.TryReserveBytes(WrapperBytes, range, out failure))
        {
            return Failed(failure, out diagnostic);
        }

        pattern = new(executor!, options, budget.ReservedBytes);
        diagnostic = default;
        return true;
    }

    private static bool Failed(RegexDiagnostic failure, out EcmaScriptDiagnostic diagnostic)
    {
        EnsureExpected(failure);
        diagnostic = new(failure, EcmaScriptDiagnosticLocation.Pattern, "ES-020");
        return false;
    }
    private static void EnsureExpected(RegexDiagnostic diagnostic)
    {
        if (diagnostic.Code is RegexDiagnosticCode.None or RegexDiagnosticCode.InvalidProgram)
        {
            throw new InvalidOperationException("ECMAScript compiler violated validated-program admission.");
        }
    }
}
