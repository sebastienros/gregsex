using System.Diagnostics;
using GregsEx.Contracts;
using GregsEx.Execution.Compiled;
using Xunit;

namespace GregsEx.Tests.Contracts;

// A call's relative timeout may begin at the first stride checkpoint instead of at construction (RunBudget's deferred
// start): a call that ends within RunBudget.ClockStride work units reads no clock, and everything else about the budget
// - cancellation, the work limit, a tiny timeout, the deterministic boundary hook - is as it was.
public class DeferredDeadlineTests
{
    private static RunBudget Deferred(TimeSpan? timeout, long maxWork = long.MaxValue, CancellationToken cancellation = default)
        => new(maxWork, cancellation, timeout, deferStart: true);

    [Fact]
    public void ACallThatEndsWithinItsFirstStrideReadsNoClock()
    {
        var budget = Deferred(TimeSpan.FromSeconds(10));
        Assert.Equal(0, budget.StartedAt);
        // Zero-unit checkpoints wait for the stride like any other charge: a start-of-call poll reads no clock.
        Assert.True(budget.TryCharge(0, 0, out _));
        Assert.True(budget.TryCharge(RunBudget.ClockStride - 1, 0, out _));
        Assert.True(budget.TryCharge(0, 0, out _));
        // The charge that reaches the stride only schedules the read; it happens on the next one.
        Assert.True(budget.TryCharge(1, 0, out _));
        Assert.Equal(0, budget.StartedAt);
        Assert.Equal(RunBudget.ClockStride, budget.WorkConsumed);
    }

    [Fact]
    public void TheFirstStrideReadBeginsTheTimeoutAndLaterReadsKeepIt()
    {
        var budget = Deferred(TimeSpan.FromSeconds(10));
        Assert.True(budget.TryCharge(RunBudget.ClockStride, 0, out _));
        var before = Stopwatch.GetTimestamp();
        Assert.True(budget.TryCharge(1, 0, out _));
        var after = Stopwatch.GetTimestamp();
        var started = budget.StartedAt;
        Assert.InRange(started, before, after);

        // The deadline is measured from that read, at the boundary and not a tick before it.
        var ticks = (long)(TimeSpan.FromSeconds(10).TotalSeconds * Stopwatch.Frequency);
        Assert.True(budget.TryChargeAt(0, 0, started + ticks - 1, out _));
        Assert.False(budget.TryChargeAt(0, 0, started + ticks, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.TimedOut, 0, budget.WorkConsumed), failure);

        // A read at a later stride does not move it.
        Assert.True(budget.TryCharge(RunBudget.ClockStride, 0, out _));
        Assert.True(budget.TryCharge(1, 0, out _));
        Assert.Equal(started, budget.StartedAt);
    }

    [Fact]
    public void TheTimeoutBeginsAtTheFirstStrideNotAtConstruction()
    {
        // The timeout is a few milliseconds and the call is idle for far longer before it charges anything. A budget
        // armed at construction reports that (ElapsedTimeoutIsObservedWithinTheClockStride); a deferred one measures
        // from its first read, which is the documented lateness of a deferred deadline.
        var budget = Deferred(TimeSpan.FromMilliseconds(5));
        Thread.Sleep(30);
        Assert.True(budget.TryCharge(RunBudget.ClockStride, 0, out _));
        Assert.True(budget.TryCharge(1, 0, out _));
        Assert.NotEqual(0, budget.StartedAt);

        // From there it is the ordinary amortized deadline: observed within the next stride of charged work.
        Thread.Sleep(30);
        long charged = 0;
        RegexExecutionFailure failure;
        while (budget.TryCharge(1, 0, out failure))
        {
            Assert.True(++charged <= 2L * RunBudget.ClockStride);
        }

        Assert.Equal(RegexExecutionFailureCode.TimedOut, failure.Code);
        Assert.True(charged <= RunBudget.ClockStride);
    }

    [Theory]
    [InlineData(1)] // one tick
    [InlineData(10_000)] // 1 ms, the short-deadline threshold itself
    public void ATimeoutWithinTheShortDeadlineBeginsAtConstructionAndPollsEveryCheckpoint(long ticks)
    {
        var timeout = TimeSpan.FromTicks(ticks);
        var before = Stopwatch.GetTimestamp();
        var budget = Deferred(timeout);
        Assert.InRange(budget.StartedAt, before, Stopwatch.GetTimestamp());

        // A zero-unit checkpoint reads the clock, so an empty subject still observes an elapsed deadline.
        var timeoutTicks = timeout.TotalSeconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() - budget.StartedAt < timeoutTicks)
        { }
        Assert.False(budget.TryCharge(0, 3, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.TimedOut, 3, 0), failure);
    }

    [Fact]
    public void ATimeoutJustOverTheShortDeadlineIsDeferred()
    {
        Assert.Equal(0, Deferred(TimeSpan.FromTicks(TimeSpan.TicksPerMillisecond + 1)).StartedAt);
    }

    [Fact]
    public void WithoutATimeoutNothingIsPendingAndTheClockIsNeverRead()
    {
        var budget = Deferred(null);
        Assert.True(budget.IsUnconstrained(1));
        Assert.True(budget.TryCharge(3 * RunBudget.ClockStride, 0, out _));
        Assert.Equal(0, budget.StartedAt);
        budget.StartDeadline();
        Assert.Equal(0, budget.StartedAt);
    }

    [Fact]
    public void ADeferredTimeoutIsStillAConstraint()
    {
        // Searches that skip charging because nothing can stop them must not skip it for a deferred deadline.
        Assert.False(Deferred(TimeSpan.FromSeconds(10)).IsUnconstrained(0));
        Assert.False(Deferred(TimeSpan.MaxValue).IsUnconstrained(0));
    }

    [Fact]
    public void CancellationIsCheckedOnEveryChargeBeforeTheFirstStride()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = Deferred(TimeSpan.FromSeconds(10), cancellation: cancellation.Token);
        Assert.True(budget.TryCharge(5, 0, out _));
        cancellation.Cancel();
        Assert.False(budget.TryCharge(0, 7, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.Cancelled, 7, 5), failure);
        Assert.False(budget.TryCharge(1, 8, out failure));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, failure.Code);
        Assert.Equal(0, budget.StartedAt);
    }

    [Fact]
    public void TheWorkLimitIsCheckedOnEveryChargeBeforeTheFirstStride()
    {
        var budget = Deferred(TimeSpan.FromSeconds(10), maxWork: 10);
        Assert.True(budget.TryCharge(6, 0, out _));
        Assert.False(budget.TryCharge(5, 4, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 4, 6), failure);
        Assert.True(budget.TryCharge(4, 0, out _));
        Assert.False(budget.TryCharge(1, 9, out failure));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, failure.Code);
        Assert.Equal(0, budget.StartedAt);
    }

    [Fact]
    public void CancellationPrecedesTheDeadlineAndTheWorkLimitAfterTheStartToo()
    {
        using var cancellation = new CancellationTokenSource();
        var budget = Deferred(TimeSpan.FromSeconds(10), maxWork: RunBudget.ClockStride + 1, cancellation: cancellation.Token);
        Assert.True(budget.TryCharge(RunBudget.ClockStride, 0, out _));
        cancellation.Cancel();
        // This charge reads the clock, begins the timeout, and is refused for the token, ahead of the work limit.
        Assert.False(budget.TryCharge(5, 9, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.Cancelled, 9, RunBudget.ClockStride), failure);
        Assert.NotEqual(0, budget.StartedAt);
    }

    [Fact]
    public void ATimestampHandedToTheBoundaryHookBeginsAPendingStart()
    {
        // The hook is a clock read, so on a budget that has not started it begins the timeout there instead of
        // measuring a real timestamp from 0 and timing out at once.
        var budget = Deferred(TimeSpan.FromSeconds(1));
        var now = Stopwatch.GetTimestamp();
        Assert.True(budget.TryChargeAt(0, 0, now, out _));
        Assert.Equal(now, budget.StartedAt);
        Assert.True(budget.TryChargeAt(0, 0, now + Stopwatch.Frequency - 1, out _));
        Assert.False(budget.TryChargeAt(0, 0, now + Stopwatch.Frequency, out var failure));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, failure.Code);
    }

    [Fact]
    public void StartDeadlineReadsTheClockOnceAndOnlyForAPendingStart()
    {
        var budget = Deferred(TimeSpan.FromSeconds(10));
        var before = Stopwatch.GetTimestamp();
        budget.StartDeadline();
        var started = budget.StartedAt;
        Assert.InRange(started, before, Stopwatch.GetTimestamp());
        Thread.Sleep(2);
        budget.StartDeadline();
        Assert.Equal(started, budget.StartedAt);
        // The first stride then reads no start of its own: the timeout stays where StartDeadline put it.
        Assert.True(budget.TryCharge(RunBudget.ClockStride, 0, out _));
        Assert.True(budget.TryCharge(1, 0, out _));
        Assert.Equal(started, budget.StartedAt);
    }

    [Fact]
    public void TheEagerConstructorsStillBeginAtConstruction()
    {
        var before = Stopwatch.GetTimestamp();
        var eager = new RunBudget(long.MaxValue, default, TimeSpan.FromSeconds(10));
        Assert.InRange(eager.StartedAt, before, Stopwatch.GetTimestamp());
        before = Stopwatch.GetTimestamp();
        var notDeferred = new RunBudget(long.MaxValue, default, TimeSpan.FromSeconds(10), deferStart: false);
        Assert.InRange(notDeferred.StartedAt, before, Stopwatch.GetTimestamp());
        var started = eager.StartedAt;
        eager.StartDeadline();
        Assert.Equal(started, eager.StartedAt);
    }

    [Fact]
    public void AKernelGuardBeginsAPendingStartOnTheOriginalSoACopyCannotRestartIt()
    {
        var budget = Deferred(TimeSpan.FromSeconds(10));
        var before = Stopwatch.GetTimestamp();
        var guard = KernelGuard.Start(ref budget);
        Assert.InRange(budget.StartedAt, before, Stopwatch.GetTimestamp());
        // The guard polls the clock itself at the start of a kernel, and the deadline it polls against is the budget's.
        Assert.Equal(0, guard.NextPoll);
        Assert.Equal(0, guard.Poll(0));
        Assert.Equal(RunBudget.ClockStride, guard.NextPoll);
    }

    [Fact]
    public void ADeferredRunStateTimesOutACatastrophicSearchThroughEachSingleCallEntry()
    {
        // (a+)+b$ over a long run of a's and a b that $ then refuses is exponential for a backtracker, and no
        // required-text or end-of-input check rejects the subject before the backtracking starts.
        // The work limit is out of the way: the default one is reached in about the time the timeout allows.
        var unlimited = new RegexCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: long.MaxValue / 2));
        Assert.True(EcmaScriptMatcher.TryParse("^(a+)+b$", new EcmaScriptProfile(EcmaScriptFlags.None), out var matcher, out var diagnostic,
            compile: unlimited), diagnostic.ToString());
        using (matcher)
        {
            var input = new string('a', 64) + "bb";
            var options = new RegexMatchOptions { Timeout = TimeSpan.FromMilliseconds(50) };
            var groups = new RegexCapture[2];
            var watch = Stopwatch.StartNew();
            Assert.Equal(RegexSearchStatus.TimedOut, matcher!.Exec(input, groups, out _, options));
            Assert.Equal(RegexExecutionFailureCode.TimedOut, matcher.LastFailure.Code);
            Assert.Equal(RegexSearchStatus.TimedOut, matcher.Test(input, options));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
            // The matcher is reusable after a timeout, and a search that finishes inside its first stride is unaffected.
            Assert.Equal(RegexSearchStatus.Match, matcher.Test("aaab", options));
            Assert.Equal(RegexSearchStatus.NoMatch, matcher.Test("aaabb", options));
        }
    }

    [Fact]
    public void ADeferredSearchStillObservesAnAlreadyCancelledTokenAndTheWorkLimit()
    {
        Assert.True(EcmaScriptMatcher.TryParse("a+b", new EcmaScriptProfile(EcmaScriptFlags.None), out var matcher, out var diagnostic),
            diagnostic.ToString());
        using (matcher)
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var cancelled = new RegexMatchOptions { CancellationToken = cancellation.Token, Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal(RegexSearchStatus.Cancelled, matcher!.Test("aaab", cancelled));
            Assert.Equal(RegexExecutionFailureCode.Cancelled, matcher.LastFailure.Code);

            var limited = new RegexMatchOptions { Limits = new RegexMatchLimits(maxWork: 3), Timeout = TimeSpan.FromSeconds(10) };
            Assert.Equal(RegexSearchStatus.LimitExceeded, matcher.Test(new string('a', 1000) + "c", limited));
            Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, matcher.LastFailure.Code);
        }
    }
}
