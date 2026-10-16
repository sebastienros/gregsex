using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

internal static class RuntimeFixture
{
    internal static readonly Pcre2ExecutionCapacity Capacity = new(1024, 16384, 64, 512);
    internal static Pcre2Profile Profile(int domain, Pcre2CompileFlags flags = default) => new(
        domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
        flags | (domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0));
    internal static Pcre2Executor Compile(string pattern, int domain = 2, Pcre2Profile? profile = null)
    {
        var budget = new CompileBudget(new());
        profile ??= Profile(domain);
        var ok = domain < 2
            ? Pcre2ExecutorFactory.TryCompile(Encoding.UTF8.GetBytes(pattern), profile, ref budget, out var executor, out var diagnostic)
            : Pcre2ExecutorFactory.TryCompile(pattern.AsSpan(), profile, ref budget, out executor, out diagnostic);
        Assert.True(ok, diagnostic.ToString());
        Assert.Equal(executor!.RetainedBytes, budget.ReservedBytes);
        return executor;
    }
    internal static Pcre2MatchResult Match(Pcre2PreparedWorkspace workspace, int domain,
        string input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default, int start = 0) =>
        domain < 2 ? workspace.Match(Encoding.UTF8.GetBytes(input), start, pairs, options) : workspace.Match(input, start, pairs, options);
}
