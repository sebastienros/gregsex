using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Execution;
using GregsEx.Flavors.XPath.Parsing;
using GregsEx.IR;

namespace GregsEx.Flavors.XPath.Integration;

// Immutable internal composition for the public runtime owner. That owner must
// separately admit its public pattern/bridge metadata before publishing it.
internal sealed class XPathBinding
{
    internal const int FrameCapacity = 4096;
    internal const long MetadataBytes = 96;
    private const long FactoryBytes = 96; // cold compiler/algebra/executor factory objects
    internal IXmlExecutor Executor { get; }
    internal XmlProgram Program => Executor.Program;
    internal long RetainedBytes { get; }
    private XPathBinding(IXmlExecutor executor, long retainedBytes)
    { Executor = executor; RetainedBytes = retainedBytes; }

    internal RegexWorkspaceCapacity RequiredCapacity(SearchOperation operation) =>
        Executor.GetRequiredCapacity(operation, FrameCapacity);

    // One caller-owned budget covers data, parsing, validation, executor and
    // binding. Earlier reservations remain charged; no phase resets the budget.
    internal static bool TryCreate(ReadOnlySpan<char> source, XPathProfile profile,
        RegexInputKind inputKind, ref CompileBudget budget,
        out XPathBinding? binding, out XmlRegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        binding = null;
        var range = new SourceRange(0, source.Length, RegexOffsetUnit.Utf16CodeUnits);
        if (!profile.Supports(inputKind))
        {
            return Failed(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, source.Length, range.Unit), out diagnostic);
        }

        if (source.Length > budget.Limits.MaxPatternUnits)
        {
            return Failed(new(RegexDiagnosticCode.PatternLimitExceeded, 0, source.Length, range.Unit), out diagnostic);
        }

        if (!budget.TryReserveBytes(FactoryBytes, range, out var admission))
        {
            return Failed(admission, out diagnostic);
        }

        try
        {
            if (!XmlUnicodeData.TryCreate(profile.UnicodeVersion, ref budget, out var data, out var failure))
            {
                return Failed(failure, out diagnostic);
            }

            if (!new XPathCompiler().TryCompile(source, profile, inputKind, data!, new XmlClassAlgebra(),
                ref budget, out var program, out diagnostic))
            { EnsureExpected(diagnostic.Diagnostic); return false; }
            if (!new XPathExecutorFactory().TryCreate(program!, data!, ref budget, out var executor, out failure))
            {
                return Failed(failure, out diagnostic);
            }
            // The executor's local plan check cannot replace this cumulative check:
            // static data, i mappings, previous owner reservations and binding count.
            if (budget.ReservedBytes - FactoryBytes > budget.Limits.MaxPlanBytes - MetadataBytes)
            {
                return Failed(new(RegexDiagnosticCode.PlanLimitExceeded, 0, source.Length, range.Unit), out diagnostic);
            }

            if (!budget.TryCharge(1, range, out failure) ||
                !budget.TryReserveBytes(MetadataBytes, range, out failure))
            {
                return Failed(failure, out diagnostic);
            }

            binding = new(executor!, budget.ReservedBytes - FactoryBytes);
            diagnostic = default;
            return true;
        }
        finally { budget.ReleaseBytes(FactoryBytes); }
    }
    private static bool Failed(RegexDiagnostic failure, out XmlRegexDiagnostic diagnostic)
    {
        EnsureExpected(failure);
        diagnostic = new(failure, XmlRegexDiagnosticLocation.Pattern, XPathErrorCode.None, "XP-017");
        return false;
    }
    private static void EnsureExpected(RegexDiagnostic diagnostic)
    {
        if (diagnostic.Code is RegexDiagnosticCode.None or RegexDiagnosticCode.InvalidProgram)
        {
            throw new InvalidOperationException("XPath compiler violated validated-program admission.");
        }
    }
}
