#nullable enable
using GregsEx.Contracts;

namespace GregsEx.Text;

internal readonly struct ByteTextReader : ITextReader<byte>
{
    public static RegexSearchStatus Validate(ReadOnlySpan<byte> input, int startAt,
        ProfileDescriptor profile, ref RunState state)
    {
        TextValidation.Admit(input.Length, startAt, profile, chars: false);
        if (!state.TryCharge(0, startAt))
        {
            return TextValidation.FailureStatus(ref state);
        }

        if (state.PrevalidatedCleanSubject)
        {
            if (!state.TryChargeRun(0, input.Length))
            {
                return TextValidation.FailureStatus(ref state);
            }

            state.ValidatedCleanSubject = true;
            return RegexSearchStatus.NoMatch;
        }
        var interior = false;
        var offset = 0;
        var scalar = profile.TextModel != TextModel.RawBytes;
        while (offset < input.Length)
        {
            // ASCII (or raw) spans are charged as one run: the per-unit work total is unchanged.
            var clean = scalar ? input.Slice(offset).IndexOfAnyExceptInRange((byte)0, (byte)0x7F) : -1;
            if (clean < 0)
            {
                clean = input.Length - offset;
            }

            if (clean > 0)
            {
                if (!state.TryChargeRun(offset, clean))
                {
                    return TextValidation.FailureStatus(ref state);
                }

                if (offset == 0 && clean == input.Length)
                {
                    state.ValidatedCleanSubject = true;
                    break;
                }
                offset += clean;
                if (offset == input.Length)
                {
                    break;
                }
            }
            var sequenceStart = offset;
            if (!state.TryCharge(1, offset))
            {
                return TextValidation.FailureStatus(ref state);
            }

            var first = input[offset++];
            var width = SequenceWidth(first);
            if (width == 0)
            {
                return TextValidation.Invalid(ref state, sequenceStart);
            }

            for (var part = 1; part < width; part++)
            {
                if (offset == input.Length)
                {
                    return TextValidation.Invalid(ref state, sequenceStart);
                }

                if (!state.TryCharge(1, offset))
                {
                    return TextValidation.FailureStatus(ref state);
                }

                var next = input[offset++];
                if (!IsContinuation(next) || (part == 1 && !ValidSecond(first, next)))
                {
                    return TextValidation.Invalid(ref state, sequenceStart);
                }
            }
            interior |= startAt > sequenceStart && startAt < offset;
        }
        return interior ? TextValidation.Invalid(ref state, startAt, boundary: true) : RegexSearchStatus.NoMatch;
    }

    public static bool TryRead(ReadOnlySpan<byte> input, int offset, ProfileDescriptor profile,
        out int value, out int width)
    {
        value = width = 0;
        if ((uint)offset >= (uint)input.Length)
        {
            return false;
        }

        var first = input[offset];
        if (profile.TextModel == TextModel.RawBytes || first <= 0x7F)
        {
            value = first;
            width = 1;
            return true;
        }
        var count = SequenceWidth(first);
        if (count == 0 || count > input.Length - offset)
        {
            return false;
        }

        var scalar = first & (0x7F >> count);
        for (var part = 1; part < count; part++)
        {
            var next = input[offset + part];
            if (!IsContinuation(next) || (part == 1 && !ValidSecond(first, next)))
            {
                return false;
            }

            scalar = (scalar << 6) | (next & 0x3F);
        }
        value = scalar;
        width = count;
        return true;
    }

    private static int SequenceWidth(byte first) => first switch
    {
        >= 0xC2 and <= 0xDF => 2,
        >= 0xE0 and <= 0xEF => 3,
        >= 0xF0 and <= 0xF4 => 4,
        _ => 0
    };

    private static bool IsContinuation(byte value) => value is >= 0x80 and <= 0xBF;
    private static bool ValidSecond(byte first, byte second) => first switch
    {
        0xE0 => second >= 0xA0, // No overlong sequence.
        0xED => second <= 0x9F, // No encoded surrogate.
        0xF0 => second >= 0x90,
        0xF4 => second <= 0x8F, // No scalar above U+10FFFF.
        _ => true
    };
}
