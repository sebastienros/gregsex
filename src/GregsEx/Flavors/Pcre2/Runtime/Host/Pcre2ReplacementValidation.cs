using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal static class Pcre2ReplacementValidation
{
    // Source substitute validates the entire replacement even for a pattern
    // admitting invalid subjects. This is not generic projection eligibility.
    internal static Pcre2ValidationResult Validate<T, U>(ReadOnlySpan<T> input, ref RunState state)
        where T : unmanaged where U : struct, IPcre2Units<T>
    {
        if (!state.TryCharge(0, 0))
        {
            return Pcre2TextCore<T, U>.Resource(in state);
        }

        for (var offset = 0; offset < input.Length;)
        {
            var error = Pcre2TextCore<T, U>.Decode(input, offset, input.Length, ref state, out _, out var width);
            if (state.Failure.Code != 0)
            {
                return Pcre2TextCore<T, U>.Resource(in state);
            }

            if (error != 0)
            {
                return Pcre2TextCore<T, U>.EncodingError(error, offset, in state);
            }

            offset += width;
        }
        return new(true);
    }
}
