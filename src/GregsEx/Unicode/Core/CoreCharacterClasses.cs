#nullable enable
using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Unicode.Core;

internal enum AsciiClass { Digit, Word, Space }

internal static class CoreCharacterClasses
{
    // The result boolean is separate from admission success. Every interval comparison polls.
    internal static bool TryContains(CharacterClass characterClass, int value,
        ProfileDescriptor profile, ref RunState state, int offset, out bool contains)
    {
        contains = false;
        if (!state.TryCharge(0, offset))
        {
            return false;
        }

        if (!profile.Contains(value))
        {
            return true; // Complements never add illegal alphabet elements.
        }

        var found = false;
        foreach (var range in characterClass.Ranges)
        {
            if (!state.TryCharge(1, offset))
            {
                return false;
            }

            if (value < range.First)
            {
                break;
            }

            if (value <= range.Last)
            { found = true; break; }
        }
        contains = characterClass.Negated ? !found : found;
        return true;
    }

    // GregsEx-ASCII-1: ordinal numeric comparisons, independent of culture/runtime categories.
    internal static bool IsAsciiDigit(int value) => value is >= '0' and <= '9';
    internal static bool IsAsciiWord(int value) => IsAsciiDigit(value) ||
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    internal static bool IsAsciiSpace(int value) => value is >= 9 and <= 13 or 32;

    internal static bool TryCreateAscii(AsciiClass kind, bool negated, ProfileDescriptor profile,
        ref CompileBudget budget, SourceRange source, out CharacterClass? result, out RegexDiagnostic diagnostic)
    {
        // Fixed maximum, allocated once outside loops; retained copy is admitted by TryCreate.
        Span<CharacterRange> ranges = stackalloc CharacterRange[4];
        int count;
        switch (kind)
        {
            case AsciiClass.Digit:
                ranges[0] = new('0', '9');
                count = 1;
                break;
            case AsciiClass.Word:
                ranges[0] = new('0', '9');
                ranges[1] = new('A', 'Z');
                ranges[2] = new('_', '_');
                ranges[3] = new('a', 'z');
                count = 4;
                break;
            case AsciiClass.Space:
                ranges[0] = new(9, 13);
                ranges[1] = new(32, 32);
                count = 2;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        return CharacterClass.TryCreate(ranges[..count], negated, profile, ref budget, source, out result, out diagnostic);
    }

    // Algebra operates on sorted, nonoverlapping base intervals (not the Negated flag).
    // Caller owns/admitted destination storage; no rent/grow/copy occurs here. Failure sets
    // written=0, may leave partial destination data, and must not publish that data as a class.
    // ClassLimitExceeded also identifies insufficient caller interval capacity.
    internal static bool TryUnion(ReadOnlySpan<CharacterRange> left, ReadOnlySpan<CharacterRange> right,
        Span<CharacterRange> destination, ProfileDescriptor profile, ref CompileBudget budget,
        SourceRange source, out int written, out RegexDiagnostic diagnostic)
    {
        written = 0;
        if (!Admit(left, right, destination, profile, ref budget, source, out diagnostic))
        {
            return false;
        }

        int i = 0, j = 0;
        while (i < left.Length || j < right.Length)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            { written = 0; return false; }
            var range = j == right.Length || (i < left.Length && left[i].First <= right[j].First)
                ? left[i++] : right[j++];
            if (!Emit(range, destination, ref written, ref budget, source, out diagnostic))
            { written = 0; return false; }
        }
        return true;
    }

    internal static bool TryIntersect(ReadOnlySpan<CharacterRange> left, ReadOnlySpan<CharacterRange> right,
        Span<CharacterRange> destination, ProfileDescriptor profile, ref CompileBudget budget,
        SourceRange source, out int written, out RegexDiagnostic diagnostic)
    {
        written = 0;
        if (!Admit(left, right, destination, profile, ref budget, source, out diagnostic))
        {
            return false;
        }

        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            { written = 0; return false; }
            var first = Math.Max(left[i].First, right[j].First);
            var last = Math.Min(left[i].Last, right[j].Last);
            if (first <= last && !Emit(new(first, last), destination, ref written, ref budget, source, out diagnostic))
            { written = 0; return false; }
            if (left[i].Last <= right[j].Last)
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        return true;
    }

    internal static bool TryComplement(ReadOnlySpan<CharacterRange> ranges, Span<CharacterRange> destination,
        ProfileDescriptor profile, ref CompileBudget budget, SourceRange source,
        out int written, out RegexDiagnostic diagnostic)
    {
        written = 0;
        if (!Admit(ranges, [], destination, profile, ref budget, source, out diagnostic))
        {
            return false;
        }

        var next = 0;
        var maximum = Maximum(profile);
        foreach (var range in ranges)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            { written = 0; return false; }
            if (next < range.First && !EmitLegal(next, range.First - 1, destination, profile,
                ref written, ref budget, source, out diagnostic))
            { written = 0; return false; }
            next = range.Last + 1; // Maximum is at most U+10FFFF, so this cannot overflow.
        }
        if (next <= maximum && !EmitLegal(next, maximum, destination, profile,
            ref written, ref budget, source, out diagnostic))
        { written = 0; return false; }
        return true;
    }

    private static bool EmitLegal(int first, int last, Span<CharacterRange> destination,
        ProfileDescriptor profile, ref int written, ref CompileBudget budget, SourceRange source,
        out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        if (profile.TextModel != TextModel.UnicodeScalars || last < 0xD800 || first > 0xDFFF)
        {
            return Emit(new(first, last), destination, ref written, ref budget, source, out diagnostic);
        }

        if (first < 0xD800 && !Emit(new(first, 0xD7FF), destination, ref written, ref budget, source, out diagnostic))
        {
            return false;
        }

        return last <= 0xDFFF || Emit(new(0xE000, last), destination, ref written, ref budget, source, out diagnostic);
    }

    private static bool Emit(CharacterRange range, Span<CharacterRange> destination, ref int written,
        ref CompileBudget budget, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        // Comparing the previous interval and writing an output are separate charged events.
        var merge = false;
        if (written != 0)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            merge = range.First <= destination[written - 1].Last + 1;
        }
        if (!budget.TryCharge(1, source, out diagnostic))
        {
            return false;
        }

        if (merge)
        {
            destination[written - 1] = new(destination[written - 1].First, Math.Max(destination[written - 1].Last, range.Last));
            return true;
        }
        if (written >= destination.Length || written >= budget.Limits.MaxClassRanges)
        {
            diagnostic = new(RegexDiagnosticCode.ClassLimitExceeded, source.Offset, source.Length, source.Unit);
            return false;
        }
        destination[written++] = range;
        return true;
    }

    private static bool Admit(ReadOnlySpan<CharacterRange> left, ReadOnlySpan<CharacterRange> right,
        Span<CharacterRange> destination, ProfileDescriptor profile, ref CompileBudget budget,
        SourceRange source, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (left.Overlaps(destination) || right.Overlaps(destination))
        {
            throw new ArgumentException("Interval output must not overlap either input.", nameof(destination));
        }

        return ValidateRanges(left, profile, ref budget, source, out diagnostic) &&
            ValidateRanges(right, profile, ref budget, source, out diagnostic);
    }

    private static bool ValidateRanges(ReadOnlySpan<CharacterRange> ranges, ProfileDescriptor profile,
        ref CompileBudget budget, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        var previous = -1;
        foreach (var range in ranges)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            if (range.First <= previous || range.First < 0 || range.Last < range.First || range.Last > Maximum(profile))
            {
                diagnostic = new(RegexDiagnosticCode.InvalidProgram, source.Offset, source.Length, source.Unit);
                return false;
            }
            previous = range.Last;
        }
        return true;
    }

    private static int Maximum(ProfileDescriptor profile) => profile.TextModel switch
    {
        TextModel.RawBytes => 255,
        TextModel.Utf16CodeUnits => 0xFFFF,
        _ => 0x10FFFF
    };
}
