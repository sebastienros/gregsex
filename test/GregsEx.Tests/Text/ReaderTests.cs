using System.Text;
using GregsEx.Contracts;
using GregsEx.Text;
using Xunit;

namespace GregsEx.Tests.Text;

public class ReaderTests
{
    internal static ProfileDescriptor Profile(RegularCoreMode mode = RegularCoreMode.UnicodeScalars,
        RegexInputKind kind = RegexInputKind.Utf16)
    {
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode), kind, out var result, out _));
        return result!;
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("A", 1)]
    [InlineData("\u007F\u0080\u07FF\u0800\uD7FF\uE000\uFFFF", 7)]
    [InlineData("a\uD800\uDC00\uDBFF\uDFFFz", 6)]
    public void Utf16PreservesScalarValuesAndNativeWidths(string input, int length)
    {
        var profile = Profile();
        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.NoMatch, CharTextReader.Validate(input, 0, profile, ref state));
        Assert.Equal(length, state.Budget.WorkConsumed);
        var offset = 0;
        foreach (var scalar in input.EnumerateRunes())
        {
            Assert.True(CharTextReader.TryRead(input, offset, profile, out var value, out var width));
            Assert.Equal(scalar.Value, value);
            Assert.Equal(scalar.Utf16SequenceLength, width);
            offset += width;
        }
        Assert.Equal(length, offset);
        Assert.False(CharTextReader.TryRead(input, offset, profile, out var end, out var endWidth));
        Assert.Equal(0, end);
        Assert.Equal(0, endWidth);
    }

    [Theory]
    [InlineData("D800", 0, 1)]
    [InlineData("DC00", 0, 1)]
    [InlineData("0061D8000062", 1, 3)]
    [InlineData("0061DC000062", 1, 2)]
    [InlineData("D800D800DC00", 0, 2)]
    [InlineData("0061D800DC00DFFF", 3, 4)]
    public void Utf16RejectsMalformedSequencesAtTheirStart(string unitHex, int expectedOffset, int work)
    {
        // Attribute strings are UTF-8 metadata and cannot preserve lone surrogate units.
        // Numeric hex is decoded explicitly; no replacement encoding touches the fixture.
        var input = new char[unitHex.Length / 4];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (char)Convert.ToInt32(unitHex.Substring(i * 4, 4), 16);
        }

        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, CharTextReader.Validate(input, 0, Profile(), ref state));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidEncoding, expectedOffset, work), state.Failure);
    }

    [Fact]
    public void Utf16BoundaryCheckFollowsFullValidation()
    {
        const string subject = "a\uD83D\uDE00b";
        for (var start = 0; start <= subject.Length; start++)
        {
            var state = new RunState(new([], [], []), new());
            Assert.Equal(start == 2 ? RegexSearchStatus.InvalidInput : RegexSearchStatus.NoMatch,
                CharTextReader.Validate(subject, start, Profile(), ref state));
            Assert.Equal(subject.Length, state.Budget.WorkConsumed);
            Assert.Equal(start == 2 ? RegexExecutionFailureCode.InvalidStartBoundary : RegexExecutionFailureCode.None, state.Failure.Code);
        }
        var invalid = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, CharTextReader.Validate(subject + "\uD800", 2, Profile(), ref invalid));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, invalid.Failure.Code);
        Assert.Equal(4, invalid.Failure.Offset);
    }

    [Theory]
    [InlineData("00", 0, 1)]
    [InlineData("7F", 127, 1)]
    [InlineData("C280", 128, 2)]
    [InlineData("DFBF", 0x7FF, 2)]
    [InlineData("E0A080", 0x800, 3)]
    [InlineData("ED9FBF", 0xD7FF, 3)]
    [InlineData("EE8080", 0xE000, 3)]
    [InlineData("EFBFBF", 0xFFFF, 3)]
    [InlineData("F0908080", 0x10000, 4)]
    [InlineData("F48FBFBF", 0x10FFFF, 4)]
    public void Utf8AdmitsExactBoundaryScalars(string hex, int scalar, int width)
    {
        var input = Convert.FromHexString(hex);
        var profile = Profile(kind: RegexInputKind.Utf8);
        for (var start = 0; start <= width; start++)
        {
            var state = new RunState(new([], [], []), new());
            Assert.Equal(start > 0 && start < width ? RegexSearchStatus.InvalidInput : RegexSearchStatus.NoMatch,
                ByteTextReader.Validate(input, start, profile, ref state));
            Assert.Equal(width, state.Budget.WorkConsumed);
        }
        Assert.True(ByteTextReader.TryRead(input, 0, profile, out var value, out var nativeWidth));
        Assert.Equal(scalar, value);
        Assert.Equal(width, nativeWidth);
        Assert.False(ByteTextReader.TryRead(input, width, profile, out var end, out var endWidth));
        Assert.Equal(0, end + endWidth);
    }

    [Theory]
    [InlineData("80", 1)]
    [InlineData("BF", 1)]
    [InlineData("C080", 1)]
    [InlineData("C1BF", 1)]
    [InlineData("C2", 1)]
    [InlineData("C241", 2)]
    [InlineData("E08080", 2)]
    [InlineData("E0A0", 2)]
    [InlineData("E1", 1)]
    [InlineData("E18041", 3)]
    [InlineData("EDA080", 2)]
    [InlineData("EDBFBF", 2)]
    [InlineData("F0808080", 2)]
    [InlineData("F0908080FF", 5)]
    [InlineData("F09080", 3)]
    [InlineData("F0908041", 4)]
    [InlineData("F4908080", 2)]
    [InlineData("F5808080", 1)]
    [InlineData("FE", 1)]
    [InlineData("FF", 1)]
    public void Utf8RejectsMalformedAtFirstSequenceStart(string hex, int inspected)
    {
        byte[] input = [0x61, .. Convert.FromHexString(hex)];
        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, ByteTextReader.Validate(input, 0,
            Profile(kind: RegexInputKind.Utf8), ref state));
        var offset = hex == "F0908080FF" ? 5 : 1;
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.InvalidEncoding, offset, 1 + inspected), state.Failure);
    }

    [Fact]
    public void Utf8OriginalOffsetsAndInvalidTailWinOverInteriorStart()
    {
        byte[] input = [0x61, 0xF0, 0x9F, 0x98, 0x80, 0xC2, 0xA2];
        var profile = Profile(kind: RegexInputKind.Utf8);
        for (var start = 0; start <= input.Length; start++)
        {
            var interior = start is 2 or 3 or 4 or 6;
            var state = new RunState(new([], [], []), new());
            Assert.Equal(interior ? RegexSearchStatus.InvalidInput : RegexSearchStatus.NoMatch,
                ByteTextReader.Validate(input, start, profile, ref state));
            Assert.Equal(input.Length, state.Budget.WorkConsumed);
            if (interior)
            {
                Assert.Equal(start, state.Failure.Offset);
            }
        }
        byte[] invalid = [.. input, 0xE0, 0x80, 0x80];
        var bad = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, ByteTextReader.Validate(invalid, 2, profile, ref bad));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, bad.Failure.Code);
        Assert.Equal(7, bad.Failure.Offset);
        Assert.True(ByteTextReader.TryRead(input, 1, profile, out var value, out var width));
        Assert.Equal(0x1F600, value);
        Assert.Equal(4, width);
    }

    [Fact]
    public void UnitAndRawViewsAdmitEveryValueAndBoundary()
    {
        var units = new char[65_536];
        for (var i = 0; i < units.Length; i++)
        {
            units[i] = (char)i;
        }

        var unitProfile = Profile(RegularCoreMode.Utf16CodeUnits);
        var unitState = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.NoMatch, CharTextReader.Validate(units, 0xD801, unitProfile, ref unitState));
        Assert.Equal(units.Length, unitState.Budget.WorkConsumed);
        for (var start = 0; start <= 4; start++)
        {
            var boundaryState = new RunState(new([], [], []), new());
            Assert.Equal(RegexSearchStatus.NoMatch, CharTextReader.Validate("a\uD800\uDC00\uDFFF", start, unitProfile, ref boundaryState));
        }
        for (var i = 0; i < units.Length; i++)
        {
            Assert.True(CharTextReader.TryRead(units, i, unitProfile, out var value, out var width));
            Assert.Equal(i, value);
            Assert.Equal(1, width);
        }
        var bytes = new byte[256];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)i;
        }

        var raw = Profile(RegularCoreMode.RawBytes, RegexInputKind.Bytes);
        for (var start = 0; start <= bytes.Length; start++)
        {
            var state = new RunState(new([], [], []), new());
            Assert.Equal(RegexSearchStatus.NoMatch, ByteTextReader.Validate(bytes, start, raw, ref state));
        }
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.True(ByteTextReader.TryRead(bytes, i, raw, out var value, out var width));
            Assert.Equal(i, value);
            Assert.Equal(1, width);
        }
    }

    [Fact]
    public void SeededScalarsAgreeWithIndependentBclEncoding()
    {
        var random = new Random(10011);
        var chars = Profile();
        var bytes = Profile(kind: RegexInputKind.Utf8);
        for (var i = 0; i < 4096; i++)
        {
            var scalar = random.Next(0x110000);
            if (scalar is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var text = char.ConvertFromUtf32(scalar);
            var utf8 = Encoding.UTF8.GetBytes(text);
            var charState = new RunState(new([], [], []), new());
            var byteState = new RunState(new([], [], []), new());
            Assert.Equal(RegexSearchStatus.NoMatch, CharTextReader.Validate(text, 0, chars, ref charState));
            Assert.Equal(RegexSearchStatus.NoMatch, ByteTextReader.Validate(utf8, 0, bytes, ref byteState));
            Assert.True(CharTextReader.TryRead(text, 0, chars, out var a, out var aw));
            Assert.True(ByteTextReader.TryRead(utf8, 0, bytes, out var b, out var bw));
            Assert.Equal(scalar, a);
            Assert.Equal(scalar, b);
            Assert.Equal(text.Length, aw);
            Assert.Equal(utf8.Length, bw);
        }
    }

    [Fact]
    public void AllOneAndTwoByteInputsAndSeededLongInputsAgreeWithStrictUtf8Oracle()
    {
        var oracle = new UTF8Encoding(false, true);
        var profile = Profile(kind: RegexInputKind.Utf8);
        var limits = new RegexMatchLimits();
        var input = new byte[8];
        for (var first = 0; first < 256; first++)
        {
            input[0] = (byte)first;
            CheckOracle(input.AsSpan(0, 1), oracle, profile, limits);
            for (var second = 0; second < 256; second++)
            {
                input[1] = (byte)second;
                CheckOracle(input.AsSpan(0, 2), oracle, profile, limits);
            }
        }
        var random = new Random(801011);
        for (var test = 0; test < 8192; test++)
        {
            random.NextBytes(input);
            CheckOracle(input.AsSpan(0, random.Next(3, 9)), oracle, profile, limits);
        }
    }

    private static void CheckOracle(ReadOnlySpan<byte> input, UTF8Encoding oracle,
        ProfileDescriptor profile, RegexMatchLimits limits)
    {
        bool valid;
        try
        { oracle.GetCharCount(input); valid = true; }
        catch (DecoderFallbackException) { valid = false; }
        var state = new RunState(new([], [], []), limits);
        Assert.Equal(valid ? RegexSearchStatus.NoMatch : RegexSearchStatus.InvalidInput,
            ByteTextReader.Validate(input, 0, profile, ref state));
    }

    [Fact]
    public void WorkLimitsArePerInspectedUnitAndStateFailureIsSticky()
    {
        var state = new RunState(new([], [], []), new(2));
        Assert.Equal(RegexSearchStatus.LimitExceeded, ByteTextReader.Validate("\u20AC"u8, 0,
            Profile(kind: RegexInputKind.Utf8), ref state));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 2, 2), state.Failure);
        Assert.Equal(RegexSearchStatus.LimitExceeded, ByteTextReader.Validate([], 0, Profile(kind: RegexInputKind.Utf8), ref state));
        state = new(new([], [], []), new(2)); // New operation can reuse the same zero-capacity partitions.
        Assert.Equal(RegexSearchStatus.NoMatch, ByteTextReader.Validate("ab"u8, 0, Profile(kind: RegexInputKind.Utf8), ref state));
        var chars = new RunState(new([], [], []), new(1));
        Assert.Equal(RegexSearchStatus.LimitExceeded, CharTextReader.Validate("\uD83D\uDE00", 0, Profile(), ref chars));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 1, 1), chars.Failure);
    }

    [Fact]
    public void EmptyAndInvalidSubjectsStillPollCancellationAndDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var options = new RegexMatchOptions { CancellationToken = cancellation.Token, Timeout = TimeSpan.FromTicks(1) };
        var chars = new RunState(new([], [], []), new(), options);
        var bytes = new RunState(new([], [], []), new(), options);
        Assert.Equal(RegexSearchStatus.Cancelled, CharTextReader.Validate([], 0, Profile(), ref chars));
        Assert.Equal(RegexSearchStatus.Cancelled, ByteTextReader.Validate([255], 0, Profile(kind: RegexInputKind.Utf8), ref bytes));
        Assert.Equal(0, chars.Budget.WorkConsumed + bytes.Budget.WorkConsumed);
        var expired = new RunState(new([], [], []), new(), new() { Timeout = TimeSpan.FromTicks(1) });
        // Wait on the observed clock, not a timing-sensitive assertion about scheduler delay.
        while (System.Diagnostics.Stopwatch.GetTimestamp() <= expired.Budget.StartedAt)
        { }
        Assert.Equal(RegexSearchStatus.TimedOut, CharTextReader.Validate([], 0, Profile(), ref expired));
    }

    [Fact]
    public void NegativePastEndAndWrongRepresentationAreArgumentErrors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidateCharArgument(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ValidateCharArgument(2));
        Assert.Throws<ArgumentException>(() => ValidateCharArgument(0, RegexInputKind.Utf8));
        Assert.Throws<ArgumentException>(() => ValidateByteArgument());
    }

    private static void ValidateCharArgument(int start, RegexInputKind kind = RegexInputKind.Utf16)
    {
        var state = new RunState(new([], [], []), new());
        CharTextReader.Validate("a", start, Profile(kind: kind), ref state);
    }
    private static void ValidateByteArgument()
    {
        var state = new RunState(new([], [], []), new());
        ByteTextReader.Validate("a"u8, 0, Profile(), ref state);
    }
}
