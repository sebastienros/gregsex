#nullable enable
using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Integration;
using GregsEx.Flavors.Xsd;
using GregsEx.IR;

namespace GregsEx.Flavors.XmlIntegration;

internal static class XmlPatternFactory
{
    // Public pattern, operation bridge and policy references, admitted before allocation.
    internal const long WrapperBytes = 320;
    private const long FactoryBytes = 96;
    internal static bool TryParse(ReadOnlySpan<char> source, RegexProfile profile,
        RegexCompileOptions options, out RegexPattern? pattern, out RegexDiagnostic diagnostic)
    {
        pattern = null;
        var range = new SourceRange(0, source.Length, RegexOffsetUnit.Utf16CodeUnits);
        if (options.InputKind is not (RegexInputKind.Utf16 or RegexInputKind.Utf8))
        { diagnostic = new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, range.Unit); return false; }
        if (source.Length > options.Limits.MaxPatternUnits)
        { diagnostic = new(RegexDiagnosticCode.PatternLimitExceeded, 0, source.Length, range.Unit); return false; }
        var budget = new CompileBudget(options.Limits);
        if (!budget.TryReserveBytes(WrapperBytes, range, out diagnostic))
        {
            return false;
        }

        if (profile is XPathProfile xpath)
        {
            if (!XPathBinding.TryCreate(source, xpath, options.InputKind, ref budget, out var binding, out var detail))
            { diagnostic = detail.Diagnostic; return false; }
            pattern = new(binding!.Executor, options, binding.RetainedBytes);
        }
        else
        {
            if (!budget.TryReserveBytes(FactoryBytes, range, out diagnostic))
            {
                return false;
            }

            try
            {
                if (!XmlUnicodeData.TryCreate(((XsdProfile)profile).UnicodeVersion, ref budget, out var data, out diagnostic))
                {
                    return false;
                }

                if (!new XsdCompiler().TryCompile(source, profile, options.InputKind, data!, new XmlClassAlgebra(),
                    ref budget, out var program, out var detail))
                { diagnostic = detail.Diagnostic; return false; }
                if (!new XsdExecutorFactory().TryCreate(program!, data!, ref budget, out var executor, out diagnostic))
                {
                    return false;
                }

                var retained = budget.ReservedBytes - FactoryBytes;
                if (retained > budget.Limits.MaxPlanBytes)
                { diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, source.Length, range.Unit); return false; }
                if (!budget.TryCharge(1, range, out diagnostic))
                {
                    return false;
                }

                pattern = new(executor!, options, retained);
            }
            finally { budget.ReleaseBytes(FactoryBytes); }
        }
        diagnostic = default;
        return true;
    }
}
