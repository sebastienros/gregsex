using GregsEx.Contracts;
using GregsEx.Execution.Posix;
using GregsEx.Flavors.Posix.Locale;
using GregsEx.Flavors.Posix.Parsing;
using GregsEx.IR;

namespace GregsEx.Flavors.Posix.Integration;

internal static class PosixPatternFactory
{
    private const long WrapperBytes = 320, FactoryBytes = 96;
    internal static bool TryParse(ReadOnlySpan<char> chars, ReadOnlySpan<byte> bytes, bool character,
        PosixProfile profile, RegexCompileOptions options, out RegexPattern? pattern, out PosixRegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        pattern = null;
        var source = new SourceRange(0, character ? chars.Length : bytes.Length,
            character ? RegexOffsetUnit.Utf16CodeUnits : RegexOffsetUnit.Bytes);
        var budget = new CompileBudget(options.Limits);
        // Representation/domain/grammar diagnostic order belongs to the parser.
        var compiler = new PosixCompiler();
        var success = character
            ? compiler.TryCompile(chars, profile, options.InputKind, CByteLocale.Instance, ref budget, out var program, out diagnostic)
            : compiler.TryCompile(bytes, profile, options.InputKind, CByteLocale.Instance, ref budget, out program, out diagnostic);
        if (!success)
        {
            return false;
        }

        if (!budget.TryReserveBytes(WrapperBytes + FactoryBytes, source, out var failure))
        { diagnostic = new(failure); return false; }
        try
        {
            if (!new PosixExecutorFactory().TryCreate(program!, CByteLocale.Instance, ref budget, out var executor, out failure))
            { diagnostic = new(failure); return false; }
            var retained = budget.ReservedBytes - FactoryBytes;
            if (retained > budget.Limits.MaxPlanBytes)
            { diagnostic = new(new(RegexDiagnosticCode.PlanLimitExceeded, 0, source.Length, source.Unit)); return false; }
            if (!budget.TryCharge(1, source, out failure))
            { diagnostic = new(failure); return false; }
            pattern = new(executor!, options, retained);
            diagnostic = default;
            return true;
        }
        finally { budget.ReleaseBytes(FactoryBytes); }
    }
}
