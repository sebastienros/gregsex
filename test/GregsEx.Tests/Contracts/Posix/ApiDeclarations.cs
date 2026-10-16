using GregsEx.Contracts;
using GregsEx.Contracts.Posix;

namespace GregsEx.Tests.Contracts.Posix.Declarations;

// Nonshipping shape only. Integration introduces concrete PosixFunctions
// with working factories/adapter, never this abstract declaration in GregsEx.dll.
internal abstract class PosixFunctions
{
    public abstract RegexPattern Parse(ReadOnlySpan<byte> pattern, PosixProfile profile,
        RegexCompileOptions? options = null);
    public abstract RegexPattern Parse(string pattern, PosixProfile profile,
        RegexCompileOptions? options = null);
    public abstract bool TryParse(ReadOnlySpan<byte> pattern, PosixProfile profile,
        out RegexPattern? result, out PosixRegexDiagnostic diagnostic, RegexCompileOptions? options = null);
    public abstract bool TryParse(ReadOnlySpan<char> pattern, PosixProfile profile,
        out RegexPattern? result, out PosixRegexDiagnostic diagnostic, RegexCompileOptions? options = null);
    public abstract RegexSearchStatus RegExec(RegexWorkspace workspace, ReadOnlySpan<byte> input,
        Span<PosixMatch> matches, PosixExecutionFlags flags = PosixExecutionFlags.None,
        RegexMatchOptions options = default);
    // ASCII unspecified message text, NUL-terminated/truncated native sizing.
    public abstract int RegError(PosixErrorCode code, Span<byte> destination);
}

internal static class CompileSamples
{
    internal static RegexSearchStatus Borrowed(PosixFunctions api, ReadOnlySpan<byte> input)
    {
        var profile = new PosixProfile(PosixDialect.Extended, PosixCompileFlags.Newline);
        var pattern = api.Parse("(a|aa)(a?)"u8, profile);
        using var workspace = pattern.CreateWorkspace();
        Span<RegexCapture> groups = stackalloc RegexCapture[3];
        var status = workspace.FindCaptures(input, 0, groups, out var range);
        if (status == RegexSearchStatus.Match)
        {
            _ = input.Slice(range.Index, range.Length);
        }

        Span<PosixMatch> native = stackalloc PosixMatch[2];
        _ = api.RegExec(workspace, input, native, PosixExecutionFlags.NotBol);
        Span<byte> error = stackalloc byte[16];
        _ = api.RegError(PosixErrorCode.InvalidBackreference, error);
        _ = workspace.IsMatch(input);
        return status;
    }
    internal static bool Parser(IPosixCompiler compiler, IPosixLocaleData data,
        ReadOnlySpan<byte> pattern, ref CompileBudget budget,
        out PosixProgram? program, out PosixRegexDiagnostic diagnostic)
        => compiler.TryCompile(pattern, new PosixProfile(), RegexInputKind.Bytes, data,
            ref budget, out program, out diagnostic);
    internal static RegexSearchStatus Executor(IPosixExecutor executor, ReadOnlySpan<byte> input,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        => executor.Search(input, 0, SearchOperation.Captures, PosixExecutionFlags.NotEol,
            PosixExecutionCapacity.Default, ref state, groups, out match);
}
