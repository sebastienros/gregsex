#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Tables;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal sealed partial class Pcre2Host
{
    private static int Expand<T, C, U>(Pcre2Program program, ReadOnlySpan<T> input, ReadOnlySpan<T> replacement,
        ReadOnlySpan<Pcre2OffsetPair> pairs, int mark, Pcre2SubstituteFlags flags, C? callbacks,
        ref int callouts, int maximumCallouts, ref Pcre2HostOutput<T> output, ref RunState state,
        out int errorOffset, ref bool quoted)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        errorOffset = -1;
        if ((flags & Pcre2SubstituteFlags.Literal) != 0)
        {
            return output.Copy(replacement, ref state) ? 0 : output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit;
        }

        int ptr = 0, end = replacement.Length, stackCount = 0;
        Span<int> returns = stackalloc int[20];
        Span<T> scalar = stackalloc T[6];
        Span<char> name = stackalloc char[128];
        var casing = new CaseState();
        var profile = program.Profile;
        var data = Pcre2Data.Instance.Bind(profile.CharacterTables);
        while (true)
        {
            if (!state.TryCharge(1, ptr))
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }

            if (ptr >= end)
            {
                if (stackCount == 0)
                {
                    break;
                }

                end = returns[--stackCount];
                ptr = returns[--stackCount];
                continue;
            }
            var c = U.Unit(replacement[ptr]);
            if (quoted)
            {
                if (c == '\\' && ptr + 1 < end && U.Unit(replacement[ptr + 1]) == 'E')
                { quoted = false; ptr += 2; continue; }
                goto Literal;
            }
            int group = -1, nameLength = 0, special = 0;
            int text1 = -1, text1End = -1, text2 = -1, text2End = -1;
            scoped ReadOnlySpan<T> value = default;
            if (c == '$')
            {
                ptr++;
                if (ptr >= end)
                { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; }
                c = U.Unit(replacement[ptr]);
                if (c == '$')
                {
                    goto Literal;
                }

                if (c == '&')
                { ptr++; group = 0; goto Group; }
                if (c is '`' or '\'')
                {
                    ptr++;
                    var length = Pcre2Substrings.Length(pairs, 0, program.GroupCount);
                    if (length < 0)
                    { errorOffset = ptr; return length; }
                    var whole = pairs[0];
                    value = c == '`' ? input[..whole.Start] : input[whole.End..];
                    goto Value;
                }
                if (c == '_')
                { ptr++; value = input; goto Value; }
                bool braces = c == '{', angle = c == '<', star = false;
                if (braces || angle)
                {
                    ptr++;
                    if (ptr >= end)
                    { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; }
                    c = U.Unit(replacement[ptr]);
                }
                if (!angle && c == '*')
                { star = true; ptr++; if (ptr >= end) { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; } c = U.Unit(replacement[ptr]); }
                if (!angle && !star && c is >= '0' and <= '9')
                {
                    group = c - '0';
                    ptr++;
                    while (ptr < end && U.Unit(replacement[ptr]) is >= '0' and <= '9')
                    {
                        if (!state.TryCharge(1, ptr))
                        {
                            return (int)Pcre2ErrorCode.Matchlimit;
                        }

                        group = group > program.GroupCount ? program.GroupCount : group;
                        group = checked(group * 10 + U.Unit(replacement[ptr]) - '0');
                        if (group >= program.GroupCount)
                        {
                            if ((flags & Pcre2SubstituteFlags.UnknownUnset) == 0)
                            { errorOffset = ptr; return (int)Pcre2ErrorCode.Nosubstring; }
                            do
                            {
                                ptr++;
                                if (!state.TryCharge(1, ptr))
                                {
                                    return (int)Pcre2ErrorCode.Matchlimit;
                                }
                            } while (ptr < end && U.Unit(replacement[ptr]) is >= '0' and <= '9');
                            group = program.GroupCount;
                            break;
                        }
                        ptr++;
                    }
                }
                else
                {
                    if (!ReadName<T, C, U>(replacement, ref ptr, end, profile, data, name, out nameLength, ref state))
                    { errorOffset = ptr; return state.Failure.Code != 0 ? (int)Pcre2ErrorCode.Matchlimit : (int)Pcre2ErrorCode.Badreplacement; }
                }
                if (braces)
                {
                    if ((flags & Pcre2SubstituteFlags.Extended) != 0 && !star && ptr < end - 2 && U.Unit(replacement[ptr]) == ':')
                    {
                        special = U.Unit(replacement[++ptr]);
                        if (special is not ('+' or '-'))
                        { errorOffset = ptr; return (int)Pcre2ErrorCode.Badsubstitution; }
                        text1 = ++ptr;
                        var rc = FindEnd<T, C, U>(program, replacement, ref ptr, end, special == '-', ref state);
                        if (rc != 0)
                        { errorOffset = ptr; return rc; }
                        text1End = ptr;
                        if (special == '+' && U.Unit(replacement[ptr]) == ':')
                        {
                            text2 = ++ptr;
                            rc = FindEnd<T, C, U>(program, replacement, ref ptr, end, true, ref state);
                            if (rc != 0)
                            { errorOffset = ptr; return rc; }
                            text2End = ptr;
                        }
                    }
                    else if (ptr >= end || U.Unit(replacement[ptr]) != '}')
                    { errorOffset = ptr; return (int)Pcre2ErrorCode.Repmissingbrace; }
                    ptr++;
                }
                if (angle)
                {
                    if (ptr >= end || U.Unit(replacement[ptr]) != '>')
                    { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; }
                    ptr++;
                }
                if (star)
                {
                    if (!name[..nameLength].SequenceEqual("MARK"))
                    { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; }
                    value = U.Mark(program, mark);
                    goto Value;
                }
                goto Group;
            }
            if (c == '\\' && (flags & Pcre2SubstituteFlags.Extended) != 0)
            {
                var mode = 0;
                var single = false;
                if (ptr + 1 < end)
                {
                    var next = U.Unit(replacement[ptr + 1]);
                    switch (next)
                    {
                        case 'L':
                            mode = 1;
                            break;
                        case 'l':
                            mode = 1;
                            single = true;
                            break;
                        case 'U':
                            mode = 2;
                            break;
                        case 'u':
                            mode = 3;
                            single = true;
                            break;
                    }
                    if (mode != 0)
                    {
                        ptr += 2;
                        // Source requires a character after the paired escape.
                        if (single && ptr + 2 < end && U.Unit(replacement[ptr]) == '\\' && U.Unit(replacement[ptr + 1]) == (next == 'l' ? 'U' : 'L'))
                        { mode = next == 'l' ? 4 : 3; single = false; ptr += 2; }
                        if (!FlushCase<T, C, U>(profile, callbacks, ref casing, ref callouts, maximumCallouts, ref output, ref state))
                        {
                            return output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit;
                        }

                        casing = new() { Mode = mode, Single = single, Start = output.Offset, Extra = output.Extra };
                        continue;
                    }
                }
                ptr++;
                var rc = Escape<T, C, U>(program, replacement, ref ptr, end, out var cp, out group, out var directive, ref state);
                if (rc != 0)
                { errorOffset = ptr; return rc; }
                if (directive == 'E')
                {
                    if (!FlushCase<T, C, U>(profile, callbacks, ref casing, ref callouts, maximumCallouts, ref output, ref state))
                    {
                        return output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit;
                    }

                    casing = default;
                    continue;
                }
                if (directive == 'Q')
                { quoted = true; continue; }
                if (directive == 'g')
                {
                    ptr++; // Escape left ptr at '<'.
                    if (!ReadName<T, C, U>(replacement, ref ptr, end, profile, data, name, out nameLength, ref state) || ptr >= end || U.Unit(replacement[ptr]) != '>')
                    { errorOffset = ptr; return (int)Pcre2ErrorCode.Badrepescape; }
                    ptr++;
                    goto Group;
                }
                if (group >= 0)
                {
                    goto Group;
                }

                var n = Encode<T, C, U>(cp, profile.Utf, scalar);
                value = scalar[..n];
                goto Value;
            }
            goto Literal;

        Group:
            if (group < 0)
            {
                group = Pcre2Substrings.Number(program, pairs, name[..nameLength]);
            }

            var groupLength = Pcre2Substrings.Length(pairs, group, program.GroupCount);
            if (groupLength == (int)Pcre2ErrorCode.Nosubstring && (flags & Pcre2SubstituteFlags.UnknownUnset) != 0)
            {
                groupLength = (int)Pcre2ErrorCode.Unset;
            }

            if (groupLength < 0 && groupLength != (int)Pcre2ErrorCode.Unset)
            { errorOffset = ptr; return groupLength; }
            if (special != 0)
            {
                if (special == '-' && groupLength >= 0)
                { value = input.Slice(pairs[group].Start, groupLength); goto Value; }
                if (special == '-')
                { text2 = text1; text2End = text1End; }
                if (stackCount >= returns.Length)
                { errorOffset = ptr; return (int)Pcre2ErrorCode.Badreplacement; }
                returns[stackCount++] = ptr;
                returns[stackCount++] = end;
                ptr = groupLength >= 0 ? text1 : text2;
                end = groupLength >= 0 ? text1End : text2End;
                continue;
            }
            if (groupLength == (int)Pcre2ErrorCode.Unset)
            {
                if ((flags & Pcre2SubstituteFlags.UnsetEmpty) != 0)
                {
                    continue;
                }

                errorOffset = ptr;
                return groupLength;
            }
            if (pairs[group].Start > input.Length || pairs[group].End > input.Length)
            { errorOffset = ptr; return (int)Pcre2ErrorCode.Invalidoffset; }
            value = input.Slice(pairs[group].Start, groupLength);
            goto Value;

        Literal:
            var read = U.Read(replacement[..end], ptr, profile, ref state);
            if (!state.TryCharge(0, ptr) || read.Status != Pcre2ReadStatus.Value)
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }

            value = replacement.Slice(ptr, read.Width);
            ptr += read.Width;
        Value:
            if (!CopyCase<T, C, U>(value, profile, callbacks, ref casing, ref output, ref state))
            {
                return output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit;
            }
        }
        if (!FlushCase<T, C, U>(profile, callbacks, ref casing, ref callouts, maximumCallouts, ref output, ref state))
        {
            return output.Error != 0 ? output.Error : (int)Pcre2ErrorCode.Matchlimit;
        }

        return 0;
    }
    private static bool ReadName<T, C, U>(ReadOnlySpan<T> text, ref int ptr, int end, Pcre2Profile profile,
        Pcre2DataView data, scoped Span<char> name, out int length, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        var start = ptr;
        length = 0;
        while (ptr < end)
        {
            var read = U.Read(text[..end], ptr, profile, ref state);
            if (!state.TryCharge(0, ptr) || read.Status != Pcre2ReadStatus.Value)
            {
                return false;
            }

            bool word;
            if (profile.Utf)
            {
                var cp = read.Value;
                var cat = data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]].ParticularCategory;
                word = cat is >= 5 and <= 9 or 13 || cp == '_';
            }
            else
            {
                word = read.Value <= 255 && (data.LowByteTables[Pcre2DataAbi.TypeOffset + read.Value] & 0x10) != 0;
            }

            if (!word)
            {
                break;
            }

            ptr += read.Width;
            if (ptr - start > 128)
            {
                return false;
            }

            if (read.Value < 0x10000)
            {
                name[length++] = (char)read.Value;
            }
            else
            { var cp = read.Value - 0x10000; name[length++] = (char)(0xD800 + (cp >> 10)); name[length++] = (char)(0xDC00 + (cp & 1023)); }
        }
        return ptr != start;
    }
    private static int FindEnd<T, C, U>(Pcre2Program program, ReadOnlySpan<T> text, ref int ptr, int end, bool last, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        var depth = 0;
        var quote = false;
        for (; ptr < end; ptr++)
        {
            if (!state.TryCharge(1, ptr))
            {
                return (int)Pcre2ErrorCode.Matchlimit;
            }

            var c = U.Unit(text[ptr]);
            if (quote)
            { if (c == '\\' && ptr + 1 < end && U.Unit(text[ptr + 1]) == 'E') { quote = false; ptr++; } }
            else if (c == '}')
            { if (depth == 0) { return 0; } depth--; }
            else if (c == ':' && !last && depth == 0)
            {
                return 0;
            }
            else if (c == '$')
            { if (ptr + 1 < end && U.Unit(text[ptr + 1]) == '{') { depth++; ptr++; } }
            else if (c == '\\')
            {
                if (ptr + 1 < end && U.Unit(text[ptr + 1]) is 'L' or 'l' or 'U' or 'u')
                { ptr++; continue; }
                ptr++;
                var rc = Escape<T, C, U>(program, text, ref ptr, end, out _, out _, out var directive, ref state);
                if (rc != 0)
                { ptr--; return rc; }
                if (directive == 'Q')
                {
                    quote = true;
                }

                ptr--;
            }
        }
        return (int)Pcre2ErrorCode.Repmissingbrace;
    }
}
