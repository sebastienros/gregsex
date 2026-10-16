using GregsEx.Contracts;
using GregsEx.IR;
using GregsEx.Parsing.Core;

namespace GregsEx.Compilation.Core;

// Runtime binds this compiler after verified lane integration. No text-reader dependency.
internal sealed class CoreCompiler : IFlavorCompiler
{
    public bool TryCompile(ReadOnlySpan<char> pattern, ProfileDescriptor profile, ref CompileBudget budget,
        out SemanticProgram? program, out RegexDiagnostic diagnostic)
        => Compile(pattern, default, false, profile, ref budget, out program, out diagnostic);

    public bool TryCompile(ReadOnlySpan<byte> pattern, ProfileDescriptor profile, ref CompileBudget budget,
        out SemanticProgram? program, out RegexDiagnostic diagnostic)
        => Compile(default, pattern, true, profile, ref budget, out program, out diagnostic);

    private static bool Compile(ReadOnlySpan<char> chars, ReadOnlySpan<byte> bytes, bool bytePattern,
        ProfileDescriptor profile, ref CompileBudget budget, out SemanticProgram? program, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        program = null;
        diagnostic = default;
        var initialBytes = budget.ReservedBytes;
        var parser = new CoreParser(chars, bytes, bytePattern, profile, budget);
        try
        {
            if (!parser.Parse(out var root))
            { diagnostic = parser.Diagnostic; return false; }
            if (parser.Nodes.Items[root].Instructions + 3 > parser.Budget.Limits.MaxInstructions)
            {
                diagnostic = new(RegexDiagnosticCode.ProgramLimitExceeded, parser.Nodes.Items[root].Source.Offset,
                    parser.Nodes.Items[root].Source.Length, parser.Unit);
                return false;
            }
            return CoreLowering.TryLower(ref parser, root, out program, out diagnostic);
        }
        finally
        {
            parser.ReleaseTemporaries();
            budget = parser.Budget;
            // Failed compilation publishes nothing: release retained class/program reservations too.
            if (program is null)
            {
                budget.ReleaseBytes(budget.ReservedBytes - initialBytes);
            }
        }
    }
}
