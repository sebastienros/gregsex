using System.Text;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

public sealed class ParityHostRepairTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ActualAheadBehindAndLaterCandidateResetResultsArePreservedOnce(int domain)
    {
        // Ratified managed iteration, docs/specs/pcre2.md native host surface.
        // Individual first pairs are native PCRE2 results, independent endpoints retained.
        foreach (var (Pattern, Input, Start, Pair) in new[] {
            (Pattern: @"(?=ab\K)", Input: "ab", Start: 0, Pair: new Pcre2OffsetPair(true, 2, 0)),
            (Pattern: @"(?<=\Ka)", Input: "a", Start: 1, Pair: new Pcre2OffsetPair(true, 0, 1)),
            (Pattern: @"(?=a\K)", Input: "aa", Start: 0, Pair: new Pcre2OffsetPair(true, 1, 0)) })
        {
            var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
                domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0, Pcre2ExtraFlags.AllowLookaroundBsk);
            using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(Pattern, domain, profile), RuntimeFixture.Capacity);
            var iteration = workspace.BeginIteration(Start);
            var pairs = new Pcre2OffsetPair[1];
            try
            {
                var first = Next(ref iteration, domain, Input, pairs);
                Assert.Equal(Pcre2MatchStatus.Match, first.Status);
                Assert.Equal(Pair, pairs[0]);
                var work = iteration.WorkConsumed;
                var next = Next(ref iteration, domain, Input, pairs);
                if (Input == "aa")
                {
                    Assert.Equal(Pcre2MatchStatus.Match, next.Status);
                    Assert.Equal(new(true, 2, 1), pairs[0]);
                    next = Next(ref iteration, domain, Input, pairs);
                }
                Assert.Equal(Pcre2MatchStatus.NoMatch, next.Status);
                Assert.True(iteration.WorkConsumed > work);
                Assert.True(workspace.IsIdle);
                Assert.Equal(RegexExecutionFailureCode.None, workspace.LastFailure.Code);
            }
            finally { iteration.Dispose(); }
            Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, Input, pairs, start: Start).Status);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NonemptyAlternativeRetryAndOrdinaryEmptyTerminalRemainUsable(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(?:|a)", domain), RuntimeFixture.Capacity);
        var iteration = workspace.BeginIteration();
        var pairs = new Pcre2OffsetPair[1];
        try
        {
            foreach (var pair in new[] { new Pcre2OffsetPair(true, 0, 0), new(true, 0, 1), new(true, 1, 1) })
            { Assert.Equal(Pcre2MatchStatus.Match, Next(ref iteration, domain, "a", pairs).Status); Assert.Equal(pair, pairs[0]); }
            Assert.Equal(Pcre2MatchStatus.NoMatch, Next(ref iteration, domain, "a", pairs).Status);
            Assert.True(workspace.IsIdle);
        }
        finally { iteration.Dispose(); }
        var empty = workspace.BeginIteration();
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref empty, domain, "", pairs).Status);
            Assert.Equal(new(true, 0, 0), pairs[0]);
            Assert.Equal(Pcre2MatchStatus.NoMatch, Next(ref empty, domain, "", pairs).Status);
            Assert.True(workspace.IsIdle);
        }
        finally { empty.Dispose(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CheckedCrlfAndNativeCharacterAdvanceSurvivesResetRepair(int domain)
    {
        var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0, newline: Pcre2Newline.Anycrlf);
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("", domain, profile), RuntimeFixture.Capacity);
        var input = domain % 2 == 1 ? "\r\n😀" : "\r\na";
        var end = domain == 1 ? 6 : domain == 3 ? 4 : 3;
        var iteration = workspace.BeginIteration();
        var pairs = new Pcre2OffsetPair[1];
        try
        {
            foreach (var offset in new[] { 0, 2, end })
            { Assert.Equal(Pcre2MatchStatus.Match, Next(ref iteration, domain, input, pairs).Status); Assert.Equal(new(true, offset, offset), pairs[0]); }
            Assert.Equal(Pcre2MatchStatus.NoMatch, Next(ref iteration, domain, input, pairs).Status);
            Assert.True(workspace.IsIdle);
        }
        finally { iteration.Dispose(); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ResetRetrySharesWorkBudgetAndCancellationUnwindsOwnership(int domain)
    {
        var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0, Pcre2ExtraFlags.AllowLookaroundBsk);
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(@"(?=ab\K)", domain, profile), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[1];
        long total;
        var full = workspace.BeginIteration();
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref full, domain, "ab", pairs).Status);
            Assert.Equal(Pcre2MatchStatus.NoMatch, Next(ref full, domain, "ab", pairs).Status);
            total = full.WorkConsumed;
        }
        finally { full.Dispose(); }
        var limited = workspace.BeginIteration(options: new() { Limits = new(maxWork: total - 1) });
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref limited, domain, "ab", pairs).Status);
            var denial = Next(ref limited, domain, "ab", pairs);
            Assert.Equal(Pcre2MatchStatus.LimitExceeded, denial.Status);
            Assert.Equal((int)Pcre2ErrorCode.Matchlimit, denial.NativeReturnCode);
            Assert.True(limited.WorkConsumed <= total - 1);
            Assert.True(workspace.IsIdle);
            // Native logical work denial is carried by MATCHLIMIT; existing
            // runtime state reserves Failure for managed budget/lifetime failures.
            Assert.Equal(denial.Failure, workspace.LastFailure);
        }
        finally { limited.Dispose(); }
        using var cancellation = new CancellationTokenSource();
        var cancelled = workspace.BeginIteration(options: new() { CancellationToken = cancellation.Token });
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref cancelled, domain, "ab", pairs).Status);
            cancellation.Cancel();
            Assert.Equal(Pcre2MatchStatus.Cancelled, Next(ref cancelled, domain, "ab", pairs).Status);
            Assert.True(workspace.IsIdle);
        }
        finally { cancelled.Dispose(); }
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "ab", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ResetRetryCallbacksAndExceptionsReleaseSession(int domain)
    {
        var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0, Pcre2ExtraFlags.AllowLookaroundBsk);
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(@"(?C7)(?=ab\K)", domain, profile), RuntimeFixture.Capacity);
        var calls = 0;
        var bytes = new Pcre2ByteCallbacks(Match: (in _, __) => { if (++calls > 1) { throw new ApplicationException("retry"); } return 0; });
        var chars = new Pcre2CharCallbacks(Match: (in _, __) => { if (++calls > 1) { throw new ApplicationException("retry"); } return 0; });
        var iteration = workspace.BeginIteration(bytes: domain < 2 ? bytes : null, chars: domain >= 2 ? chars : null);
        var pairs = new Pcre2OffsetPair[1];
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref iteration, domain, "ab", pairs).Status);
            var threw = false;
            try
            { Next(ref iteration, domain, "ab", pairs); }
            catch (ApplicationException) { threw = true; }
            Assert.True(threw);
            Assert.Equal(2, calls);
            Assert.True(workspace.IsIdle);
        }
        finally { iteration.Dispose(); }
        calls = 0;
        var limited = workspace.BeginIteration(options: new() { Limits = new(maxCallouts: 1) },
            bytes: domain < 2 ? bytes : null, chars: domain >= 2 ? chars : null);
        try
        {
            Assert.Equal(Pcre2MatchStatus.Match, Next(ref limited, domain, "ab", pairs).Status);
            Assert.Equal(Pcre2MatchStatus.LimitExceeded, Next(ref limited, domain, "ab", pairs).Status);
            Assert.Equal(1, calls);
            Assert.True(workspace.IsIdle);
        }
        finally { limited.Dispose(); }
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "ab", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ConcreteHostOverloadAndFrozenInterfaceBothExecuteRealSource(int domain)
    {
        var profile = RuntimeFixture.Profile(domain);
        var fixture = new HostFixture("(a)", profile);
        IPcre2Host frozen = new Pcre2Host();
        var host = new Pcre2Host();
        foreach (var cap in new int?[] { 1, 2, null })
        {
            var state = fixture.State();
            var result = domain < 2
                ? host.Substitute(fixture, "a"u8, 0, "$1$1"u8, new byte[128], default, default, null, new(), default, default, fixture.Capacity, ref state, fixture.CallbackPairs, cap)
                : host.Substitute(fixture, "a", 0, "$1$1", new char[128], default, default, null, new(), default, default, fixture.Capacity, ref state, fixture.CallbackPairs, cap);
            Assert.Equal(cap == 1 ? -54 : 1, result.NativeReturnCode);
            Assert.Equal(2, result.OutputUnits);
            Assert.True(fixture.Released);
        }
        var final = fixture.State();
        var auto = domain < 2
            ? frozen.Substitute(fixture, "a"u8, 0, "$1$1"u8, new byte[128], default, default, null, new(), default, default, fixture.Capacity, ref final, fixture.CallbackPairs)
            : frozen.Substitute(fixture, "a", 0, "$1$1", new char[128], default, default, null, new(), default, default, fixture.Capacity, ref final, fixture.CallbackPairs);
        Assert.Equal(1, auto.NativeReturnCode);
        Assert.Equal(2, auto.OutputUnits);
    }

    private static Pcre2MatchResult Next(ref Pcre2PreparedIteration iteration, int domain, string input, Span<Pcre2OffsetPair> pairs) =>
        domain < 2 ? iteration.Next(Encoding.UTF8.GetBytes(input), pairs) : iteration.Next(input, pairs);
}
