using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2AssertionResourceTests
{
    private static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    private static AssertionFixture RetryFixture(Pcre2ExecutionCapacity? capacity = null, Pcre2PatternLimits limits = default)
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Seq(p.Literal('a'), p.Literal('a')));
        var scan = p.Scan(p.Capture(p.Repeat(p.Literal('a'), 1, 2), 2));
        p.Seq(capture, scan, p.Backref([2]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        return new(p.Build(limits: limits), capacity);
    }
    [Fact]
    public void LimitsAreDistinctLeaveOutputsUntouchedAndAllowReuse()
    {
        var fixture = RetryFixture();
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 7, 8), 3).ToArray();
        var work = fixture.Match("aaa", pairs, options: Anchored, limits: new(maxWork: 20));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, work.Status);
        Assert.All(pairs, p => Assert.Equal(new(true, 7, 8), p));
        var depth = fixture.Match("aaa", pairs, options: Anchored, limits: new(maxDepth: 2));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, depth.Status);
        Assert.Equal((int)Pcre2ErrorCode.Depthlimit, depth.NativeReturnCode);
        var heap = fixture.Match("aaa", pairs, options: Anchored, limits: new(maxWorkspaceBytes: 1));
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, heap.NativeReturnCode);
        var events = fixture.Match("aaa", pairs, options: Anchored, limits: new(maxCaptureEvents: 1));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, events.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, events.Failure.Code);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aaa", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 0, 1), pairs[2]);
    }
    [Fact]
    public void PreparedFrameAndWindowDenialsDoNotBecomeNoMatch()
    {
        var small = RetryFixture(new(2, 128, 1, 64));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, small.Match("aaa", pairs, options: Anchored).Status);
        var p = new AssertionPlan();
        var capture = p.Capture(p.Literal('a'));
        var nested = p.Scan(p.Literal('a'));
        var outer = p.Scan(nested);
        p.Seq(capture, outer);
        var fixture = new AssertionFixture(p.Build(), new(128, 128, 1, 64));
        var result = fixture.Match("a", new Pcre2OffsetPair[2], options: Anchored);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, result.Failure.Code);
    }
    [Fact]
    public void InitialZeroAndReducedPatternWorkUseOneBudget()
    {
        var fixture = RetryFixture();
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, fixture.Match("aaa", pairs, options: Anchored, limits: new(maxWork: 0)).NativeReturnCode);
        fixture = RetryFixture(limits: new(MatchWork: 25, Depth: null, HeapBytes: null));
        var result = fixture.Match("aaa", pairs, options: Anchored);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.True(result.Failure.WorkConsumed <= 25);
    }
    [Fact]
    public void InitialCancellationAndExpiredDeadlinePollEvenEmptyAssertions()
    {
        var p = new AssertionPlan();
        p.Assertion(p.Leaf(Pcre2NodeKind.Empty));
        var fixture = new AssertionFixture(p.Build());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(Pcre2MatchStatus.Cancelled, fixture.Match("", new Pcre2OffsetPair[1], options: new() { CancellationToken = cts.Token }).Status);
        Assert.Equal(Pcre2MatchStatus.TimedOut, fixture.Match("", new Pcre2OffsetPair[1], options: new() { Timeout = TimeSpan.FromTicks(1) }).Status);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("", new Pcre2OffsetPair[1]).Status);
    }
    [Fact]
    public void SharedPlanPrivatePreparedWorkspacesAndMutatedBorrowedInput()
    {
        var original = RetryFixture();
        var states = original.States;
        var scratch = original.Scratch;
        Parallel.For(0, 16, _ =>
        {
            var fixture = new AssertionFixture(original.Program);
            var pairs = new Pcre2OffsetPair[3];
            Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aaa", pairs, options: Anchored).Status);
            Assert.Equal(new(true, 0, 1), pairs[2]);
        });
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(Pcre2MatchStatus.Match, original.Match("aaa", new Pcre2OffsetPair[3], options: Anchored).Status);
            Assert.Equal(Pcre2MatchStatus.NoMatch, original.Match("aab", new Pcre2OffsetPair[3], options: Anchored).Status);
            Assert.Same(states, original.States);
            Assert.Same(scratch, original.Scratch);
        }
        var p = new AssertionPlan(new(width: Pcre2CodeUnitWidth.Eight));
        p.Seq(p.Capture(p.Literal('a')), p.Backref([1]));
        var bytes = "aa"u8.ToArray();
        var borrowed = new AssertionFixture(p.Build());
        Assert.Equal(Pcre2MatchStatus.Match, borrowed.Match(bytes, new Pcre2OffsetPair[2], options: Anchored).Status);
        bytes[1] = 98;
        Assert.Equal(Pcre2MatchStatus.NoMatch, borrowed.Match(bytes, new Pcre2OffsetPair[2], options: Anchored).Status);
    }
    [Theory]
    [InlineData(255, true)]
    [InlineData(65535, false)]
    public void SourceFiniteLookbehindWidthLimitsRemainUsable(int length, bool variable)
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Repeat(p.Literal('a'), variable ? 0 : length, length));
        var behind = p.Assertion(capture, Pcre2AssertionKind.PositiveBehind, variable ? 0 : length, length, variable);
        p.Seq(behind, p.Literal('b'));
        var pairs = new Pcre2OffsetPair[2];
        var subject = new string('a', length) + "b";
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match(subject, pairs, length, Anchored).Status);
        Assert.Equal(new(true, 0, length), pairs[1]);
    }
}
