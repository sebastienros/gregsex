using System.Text;
using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using Xunit;

namespace GregsEx.Tests.Flavors.XmlShared.Semantics;

public class ReaderTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(32)]
    [InlineData(0xD7FF)]
    [InlineData(0xE000)]
    [InlineData(0xFFFD)]
    [InlineData(0x10000)]
    [InlineData(0xFDD0)]
    [InlineData(0x1FFFE)]
    [InlineData(0x10FFFF)]
    public void EveryAdmittedBoundaryDecodesAndChargesNativeUnits(int cp)
    {
        var input = char.ConvertFromUtf32(cp);
        var bytes = Encoding.UTF8.GetBytes(input);
        var chars = new RunState(new([], [], []), new());
        var utf8 = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.NoMatch, XmlCharTextReader.Validate(input, 0, ref chars));
        Assert.Equal(RegexSearchStatus.NoMatch, XmlByteTextReader.Validate(bytes, 0, ref utf8));
        Assert.Equal(input.Length, chars.Budget.WorkConsumed);
        Assert.Equal(bytes.Length, utf8.Budget.WorkConsumed);
        Assert.True(XmlCharTextReader.TryRead(input, 0, out var c, out var cw));
        Assert.Equal(cp, c);
        Assert.Equal(input.Length, cw);
        Assert.True(XmlByteTextReader.TryRead(bytes, 0, out var b, out var bw));
        Assert.Equal(cp, b);
        Assert.Equal(bytes.Length, bw);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(31)]
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    public void WellFormedIllegalXmlScalarsGiveDomainFailureAtFirstNativeOffset(int cp)
    {
        var input = "a" + char.ConvertFromUtf32(cp) + "z";
        var bytes = Encoding.UTF8.GetBytes(input);
        var chars = new RunState(new([], [], []), new());
        var utf8 = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlCharTextReader.Validate(input, 0, ref chars));
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlByteTextReader.Validate(bytes, 0, ref utf8));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidInputDomain, 1, 2), chars.Failure);
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidInputDomain, 1, bytes.Length - 1), utf8.Failure);
        Assert.False(XmlCharTextReader.TryRead(input, 1, out _, out _));
        Assert.False(XmlByteTextReader.TryRead(bytes, 1, out _, out _));
    }
    [Theory]
    [InlineData("D800", 0, 1)]
    [InlineData("DC00", 0, 1)]
    [InlineData("0061D8000062", 1, 3)]
    [InlineData("0061DC000062", 1, 2)]
    [InlineData("D800D800DC00", 0, 2)]
    public void Utf16MalformedSequencesKeepOriginalUnits(string hex, int offset, int work)
    {
        var input = new char[hex.Length / 4];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (char)Convert.ToInt32(hex.AsSpan(i * 4, 4).ToString(), 16);
        }

        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlCharTextReader.Validate(input, 0, ref state));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidEncoding, offset, work), state.Failure);
    }
    [Theory]
    [InlineData("80", 1)]
    [InlineData("C0AF", 1)]
    [InlineData("C1BF", 1)]
    [InlineData("E08080", 2)]
    [InlineData("EDA080", 2)]
    [InlineData("F0808080", 2)]
    [InlineData("F4908080", 2)]
    [InlineData("F5808080", 1)]
    [InlineData("C2", 1)]
    [InlineData("E2A0", 2)]
    [InlineData("F0A080", 3)]
    [InlineData("E24180", 2)]
    [InlineData("FE", 1)]
    [InlineData("FF", 1)]
    public void Utf8RejectsMalformedOverlongSurrogateAndTruncatedSequences(string hex, int inspected)
    {
        byte[] input = [0x61, .. Convert.FromHexString(hex)];
        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlByteTextReader.Validate(input, 0, ref state));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidEncoding, 1, inspected + 1), state.Failure);
        Assert.False(XmlByteTextReader.TryRead(input, 1, out var cp, out var width));
        Assert.Equal(0, cp);
        Assert.Equal(0, width);
    }
    [Fact]
    public void WholeSubjectValidationWinsOverStartBoundaryAndFirstViolationWins()
    {
        var input = "a\U0001F600z";
        var bytes = Encoding.UTF8.GetBytes(input);
        for (var start = 0; start <= input.Length; start++)
        {
            var state = new RunState(new([], [], []), new());
            var interior = start == 2;
            Assert.Equal(interior ? RegexSearchStatus.InvalidInput : RegexSearchStatus.NoMatch, XmlCharTextReader.Validate(input, start, ref state));
            Assert.Equal(input.Length, state.Budget.WorkConsumed);
            Assert.Equal(interior ? RegexExecutionFailureCode.InvalidStartBoundary : RegexExecutionFailureCode.None, state.Failure.Code);
        }
        for (var start = 0; start <= bytes.Length; start++)
        {
            var state = new RunState(new([], [], []), new());
            var interior = start is >= 2 and <= 4;
            Assert.Equal(interior ? RegexSearchStatus.InvalidInput : RegexSearchStatus.NoMatch, XmlByteTextReader.Validate(bytes, start, ref state));
            Assert.Equal(bytes.Length, state.Budget.WorkConsumed);
        }
        char[] malformed = [.. input, '\uD800'];
        var bad = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlCharTextReader.Validate(malformed, 2, ref bad));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, bad.Failure.Code);
        Assert.Equal(4, bad.Failure.Offset);
        byte[] domain = [.. bytes, 0];
        bad = new(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlByteTextReader.Validate(domain, 2, ref bad));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, bad.Failure.Code);
        Assert.Equal(6, bad.Failure.Offset);
        bad = new(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, XmlCharTextReader.Validate(['\0', '\uD800'], 0, ref bad));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, bad.Failure.Code);
        Assert.Equal(0, bad.Failure.Offset);
    }
    [Fact]
    public void InitialPollCumulativeLimitsAndRecoveryAreDistinct()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        var cancelled = new RunState(new([], [], []), new(), new() { CancellationToken = token.Token });
        Assert.Equal(RegexSearchStatus.Cancelled, XmlCharTextReader.Validate([], 0, ref cancelled));
        Assert.Equal(0, cancelled.Budget.WorkConsumed);
        var limited = new RunState(new([], [], []), new(maxWork: 3));
        Assert.Equal(RegexSearchStatus.NoMatch, XmlCharTextReader.Validate("ab", 0, ref limited));
        Assert.Equal(RegexSearchStatus.LimitExceeded, XmlByteTextReader.Validate("cd"u8, 0, ref limited));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 1, 3), limited.Failure);
        var fresh = new RunState(new([], [], []), new(maxWork: 3));
        Assert.Equal(RegexSearchStatus.NoMatch, XmlByteTextReader.Validate("cd"u8, 0, ref fresh));
        Assert.False(XmlCharTextReader.TryRead("a", -1, out _, out _));
        Assert.False(XmlByteTextReader.TryRead("a"u8, 1, out _, out _));
    }
    [Fact]
    public void ArgumentAdmissionPrecedesCancellationAndInvalidInput()
    {
        CheckChar(-1);
        CheckChar(2);
        CheckByte(-1);
        CheckByte(2);
        static void CheckChar(int start)
        {
            var caught = false;
            var state = new RunState(new([], [], []), new());
            try
            { XmlCharTextReader.Validate("\0", start, ref state); }
            catch (ArgumentOutOfRangeException) { caught = true; }
            Assert.True(caught);
            Assert.Equal(0, state.Budget.WorkConsumed);
        }
        static void CheckByte(int start)
        {
            var caught = false;
            var state = new RunState(new([], [], []), new());
            try
            { XmlByteTextReader.Validate([0xFF], start, ref state); }
            catch (ArgumentOutOfRangeException) { caught = true; }
            Assert.True(caught);
            Assert.Equal(0, state.Budget.WorkConsumed);
        }
    }
}
