#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;

namespace GregsEx.Flavors.XmlShared;

internal static class XmlTextValidation
{
    internal static void Admit(int length, int startAt)
    {
        if ((uint)startAt > (uint)length)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }
    }
    internal static RegexSearchStatus FailureStatus(ref RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidInputDomain or
            RegexExecutionFailureCode.InvalidStartBoundary => RegexSearchStatus.InvalidInput,
        _ => RegexSearchStatus.LimitExceeded
    };
    internal static RegexSearchStatus Invalid(ref RunState state, int offset, RegexExecutionFailureCode code)
    {
        state.Failure = new(code, offset, state.Budget.WorkConsumed);
        return RegexSearchStatus.InvalidInput;
    }
}

internal readonly struct XmlCharTextReader : IXmlTextReader<char>
{
    public static RegexSearchStatus Validate(ReadOnlySpan<char> input, int startAt, ref RunState state)
    {
        XmlTextValidation.Admit(input.Length, startAt);
        if (!state.TryCharge(0, startAt))
        {
            return XmlTextValidation.FailureStatus(ref state);
        }

        var interior = false;
        for (var offset = 0; offset < input.Length;)
        {
            var start = offset;
            if (!state.TryCharge(1, offset))
            {
                return XmlTextValidation.FailureStatus(ref state);
            }

            int scalar = input[offset++];
            if (scalar is >= 0xD800 and <= 0xDFFF)
            {
                if (scalar > 0xDBFF || offset == input.Length)
                {
                    return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidEncoding);
                }

                if (!state.TryCharge(1, offset))
                {
                    return XmlTextValidation.FailureStatus(ref state);
                }

                int next = input[offset++];
                if (next is < 0xDC00 or > 0xDFFF)
                {
                    return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidEncoding);
                }

                scalar = 0x10000 + ((scalar - 0xD800) << 10) + next - 0xDC00;
                interior |= startAt == offset - 1;
            }
            if (!XmlAlphabet.Contains(scalar))
            {
                return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidInputDomain);
            }
        }
        return interior ? XmlTextValidation.Invalid(ref state, startAt, RegexExecutionFailureCode.InvalidStartBoundary) :
            RegexSearchStatus.NoMatch;
    }
    public static bool TryRead(ReadOnlySpan<char> input, int offset, out int scalar, out int width)
    {
        scalar = width = 0;
        if ((uint)offset >= (uint)input.Length)
        {
            return false;
        }

        int value = input[offset], count = 1;
        if (value is >= 0xD800 and <= 0xDFFF)
        {
            if (value > 0xDBFF || offset + 1 == input.Length || input[offset + 1] is < '\uDC00' or > '\uDFFF')
            {
                return false;
            }

            value = 0x10000 + ((value - 0xD800) << 10) + input[offset + 1] - 0xDC00;
            count = 2;
        }
        if (!XmlAlphabet.Contains(value))
        {
            return false;
        }

        scalar = value;
        width = count;
        return true;
    }

    RegexSearchStatus IXmlTextReader<char>.Validate(ReadOnlySpan<char> input, int startAt, ref RunState state) => Validate(input, startAt, ref state);
    bool IXmlTextReader<char>.TryRead(ReadOnlySpan<char> input, int offset, out int scalar, out int width) => TryRead(input, offset, out scalar, out width);
}

internal readonly struct XmlByteTextReader : IXmlTextReader<byte>
{
    public static RegexSearchStatus Validate(ReadOnlySpan<byte> input, int startAt, ref RunState state)
    {
        XmlTextValidation.Admit(input.Length, startAt);
        if (!state.TryCharge(0, startAt))
        {
            return XmlTextValidation.FailureStatus(ref state);
        }

        var interior = false;
        var offset = 0;
        while (offset < input.Length)
        {
            var start = offset;
            if (!state.TryCharge(1, offset))
            {
                return XmlTextValidation.FailureStatus(ref state);
            }

            var first = input[offset++];
            var count = first <= 0x7F ? 1 : SequenceWidth(first);
            if (count == 0)
            {
                return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidEncoding);
            }

            var scalar = count == 1 ? first : first & (0x7F >> count);
            for (var part = 1; part < count; part++)
            {
                if (offset == input.Length)
                {
                    return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidEncoding);
                }

                if (!state.TryCharge(1, offset))
                {
                    return XmlTextValidation.FailureStatus(ref state);
                }

                var next = input[offset++];
                if (!IsContinuation(next) || part == 1 && !ValidSecond(first, next))
                {
                    return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidEncoding);
                }

                scalar = (scalar << 6) | (next & 0x3F);
            }
            if (!XmlAlphabet.Contains(scalar))
            {
                return XmlTextValidation.Invalid(ref state, start, RegexExecutionFailureCode.InvalidInputDomain);
            }

            interior |= startAt > start && startAt < offset;
        }
        return interior ? XmlTextValidation.Invalid(ref state, startAt, RegexExecutionFailureCode.InvalidStartBoundary) :
            RegexSearchStatus.NoMatch;
    }
    public static bool TryRead(ReadOnlySpan<byte> input, int offset, out int scalar, out int width)
    {
        scalar = width = 0;
        if ((uint)offset >= (uint)input.Length)
        {
            return false;
        }

        var first = input[offset];
        var count = first <= 0x7F ? 1 : SequenceWidth(first);
        if (count == 0 || count > input.Length - offset)
        {
            return false;
        }

        var value = count == 1 ? first : first & (0x7F >> count);
        for (var part = 1; part < count; part++)
        {
            var next = input[offset + part];
            if (!IsContinuation(next) || part == 1 && !ValidSecond(first, next))
            {
                return false;
            }

            value = (value << 6) | (next & 0x3F);
        }
        if (!XmlAlphabet.Contains(value))
        {
            return false;
        }

        scalar = value;
        width = count;
        return true;
    }
    private static int SequenceWidth(byte first) => first switch
    { >= 0xC2 and <= 0xDF => 2, >= 0xE0 and <= 0xEF => 3, >= 0xF0 and <= 0xF4 => 4, _ => 0 };
    private static bool IsContinuation(byte next) => next is >= 0x80 and <= 0xBF;
    private static bool ValidSecond(byte first, byte second) => first switch
    { 0xE0 => second >= 0xA0, 0xED => second <= 0x9F, 0xF0 => second >= 0x90, 0xF4 => second <= 0x8F, _ => true };

    RegexSearchStatus IXmlTextReader<byte>.Validate(ReadOnlySpan<byte> input, int startAt, ref RunState state) => Validate(input, startAt, ref state);
    bool IXmlTextReader<byte>.TryRead(ReadOnlySpan<byte> input, int offset, out int scalar, out int width) => TryRead(input, offset, out scalar, out width);
}
