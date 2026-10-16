using GregsEx.Contracts;

namespace GregsEx.IR;

internal readonly record struct SourceRange(int Offset, int Length, RegexOffsetUnit Unit);
internal readonly record struct CharacterRange(int First, int Last);

// Intervals are immutable and sorted; complements are relative to ProfileDescriptor.Contains.
internal sealed class CharacterClass
{
    private readonly CharacterRange[] _ranges;
    private readonly ulong _b0, _b1, _b2, _b3;
    private readonly int _minFirst;
    private readonly int _maxLast;
    private readonly ulong[]? _bmp;
    internal ReadOnlySpan<CharacterRange> Ranges => _ranges;
    internal bool Negated { get; }
    internal long RetainedBytes => 64L + 8L * _ranges.Length;

    private CharacterClass(ReadOnlySpan<CharacterRange> ranges, bool negated, ProfileDescriptor profile)
    {
        _ranges = ranges.ToArray(); // Admission has already charged work and memory.
        Negated = negated;
        if (_ranges.Length == 0)
        {
            _minFirst = int.MaxValue;
            _maxLast = -1;
            if (negated)
            {
                _b0 = _b1 = _b2 = _b3 = ulong.MaxValue;
            }

            return;
        }
        _minFirst = _ranges[0].First;
        _maxLast = _ranges[^1].Last;

        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        foreach (var range in _ranges)
        {
            if (range.First > 255)
            {
                break;
            }

            var last = Math.Min(range.Last, 255);
            for (var c = range.First; c <= last; c++)
            {
                var w = c >> 6;
                var bit = 1UL << (c & 63);
                if (w == 0)
                {
                    b0 |= bit;
                }
                else if (w == 1)
                {
                    b1 |= bit;
                }
                else if (w == 2)
                {
                    b2 |= bit;
                }
                else
                {
                    b3 |= bit;
                }
            }
        }
        if (negated)
        {
            b0 = ~b0;
            b1 = ~b1;
            b2 = ~b2;
            b3 = ~b3;
        }
        _b0 = b0;
        _b1 = b1;
        _b2 = b2;
        _b3 = b3;

        if (_maxLast >= 256 && _ranges.Length >= 8)
        {
            var bmp = new ulong[1024];
            foreach (var range in _ranges)
            {
                if (range.First > 0xFFFF)
                {
                    break;
                }

                var last = Math.Min(range.Last, 0xFFFF);
                var startWord = range.First >> 6;
                var endWord = last >> 6;
                var startMask = ulong.MaxValue << (range.First & 63);
                var endMask = ulong.MaxValue >> (63 - (last & 63));
                if (startWord == endWord)
                {
                    bmp[startWord] |= startMask & endMask;
                }
                else
                {
                    bmp[startWord] |= startMask;
                    for (var w = startWord + 1; w < endWord; w++)
                    {
                        bmp[w] = ulong.MaxValue;
                    }

                    bmp[endWord] |= endMask;
                }
            }
            if (negated)
            {
                for (var i = 0; i < bmp.Length; i++)
                {
                    bmp[i] = ~bmp[i];
                }

                if (profile.TextModel == TextModel.UnicodeScalars)
                {
                    Array.Clear(bmp, 0xD800 >> 6, (0xE000 - 0xD800) >> 6);
                }
            }
            _bmp = bmp;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool Contains(int value, ProfileDescriptor profile)
    {
        if ((uint)value < 256u)
        {
            var mask = (value >> 6) switch
            {
                0 => _b0,
                1 => _b1,
                2 => _b2,
                _ => _b3
            };
            return (mask & (1UL << (value & 63))) != 0;
        }
        return ContainsNonLatin1(value, profile);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool MatchesBmpChar(char c)
    {
        uint value = c;
        if (value < 256u)
        {
            var mask = (value >> 6) switch
            {
                0 => _b0,
                1 => _b1,
                2 => _b2,
                _ => _b3
            };
            return (mask & (1UL << ((int)value & 63))) != 0;
        }
        if ((int)value < _minFirst || (int)value > _maxLast)
        {
            return Negated;
        }

        if (_bmp is { } bmp)
        {
            return (bmp[value >> 6] & (1UL << ((int)value & 63))) != 0;
        }

        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _ranges[mid];
            if (value < r.First)
            {
                hi = mid - 1;
            }
            else if (value > r.Last)
            {
                lo = mid + 1;
            }
            else
            {
                return !Negated;
            }
        }
        return Negated;
    }

    internal System.Buffers.SearchValues<char>? CreateCharSearchValues(out bool isNegatedSearchValues)
    {
        isNegatedSearchValues = false;
        var totalBmp = 0;
        foreach (var r in _ranges)
        {
            if (r.First > 0xFFFF)
            {
                break;
            }

            var last = Math.Min(r.Last, 0xFFFF);
            totalBmp += last - r.First + 1;
        }
        if (totalBmp is >= 1 and <= 512)
        {
            var chars = new char[totalBmp];
            var idx = 0;
            foreach (var r in _ranges)
            {
                if (r.First > 0xFFFF)
                {
                    break;
                }

                var last = Math.Min(r.Last, 0xFFFF);
                for (var c = r.First; c <= last; c++)
                {
                    chars[idx++] = (char)c;
                }
            }
            isNegatedSearchValues = Negated;
            return System.Buffers.SearchValues.Create(chars);
        }
        return null;
    }

    private bool ContainsNonLatin1(int value, ProfileDescriptor profile)
    {
        if (!profile.Contains(value))
        {
            return false;
        }

        if (value < _minFirst || value > _maxLast)
        {
            return Negated;
        }

        if (_bmp is { } bmp && (uint)value <= 0xFFFFu)
        {
            return (bmp[value >> 6] & (1UL << (value & 63))) != 0;
        }

        int lo = 0, hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _ranges[mid];
            if (value < r.First)
            {
                hi = mid - 1;
            }
            else if (value > r.Last)
            {
                lo = mid + 1;
            }
            else
            {
                return !Negated;
            }
        }
        return Negated;
    }

    internal static bool TryCreate(ReadOnlySpan<CharacterRange> ranges, bool negated,
        ProfileDescriptor profile, ref CompileBudget budget, SourceRange source,
        out CharacterClass? result, out RegexDiagnostic diagnostic)
    {
        result = null;
        diagnostic = default;
        var bytes = 64L + 8L * ranges.Length;
        var code = ranges.Length > budget.Limits.MaxClassRanges ? RegexDiagnosticCode.ClassLimitExceeded :
            bytes > budget.Limits.MaxPlanBytes ? RegexDiagnosticCode.PlanLimitExceeded : RegexDiagnosticCode.None;
        if (code != RegexDiagnosticCode.None)
        {
            diagnostic = new(code, source.Offset, source.Length, source.Unit);
            return false;
        }
        var previous = -1;
        foreach (var range in ranges)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            var maximum = profile.TextModel == TextModel.RawBytes ? 255 :
                profile.TextModel == TextModel.Utf16CodeUnits ? 0xFFFF : 0x10FFFF;
            if (range.First < 0 || range.Last < range.First || range.Last > maximum || range.First <= previous)
            {
                diagnostic = new(RegexDiagnosticCode.InvalidProgram, source.Offset, source.Length, source.Unit);
                return false;
            }
            previous = range.Last;
        }
        if (!budget.TryCharge(ranges.Length, source, out diagnostic) ||
            !budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        result = new(ranges, negated, profile);
        return true;
    }
}

internal enum CoreOpcode
{
    Literal, Class, Any, Split, Jump, CaptureStart, CaptureEnd, ClearCaptures,
    AssertStart, AssertEnd, Match
}

// X is the continuation; Split visits X before Y. Unused operands must be zero.
// Literal Operand is an alphabet element, Class Operand is an index, captures use group IDs.
// ClearCaptures clears Count groups beginning at Operand; zero never occurs in that range.
internal readonly record struct CoreInstruction(
    CoreOpcode Opcode, int X, int Y, int Operand, int Count, SourceRange Source);

internal sealed class SemanticProgram
{
    private readonly CoreInstruction[] _instructions;
    private readonly CharacterClass[] _classes;
    internal ReadOnlySpan<CoreInstruction> Instructions => _instructions;
    internal ReadOnlySpan<CharacterClass> Classes => _classes;
    internal ProfileDescriptor Profile { get; }
    internal int GroupCount { get; }
    internal int PatternLength { get; }
    internal RegexOffsetUnit PatternUnit { get; }
    internal CoreFeatures Features { get; }
    internal long RetainedBytes { get; }
    internal int EntryPoint => 0;

    private SemanticProgram(ReadOnlySpan<CoreInstruction> instructions, ReadOnlySpan<CharacterClass> classes,
        ProfileDescriptor profile, int groupCount, int patternLength, RegexOffsetUnit patternUnit,
        CoreFeatures features, long retainedBytes)
    {
        _instructions = instructions.ToArray();
        _classes = classes.ToArray(); // Elements already own immutable interval copies.
        Profile = profile;
        GroupCount = groupCount;
        PatternLength = patternLength;
        PatternUnit = patternUnit;
        Features = features;
        RetainedBytes = retainedBytes;
    }

    // Only admission path. Expected size/work failures are diagnostic; InvalidProgram is a
    // compiler-contract defect and must not be mislabeled as invalid user syntax by integration.
    internal static bool TryCreate(ReadOnlySpan<CoreInstruction> instructions,
        ReadOnlySpan<CharacterClass> classes, ProfileDescriptor profile, int groupCount,
        int patternLength, RegexOffsetUnit patternUnit, ref CompileBudget budget,
        out SemanticProgram? program, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        program = null;
        diagnostic = default;
        var limits = budget.Limits;
        var source = new SourceRange(0, Math.Max(0, patternLength), patternUnit);
        var code = patternLength < 0 || !Enum.IsDefined(patternUnit) ? RegexDiagnosticCode.InvalidProgram :
            patternLength > limits.MaxPatternUnits ? RegexDiagnosticCode.PatternLimitExceeded :
            groupCount < 1 ? RegexDiagnosticCode.InvalidProgram :
            groupCount > limits.MaxCaptureGroups ? RegexDiagnosticCode.CaptureLimitExceeded :
            instructions.Length > limits.MaxInstructions ? RegexDiagnosticCode.ProgramLimitExceeded :
            instructions.Length < 3 ? RegexDiagnosticCode.InvalidProgram : RegexDiagnosticCode.None;
        var bytes = checked(256L + 2L * Math.Max(0, patternLength) + 32L * instructions.Length + 8L * classes.Length);
        long rangeCount = 0;
        var features = CoreFeatures.None;
        if (code == RegexDiagnosticCode.None && bytes > limits.MaxPlanBytes)
        {
            code = RegexDiagnosticCode.PlanLimitExceeded;
        }

        if (code == RegexDiagnosticCode.None)
        {
            foreach (var @class in classes)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                if (@class is null)
                { code = RegexDiagnosticCode.InvalidProgram; break; }
                bytes = checked(bytes + @class.RetainedBytes);
                if (bytes > limits.MaxPlanBytes)
                { code = RegexDiagnosticCode.PlanLimitExceeded; break; }
                rangeCount += @class.Ranges.Length;
                if (rangeCount > limits.MaxClassRanges)
                { code = RegexDiagnosticCode.ClassLimitExceeded; break; }
                foreach (var range in @class.Ranges)
                {
                    if (!budget.TryCharge(1, source, out diagnostic))
                    {
                        return false;
                    }
                    // A scalar interval may cross the surrogate hole; classification excludes it.
                    if (range.Last > (profile.TextModel == TextModel.RawBytes ? 255 :
                        profile.TextModel == TextModel.Utf16CodeUnits ? 0xFFFF : 0x10FFFF))
                    { code = RegexDiagnosticCode.InvalidProgram; break; }
                }
                if (code != RegexDiagnosticCode.None)
                {
                    break;
                }
            }
            if (code == RegexDiagnosticCode.None && bytes > limits.MaxPlanBytes)
            {
                code = RegexDiagnosticCode.PlanLimitExceeded;
            }
        }
        if (code != RegexDiagnosticCode.None)
        {
            diagnostic = new(code, source.Offset, source.Length, source.Unit);
            return false;
        }
        // Central group-zero prologue/epilogue and acceptance; no other group-zero writes.
        if (instructions[0].Opcode != CoreOpcode.CaptureStart || instructions[0].Operand != 0 ||
            instructions[^2].Opcode != CoreOpcode.CaptureEnd || instructions[^2].Operand != 0 ||
            instructions[^2].X != instructions.Length - 1 || instructions[^1].Opcode != CoreOpcode.Match)
        {
            diagnostic = new(RegexDiagnosticCode.InvalidProgram, 0, 0, patternUnit);
            return false;
        }
        for (var i = 0; i < instructions.Length; i++)
        {
            var instruction = instructions[i];
            if (!budget.TryCharge(1, instruction.Source, out diagnostic))
            {
                return false;
            }

            var valid = Enum.IsDefined(instruction.Opcode) && instruction.Source.Unit == patternUnit &&
                instruction.Source.Offset >= 0 && instruction.Source.Length >= 0 &&
                (long)instruction.Source.Offset + instruction.Source.Length <= patternLength &&
                (instruction.Opcode == CoreOpcode.Match ? i == instructions.Length - 1 && instruction.X == 0 :
                    (uint)instruction.X < (uint)instructions.Length) &&
                (instruction.Opcode == CoreOpcode.Split ? (uint)instruction.Y < (uint)instructions.Length : instruction.Y == 0);
            // No transition can re-enter the group-zero prologue or skip its epilogue.
            if (instruction.Opcode != CoreOpcode.Match)
            {
                valid &= instruction.X != 0 && (instruction.X != instructions.Length - 1 || i == instructions.Length - 2);
            }

            if (instruction.Opcode == CoreOpcode.Split)
            {
                valid &= instruction.Y != 0 && instruction.Y != instructions.Length - 1;
            }

            valid &= instruction.Opcode switch
            {
                CoreOpcode.Literal => profile.Contains(instruction.Operand) && instruction.Count == 0,
                CoreOpcode.Class => (uint)instruction.Operand < (uint)classes.Length && instruction.Count == 0,
                CoreOpcode.CaptureStart => instruction.Count == 0 && (uint)instruction.Operand < (uint)groupCount &&
                    (instruction.Operand != 0 || i == 0),
                CoreOpcode.CaptureEnd => instruction.Count == 0 && (uint)instruction.Operand < (uint)groupCount &&
                    (instruction.Operand != 0 || i == instructions.Length - 2),
                CoreOpcode.ClearCaptures => instruction.Operand >= 1 && instruction.Count >= 1 &&
                    (long)instruction.Operand + instruction.Count <= groupCount,
                _ => instruction.Operand == 0 && instruction.Count == 0
            };
            if (!valid)
            {
                diagnostic = new(RegexDiagnosticCode.InvalidProgram, instruction.Source.Offset,
                    instruction.Source.Length, patternUnit);
                return false;
            }
            features |= instruction.Opcode switch
            {
                CoreOpcode.Literal => CoreFeatures.Literal,
                CoreOpcode.Class => CoreFeatures.CharacterClass,
                CoreOpcode.Any => CoreFeatures.Any,
                CoreOpcode.Split => CoreFeatures.Alternation,
                CoreOpcode.CaptureStart or CoreOpcode.CaptureEnd or CoreOpcode.ClearCaptures => CoreFeatures.Captures,
                CoreOpcode.AssertStart or CoreOpcode.AssertEnd => CoreFeatures.AbsoluteAnchors,
                _ => CoreFeatures.None
            };
        }
        // Defensive copies are charged before allocation; retained-size admission already passed.
        if (!budget.TryCharge((long)instructions.Length + classes.Length, source, out diagnostic) ||
            !budget.TryReserveBytes(256L + 32L * instructions.Length + 8L * classes.Length, source, out diagnostic))
        {
            return false;
        }

        program = new(instructions, classes, profile, groupCount, patternLength, patternUnit, features, bytes);
        return true;
    }
}
