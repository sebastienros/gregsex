using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Recursion;

public sealed class Pcre2RecursionLimitTests
{
    private static RecursionFixture Routine(Pcre2ExecutionCapacity? capacity = null, Pcre2PatternLimits limits = default)
    {
        var p = new RecursionPlan();
        p.Seq(p.Define(p.Capture(p.Alt(p.Capture(p.Lit('a'), 2), p.Capture(p.Seq(p.Lit('a'), p.Lit('b')), 3)), 1)), p.Call(1), p.Lit('c'));
        return new(p.Build(limits), capacity);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LowerOnlyLimitsFailDistinctlyAndReuse(int limit)
    {
        var f = Routine();
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 8, 9), 4).ToArray();
        var limits = limit switch { 0 => new Pcre2MatchLimits(maxWork: 0), 1 => new(maxDepth: 0), 2 => new(maxWorkspaceBytes: 0), 3 => new(maxCaptureEvents: 1), _ => new(maxWork: 20) };
        var result = f.Match("abc", pairs, limits: limits);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        if (limit == 3)
        {
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, result.Failure.Code);
        }
        else
        {
            Assert.Equal(limit == 1 ? (int)Pcre2ErrorCode.Depthlimit : limit == 2 ? (int)Pcre2ErrorCode.Heaplimit : (int)Pcre2ErrorCode.Matchlimit, result.NativeReturnCode);
        }

        Assert.All(pairs, x => Assert.Equal(new(true, 8, 9), x));
        var states = f.States;
        var captures = f.Captures;
        var scratch = f.Scratch;
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("abc", pairs).Status);
        Assert.Equal(new(true, 0, 3), pairs[0]);
        Assert.All(pairs.Skip(1), x => Assert.Equal(default, x));
        Assert.Same(states, f.States);
        Assert.Same(captures, f.Captures);
        Assert.Same(scratch, f.Scratch);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PatternDirectivesOnlyReduceCeilings(int which)
    {
        var pattern = which switch { 0 => new Pcre2PatternLimits(0, null, null), 1 => new(null, 0, null), _ => new(null, null, 0) };
        var f = Routine(limits: pattern);
        var pairs = new Pcre2OffsetPair[4];
        var r = f.Match("abc", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, r.Status);
        Assert.Equal(which == 0 ? (int)Pcre2ErrorCode.Matchlimit : which == 1 ? (int)Pcre2ErrorCode.Depthlimit : (int)Pcre2ErrorCode.Heaplimit, r.NativeReturnCode);
    }
    [Fact]
    public void InitialCancellationPrecedesNativeZeroDenial()
    {
        var f = Routine();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var pairs = new Pcre2OffsetPair[4];
        var r = f.Match("abc", pairs, new() { CancellationToken = cancel.Token }, new(0, 0, 0, 0, 0));
        Assert.Equal(Pcre2MatchStatus.Cancelled, r.Status);
        Assert.Equal(RegexExecutionFailureCode.Cancelled, r.Failure.Code);
        Assert.Equal(0, r.Failure.WorkConsumed);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("abc", pairs).Status);
    }
    [Fact]
    public void ExpiredDeadlinePrecedesNativeZeroDenial()
    {
        var f = Routine();
        var machine = new Pcre2VmState(f.Capacity, new(0, 0, 0, 0, 0), 3);
        var state = f.State(machine.Limits, new() { Timeout = TimeSpan.FromTicks(1) });
        Thread.Sleep(1);
        Assert.False(GregsEx.Flavors.Pcre2.Runtime.Kernel.Pcre2KernelOperation.PollEntry(f.Program, ref machine, ref state));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, state.Failure.Code);
        Assert.Equal(0, machine.SourceReturnCode);
    }
    [Fact]
    public void PhysicalFrameCapacityIsBounded()
    {
        var f = Routine(new(3, 128, 4, 32));
        var pairs = new Pcre2OffsetPair[4];
        var r = f.Match("abc", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, r.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, r.Failure.Code);
    }
    [Fact]
    public async Task SeparatePreparedWorkspacesShareImmutableRecursivePlan()
    {
        var p = new RecursionPlan();
        p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
        var program = p.Build();
        var cancellation = TestContext.Current.CancellationToken;
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var f = new RecursionFixture(program);
            var pairs = new Pcre2OffsetPair[1];
            for (var i = 0; i < 12; i++)
            { Assert.Equal(Pcre2MatchStatus.Match, f.Match("aaaabbbb", pairs).Status); Assert.Equal(new(true, 0, 8), pairs[0]); }
        }, cancellation)).ToArray();
        await Task.WhenAll(tasks);
    }
    [Fact]
    public void DeepCallsUseIterativeFramesAndSharedWork()
    {
        var p = new RecursionPlan();
        p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
        var f = new RecursionFixture(p.Build(), new(2048, 1024, 8, 64));
        var input = new string('a', 100) + new string('b', 100);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status);
        Assert.True(f.Calls >= 100);
        Assert.Equal(new(true, 0, 200), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match(input, pairs, limits: new(maxWork: 100)).Status);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status);
    }
}
