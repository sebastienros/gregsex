#nullable enable
using GregsEx.Contracts;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

// Source CHECKMEMCPY accounting: once overflowed, subsequent copies only size.
internal ref struct Pcre2HostOutput<T> where T : unmanaged
{
    internal Span<T> Buffer;
    internal int Offset;
    internal long Extra;
    internal bool Overflowed;
    internal int Error;
    internal readonly bool Measure;
    internal readonly Pcre2HostLimits Limits;
    internal readonly long Total => Overflowed ? Buffer.Length + Extra : Offset;
    internal Pcre2HostOutput(Span<T> buffer, Pcre2SubstituteFlags flags, Pcre2HostLimits limits)
    { Buffer = buffer; Offset = 0; Extra = 0; Overflowed = false; Error = 0; Measure = (flags & Pcre2SubstituteFlags.OverflowLength) != 0; Limits = limits; }
    internal bool Accept(long length, ref RunState state)
    {
        if (Error != 0 || !state.TryCharge(length, Offset))
        {
            return false;
        }

        if (length > long.MaxValue - Total)
        { Error = (int)Pcre2ErrorCode.Toolargereplace; return false; }
        if (Total + length > Limits.MaxOutputUnits)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, Offset, state.Budget.WorkConsumed, Total + length); Error = (int)Pcre2ErrorCode.Heaplimit; return false; }
        if (Overflowed)
        { Extra += length; return true; }
        var left = Buffer.Length - Offset;
        if (length > left)
        {
            Error = Measure ? 0 : (int)Pcre2ErrorCode.Nomemory;
            if (Error != 0)
            {
                return false;
            }

            Overflowed = true;
            Extra = length - left;
            return true;
        }
        Offset += (int)length;
        return true;
    }
    internal bool Copy(scoped ReadOnlySpan<T> input, ref RunState state)
    {
        var old = Offset;
        var wasOverflow = Overflowed;
        if (!Accept(input.Length, ref state))
        {
            return false;
        }

        if (!wasOverflow && !Overflowed)
        {
            input.CopyTo(Buffer[old..]);
        }

        return true;
    }
    internal bool Inflate(long extra, ref RunState state)
    {
        if (extra < 0 || extra > long.MaxValue - Total)
        { Error = (int)Pcre2ErrorCode.Toolargereplace; return false; }
        if (Total + extra > Limits.MaxOutputUnits)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, Offset, state.Budget.WorkConsumed, Total + extra); Error = (int)Pcre2ErrorCode.Heaplimit; return false; }
        Extra += extra;
        return true;
    }
}
