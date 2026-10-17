#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Tables;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal sealed partial class Pcre2Host
{
    private struct CaseState
    {
        internal int Mode; // 0 none, 1 lower, 2 upper, 3 title, 4 reverse-title.
        internal bool Single;
        internal int Start;
        internal long Extra;
    }
    private static int Encode<T, C, U>(int cp, bool utf, Span<T> target)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        if (!utf)
        { target[0] = U.From(cp); return 1; }
        if (U.Width == Pcre2CodeUnitWidth.Sixteen)
        {
            if (cp < 0x10000)
            { target[0] = U.From(cp); return 1; }
            cp -= 0x10000;
            target[0] = U.From(0xD800 + (cp >> 10));
            target[1] = U.From(0xDC00 + (cp & 1023));
            return 2;
        }
        if (cp < 128)
        { target[0] = U.From(cp); return 1; }
        if (cp < 0x800)
        { target[0] = U.From(0xC0 | (cp >> 6)); target[1] = U.From(0x80 | (cp & 63)); return 2; }
        if (cp < 0x10000)
        { target[0] = U.From(0xE0 | (cp >> 12)); target[1] = U.From(0x80 | ((cp >> 6) & 63)); target[2] = U.From(0x80 | (cp & 63)); return 3; }
        target[0] = U.From(0xF0 | (cp >> 18));
        target[1] = U.From(0x80 | ((cp >> 12) & 63));
        target[2] = U.From(0x80 | ((cp >> 6) & 63));
        target[3] = U.From(0x80 | (cp & 63));
        return 4;
    }
    private static int MapCase(int cp, bool upper, Pcre2Profile profile, Pcre2DataView data)
    {
        if ((profile.Utf || profile.Ucp) && cp >= 128)
        {
            var cases = data.OutputCases;
            int lo = 0, hi = cases.Length;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                var entry = cases[mid];
                if (entry.CodePoint < cp)
                {
                    lo = mid + 1;
                }
                else if (entry.CodePoint > cp)
                {
                    hi = mid;
                }
                else
                {
                    return upper ? entry.Upper : entry.Lower;
                }
            }
            return cp;
        }
        if (cp <= 255)
        {
            var tables = data.LowByteTables;
            var offset = Pcre2DataAbi.ClassOffset + (upper ? 96 : 128);
            if ((tables[offset + cp / 8] & (1 << (cp % 8))) == 0)
            {
                return tables[Pcre2DataAbi.FlipOffset + cp];
            }
        }
        return cp;
    }
    private static bool CopyCase<T, C, U>(scoped ReadOnlySpan<T> input, Pcre2Profile profile, C? callbacks,
        ref CaseState casing, ref Pcre2HostOutput<T> output, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        if (casing.Mode == 0 || U.HasCase(callbacks))
        {
            return output.Copy(input, ref state);
        }

        if (input.IsEmpty)
        {
            return true;
        }

        bool firstUpper = casing.Mode is 2 or 3, restUpper = casing.Mode is 2 or 4;
        if (casing.Mode == 3)
        {
            casing.Mode = 1;
        }

        if (casing.Mode == 4)
        {
            casing.Mode = 2;
        }

        var single = casing.Single;
        if (single)
        {
            casing.Mode = 0;
        }

        var data = Pcre2Data.Instance.Bind(profile.CharacterTables);
        int length = 0, written = 0, left = output.Overflowed ? 0 : output.Buffer.Length - output.Offset;
        var stopped = false;
        Span<T> encoded = stackalloc T[6];
        for (var offset = 0; offset < input.Length;)
        {
            var read = U.Read(input, offset, profile, ref state);
            if (!state.TryCharge(0, offset) || read.Status != Pcre2ReadStatus.Value)
            {
                return false;
            }

            offset += read.Width;
            var cp = MapCase(read.Value, firstUpper, profile, data);
            firstUpper = restUpper;
            var n = Encode<T, C, U>(cp, profile.Utf, encoded);
            if (n > int.MaxValue - length)
            { output.Error = (int)Pcre2ErrorCode.Toolargereplace; return false; }
            length += n;
            if (!stopped && n <= left - written)
            { encoded[..n].CopyTo(output.Buffer[(output.Offset + written)..]); written += n; }
            else
            {
                stopped = true;
            }

            if (single)
            {
                var remaining = input.Length - offset;
                if (remaining > int.MaxValue - length)
                { output.Error = (int)Pcre2ErrorCode.Toolargereplace; return false; }
                length += remaining;
                if (!stopped && remaining <= left - written)
                {
                    input[offset..].CopyTo(output.Buffer[(output.Offset + written)..]);
                }

                if (!state.TryCharge(remaining, offset))
                {
                    return false;
                }

                break;
            }
        }
        return output.Accept(length, ref state);
    }
    private static bool FlushCase<T, C, U>(Pcre2Profile profile, C? callbacks, ref CaseState casing,
        ref int callouts, int maxCallouts, ref Pcre2HostOutput<T> output, ref RunState state)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        if (!U.HasCase(callbacks) || casing.Mode == 0)
        {
            return true;
        }

        var outstanding = output.Offset - casing.Start + output.Extra - casing.Extra;
        if (outstanding == 0)
        {
            return true;
        }

        if (output.Overflowed)
        {
            return output.Inflate((outstanding >> 3) + 10, ref state);
        }

        var length = output.Offset - casing.Start;
        var region = output.Buffer[casing.Start..];
        output.Offset = casing.Start;
        int required;
        if (!casing.Single && casing.Mode != 4)
        {
            if (!BeginCallback(ref callouts, maxCallouts, casing.Start, ref state))
            {
                return false;
            }

            required = U.Case(callbacks!, region[..length], region, (Pcre2SubstituteCase)casing.Mode);
            if (!state.TryCharge(0, casing.Start))
            {
                return false;
            }

            if (casing.Mode == 3)
            {
                casing.Mode = 1;
            }
        }
        else
        {
            var read = U.Read(region[..length], 0, profile, ref state);
            if (!state.TryCharge(0, casing.Start) || read.Status != Pcre2ReadStatus.Value)
            {
                return false;
            }

            Span<T> first = stackalloc T[6];
            region[..read.Width].CopyTo(first);
            int restLength = length - read.Width, restStart = read.Width, firstCap = read.Width;
            var maxFirst = region.Length - restLength;
            var firstOverflow = false;
            while (true)
            {
                if (!BeginCallback(ref callouts, maxCallouts, casing.Start, ref state))
                {
                    return false;
                }

                required = U.Case(callbacks!, first[..read.Width], region[..firstCap], casing.Mode == 4 ? Pcre2SubstituteCase.Lower : (Pcre2SubstituteCase)casing.Mode);
                if (!state.TryCharge(0, casing.Start))
                {
                    return false;
                }

                if (required < 0)
                {
                    break;
                }

                if (required <= firstCap)
                {
                    break;
                }

                if (required > maxFirst)
                { firstOverflow = true; break; }
                region.Slice(restStart, restLength).CopyTo(region[required..]);
                restStart = required;
                firstCap = required;
            }
            if (required >= 0)
            {
                var restRequired = restLength;
                if (casing.Mode == 4)
                {
                    if (!BeginCallback(ref callouts, maxCallouts, casing.Start, ref state))
                    {
                        return false;
                    }

                    restRequired = U.Case(callbacks!, region.Slice(restStart, restLength), firstOverflow ? Span<T>.Empty : region[required..], Pcre2SubstituteCase.Upper);
                    if (!state.TryCharge(0, casing.Start))
                    {
                        return false;
                    }

                    if (firstOverflow && restRequired >= 0)
                    {
                        restRequired = Math.Max(restRequired, restLength);
                    }

                    casing.Mode = 2;
                }
                else
                { if (!firstOverflow) { region.Slice(restStart, restLength).CopyTo(region[required..]); } casing.Mode = 0; }
                if (restRequired < 0)
                {
                    required = -1;
                }
                else if ((long)required + restRequired > int.MaxValue)
                { output.Error = (int)Pcre2ErrorCode.Toolargereplace; return false; }
                else
                {
                    required += restRequired;
                }
            }
        }
        if (required < 0)
        { output.Error = (int)Pcre2ErrorCode.Replacecase; return false; }
        return output.Accept(required, ref state);
    }
}
