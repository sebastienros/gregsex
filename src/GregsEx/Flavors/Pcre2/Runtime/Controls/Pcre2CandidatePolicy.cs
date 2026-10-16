using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;

namespace GregsEx.Flavors.Pcre2.Runtime.Controls;

// Source match.c7397–7787 candidate admission. This policy consumes verified
// compiler hints; it does not infer study results from syntax or classes.
internal struct Pcre2CandidatePolicy
{
    private int requiredPosition;

    public Pcre2CandidatePolicy() => requiredPosition = -1;

    internal bool Admit<TUnit, TReader, TUnits>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, bool anchored, bool firstline,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
        where TUnits : struct, IPcre2Units<TUnit>
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.CandidateStart))
        {
            return false;
        }

        if (!program.Profile.EffectiveOptimization.StartOptimize)
        {
            return true;
        }

        var hints = program.SearchHints ?? new Pcre2SearchHints(0, 0, false);
        int candidate = machine.CandidateStart, end = machine.VisibleEnd, scanEnd = end;
        if (firstline)
        {
            var p = candidate;
            while (p < end && !AtNewline<TUnit, TReader>(program, input, p, end, ref state))
            {
                var read = TReader.Read(input, p, end, false, program.Profile, ref state);
                if (read.Status != Pcre2ReadStatus.Value || Denied(ref machine, ref state))
                {
                    return false;
                }

                p += read.Width;
            }
            scanEnd = p;
        }
        var first = hints.FirstCodeUnit;
        var firstOther = first < 0 ? -1 : Other(first, hints.FirstCodeUnitCaseless, program.Profile, data);
        var bitmap = !program.StartBitmap.IsEmpty && first < 0 && !hints.StartLine;
        if (anchored)
        {
            if (first >= 0 || bitmap)
            {
                if (candidate >= scanEnd || !Inspect<TUnit, TUnits>(input, candidate, ref machine, ref state, out var unit))
                {
                    return false;
                }

                if (first >= 0 ? unit != first && unit != firstOther : !InBitmap(program.StartBitmap, unit))
                {
                    return false;
                }
            }
        }
        else if (first >= 0 || bitmap)
        {
            if (first >= 0 && candidate < scanEnd && state.Budget.IsUnconstrained(scanEnd - candidate + 4))
            {
                var idx = FindCodeUnit(input.Slice(candidate, scanEnd - candidate), first, firstOther);
                var charges = idx >= 0 ? idx + 1 : scanEnd - candidate;
                if (!Pcre2Storage.Charge(ref machine, ref state, charges, candidate))
                {
                    return false;
                }

                candidate = idx >= 0 ? candidate + idx : scanEnd;
            }
            else
            {
                while (candidate < scanEnd)
                {
                    if (!Inspect<TUnit, TUnits>(input, candidate, ref machine, ref state, out var unit))
                    {
                        return false;
                    }

                    if (first >= 0 ? unit == first || unit == firstOther : InBitmap(program.StartBitmap, unit))
                    {
                        break;
                    }

                    candidate++;
                }
            }
            machine.CandidateStart = candidate;
            if ((flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) == 0 && candidate >= end)
            {
                return false;
            }
        }
        else if (hints.StartLine && candidate > machine.SearchStart)
        {
            if (program.Profile.Newline == Pcre2Newline.Lf && candidate < scanEnd &&
                state.Budget.IsUnconstrained((scanEnd - candidate) * 3L + 16))
            {
                var idx = FindCodeUnit(input.Slice(candidate - 1, scanEnd - candidate), 10, 10);
                var steps = idx >= 0 ? idx : scanEnd - candidate;
                if (!state.TryChargeRun(candidate, steps * 2 + 1))
                {
                    return false;
                }

                candidate = idx >= 0 ? candidate + idx : scanEnd;
            }
            else
            {
                while (candidate < scanEnd && !AfterNewline<TUnit, TReader>(program, input, candidate, end, ref state))
                {
                    var read = TReader.Read(input, candidate, scanEnd, false, program.Profile, ref state);
                    if (read.Status != Pcre2ReadStatus.Value || Denied(ref machine, ref state))
                    {
                        return false;
                    }

                    candidate += read.Width;
                }
            }
            // Source7649 skips the LF half even when HASCRORLF is present.
            if (candidate > 0 && candidate < scanEnd && program.Profile.Newline is Pcre2Newline.Any or Pcre2Newline.Anycrlf)
            {
                if (!Inspect<TUnit, TUnits>(input, candidate - 1, ref machine, ref state, out var previous) ||
                    !Inspect<TUnit, TUnits>(input, candidate, ref machine, ref state, out var current))
                {
                    return false;
                }

                if (previous == 13 && current == 10)
                {
                    candidate++;
                }
            }
            machine.CandidateStart = candidate;
        }
        if (Denied(ref machine, ref state))
        {
            return false;
        }

        if ((flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) != 0)
        {
            return true;
        }

        if (end - candidate < hints.MinimumLength)
        {
            return false;
        }

        int required = hints.RequiredCodeUnit, start = candidate + (first >= 0 ? 1 : 0);
        var maximum = program.Profile.Width == Pcre2CodeUnitWidth.Eight ? 5000 : 2000;
        if (required < 0 || start <= requiredPosition || end - candidate >= (anchored ? maximum : maximum * 1000))
        {
            return true;
        }

        var other = Other(required, hints.RequiredCodeUnitCaseless, program.Profile, data);
        if (start < end && state.Budget.IsUnconstrained(end - start + 4))
        {
            var idx = FindCodeUnit(input.Slice(start, end - start), required, other);
            var charges = idx >= 0 ? idx + 1 : end - start;
            if (!Pcre2Storage.Charge(ref machine, ref state, charges, start))
            {
                return false;
            }

            if (idx >= 0)
            { requiredPosition = start + idx; return true; }
            return false;
        }
        for (var p = start; p < end; p++)
        {
            if (!Inspect<TUnit, TUnits>(input, p, ref machine, ref state, out var unit))
            {
                return false;
            }

            if (unit == required || unit == other)
            { requiredPosition = p; return true; }
        }
        return false;
    }

    private static int FindCodeUnit<TUnit>(ReadOnlySpan<TUnit> span, int a, int b) where TUnit : unmanaged
    {
        if (typeof(TUnit) == typeof(char))
        {
            var chars = System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, char>(span);
            return a == b ? chars.IndexOf((char)a) : chars.IndexOfAny((char)a, (char)b);
        }
        if (typeof(TUnit) == typeof(byte))
        {
            var bytes = System.Runtime.InteropServices.MemoryMarshal.Cast<TUnit, byte>(span);
            return a == b ? bytes.IndexOf((byte)a) : bytes.IndexOfAny((byte)a, (byte)b);
        }
        return -1;
    }

    private static bool Denied(ref Pcre2VmState machine, ref RunState state) =>
        machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;

    private static bool Inspect<TUnit, TUnits>(ReadOnlySpan<TUnit> input, int offset,
        ref Pcre2VmState machine, ref RunState state, out int unit)
        where TUnit : unmanaged where TUnits : struct, IPcre2Units<TUnit>
    {
        unit = 0;
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, offset))
        {
            return false;
        }

        unit = TUnits.Unit(input, offset);
        return true;
    }

    private static bool InBitmap(ReadOnlySpan<byte> bitmap, int unit)
    {
        unit = Math.Min(unit, 255);
        return (bitmap[unit / 8] & (1 << (unit & 7))) != 0;
    }

    private static int Other(int unit, bool caseless, Pcre2Profile profile, Pcre2DataView data)
    {
        if (!caseless)
        {
            return unit;
        }

        if (unit > 127 && (profile.Width == Pcre2CodeUnitWidth.Eight ? profile.Ucp && !profile.Utf : profile.Utf || profile.Ucp))
        {
            return unit + data.Records[data.Stage2[data.Stage1[unit / 128] * 128 + unit % 128]].OtherCaseDelta;
        }

        return unit <= 255 ? data.LowByteTables[256 + unit] : unit;
    }

    internal static bool AtNewline<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int position, int end, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        if (position >= end)
        {
            return false;
        }

        var read = TReader.Read(input, position, end, false, program.Profile, ref state);
        if (read.Status != Pcre2ReadStatus.Value)
        {
            return false;
        }

        if (program.Profile.Newline == Pcre2Newline.Crlf)
        {
            if (read.Value != 13 || position + read.Width == end)
            {
                return false;
            }

            var next = TReader.Read(input, position + read.Width, end, false, program.Profile, ref state);
            return next.Status == Pcre2ReadStatus.Value && next.Value == 10;
        }
        return program.Profile.Newline switch
        {
            Pcre2Newline.Cr => read.Value == 13,
            Pcre2Newline.Lf => read.Value == 10,
            Pcre2Newline.Nul => read.Value == 0,
            Pcre2Newline.Anycrlf => read.Value is 10 or 13,
            Pcre2Newline.Any => read.Value is 10 or 13 or 11 or 12 or 0x85 or 0x2028 or 0x2029,
            _ => false
        };
    }

    private static bool AfterNewline<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int position, int end, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var before = TReader.ReadPrevious(input, position, program.Profile, ref state);
        if (before.Status != Pcre2ReadStatus.Value)
        {
            return false;
        }

        if (program.Profile.Newline == Pcre2Newline.Crlf)
        {
            if (before.Value != 10 || position <= before.Width)
            {
                return false;
            }

            var earlier = TReader.ReadPrevious(input, position - before.Width, program.Profile, ref state);
            return earlier.Status == Pcre2ReadStatus.Value && earlier.Value == 13;
        }
        return AtNewline<TUnit, TReader>(program, input, position - before.Width, end, ref state);
    }
}
