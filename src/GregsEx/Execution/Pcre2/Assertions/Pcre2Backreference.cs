using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Assertions;

internal readonly partial struct Pcre2AssertionOps
{
    private static Pcre2Step Backreference<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var node = program.Nodes[machine.Node];
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var set = Select(program, program.References[node.Operand], storage, ref machine, ref state, out var capture);
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        if (!set)
        {
            if ((program.Profile.Flags & Pcre2CompileFlags.MatchUnsetBackref) != 0)
            { Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state); return Continued(machine.Node); }
            return ReferenceFailure(program, flags, false, ref machine, ref state);
        }
        if (capture.Start < 0 || capture.End < capture.Start || capture.End > input.Length)
        {
            throw new InvalidOperationException("Invalid backreference capture coordinates.");
        }

        int left = capture.Start, right = machine.Position;
        var caseless = (node.Flags & Pcre2CompileFlags.Caseless) != 0;
        var unicode = caseless && (node.Flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) != 0;
        // match_ref checks native length before memcmp only outside partial mode.
        if (!caseless && (flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) == 0 && capture.End - left > machine.VisibleEnd - right)
        {
            return ReferenceFailure(program, flags, true, ref machine, ref state);
        }

        while (left < capture.End)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, right))
            {
                return Resource();
            }

            if (right >= machine.VisibleEnd)
            {
                return ReferenceFailure(program, flags, true, ref machine, ref state);
            }

            var expected = TReader.Read(input, left, capture.End, !unicode, program.Profile, ref state);
            var actual = TReader.Read(input, right, machine.VisibleEnd, !unicode, program.Profile, ref state);
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (actual.Status == Pcre2ReadStatus.End)
            {
                return ReferenceFailure(program, flags, true, ref machine, ref state);
            }

            if (expected.Status != Pcre2ReadStatus.Value || actual.Status != Pcre2ReadStatus.Value)
            {
                return ReferenceFailure(program, flags, false, ref machine, ref state);
            }

            var equal = !caseless ? expected.Value == actual.Value : unicode ?
                ReferenceEqual(expected.Value, actual.Value, node.ExtraFlags, data, ref machine, ref state) :
                Low(expected.Value, data) == Low(actual.Value, data);
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (!equal)
            {
                return ReferenceFailure(program, flags, false, ref machine, ref state);
            }

            left += expected.Width;
            right += actual.Width;
        }
        machine.Position = right;
        Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
        return Continued(machine.Node);
    }
    private static int Low(int unit, Pcre2DataView data) => unit <= 255 ? data.LowByteTables[Pcre2DataAbi.LowerOffset + unit] : unit;
    // match_ref Unicode comparisons do NOT use CHARI's imported ASCII table
    // shortcut. Native raw16 units above255 are unchanged outside UTF/UCP.
    private static bool ReferenceEqual(int expected, int actual, Pcre2ExtraFlags extra,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
    {
        if (expected == actual)
        {
            return true;
        }

        var restrict = (extra & Pcre2ExtraFlags.CaselessRestrict) != 0;
        if (!restrict && (extra & Pcre2ExtraFlags.TurkishCasing) != 0 && expected is 0x49 or 0x69 or 0x130 or 0x131)
        {
            return expected is 0x49 or 0x131 ? actual is 0x49 or 0x131 : actual is 0x69 or 0x130;
        }

        var record = Pcre2CharacterOps.Record(expected, data);
        if (expected + record.OtherCaseDelta == actual)
        {
            return true;
        }

        var cases = data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount);
        if (restrict && !cases.IsEmpty && cases[0] < 128)
        {
            return false;
        }

        foreach (var cp in cases)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (cp == actual)
            {
                return true;
            }
        }
        return false;
    }
    private static Pcre2Step ReferenceFailure(Pcre2Program program, Pcre2MatchFlags flags, bool end,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (end)
        {
            machine.Position = machine.VisibleEnd;
        }

        if (machine.Position >= machine.VisibleEnd && Pcre2Kernel.RememberPartial(program, flags, ref machine, ref state))
        {
            return new(Pcre2StepKind.Partial);
        }

        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        Pcre2Kernel.Fail(ref state);
        return Continued(machine.Node);
    }
}
