using GregsEx.Contracts;
using GregsEx.Text;
using Xunit;

namespace GregsEx.Tests.Text;

// Run-charged validation and the amortized clock must be indistinguishable from per-unit charging.
public class RunChargeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(299)]
    [InlineData(300)]
    public void CharValidationWorkLimitFailsAtTheSameOffsetAsPerUnitCharging(int maxWork)
    {
        var input = new string('a', 300);
        var state = new RunState(new([], [], []), new RegexMatchLimits(maxWork, 1 << 20));
        var status = CharTextReader.Validate(input, 0, ReaderTests.Profile(), ref state);
        if (maxWork >= input.Length)
        {
            Assert.Equal(RegexSearchStatus.NoMatch, status);
            Assert.Equal(input.Length, state.Budget.WorkConsumed);
            return;
        }
        Assert.Equal(RegexSearchStatus.LimitExceeded, status);
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, maxWork, maxWork), state.Failure);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(40)]
    [InlineData(41)]
    public void ByteValidationWorkLimitFailsAtTheSameOffsetAsPerUnitCharging(int maxWork)
    {
        var input = new byte[50];
        Array.Fill(input, (byte)'x');
        input[40] = 0xC3;
        input[41] = 0xA9;
        var state = new RunState(new([], [], []), new RegexMatchLimits(maxWork, 1 << 20));
        var status = ByteTextReader.Validate(input, 0, ReaderTests.Profile(kind: RegexInputKind.Utf8), ref state);
        Assert.Equal(RegexSearchStatus.LimitExceeded, status);
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, maxWork, maxWork), state.Failure);
    }

    [Fact]
    public void MixedRunsAndSequencesCostOneUnitPerCodeUnit()
    {
        var chars = "abc\U0001F600def\U0001F601" + new string('z', 100);
        var charState = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.NoMatch, CharTextReader.Validate(chars, 0, ReaderTests.Profile(), ref charState));
        Assert.Equal(chars.Length, charState.Budget.WorkConsumed);
        var bytes = System.Text.Encoding.UTF8.GetBytes(chars);
        var byteState = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.NoMatch,
            ByteTextReader.Validate(bytes, 0, ReaderTests.Profile(kind: RegexInputKind.Utf8), ref byteState));
        Assert.Equal(bytes.Length, byteState.Budget.WorkConsumed);
    }

    [Fact]
    public void InteriorStartInsideARunFollowedBySequenceStillRejected()
    {
        var input = "xxxx\U0001F600yy";
        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, CharTextReader.Validate(input, 5, ReaderTests.Profile(), ref state));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, state.Failure.Code);
    }

    [Fact]
    public void ElapsedTimeoutIsObservedWithinTheClockStride()
    {
        var state = new RunState(new([], [], []), new(), new() { Timeout = TimeSpan.FromMilliseconds(5) });
        Thread.Sleep(20);
        long charged = 0;
        // The first charge reads the clock; later reads happen at most every ClockStride units.
        while (state.TryCharge(1, 0))
        {
            if (++charged > 2L * RunBudget.ClockStride)
            {
                break;
            }
        }

        Assert.Equal(RegexExecutionFailureCode.TimedOut, state.Failure.Code);
        Assert.True(charged <= RunBudget.ClockStride);
    }

    [Fact]
    public void UnboundedTimeoutNeverTimesOutAndCancellationIsPolledEveryCharge()
    {
        using var cancellation = new CancellationTokenSource();
        var state = new RunState(new([], [], []), new(), new() { CancellationToken = cancellation.Token });
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(state.TryCharge(1, i));
        }

        cancellation.Cancel();
        Assert.False(state.TryCharge(1, 1000));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, state.Failure.Code);
    }
}
