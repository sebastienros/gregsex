using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Tests.Contracts.Declarations;

// NONSHIPPING declaration fixture. These types occur only in GregsEx.Tests.dll. They
// deliberately cannot produce a pattern; CompileSamples is compiled but never executed.
internal abstract class RegexPattern
{
    public static RegexPattern Parse(string pattern, RegexProfile profile, RegexCompileOptions? options = null)
        => throw new InvalidOperationException("Nonshipping API declaration fixture.");
    public static bool TryParse(ReadOnlySpan<char> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic, RegexCompileOptions? options = null)
        => throw new InvalidOperationException("Nonshipping API declaration fixture.");
    public static bool TryParse(ReadOnlySpan<byte> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic, RegexCompileOptions? options = null)
        => throw new InvalidOperationException("Nonshipping API declaration fixture.");
    public abstract RegexProfile Profile { get; }
    public abstract RegexInputKind InputKind { get; }
    public abstract RegexExecutionBackend ExecutionBackend { get; }
    public abstract int GroupCount { get; }
    public abstract RegexWorkspace CreateWorkspace(RegexWorkspaceOptions? options = null);
    public abstract bool IsMatch(ReadOnlySpan<char> input);
    public abstract bool IsMatch(ReadOnlySpan<byte> input);
}

internal abstract class RegexWorkspace : IDisposable
{
    public abstract void Reserve(RegexWorkspaceCapacity capacity);
    public abstract RegexSearchStatus IsMatch(ReadOnlySpan<char> input, int startAt = 0, RegexMatchOptions options = default);
    public abstract RegexSearchStatus IsMatch(ReadOnlySpan<byte> input, int startAt = 0, RegexMatchOptions options = default);
    public abstract RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, out RegexMatchRange match,
        RegexMatchOptions options = default);
    public abstract RegexSearchStatus Find(ReadOnlySpan<byte> input, int startAt, out RegexMatchRange match,
        RegexMatchOptions options = default);
    public abstract RegexSearchStatus FindCaptures(ReadOnlySpan<char> input, int startAt, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public abstract RegexSearchStatus FindCaptures(ReadOnlySpan<byte> input, int startAt, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public abstract RegexExecutionFailure LastFailure { get; }
    public abstract void Dispose();
}

internal static class CompileSamples
{
    internal static RegexSearchStatus CharUsage(ReadOnlySpan<char> source)
    {
        var pattern = RegexPattern.Parse("(a)(b)?", new RegularCoreProfile());
        using var workspace = pattern.CreateWorkspace();
        workspace.Reserve(new RegexWorkspaceCapacity(256, 1024, 512));
        _ = pattern.IsMatch(source); // Explicitly allocating convenience policy, separate from warmed APIs.
        _ = workspace.IsMatch(source);
        var status = workspace.Find(source, 0, out var range);
        if (status == RegexSearchStatus.Match)
        {
            var borrowedMatch = source.Slice(range.Index, range.Length);
            _ = borrowedMatch.Length;
        }
        Span<RegexCapture> groups = stackalloc RegexCapture[3]; // Fixed example, never input sized.
        return workspace.FindCaptures(source, 0, groups, out _);
    }

    internal static RegexSearchStatus ByteUsage(ReadOnlySpan<byte> source)
    {
        var profile = new RegularCoreProfile(RegularCoreMode.RawBytes);
        var options = new RegexCompileOptions(RegexInputKind.Bytes);
        _ = RegexPattern.TryParse("(a)"u8, profile, out _, out _, options);
        var pattern = RegexPattern.Parse("(a)", profile, options);
        using var workspace = pattern.CreateWorkspace(new(new(256, 1024, 512)));
        _ = workspace.IsMatch(source);
        _ = workspace.Find(source, 0, out var range);
        if (range.Length > 0)
        {
            _ = source.Slice(range.Index, range.Length).Length;
        }

        Span<RegexCapture> groups = stackalloc RegexCapture[2];
        return workspace.FindCaptures(source, 0, groups, out _);
    }

    internal static bool TryParseUsage(ReadOnlySpan<char> pattern)
        => RegexPattern.TryParse(pattern, new RegularCoreProfile(), out _, out _);

    // No text/compiler/executor implementation is needed to compile these lane signatures.
    internal static bool CompilerLane(IFlavorCompiler compiler, ReadOnlySpan<char> pattern,
        ProfileDescriptor profile, ref CompileBudget budget, out SemanticProgram? program,
        out RegexDiagnostic diagnostic) => compiler.TryCompile(pattern, profile, ref budget, out program, out diagnostic);

    internal static RegexSearchStatus ExecutorLane<TUnit, TReader>(IRegularExecutor executor,
        ReadOnlySpan<TUnit> input, ref RunState state, Span<RegexCapture> captures, out RegexMatchRange range)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
        => executor.Search<TUnit, TReader>(input, 0, SearchOperation.Captures, ref state, captures, out range);

    internal static RegexSearchStatus TextLane<TUnit, TReader>(ReadOnlySpan<TUnit> input,
        ProfileDescriptor profile, ref RunState state) where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
        => default(TReader).Validate(input, 0, profile, ref state);
}
