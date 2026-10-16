using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.XPath.Execution;

internal sealed class XPathExecutorFactory : IXmlExecutorFactory
{
    public bool TryCreate(XmlProgram program, IXmlUnicodeData unicode, ref CompileBudget budget,
        out IXmlExecutor? executor, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(unicode);
        executor = null;
        diagnostic = default;
        var source = new SourceRange(0, program.PatternLength, RegexOffsetUnit.Utf16CodeUnits);
        if (program.Dialect != XmlDialect.XPath || unicode.DataId != program.Profile.DataId)
        { diagnostic = new(RegexDiagnosticCode.InvalidProgram, 0, program.PatternLength, source.Unit); return false; }
        // Copy the provider's immutable mappings once; no provider calls during search.
        var mappings = (((XPathProfile)program.Profile).Flags & XPathFlags.IgnoreCase) != 0
            ? unicode.CaseMappings : [];
        var bytes = 160L + 12L * mappings.Length;
        if (bytes > budget.Limits.MaxPlanBytes - program.RetainedBytes)
        { diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, program.PatternLength, source.Unit); return false; }
        var scratch = 32L + 4L * program.Nodes.Length;
        if (!budget.TryReserveBytes(scratch, source, out diagnostic))
        {
            return false;
        }

        try
        {
            var depths = new int[program.Nodes.Length];
            for (var i = 0; i < depths.Length; i++)
            {
                var n = program.Nodes[i];
                if (!budget.TryCharge(1, n.Source, out diagnostic))
                {
                    return false;
                }

                depths[i] = 1 + Math.Max(n.X < 0 ? 0 : depths[n.X], n.Y < 0 ? 0 : depths[n.Y]);
            }
            if (!budget.TryCharge(mappings.Length, source, out diagnostic) ||
                !budget.TryReserveBytes(bytes, source, out diagnostic))
            {
                return false;
            }

            executor = new XPathExecutor(program, mappings.ToArray(), checked(2 * depths[program.Root] + 4));
            return true;
        }
        finally { budget.ReleaseBytes(scratch); }
    }
}
