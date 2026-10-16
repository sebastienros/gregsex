using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Text;

// Error precedence follows pinned pcre2_valid_utf.c (PCRE2 10.46), covered by
// the repository's retained BSD-3-Clause WITH PCRE2-exception source notice.
// No decoding replacement, input copy, runtime Unicode data or retained span.
internal interface IPcre2Units<TUnit> where TUnit : unmanaged
{
    static abstract Pcre2CodeUnitWidth Width { get; }
    static abstract int Unit(scoped ReadOnlySpan<TUnit> input, int offset);
    static abstract bool Interior(int value);
}

internal readonly struct Pcre2ByteUnits : IPcre2Units<byte>
{
    public static Pcre2CodeUnitWidth Width => Pcre2CodeUnitWidth.Eight;
    public static int Unit(scoped ReadOnlySpan<byte> input, int offset) => input[offset];
    public static bool Interior(int value) => (value & 0xC0) == 0x80;
}

internal readonly struct Pcre2CharUnits : IPcre2Units<char>
{
    public static Pcre2CodeUnitWidth Width => Pcre2CodeUnitWidth.Sixteen;
    public static int Unit(scoped ReadOnlySpan<char> input, int offset) => input[offset];
    public static bool Interior(int value) => (value & 0xFC00) == 0xDC00;
}

internal static class Pcre2TextCore<TUnit, TUnits>
    where TUnit : unmanaged where TUnits : struct, IPcre2Units<TUnit>
{
    internal static bool AllowsInvalid(Pcre2Profile profile) =>
        (profile.Flags & Pcre2CompileFlags.MatchInvalidUtf) != 0;

    internal static void Admit(int length, int offset, Pcre2Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Width != TUnits.Width)
        {
            throw new ArgumentException("Native reader width differs from profile.", nameof(profile));
        }

        if ((uint)offset > (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    internal static bool Inspect(scoped ReadOnlySpan<TUnit> input, int offset, ref RunState state, out int value)
    {
        value = 0;
        if (!state.TryCharge(1, offset))
        {
            return false;
        }

        value = TUnits.Unit(input, offset);
        return true;
    }

    internal static Pcre2ValidationResult Resource(in RunState state) => new(false, Failure: state.Failure);

    // NativeError, not shared InvalidInput, governs the typed outcome. Failure
    // carries a defined UTF error offset; it does not latch a managed failure.
    internal static Pcre2ValidationResult EncodingError(int error, int offset, in RunState state) =>
        new(false, error, new(RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed));

    internal static Pcre2ValidationResult BoundaryError(int offset, in RunState state) =>
        new(false, (int)Pcre2ErrorCode.Badutfoffset,
            new(RegexExecutionFailureCode.InvalidStartBoundary, offset, state.Budget.WorkConsumed));

    internal static int PreviousStart(scoped ReadOnlySpan<TUnit> input, int offset, ref RunState state)
    {
        var start = offset - 1;
        while (start >= 0)
        {
            if (!Inspect(input, start, ref state, out var unit))
            {
                return -1;
            }

            if (!TUnits.Interior(unit) || start == 0)
            {
                return start;
            }

            start--;
        }
        return -1;
    }

    internal static Pcre2ValidationResult Validate(scoped ReadOnlySpan<TUnit> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state) =>
        Pcre2TextFragments<TUnit, TUnits>.Bind(input, startAt, maxLookbehind, profile, flags, policy, ref state).Validation;

    internal static int IsolatedError => TUnits.Width == Pcre2CodeUnitWidth.Eight
        ? (int)Pcre2ErrorCode.Utf8Err20 : (int)Pcre2ErrorCode.Utf16Err3;

    // On failure width is zero. Every actually inspected unit is charged,
    // including speculative/boundary/reverse reads; missing units are not read.
    internal static int Decode(scoped ReadOnlySpan<TUnit> input, int offset, int end, ref RunState state,
        out int value, out int width)
    {
        value = width = 0;
        if (!Inspect(input, offset, ref state, out var first))
        {
            return 0;
        }

        if (TUnits.Width == Pcre2CodeUnitWidth.Sixteen)
        {
            if (first < 0xD800 || first > 0xDFFF)
            { value = first; width = 1; return 0; }
            if (first >= 0xDC00)
            {
                return (int)Pcre2ErrorCode.Utf16Err3;
            }

            if (end - offset < 2)
            {
                return (int)Pcre2ErrorCode.Utf16Err1;
            }

            if (!Inspect(input, offset + 1, ref state, out var second))
            {
                return 0;
            }

            if (!TUnits.Interior(second))
            {
                return (int)Pcre2ErrorCode.Utf16Err2;
            }

            value = 0x10000 + ((first - 0xD800) << 10) + second - 0xDC00;
            width = 2;
            return 0;
        }
        if (first < 0x80)
        { value = first; width = 1; return 0; }
        if (first < 0xC0)
        {
            return (int)Pcre2ErrorCode.Utf8Err20;
        }

        if (first >= 0xFE)
        {
            return (int)Pcre2ErrorCode.Utf8Err21;
        }

        var count = first < 0xE0 ? 2 : first < 0xF0 ? 3 : first < 0xF8 ? 4 : first < 0xFC ? 5 : 6;
        var missing = count - (end - offset);
        if (missing > 0)
        {
            return -2 - missing; // UTF8_ERR1..5, before continuation checks.
        }

        int scalar = first & (0x7F >> count), secondByte = 0;
        for (var part = 1; part < count; part++)
        {
            if (!Inspect(input, offset + part, ref state, out var unit))
            {
                return 0;
            }

            if (!TUnits.Interior(unit))
            {
                return -7 - part; // UTF8_ERR6..10.
            }

            if (part == 1)
            {
                secondByte = unit;
            }

            scalar = (scalar << 6) | (unit & 0x3F);
        }
        if (count == 2 && first < 0xC2)
        {
            return (int)Pcre2ErrorCode.Utf8Err15;
        }

        if (count == 3 && first == 0xE0 && secondByte < 0xA0)
        {
            return (int)Pcre2ErrorCode.Utf8Err16;
        }

        if (count == 3 && first == 0xED && secondByte >= 0xA0)
        {
            return (int)Pcre2ErrorCode.Utf8Err14;
        }

        if (count == 4 && first == 0xF0 && secondByte < 0x90)
        {
            return (int)Pcre2ErrorCode.Utf8Err17;
        }

        if (count == 4 && (first > 0xF4 || first == 0xF4 && secondByte > 0x8F))
        {
            return (int)Pcre2ErrorCode.Utf8Err13;
        }

        if (count == 5 && first == 0xF8 && secondByte < 0x88)
        {
            return (int)Pcre2ErrorCode.Utf8Err18;
        }

        if (count == 6 && first == 0xFC && secondByte < 0x84)
        {
            return (int)Pcre2ErrorCode.Utf8Err19;
        }

        if (count > 4)
        {
            return count == 5 ? (int)Pcre2ErrorCode.Utf8Err11 : (int)Pcre2ErrorCode.Utf8Err12;
        }

        value = scalar;
        width = count;
        return 0;
    }

    internal static Pcre2ReadResult Barrier(int offset, Pcre2Profile profile, ref RunState state, bool boundary = false)
    {
        if (!AllowsInvalid(profile) && state.Failure.Code == RegexExecutionFailureCode.None)
        {
            state.Failure = new(boundary ? RegexExecutionFailureCode.InvalidStartBoundary : RegexExecutionFailureCode.InvalidEncoding,
                offset, state.Budget.WorkConsumed);
        }

        return new(Pcre2ReadStatus.InvalidBarrier, 0, 1);
    }

    internal static Pcre2ReadResult Read(scoped ReadOnlySpan<TUnit> input, int offset, int visibleEnd, bool codeUnit,
        Pcre2Profile profile, ref RunState state)
    {
        Admit(input.Length, offset, profile);
        if ((uint)visibleEnd > (uint)input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(visibleEnd));
        }

        if (!state.TryCharge(0, offset) || offset >= visibleEnd)
        {
            return default;
        }

        if (!profile.Utf)
        {
            return Inspect(input, offset, ref state, out var raw) ? new(Pcre2ReadStatus.Value, raw, 1) : default;
        }

        var start = offset;
        if (codeUnit)
        {
            if (!Inspect(input, offset, ref state, out var raw))
            {
                return default;
            }

            if (TUnits.Interior(raw))
            {
                // A chain of code-unit atoms can consume a valid scalar's
                // individual units. Malformed original input remains a barrier.
                start = PreviousStart(input, offset + 1, ref state);
                if (start < 0)
                {
                    return default;
                }
            }
            var unitError = Decode(input, start, input.Length, ref state, out _, out var fullWidth);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return default;
            }

            if (unitError != 0 || offset >= start + fullWidth)
            {
                return Barrier(offset, profile, ref state);
            }

            return new(Pcre2ReadStatus.Value, raw, 1);
        }
        var error = Decode(input, start, input.Length, ref state, out var value, out var width);
        if (state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return default;
        }

        if (error != 0)
        {
            return Barrier(offset, profile, ref state);
        }

        return width <= visibleEnd - offset ? new(Pcre2ReadStatus.Value, value, width) : default;
    }

    internal static Pcre2ReadResult ReadPrevious(scoped ReadOnlySpan<TUnit> input, int offset,
        Pcre2Profile profile, ref RunState state)
    {
        Admit(input.Length, offset, profile);
        if (!state.TryCharge(0, offset) || offset == 0)
        {
            return default;
        }

        if (!profile.Utf)
        {
            return Inspect(input, offset - 1, ref state, out var raw) ? new(Pcre2ReadStatus.Value, raw, 1) : default;
        }

        var start = PreviousStart(input, offset, ref state);
        if (start < 0)
        {
            return default;
        }

        var error = Decode(input, start, input.Length, ref state, out var value, out var width);
        if (state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return default;
        }

        if (error != 0 || width != offset - start)
        {
            return Barrier(offset - 1, profile, ref state, boundary: error == 0);
        }

        return new(Pcre2ReadStatus.Value, value, width);
    }

    internal static int AdvanceAfterEmpty(scoped ReadOnlySpan<TUnit> input, int offset,
        Pcre2Profile profile, ref RunState state)
    {
        Admit(input.Length, offset, profile);
        if (!state.TryCharge(0, offset))
        {
            return offset;
        }

        if (offset == input.Length)
        {
            return offset; // host emits terminal empty once.
        }

        var read = Read(input, offset, input.Length, false, profile, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return offset;
        }

        var next = offset + read.Width;
        if (read.Status == Pcre2ReadStatus.InvalidBarrier)
        {
            while (next < input.Length)
            {
                if (!Inspect(input, next, ref state, out var unit))
                {
                    return offset;
                }

                if (!TUnits.Interior(unit))
                {
                    break;
                }

                next++;
            }
            return next;
        }
        if (read.Value == '\r' && next < input.Length &&
            profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Any or Pcre2Newline.Anycrlf)
        {
            if (!Inspect(input, next, ref state, out var unit))
            {
                return offset;
            }

            if (unit == '\n')
            {
                next++;
            }
        }
        return next;
    }
}
