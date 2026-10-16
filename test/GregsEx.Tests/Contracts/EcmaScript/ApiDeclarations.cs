using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;

namespace GregsEx.Tests.Contracts.EcmaScript.Declarations;

internal readonly record struct NamedCapture(string Name, RegexCapture Capture);
internal abstract class ExecResult
{
    public abstract RegexMatchRange Range { get; }
    public abstract ReadOnlySpan<RegexCapture> Captures { get; }
    public abstract ReadOnlySpan<NamedCapture> Names { get; }
    public abstract bool HasIndices { get; }
}
internal abstract class MatchResult
{
    public abstract bool IsGlobal { get; }
    public abstract ExecResult? Single { get; }
    public abstract IReadOnlyList<string> GlobalMatches { get; }
}

internal abstract class MatcherFactory
{
    public abstract bool TryParse(ReadOnlySpan<char> pattern, EcmaScriptProfile profile,
        out EcmaScriptMatcher? matcher, out EcmaScriptDiagnostic diagnostic,
        RegexCompileOptions? compile = null, RegexWorkspaceOptions? workspace = null,
        int frameCapacity = 4096, EcmaScriptHostLimits? hostLimits = null);
    public abstract EcmaScriptMatcher Bind(RegexPattern pattern,
        RegexWorkspaceOptions? workspace = null, int frameCapacity = 4096,
        EcmaScriptHostLimits? hostLimits = null);
}

// NONSHIPPING signatures. No concrete matcher/factory or throwing stub is exported.
internal abstract class EcmaScriptMatcher : IDisposable
{
    public abstract double LastIndex { get; set; }
    public abstract RegexExecutionFailure LastFailure { get; }
    public abstract void Reserve(int frameCapacity);
    public abstract RegexSearchStatus Exec(ReadOnlySpan<char> input, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public abstract RegexSearchStatus Test(ReadOnlySpan<char> input, RegexMatchOptions options = default);
    public abstract RegexSearchStatus ExecNext(ReadOnlySpan<char> input, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public abstract MatchResult? Match(string input, RegexMatchOptions options = default);
    public abstract IEnumerable<ExecResult> MatchAll(string input, RegexMatchOptions options = default);
    public abstract int Search(string input, RegexMatchOptions options = default);
    public abstract string Replace(string input, string replacement, RegexMatchOptions options = default);
    public abstract string ReplaceAll(string input, string replacement, RegexMatchOptions options = default);
    public abstract string?[] Split(string input, double? limit = null, RegexMatchOptions options = default);
    public abstract void Dispose();
}

internal static class CompileSamples
{
    internal static bool Parse(MatcherFactory factory, ReadOnlySpan<char> pattern,
        out EcmaScriptMatcher? matcher, out EcmaScriptDiagnostic diagnostic)
        => factory.TryParse(pattern, new(EcmaScriptFlags.Global | EcmaScriptFlags.UnicodeSets),
            out matcher, out diagnostic, hostLimits: new(128, 4096, 65536));
    internal static RegexSearchStatus Borrowed(EcmaScriptMatcher matcher, ReadOnlySpan<char> input)
    {
        using (matcher)
        {
            matcher.LastIndex = 1;
            matcher.Reserve(128);
            Span<RegexCapture> groups = stackalloc RegexCapture[2];
            var status = matcher.Exec(input, groups, out var range);
            if (status == RegexSearchStatus.Match)
            {
                _ = input.Slice(range.Index, range.Length);
            }

            return status;
        }
    }

    internal static string Materialized(EcmaScriptMatcher matcher, string input)
    {
        _ = matcher.Match(input);
        _ = matcher.MatchAll(input);
        _ = matcher.Search(input);
        _ = matcher.Split(input, 3);
        _ = matcher.ReplaceAll(input, "$<name>");
        return matcher.Replace(input, "$1");
    }

    internal static bool ParserLane(IEcmaCompiler compiler, IEcmaUnicodeData unicode,
        ReadOnlySpan<char> pattern, ref CompileBudget budget, out EcmaProgram? plan,
        out EcmaScriptDiagnostic diagnostic)
        => compiler.TryCompile(pattern, new(EcmaScriptFlags.UnicodeSets), unicode,
            ref budget, out plan, out diagnostic);

    internal static RegexSearchStatus VmLane(IEcmaExecutor executor, ReadOnlySpan<char> input,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        => executor.Search(input, 1, true, EcmaStartPolicy.BuiltinLastIndex,
            SearchOperation.Captures, ref state, groups, out match);
}
