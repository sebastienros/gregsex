#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal sealed partial class Pcre2Host
{
    private static int Digit(int c, int radix)
    {
        var value = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
        return value < radix ? value : -1;
    }
    private static int Escape<T, C, U>(Pcre2Program program, ReadOnlySpan<T> text, ref int ptr, int end,
        out int cp, out int group, out int directive, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        cp = 0;
        group = -1;
        directive = 0;
        if (ptr >= end)
        {
            return (int)Pcre2ErrorCode.Badrepescape;
        }

        var c = U.Unit(text[ptr++]);
        cp = c;
        if (!state.TryCharge(1, ptr))
        {
            return (int)Pcre2ErrorCode.Matchlimit;
        }

        var profile = program.Profile;
        switch (c)
        {
            case 'a':
                cp = 7;
                return 0;
            case 'b':
                cp = 8;
                return 0;
            case 'e':
                cp = 27;
                return 0;
            case 'f':
                cp = 12;
                return 0;
            case 'n':
                cp = 10;
                return 0;
            case 'r':
                cp = (profile.ExtraFlags & Pcre2ExtraFlags.EscapedCrIsLf) != 0 ? 10 : 13;
                return 0;
            case 't':
                cp = 9;
                return 0;
            case 'v':
                cp = 11;
                return 0;
            case 'Q':
            case 'E':
                directive = c;
                return 0;
            case 'c':
                if (ptr >= end)
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                cp = U.Unit(text[ptr]);
                if (cp < 32 || cp > 126)
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                if (cp is >= 'a' and <= 'z')
                {
                    cp -= 32;
                }

                cp ^= 64;
                ptr++;
                return 0;
            case 'g':
                if (ptr >= end || U.Unit(text[ptr]) != '<')
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                var start = ptr;
                var n = ptr + 1;
                if (n < end && U.Unit(text[n]) is >= '0' and <= '9')
                {
                    long number = 0;
                    while (n < end && U.Unit(text[n]) is >= '0' and <= '9')
                    {
                        if (!state.TryCharge(1, n))
                        {
                            return (int)Pcre2ErrorCode.Matchlimit;
                        }

                        number = Math.Min(65536, number * 10 + U.Unit(text[n++]) - '0');
                    }
                    if (number > 65535 || n >= end || U.Unit(text[n]) != '>')
                    { ptr = start; return (int)Pcre2ErrorCode.Badrepescape; }
                    group = (int)number;
                    ptr = n + 1;
                    return 0;
                }
                directive = 'g';
                return 0;
            case 'N':
                if (!profile.Utf || ptr >= end || U.Unit(text[ptr]) != '{')
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                ptr++;
                while (ptr < end && U.Unit(text[ptr]) is ' ' or '\t')
                {
                    ptr++;
                    if (!state.TryCharge(1, ptr))
                    {
                        return (int)Pcre2ErrorCode.Matchlimit;
                    }
                }
                if (ptr + 1 >= end || U.Unit(text[ptr]) != 'U' || U.Unit(text[ptr + 1]) != '+')
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                ptr += 2;
                return BracedNumber<T, C, U>(profile, text, ref ptr, end, 16, out cp, ref state);
            case 'x':
            case 'o':
                var braced = ptr < end && U.Unit(text[ptr]) == '{';
                if (c == 'o' && !braced)
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                if (braced)
                { ptr++; return BracedNumber<T, C, U>(profile, text, ref ptr, end, c == 'x' ? 16 : 8, out cp, ref state); }
                if (ptr >= end || Digit(U.Unit(text[ptr]), 16) < 0)
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                cp = Digit(U.Unit(text[ptr++]), 16);
                if (ptr < end && Digit(U.Unit(text[ptr]), 16) >= 0)
                {
                    cp = cp * 16 + Digit(U.Unit(text[ptr++]), 16);
                }

                return 0;
        }
        if (c is >= '0' and <= '9')
        {
            var first = ptr - 1;
            var numberEnd = ptr;
            long number = c - '0';
            while (numberEnd < end && U.Unit(text[numberEnd]) is >= '0' and <= '9')
            {
                if (!state.TryCharge(1, numberEnd))
                {
                    return (int)Pcre2ErrorCode.Matchlimit;
                }

                number = Math.Min(65536, number * 10 + U.Unit(text[numberEnd++]) - '0');
            }
            var python = (profile.ExtraFlags & Pcre2ExtraFlags.PythonOctal) != 0;
            var threeOctal = first + 2 < end && Digit(c, 8) >= 0 && Digit(U.Unit(text[first + 1]), 8) >= 0 && Digit(U.Unit(text[first + 2]), 8) >= 0;
            var reference = c != '0' && (python ? !threeOctal : number < 10 || c >= '8' || number < program.GroupCount);
            if (reference)
            {
                ptr = numberEnd;
                if (number > 65535)
                {
                    return (int)Pcre2ErrorCode.Badrepescape;
                }

                group = (int)number;
                return 0;
            }
            cp = c - '0';
            var digits = 1;
            while (digits < 3 && ptr < end && Digit(U.Unit(text[ptr]), 8) >= 0)
            { cp = cp * 8 + Digit(U.Unit(text[ptr++]), 8); digits++; }
            if (cp > 255 && (python || U.Width == Pcre2CodeUnitWidth.Eight && !profile.Utf))
            {
                return (int)Pcre2ErrorCode.Badrepescape;
            }

            if (digits == 1 && cp == 0 && (profile.ExtraFlags & Pcre2ExtraFlags.NoBs0) != 0)
            {
                return (int)Pcre2ErrorCode.Badrepescape;
            }

            return 0;
        }
        // Unrecognized ASCII letters are errors even under BAD_ESCAPE_IS_LITERAL
        // (source check_escape cb==NULL). Non-alphanumerics are literal.
        if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            return (int)Pcre2ErrorCode.Badrepescape;
        }

        return 0;
    }
    private static int BracedNumber<T, C, U>(Pcre2Profile profile, ReadOnlySpan<T> text, ref int ptr, int end,
        int radix, out int cp, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        cp = 0;
        while (ptr < end && U.Unit(text[ptr]) is ' ' or '\t')
        {
            ptr++;
            if (!state.TryCharge(1, ptr))
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }
        }
        var digits = 0;
        var tooLarge = false;
        var ceiling = profile.Utf ? 0x10FFFF : U.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535;
        while (ptr < end && Digit(U.Unit(text[ptr]), radix) >= 0)
        {
            if (!state.TryCharge(1, ptr))
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }

            var digit = Digit(U.Unit(text[ptr++]), radix);
            digits++;
            if (cp > (ceiling - digit) / radix)
            {
                tooLarge = true;
            }

            if (!tooLarge)
            {
                cp = cp * radix + digit;
            }
        }
        while (ptr < end && U.Unit(text[ptr]) is ' ' or '\t')
        {
            ptr++;
            if (!state.TryCharge(1, ptr))
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }
        }
        if (digits == 0 || tooLarge)
        {
            return (int)Pcre2ErrorCode.Badrepescape;
        }

        if (ptr >= end || U.Unit(text[ptr]) != '}')
        { if (ptr > 0) { ptr--; } return (int)Pcre2ErrorCode.Badrepescape; }
        ptr++;
        if (profile.Utf && cp is >= 0xD800 and <= 0xDFFF && (profile.ExtraFlags & Pcre2ExtraFlags.AllowSurrogateEscapes) == 0)
        { ptr--; return (int)Pcre2ErrorCode.Badrepescape; }
        return 0;
    }
}
