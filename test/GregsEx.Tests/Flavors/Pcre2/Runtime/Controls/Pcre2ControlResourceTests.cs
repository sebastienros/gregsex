using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2ControlResourceTests
{
    [Fact]
    public void BehindThenBindingSharesCumulativeWorkAndReusesWorkspaceAfterDenial()
    {
        var f = new ControlFixture("(?<=a(*THEN)b|ac)x", new(flags: Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoAutoPossess));
        var pairs = new Pcre2OffsetPair[1];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored };
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("acx", pairs, options, start: 2).Status);
        var ceiling = Math.Max(1, f.Work / 2);
        pairs[0] = new(true, 7, 9);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("acx", pairs, options, limits: new(maxWork: ceiling), start: 2).Status);
        Assert.True(f.Work <= ceiling);
        Assert.Equal(new(true, 7, 9), pairs[0]);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("acx", pairs, options, start: 2).Status);
        Assert.Equal(new(true, 2, 3), pairs[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ZeroAdmissionAndCancellationPrecedeEmptyAccept(int which)
    {
        var f = new ControlFixture("(*ACCEPT)");
        Pcre2OffsetPair[] pairs = [new(true, 7, 9)];
        var limits = which switch
        {
            0 => new Pcre2MatchLimits(maxWork: 0),
            1 => new(maxDepth: 0),
            _ => new Pcre2MatchLimits(maxWorkspaceBytes: 0)
        };
        using var token = new CancellationTokenSource();
        if (which == 3)
        {
            token.Cancel();
        }

        var result = f.Match("", pairs, new() { CancellationToken = token.Token }, limits: limits);
        Assert.Equal(which == 3 ? Pcre2MatchStatus.Cancelled : Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(which == 3 ? 0 : which == 0 ? (int)Pcre2ErrorCode.Matchlimit : which == 1 ?
            (int)Pcre2ErrorCode.Depthlimit : (int)Pcre2ErrorCode.Heaplimit, result.NativeReturnCode);
        Assert.Equal(new(true, 7, 9), pairs[0]);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ActualFramesHeapAndCaptureEventsStayBounded(int denied)
    {
        var f = new ControlFixture("(a)(?C1)", capacity: new(16, 16, 8, 32));
        var limits = denied switch
        {
            0 => new Pcre2MatchLimits(maxDepth: 1),
            1 => new(maxWorkspaceBytes: f.Capacity.RequiredStorage(f.Program.GroupCount).RequiredBytes - 1),
            _ => new Pcre2MatchLimits(maxCaptureEvents: 0)
        };
        var pairs = new Pcre2OffsetPair[2];
        var count = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) => { count++; return 0; });
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("a", pairs, callbacks: callbacks, limits: limits).Status);
        Assert.Equal(0, count);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("a", pairs, callbacks: callbacks).Status);
        Assert.Equal(1, count);
    }

    [Fact]
    public void NamedSkipRematchKeepsOneCalloutBudgetAndWorkspaceCanBeReused()
    {
        var f = new ControlFixture("(?C1)a(*SKIP:X)b|ac", new(flags: Pcre2CompileFlags.NoStartOptimize));
        var pairs = new Pcre2OffsetPair[1];
        var count = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) => { count++; return 0; });
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("ac", pairs, callbacks: callbacks, limits: new(maxCallouts: 1)).Status);
        Assert.Equal(1, count);
        Assert.True(f.Released);
        count = 0;
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ac", pairs, callbacks: callbacks).Status);
        Assert.Equal(2, count);
        Assert.Equal(new(true, 0, 2), pairs[0]);
    }

    [Fact]
    public void CallbackExceptionAndCancellationReleaseActualSearch()
    {
        var f = new ControlFixture("a(*MARK:X)(?C1)b");
        var pairs = new Pcre2OffsetPair[1];
        var throws = new Pcre2CharCallbacks((in c, _) => throw new ApplicationException("user"));
        Assert.Throws<ApplicationException>(() => f.Match("ab", pairs, callbacks: throws));
        Assert.True(f.Released);
        using var token = new CancellationTokenSource();
        var cancels = new Pcre2CharCallbacks((in c, _) => { token.Cancel(); return 0; });
        Assert.Equal(Pcre2MatchStatus.Cancelled, f.Match("ab", pairs, new() { CancellationToken = token.Token }, cancels).Status);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs).Status);
    }

    [Fact]
    public void ReusedSubjectAndWorkspaceDoNotLeakAcceptanceOrGrowPreparedStorage()
    {
        var f = new ControlFixture("a(*MARK:X)b", new(Pcre2CodeUnitWidth.Eight));
        var stateArray = f.States;
        var scratch = f.Scratch;
        var staging = f.CallbackPairs;
        byte[] input = [97, 98];
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status);
        input[0] = 99;
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match(input, pairs).Status);
        Assert.Equal(new(true, 0, 2), pairs[0]); // native failure leaves caller output unchanged
        input[0] = 97;
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status);
        Assert.Same(stateArray, f.States);
        Assert.Same(scratch, f.Scratch);
        Assert.Same(staging, f.CallbackPairs);
        Assert.True(f.Released);
    }

    [Fact]
    public async Task ImmutablePlanHasIndependentConcurrentWorkspaces()
    {
        var original = new ControlFixture("a(*MARK:X)b|ac");
        var other = new ControlFixture(original.Program);
        async Task<Pcre2OffsetPair> Run(ControlFixture f, string input)
        {
            return await Task.Run(() => { var pairs = new Pcre2OffsetPair[1]; Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status); return pairs[0]; });
        }
        var results = await Task.WhenAll(Run(original, "xxab"), Run(other, "xac"));
        Assert.Equal(new(true, 2, 4), results[0]);
        Assert.Equal(new(true, 1, 3), results[1]);
        Assert.True(original.Released);
        Assert.True(other.Released);
    }
}
