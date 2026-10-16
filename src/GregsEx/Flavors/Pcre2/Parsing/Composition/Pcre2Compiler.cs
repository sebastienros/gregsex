using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;

namespace GregsEx.Flavors.Pcre2.Parsing.Composition;

// Stateless factory boundary. Each invocation owns its storage and one budget;
// the Syntax reservation stays live until the executable has copied its data.
internal sealed class Pcre2Compiler : IPcre2Compiler
{
    private static readonly Pcre2SyntaxParser Parser = new();
    private static readonly Pcre2Lowerer Lowerer = new();

    public bool TryCompile(ReadOnlySpan<char> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? program, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(data);
        program = null;
        var initial = budget.ReservedBytes;
        try
        {
            return Parser.TryParse(pattern, profile, data, ref budget, out var syntax, out diagnostic) &&
                Lowerer.TryLower(syntax!, data, ref budget, out program, out diagnostic);
        }
        finally { budget.ReleaseBytes(budget.ReservedBytes - initial - (program?.RetainedBytes ?? 0)); }
    }

    public bool TryCompile(ReadOnlySpan<byte> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? program, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(data);
        program = null;
        var initial = budget.ReservedBytes;
        try
        {
            return Parser.TryParse(pattern, profile, data, ref budget, out var syntax, out diagnostic) &&
                Lowerer.TryLower(syntax!, data, ref budget, out program, out diagnostic);
        }
        finally { budget.ReleaseBytes(budget.ReservedBytes - initial - (program?.RetainedBytes ?? 0)); }
    }

    // Explicit generic factory admission after semantic validation, before any
    // executor is created. Typed compilation retains all valid native outcomes.
    internal bool TryCompile(ReadOnlySpan<char> pattern, Pcre2Profile profile, IPcre2Data data,
        Pcre2ProjectionPolicy projection, ref CompileBudget budget,
        out Pcre2Program? program, out Pcre2Diagnostic diagnostic)
    {
        if (!Enum.IsDefined(projection))
        {
            throw new ArgumentOutOfRangeException(nameof(projection));
        }

        return TryCompile(pattern, profile, data, ref budget, out program, out diagnostic) &&
            AdmitProjection(projection, ref budget, ref program, out diagnostic);
    }

    internal bool TryCompile(ReadOnlySpan<byte> pattern, Pcre2Profile profile, IPcre2Data data,
        Pcre2ProjectionPolicy projection, ref CompileBudget budget,
        out Pcre2Program? program, out Pcre2Diagnostic diagnostic)
    {
        if (!Enum.IsDefined(projection))
        {
            throw new ArgumentOutOfRangeException(nameof(projection));
        }

        return TryCompile(pattern, profile, data, ref budget, out program, out diagnostic) &&
            AdmitProjection(projection, ref budget, ref program, out diagnostic);
    }

    private static bool AdmitProjection(Pcre2ProjectionPolicy projection, ref CompileBudget budget,
        ref Pcre2Program? program, out Pcre2Diagnostic diagnostic)
    {
        if (program!.TryAdmitProjection(projection, out diagnostic))
        {
            return true;
        }

        budget.ReleaseBytes(program.RetainedBytes);
        program = null;
        return false;
    }
}
