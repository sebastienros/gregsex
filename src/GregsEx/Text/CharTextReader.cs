#nullable enable
using GregsEx.Contracts;

namespace GregsEx.Text;

// Borrows the original subject. No scratch capacity, transcoding or retained validation token.
internal readonly struct CharTextReader : ITextReader<char>
{
    public static RegexSearchStatus Validate(ReadOnlySpan<char> input, int startAt,
        ProfileDescriptor profile, ref RunState state)
    {
        TextValidation.Admit(input.Length, startAt, profile, chars: true);
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
        var scalar = profile.TextModel != TextModel.Utf16CodeUnits;
        for (var offset = 0; offset < input.Length; offset++)
        {
            // Surrogate-free spans are charged as one run: the per-unit work total is unchanged.
            var clean = scalar ? input.Slice(offset).IndexOfAnyInRange('\uD800', '\uDFFF') : -1;
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
            if (!state.TryCharge(1, offset))
            {
                return TextValidation.FailureStatus(ref state);
            }

            var first = input[offset];
            var sequenceStart = offset;
            if (!char.IsHighSurrogate(first) || offset + 1 == input.Length)
            {
                return TextValidation.Invalid(ref state, sequenceStart);
            }

            if (!state.TryCharge(1, offset + 1))
            {
                return TextValidation.FailureStatus(ref state);
            }

            if (!char.IsLowSurrogate(input[++offset]))
            {
                return TextValidation.Invalid(ref state, sequenceStart);
            }

            interior |= startAt == offset;
        }
        // An invalid trailing sequence always wins over an earlier interior start.
        return interior ? TextValidation.Invalid(ref state, startAt, boundary: true) : RegexSearchStatus.NoMatch;
    }

    public static bool TryRead(ReadOnlySpan<char> input, int offset, ProfileDescriptor profile,
        out int value, out int width)
    {
        value = width = 0;
        if ((uint)offset >= (uint)input.Length)
        {
            return false;
        }

        var first = input[offset];
        if (profile.TextModel == TextModel.Utf16CodeUnits || !char.IsSurrogate(first))
        {
            value = first;
            width = 1;
            return true;
        }
        if (!char.IsHighSurrogate(first) || offset + 1 == input.Length || !char.IsLowSurrogate(input[offset + 1]))
        {
            return false;
        }

        value = 0x10000 + ((first - 0xD800) << 10) + input[offset + 1] - 0xDC00;
        width = 2;
        return true;
    }

    RegexSearchStatus ITextReader<char>.Validate(ReadOnlySpan<char> input, int startAt, ProfileDescriptor profile, ref RunState state) => Validate(input, startAt, profile, ref state);
    bool ITextReader<char>.TryRead(ReadOnlySpan<char> input, int offset, ProfileDescriptor profile, out int value, out int width) => TryRead(input, offset, profile, out value, out width);
}
