using System.Text;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class ResetStartIterationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ReversedAndPreBoundResetResultsAreEmittedOnce(int domain)
    {
        // Managed session rule from docs/specs/pcre2.md, Native host surface:
        // preserve the first native result, then bounded NOTEMPTY_ATSTART+ANCHORED retry/advance.
        foreach (var (Pattern, Subject, Start, Pair) in new[] { (Pattern: @"(?=ab\K)", Subject: "ab", Start: 0, Pair: new Pcre2OffsetPair(true, 2, 0)), (Pattern: @"(?<=\Ka)", Subject: "a", Start: 1, Pair: new Pcre2OffsetPair(true, 0, 1)) })
        {
            var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
                domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0, Pcre2ExtraFlags.AllowLookaroundBsk);
            var budget = new CompileBudget(new());
            var ok = domain < 2 ? Pcre2ExecutorFactory.TryCompile(Encoding.UTF8.GetBytes(Pattern), profile, ref budget, out var executor, out var error)
                : Pcre2ExecutorFactory.TryCompile(Pattern.AsSpan(), profile, ref budget, out executor, out error);
            Assert.True(ok, error.ToString());
            using var workspace = new Pcre2PreparedWorkspace(executor!, new(4096, 65536, 512, 2048));
            using var iteration = workspace.BeginIteration(Start);
            var pairs = new Pcre2OffsetPair[1];
            var first = domain < 2 ? iteration.Next(Encoding.UTF8.GetBytes(Subject), pairs) : iteration.Next(Subject, pairs);
            Assert.Equal(Pcre2MatchStatus.Match, first.Status);
            Assert.Equal(Pair, pairs[0]);
            var second = domain < 2 ? iteration.Next(Encoding.UTF8.GetBytes(Subject), pairs) : iteration.Next(Subject, pairs);
            Assert.Equal(Pcre2MatchStatus.NoMatch, second.Status);
            Assert.True(workspace.IsIdle);
        }
    }
}
