#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Kernel;

// Pinned pcre2_xclass.c property rules and pcre2_internal.h UCD namespaces.
// Runtime classification never consults .NET Unicode tables or culture.
internal static class Pcre2CharacterOps
{
    internal static Pcre2UnicodeRecord Record(int cp, Pcre2DataView data) =>
        data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]];
    internal static int Major(int category) => category switch
    {
        <= 4 => 0,
        <= 9 => 1,
        <= 12 => 2,
        <= 15 => 3,
        <= 22 => 4,
        <= 26 => 5,
        _ => 6
    };
    internal static bool Word(int cp, Pcre2CompileFlags flags, Pcre2ExtraFlags extra, Pcre2DataView data) =>
        (flags & Pcre2CompileFlags.Ucp) != 0 && (extra & Pcre2ExtraFlags.AsciiBsw) == 0 ?
            UnicodeWord(Record(cp, data).ParticularCategory) : cp <= 255 && (data.LowByteTables[Pcre2DataAbi.TypeOffset + cp] & 0x10) != 0;
    private static bool UnicodeWord(int category) => Major(category) is 1 or 3 || category is 12 or 16;

    internal static bool Equal(int expected, int actual, Pcre2CompileFlags flags, Pcre2ExtraFlags extra,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if (expected == actual)
        {
            return true;
        }

        if ((flags & Pcre2CompileFlags.Caseless) == 0)
        {
            return false;
        }

        if ((extra & Pcre2ExtraFlags.CaselessRestrict) != 0 && (expected < 128) != (actual < 128))
        {
            return false;
        }

        if ((extra & Pcre2ExtraFlags.TurkishCasing) != 0)
        {
            if (expected is 0x49 or 0x131)
            {
                return actual is 0x49 or 0x131;
            }

            if (expected is 0x69 or 0x130)
            {
                return actual is 0x69 or 0x130;
            }
        }
        if ((flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0)
        {
            return expected <= 255 && actual <= 255 && data.LowByteTables[Pcre2DataAbi.LowerOffset + expected] ==
                data.LowByteTables[Pcre2DataAbi.LowerOffset + actual];
        }

        var record = Record(expected, data);
        if (expected < 128 && (record.CaseSetCount == 0 || (extra & Pcre2ExtraFlags.CaselessRestrict) != 0))
        {
            return actual <= 255 && data.LowByteTables[Pcre2DataAbi.LowerOffset + expected] ==
                data.LowByteTables[Pcre2DataAbi.LowerOffset + actual];
        }

        if (expected + record.OtherCaseDelta == actual)
        {
            return true;
        }

        foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (equivalent == actual)
            {
                return true;
            }
        }
        return false;
    }
    internal static bool LiteralUsesUnit(Pcre2Node node, Pcre2DataView data) => node.Kind == Pcre2NodeKind.Literal &&
        node.Operand < 128 && (node.Flags & Pcre2CompileFlags.Caseless) != 0 &&
        (node.ExtraFlags & Pcre2ExtraFlags.TurkishCasing) == 0 &&
        ((node.Flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0 ||
            Record(node.Operand, data).CaseSetCount == 0 || (node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) != 0);

    internal static bool Class(Pcre2Program program, Pcre2Node node, int cp, Pcre2DataView data,
        Span<int> scratch, ref Pcre2VmState machine, ref RunState state)
    {
        if (scratch.Length <= node.Operand)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed, node.Operand + 1); return false; }
        // Class nodes are validated in topological order. This bounded scratch
        // view avoids recursion for arbitrarily nested class algebra.
        for (var id = 0; id <= node.Operand; id++)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            var item = program.Classes[id];
            var value = item.Kind switch
            {
                Pcre2ClassKind.Intervals => Intervals(program.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count),
                    cp, node.Flags, node.ExtraFlags, data, ref machine, ref state),
                Pcre2ClassKind.Property => Property(cp, item.Property, data),
                Pcre2ClassKind.TableClass => Table(cp, item.TableClass, node.Flags, data),
                Pcre2ClassKind.Union => scratch[item.Left] != 0 || scratch[item.Right] != 0,
                Pcre2ClassKind.Intersection => scratch[item.Left] != 0 && scratch[item.Right] != 0,
                Pcre2ClassKind.Subtraction => scratch[item.Left] != 0 && scratch[item.Right] == 0,
                Pcre2ClassKind.SymmetricDifference => (scratch[item.Left] != 0) != (scratch[item.Right] != 0),
                Pcre2ClassKind.Complement => scratch[item.Left] == 0,
                _ => throw new InvalidOperationException("Unknown class operation.")
            };
            scratch[id] = value ? 1 : 0;
        }
        return scratch[node.Operand] != 0;
    }
    private static bool Table(int cp, int table, Pcre2CompileFlags flags, Pcre2DataView data)
    {
        if (cp > 255)
        {
            return false;
        }

        var original = (data.LowByteTables[Pcre2DataAbi.ClassOffset + table * 32 + cp / 8] & (1 << (cp % 8))) != 0;
        if (original || (flags & Pcre2CompileFlags.Caseless) == 0 || table is not (3 or 4))
        {
            return original;
        }

        var other = table == 3 ? 4 : 3;
        return (data.LowByteTables[Pcre2DataAbi.ClassOffset + other * 32 + cp / 8] & (1 << (cp % 8))) != 0;
    }
    private static bool Intervals(ReadOnlySpan<Pcre2Interval> intervals, int cp, Pcre2CompileFlags flags,
        Pcre2ExtraFlags extra, Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
    {
        if (Contains(intervals, cp, ref machine, ref state))
        {
            return true;
        }

        if ((flags & Pcre2CompileFlags.Caseless) == 0)
        {
            return false;
        }

        if ((extra & Pcre2ExtraFlags.TurkishCasing) != 0 && cp is 0x49 or 0x69 or 0x130 or 0x131)
        {
            var partner = cp switch { 0x49 => 0x131, 0x131 => 0x49, 0x69 => 0x130, _ => 0x69 };
            return Contains(intervals, partner, ref machine, ref state);
        }
        if ((flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0)
        {
            return cp <= 255 && Contains(intervals, data.LowByteTables[Pcre2DataAbi.FlipOffset + cp], ref machine, ref state);
        }

        var record = Record(cp, data);
        var other = cp + record.OtherCaseDelta;
        if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (other < 128)) && Contains(intervals, other, ref machine, ref state))
        {
            return true;
        }

        foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (equivalent < 128)) &&
                Contains(intervals, equivalent, ref machine, ref state))
            {
                return true;
            }
        }
        return false;
    }
    private static bool Contains(ReadOnlySpan<Pcre2Interval> intervals, int cp, ref Pcre2VmState machine, ref RunState state)
    {
        int low = 0, high = intervals.Length - 1;
        while (low <= high)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            var mid = low + (high - low) / 2;
            var item = intervals[mid];
            if (cp < item.First)
            {
                high = mid - 1;
            }
            else if (cp > item.Last)
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
    internal static bool Property(int cp, Pcre2PropertyKey property, Pcre2DataView data)
    {
        var record = Record(cp, data);
        int category = record.ParticularCategory, major = Major(category);
        return property.Kind switch
        {
            Pcre2PropertyKind.MajorCategory => major == property.Id,
            Pcre2PropertyKind.ParticularCategory => category == property.Id,
            Pcre2PropertyKind.Script => record.Script == property.Id,
            Pcre2PropertyKind.ScriptExtensions => record.Script == property.Id ||
                (data.ScriptSets[record.ScriptSetOffset + property.Id / 32] & (1u << (property.Id % 32))) != 0,
            Pcre2PropertyKind.Binary => (data.BooleanSets[record.BooleanSetOffset + property.Id / 32] & (1u << (property.Id % 32))) != 0,
            Pcre2PropertyKind.BidiClass => record.BidiClass == property.Id,
            Pcre2PropertyKind.Special => property.Id switch
            {
                0 => category is 5 or 8 or 9,
                5 => major is 1 or 3,
                6 or 7 => major == 6 || cp is 9 or 10 or 11 or 12 or 13 or 0x85 or 0x180E,
                8 => UnicodeWord(category),
                10 => cp < 0xA0 ? cp is 0x24 or 0x40 or 0x60 : cp is not (>= 0xD800 and <= 0xDFFF),
                13 => true,
                14 => major != 6 && (major != 0 || category == 1 && cp is not (0x061C or 0x180E or >= 0x2066 and <= 0x2069)),
                15 => category is not (27 or 28) && (major != 0 || category == 1 && cp is not (0x061C or >= 0x2066 and <= 0x2069)),
                16 => major == 4 || cp < 128 && major == 5,
                17 => cp is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x46 or >= 0x61 and <= 0x66 or
                    >= 0xFF10 and <= 0xFF19 or >= 0xFF21 and <= 0xFF26 or >= 0xFF41 and <= 0xFF46,
                _ => throw new InvalidOperationException("Unknown special property.")
            },
            _ => throw new InvalidOperationException("Unknown property kind.")
        };
    }
}
