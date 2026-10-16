using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Text;

public sealed class Pcre2TextTests
{
    private static Pcre2Profile Byte(bool utf = true, bool invalid = false, Pcre2Newline newline = Pcre2Newline.Lf) =>
        new(Pcre2CodeUnitWidth.Eight, invalid ? Pcre2CompileFlags.MatchInvalidUtf : utf ? Pcre2CompileFlags.Utf : 0, newline: newline);
    private static Pcre2Profile Char(bool utf = true, bool invalid = false, Pcre2Newline newline = Pcre2Newline.Lf) =>
        new(Pcre2CodeUnitWidth.Sixteen, invalid ? Pcre2CompileFlags.MatchInvalidUtf : utf ? Pcre2CompileFlags.Utf : 0, newline: newline);
    private static RunState State(long work = 10000, RegexMatchOptions options = default) => new(default, new(maxWork: work), options);

    // Independent source expectations: pcre2_valid_utf.c111–131/155–313.
    // Native intake getter supplements independently retain exact UTF offsets.
    public static IEnumerable<TheoryDataRow<byte[], int>> Utf8Errors()
    {
        yield return new TheoryDataRow<byte[], int>([0xC2], -3);
        yield return new TheoryDataRow<byte[], int>([0xE0], -4);
        yield return new TheoryDataRow<byte[], int>([0xF0], -5);
        yield return new TheoryDataRow<byte[], int>([0xF8], -6);
        yield return new TheoryDataRow<byte[], int>([0xFC], -7);
        yield return new TheoryDataRow<byte[], int>([0xC2, 0x41], -8);
        yield return new TheoryDataRow<byte[], int>([0xE0, 0x80, 0x41], -9);
        yield return new TheoryDataRow<byte[], int>([0xF0, 0x80, 0x80, 0x41], -10);
        yield return new TheoryDataRow<byte[], int>([0xF8, 0x80, 0x80, 0x80, 0x41], -11);
        yield return new TheoryDataRow<byte[], int>([0xFC, 0x80, 0x80, 0x80, 0x80, 0x41], -12);
        yield return new TheoryDataRow<byte[], int>([0xF8, 0x88, 0x80, 0x80, 0x80], -13);
        yield return new TheoryDataRow<byte[], int>([0xFC, 0x84, 0x80, 0x80, 0x80, 0x80], -14);
        yield return new TheoryDataRow<byte[], int>([0xF4, 0x90, 0x80, 0x80], -15);
        yield return new TheoryDataRow<byte[], int>([0xED, 0xA0, 0x80], -16);
        yield return new TheoryDataRow<byte[], int>([0xC0, 0x80], -17);
        yield return new TheoryDataRow<byte[], int>([0xE0, 0x80, 0x80], -18);
        yield return new TheoryDataRow<byte[], int>([0xF0, 0x80, 0x80, 0x80], -19);
        yield return new TheoryDataRow<byte[], int>([0xF8, 0x80, 0x80, 0x80, 0x80], -20);
        yield return new TheoryDataRow<byte[], int>([0xFC, 0x80, 0x80, 0x80, 0x80, 0x80], -21);
        yield return new TheoryDataRow<byte[], int>([0x80], -22);
        yield return new TheoryDataRow<byte[], int>([0xFE], -23);
        yield return new TheoryDataRow<byte[], int>([0xFF], -23);
        // Missing-byte check precedes invalid continuation, and later
        // continuation failure precedes scalar/overlong errors.
        yield return new TheoryDataRow<byte[], int>([0xF0, 0x41], -4);
        yield return new TheoryDataRow<byte[], int>([0xED, 0xA0, 0x41], -9);
    }

    [Theory, MemberData(nameof(Utf8Errors))]
    public void AllUtf8ErrorIdentitiesAndOriginalOffsets(byte[] malformed, int nativeError)
    {
        byte[] input = [65, .. malformed];
        var state = State();
        var result = Pcre2ByteTextReader.Validate(input, 0, 0, Byte(), 0, Pcre2ValidationPolicy.NativeWindow, ref state);
        Assert.False(result.Valid);
        Assert.Equal(nativeError, result.NativeError);
        Assert.Equal(1, result.Failure.Offset);
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, result.Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code); // typed source error, no generic latching.
    }

    [Theory]
    [InlineData(0xD800, false, -24)]
    [InlineData(0xD800, true, -25)]
    [InlineData(0xDC00, false, -26)]
    public void Utf16ErrorIdentitiesAndOffsets(int unit, bool append, int error)
    {
        // Attribute strings cannot preserve lone surrogates through UTF8
        // metadata serialization; construct the exact units inside the test.
        string input = append ? new(['a', (char)unit, 'x']) : new(['a', (char)unit]);
        var state = State();
        var result = Pcre2CharTextReader.Validate(input, 0, 0, Char(), 0, Pcre2ValidationPolicy.NativeWindow, ref state);
        Assert.False(result.Valid);
        Assert.Equal(error, result.NativeError);
        Assert.Equal(1, result.Failure.Offset);
    }

    [Fact]
    public void RawDomainsPreserveEveryUnitIncludingNulAndSurrogates()
    {
        var raw8 = Byte(false);
        var raw16 = Char(false);
        var state = State();
        for (var unit = 0; unit <= 255; unit++)
        {
            ReadOnlySpan<byte> input = [(byte)unit];
            Assert.True(Pcre2ByteTextReader.Validate(input, 0, 0, raw8, 0, 0, ref state).Valid);
            Assert.Equal(new(Pcre2ReadStatus.Value, unit, 1), Pcre2ByteTextReader.Read(input, 0, 1, false, raw8, ref state));
            Assert.Equal(new(Pcre2ReadStatus.Value, unit, 1), Pcre2ByteTextReader.ReadPrevious(input, 1, raw8, ref state));
        }
        state = State(300000);
        for (var unit = 0; unit <= 65535; unit++)
        {
            ReadOnlySpan<char> input = [(char)unit];
            Assert.True(Pcre2CharTextReader.Validate(input, 0, 0, raw16, 0, 0, ref state).Valid);
            Assert.Equal(new(Pcre2ReadStatus.Value, unit, 1), Pcre2CharTextReader.Read(input, 0, 1, false, raw16, ref state));
            Assert.Equal(new(Pcre2ReadStatus.Value, unit, 1), Pcre2CharTextReader.ReadPrevious(input, 1, raw16, ref state));
        }
    }

    [Fact]
    public void UtfDomainsKeepNativeWidthsAndRoundTripReverse()
    {
        byte[] bytes = [0, 0x7F, 0xC2, 0x80, 0xDF, 0xBF, 0xE0, 0xA0, 0x80, 0xED, 0x9F, 0xBF,
            0xEE, 0x80, 0x80, 0xEF, 0xBF, 0xBF, 0xF0, 0x90, 0x80, 0x80, 0xF4, 0x8F, 0xBF, 0xBF];
        int[] points = [0, 0x7F, 0x80, 0x7FF, 0x800, 0xD7FF, 0xE000, 0xFFFF, 0x10000, 0x10FFFF];
        int[] widths = [1, 1, 2, 2, 3, 3, 3, 3, 4, 4];
        var state = State();
        Assert.True(Pcre2ByteTextReader.Validate(bytes, 0, 0, Byte(), 0, 0, ref state).Valid);
        var offset = 0;
        for (var i = 0; i < points.Length; i++)
        {
            var expected = new Pcre2ReadResult(Pcre2ReadStatus.Value, points[i], widths[i]);
            Assert.Equal(expected, Pcre2ByteTextReader.Read(bytes, offset, bytes.Length, false, Byte(), ref state));
            offset += widths[i];
            Assert.Equal(expected, Pcre2ByteTextReader.ReadPrevious(bytes, offset, Byte(), ref state));
        }
        var chars = "\0\uD7FF\uE000\uFFFF\uD800\uDC00\uDBFF\uDFFF";
        int[] cp16 = [0, 0xD7FF, 0xE000, 0xFFFF, 0x10000, 0x10FFFF];
        offset = 0;
        Assert.True(Pcre2CharTextReader.Validate(chars, 0, 0, Char(), 0, 0, ref state).Valid);
        foreach (var cp in cp16)
        {
            var width = cp > 0xFFFF ? 2 : 1;
            var expected = new Pcre2ReadResult(Pcre2ReadStatus.Value, cp, width);
            Assert.Equal(expected, Pcre2CharTextReader.Read(chars, offset, chars.Length, false, Char(), ref state));
            offset += width;
            Assert.Equal(expected, Pcre2CharTextReader.ReadPrevious(chars, offset, Char(), ref state));
        }
    }

    [Fact]
    public void NativeWindowDiffersFromWholeSubjectAndLookbehindCountsCharacters()
    {
        byte[] bytes = [0xFF, 0xF0, 0x9F, 0x98, 0x80, 97];
        var state = State();
        Assert.True(Pcre2ByteTextReader.Validate(bytes, 5, 1, Byte(), 0, 0, ref state).Valid);
        var window = Pcre2ByteTextReader.Bind(bytes, 5, 1, Byte(), 0, 0, ref state);
        Assert.Equal(1, window.ScanStart);
        Assert.Equal(5, window.CandidateStart);
        Assert.Equal(6, window.VisibleEnd);
        Assert.Equal(-23, Pcre2ByteTextReader.Validate(bytes, 5, 2, Byte(), 0, 0, ref state).NativeError);
        Assert.Equal(-23, Pcre2ByteTextReader.Validate(bytes, 5, 0, Byte(), 0, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
        var chars = "\uD800\uD83D\uDE00a";
        Assert.True(Pcre2CharTextReader.Validate(chars, 3, 1, Char(), 0, 0, ref state).Valid);
        Assert.Equal(1, Pcre2CharTextReader.Bind(chars, 3, 1, Char(), 0, 0, ref state).ScanStart);
        Assert.Equal(-25, Pcre2CharTextReader.Validate(chars, 3, 2, Char(), 0, 0, ref state).NativeError);
        Assert.Equal(-25, Pcre2CharTextReader.Validate(chars, 3, 0, Char(), 0, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
    }

    [Fact]
    public void InteriorNativeStartPrecedesLaterEncodingErrorButWholeValidationPrecedesBoundary()
    {
        byte[] input = [0xC2, 0xA0, 0xFF];
        var state = State();
        Assert.Equal(-36, Pcre2ByteTextReader.Validate(input, 1, 0, Byte(), 0, 0, ref state).NativeError);
        Assert.Equal(-23, Pcre2ByteTextReader.Validate(input, 1, 0, Byte(), 0, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
        var chars = "\uD800\uDC00\uD800";
        Assert.Equal(-36, Pcre2CharTextReader.Validate(chars, 1, 0, Char(), 0, 0, ref state).NativeError);
        Assert.Equal(-24, Pcre2CharTextReader.Validate(chars, 1, 0, Char(), 0, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
        Assert.Equal(-22, Pcre2ByteTextReader.Validate([0x80], 0, 0, Byte(), 0, 0, ref state).NativeError);
        Assert.Equal(-26, Pcre2CharTextReader.Validate("\uDC00", 0, 0, Char(), 0, 0, ref state).NativeError);
    }

    [Fact]
    public void CodeUnitsSplitValidUtfAndCheckedScalarContinuationDoesNotRepair()
    {
        byte[] bytes = [0xF0, 0x9F, 0x98, 0x80];
        var state = State();
        for (var i = 0; i < bytes.Length; i++)
        {
            Assert.Equal(new(Pcre2ReadStatus.Value, (int)bytes[i], 1), Pcre2ByteTextReader.Read(bytes, i, 4, true, Byte(), ref state));
        }

        Assert.Equal(-36, Pcre2ByteTextReader.Validate(bytes, 1, 0, Byte(), 0, 0, ref state).NativeError);
        Assert.Equal(Pcre2ReadStatus.InvalidBarrier, Pcre2ByteTextReader.Read(bytes, 1, 4, false, Byte(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, state.Failure.Code); // managed safe disposition, no unsafe native outcome claim.
        state = State();
        var chars = "\uD83D\uDE00";
        Assert.Equal(new(Pcre2ReadStatus.Value, 0xD83D, 1), Pcre2CharTextReader.Read(chars, 0, 2, true, Char(), ref state));
        Assert.Equal(new(Pcre2ReadStatus.Value, 0xDE00, 1), Pcre2CharTextReader.Read(chars, 1, 2, true, Char(), ref state));
        Assert.Equal(-36, Pcre2CharTextReader.Validate(chars, 1, 0, Char(), 0, 0, ref state).NativeError);
        Assert.Equal(Pcre2ReadStatus.InvalidBarrier, Pcre2CharTextReader.ReadPrevious(chars, 1, Char(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, state.Failure.Code);
    }

    [Fact]
    public void VisibleEndRestrictsConsumptionAndOriginalBeginningRemainsVisible()
    {
        var state = State();
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.Read(" "u8, 0, 1, false, Byte(), ref state).Status);
        Assert.Equal(Pcre2ReadStatus.End, Pcre2CharTextReader.Read("\uD800\uDC00", 0, 1, false, Char(), ref state).Status);
        Assert.Equal(new(Pcre2ReadStatus.Value, 0xD800, 1), Pcre2CharTextReader.Read("\uD800\uDC00", 0, 1, true, Char(), ref state));
        Assert.Equal(new(Pcre2ReadStatus.Value, 97, 1), Pcre2CharTextReader.ReadPrevious("ab", 1, Char(), ref state));
        Assert.Equal(Pcre2ReadStatus.End, Pcre2CharTextReader.Read("ab", 1, 1, false, Char(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
    }

    [Fact]
    public void InvalidUtfFragmentsPreserveOffsetsAndBoundaryControls()
    {
        byte[] bytes = [97, 0xFF, 0x80, 0xFE, 0xC2, 0xA0, 98, 0xFF];
        var state = State();
        var first = Pcre2ByteTextReader.Bind(bytes, 0, 0, Byte(invalid: true), 0, 0, ref state);
        Assert.True(first.Validation.Valid);
        Assert.True(first.Available);
        Assert.Equal(1, first.VisibleEnd);
        Assert.Equal(Pcre2MatchFlags.Noteol, first.FragmentFlags);
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.Read(bytes, 1, first.VisibleEnd, true, Byte(invalid: true), ref state).Status);
        var second = Pcre2ByteTextReader.NextFragment(bytes, first, Byte(invalid: true), ref state);
        Assert.Equal(4, second.CandidateStart);
        Assert.Equal(4, second.ScanStart);
        Assert.Equal(7, second.VisibleEnd);
        Assert.Equal(Pcre2MatchFlags.Notbol | Pcre2MatchFlags.Noteol, second.FragmentFlags);
        Assert.Equal(new(Pcre2ReadStatus.Value, 160, 2), Pcre2ByteTextReader.Read(bytes, 4, second.VisibleEnd, false, Byte(invalid: true), ref state));
        var last = Pcre2ByteTextReader.NextFragment(bytes, second, Byte(invalid: true), ref state);
        Assert.False(last.Available);
        Assert.Equal(8, last.CandidateStart);
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
        // Initial interior offset skips without moving the original invocation
        // start or rewinding to lookbehind; valid suffix still has native offsets.
        var interior = Pcre2ByteTextReader.Bind(bytes, 5, 10, Byte(invalid: true), 0, 0, ref state);
        Assert.Equal(6, interior.CandidateStart);
        Assert.Equal(6, interior.ScanStart);
    }

    [Fact]
    public void InvalidUtfLookbehindFloorAndUtf16FragmentsDoNotCrossBarriers()
    {
        var input = "a\uD800b\uDC00\uD83D\uDE00z";
        var state = State();
        var initial = Pcre2CharTextReader.Bind(input, 2, 3, Char(invalid: true), 0, 0, ref state);
        Assert.Equal(2, initial.CandidateStart);
        Assert.Equal(2, initial.ScanStart);
        Assert.Equal(3, initial.VisibleEnd);
        var next = Pcre2CharTextReader.NextFragment(input, initial, Char(invalid: true), ref state);
        Assert.Equal(4, next.CandidateStart);
        Assert.Equal(4, next.ScanStart);
        Assert.Equal(7, next.VisibleEnd);
        Assert.Equal(Pcre2MatchFlags.Notbol, next.FragmentFlags);
        Assert.Equal(Pcre2ReadStatus.InvalidBarrier, Pcre2CharTextReader.Read(input, 1, 7, true, Char(invalid: true), ref state).Status);
        Assert.Equal(Pcre2ReadStatus.InvalidBarrier, Pcre2CharTextReader.ReadPrevious(input, 4, Char(invalid: true), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
    }

    [Theory]
    [InlineData(Pcre2Newline.Crlf, 2)]
    [InlineData(Pcre2Newline.Any, 2)]
    [InlineData(Pcre2Newline.Anycrlf, 2)]
    [InlineData(Pcre2Newline.Lf, 1)]
    [InlineData(Pcre2Newline.Cr, 1)]
    [InlineData(Pcre2Newline.Nul, 1)]
    public void EmptyAdvancementUsesNewlinePolicyNotBsr(Pcre2Newline newline, int next)
    {
        var state = State();
        Assert.Equal(next, Pcre2ByteTextReader.AdvanceAfterEmpty("\r\n"u8, 0, Byte(newline: newline), ref state));
        Assert.Equal(next, Pcre2CharTextReader.AdvanceAfterEmpty("\r\n", 0, Char(newline: newline), ref state));
        foreach (var bsr in Enum.GetValues<Pcre2Bsr>())
        {
            var profile = new Pcre2Profile(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp, newline: newline, bsr: bsr);
            Assert.Equal(next, Pcre2CharTextReader.AdvanceAfterEmpty("\r\n", 0, profile, ref state));
        }
    }

    [Fact]
    public void EmptyAdvancementIsNativeWidthBarrierAwareAndTerminalBounded()
    {
        var state = State();
        Assert.Equal(4, Pcre2ByteTextReader.AdvanceAfterEmpty("😀"u8, 0, Byte(), ref state));
        Assert.Equal(2, Pcre2CharTextReader.AdvanceAfterEmpty("\uD83D\uDE00", 0, Char(), ref state));
        Assert.Equal(1, Pcre2CharTextReader.AdvanceAfterEmpty("\uD83D\uDE00", 0, Char(false), ref state));
        Assert.Equal(3, Pcre2ByteTextReader.AdvanceAfterEmpty([0xFF, 0x80, 0x80, 97], 0, Byte(invalid: true), ref state));
        Assert.Equal(2, Pcre2CharTextReader.AdvanceAfterEmpty("\uD800\uDC00", 2, Char(), ref state));
        Assert.Equal(0, Pcre2ByteTextReader.AdvanceAfterEmpty([], 0, Byte(), ref state));
    }

    [Fact]
    public void NoCheckHasDefinedValidPreconditionsAndWholeProjectionRejectsIt()
    {
        var state = State();
        Assert.True(Pcre2ByteTextReader.Validate("abc"u8, 1, 0, Byte(), Pcre2MatchFlags.NoUtfCheck, 0, ref state).Valid);
        Assert.Equal(-23, Pcre2ByteTextReader.Validate([0xFF], 0, 0, Byte(), Pcre2MatchFlags.NoUtfCheck, 0, ref state).NativeError);
        Assert.Equal(-34, Pcre2ByteTextReader.Validate("abc"u8, 0, 0, Byte(), Pcre2MatchFlags.NoUtfCheck, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
        Assert.Equal(-34, Pcre2CharTextReader.Validate("abc", 0, 0, Char(invalid: true), 0, Pcre2ValidationPolicy.WholeSubjectProjection, ref state).NativeError);
        Assert.True(Pcre2CharTextReader.Validate("\uD800", 0, 0, Char(invalid: true), Pcre2MatchFlags.NoUtfCheck, 0, ref state).Valid);
    }

    [Fact]
    public void WorkAndFailureAreCumulativeAcrossValidationReadsFragmentsAndAdvancement()
    {
        var state = State(2);
        Assert.True(Pcre2ByteTextReader.Validate("ab"u8, 0, 0, Byte(false), 0, 0, ref state).Valid);
        Assert.Equal(Pcre2ReadStatus.Value, Pcre2ByteTextReader.Read("ab"u8, 0, 2, false, Byte(false), ref state).Status);
        Assert.Equal(2, Pcre2ByteTextReader.AdvanceAfterEmpty("ab"u8, 1, Byte(false), ref state));
        Assert.Equal(2, state.Budget.WorkConsumed);
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.ReadPrevious("ab"u8, 2, Byte(false), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        var work = state.Budget.WorkConsumed;
        Assert.False(Pcre2ByteTextReader.Validate([], 0, 0, Byte(), 0, 0, ref state).Valid);
        Assert.Equal(work, state.Budget.WorkConsumed);
        state = State(3);
        var window = Pcre2ByteTextReader.Bind([97, 0xFF, 98], 0, 0, Byte(invalid: true), 0, 0, ref state);
        Assert.True(window.Available);
        var next = Pcre2ByteTextReader.NextFragment([97, 0xFF, 98], window, Byte(invalid: true), ref state);
        Assert.False(next.Validation.Valid);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, next.Validation.Failure.Code);
    }

    [Fact]
    public void CancellationAndDeadlinePollEmptyImmediateAndReversePaths()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var state = State(options: new() { CancellationToken = cancellation.Token });
        var result = Pcre2CharTextReader.Validate("", 0, 0, Char(false), 0, 0, ref state);
        Assert.False(result.Valid);
        Assert.Equal(RegexExecutionFailureCode.Cancelled, result.Failure.Code);
        state = State(options: new() { CancellationToken = cancellation.Token });
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.Read([], 0, 0, false, Byte(false), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.Cancelled, state.Failure.Code);
        state = State(options: new() { CancellationToken = cancellation.Token });
        Assert.Equal(0, Pcre2CharTextReader.AdvanceAfterEmpty("", 0, Char(), ref state));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, state.Failure.Code);
        state = State(options: new() { Timeout = TimeSpan.FromTicks(1) });
        // Deterministically install the already elapsed deadline without sleeps.
        Assert.False(state.Budget.TryChargeAt(0, 0, state.Budget.StartedAt + 1000000000, out state.Failure));
        Assert.Equal(Pcre2ReadStatus.End, Pcre2CharTextReader.ReadPrevious("", 0, Char(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.TimedOut, state.Failure.Code);
    }

    [Fact]
    public void ReuseReadsCurrentBorrowedInputWithoutRetainedValidationOrStorage()
    {
        byte[] input = [97];
        var state = State();
        Assert.True(Pcre2ByteTextReader.Validate(input, 0, 0, Byte(), 0, 0, ref state).Valid);
        input[0] = 0xFF;
        var next = State();
        Assert.Equal(-23, Pcre2ByteTextReader.Validate(input, 0, 0, Byte(), 0, 0, ref next).NativeError);
        input[0] = 98;
        next = State();
        Assert.Equal(new(Pcre2ReadStatus.Value, 98, 1), Pcre2ByteTextReader.Read(input, 0, 1, false, Byte(), ref next));
        Assert.Equal(default, next.Buffers.Capacity);
    }

    [Fact]
    public void LongValidationReverseAndFragmentRunsStopAtFiniteWorkWithoutScratchGrowth()
    {
        var input = new byte[100000];
        Array.Fill(input, (byte)0x80);
        input[0] = 97;
        var state = State(16);
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.ReadPrevious(input, input.Length, Byte(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        Assert.Equal(16, state.Budget.WorkConsumed);
        Assert.Equal(default, state.Buffers.Capacity);
        state = State(16);
        var result = Pcre2ByteTextReader.Bind(input, 50000, int.MaxValue, Byte(invalid: true), 0, 0, ref state);
        Assert.False(result.Validation.Valid);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        Assert.Equal(16, state.Budget.WorkConsumed);
        Array.Fill(input, (byte)97);
        state = State(16);
        result = Pcre2ByteTextReader.Bind(input, 100000, int.MaxValue, Byte(), 0, 0, ref state);
        Assert.False(result.Validation.Valid);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        Assert.Equal(16, state.Budget.WorkConsumed);
        // Shared RunBudget's constructor is strictly positive. The VM owner
        // admits native zero-work denial before binding text; here exercise
        // its zero remaining capacity using the same exhausted budget.
        state = State(1);
        Assert.True(state.TryCharge(1, 0));
        Assert.Equal(Pcre2ReadStatus.End, Pcre2ByteTextReader.Read("a"u8, 0, 1, false, Byte(), ref state).Status);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
    }

    [Fact]
    public void UnitStorageIsIndependentOfUcpBsrTablesAndCaseFlags()
    {
        var state = State();
        foreach (var bsr in Enum.GetValues<Pcre2Bsr>())
        {
            var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Ucp | Pcre2CompileFlags.Caseless, bsr: bsr);
            Assert.Equal(new(Pcre2ReadStatus.Value, 0xFF, 1), Pcre2ByteTextReader.Read([0xFF], 0, 1, false, profile, ref state));
            profile = new(Pcre2CodeUnitWidth.Sixteen, Pcre2CompileFlags.Ucp | Pcre2CompileFlags.Caseless, bsr: bsr);
            Assert.Equal(new(Pcre2ReadStatus.Value, 0xD800, 1), Pcre2CharTextReader.Read("\uD800", 0, 1, false, profile, ref state));
        }
    }

    [Fact]
    public void ArgumentsRejectWidthAndBoundsBeforeSpanAccess()
    {
        Assert.Throws<ArgumentException>(() => { var state = State(); Pcre2ByteTextReader.Read([], 0, 0, false, Char(), ref state); });
        Assert.Throws<ArgumentException>(() => { var state = State(); Pcre2CharTextReader.Validate("", 0, 0, Byte(), 0, 0, ref state); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { var state = State(); Pcre2ByteTextReader.Read([], -1, 0, false, Byte(), ref state); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { var state = State(); Pcre2CharTextReader.Read("a", 0, 2, false, Char(), ref state); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { var state = State(); Pcre2ByteTextReader.Validate([], 0, -1, Byte(), 0, 0, ref state); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { var state = State(); Pcre2ByteTextReader.Validate([], 0, 0, Byte(), 0, (Pcre2ValidationPolicy)42, ref state); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { var state = State(); Pcre2CharTextReader.ReadPrevious("a", int.MaxValue, Char(), ref state); });
    }
}
