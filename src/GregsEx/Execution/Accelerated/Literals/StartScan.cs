#nullable enable
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.IR;

namespace GregsEx.Execution.Accelerated.Literals;

internal enum CoreFastKind : byte
{
    None = 0,
    Literal,
    LiteralAlternation,
    SingleSetRepeat,
    SetRepeatSuffixLiteral,
    AtomicSequence
}

internal readonly struct CoreSeqStep
{
    internal readonly bool IsLiteral;
    internal readonly char[]? LiteralChars;
    internal readonly CharacterClass? Class;
    internal readonly int Min;
    internal readonly int Max;
    internal readonly int CaptureGroup; // > 0 if this step alone is captured by group g

    internal CoreSeqStep(char[] literalChars, int captureGroup)
    {
        IsLiteral = true;
        LiteralChars = literalChars;
        Class = null;
        Min = literalChars.Length;
        Max = literalChars.Length;
        CaptureGroup = captureGroup;
    }

    internal CoreSeqStep(CharacterClass @class, int min, int max, int captureGroup)
    {
        IsLiteral = false;
        LiteralChars = null;
        Class = @class;
        Min = min;
        Max = max;
        CaptureGroup = captureGroup;
    }
}

// Immutable candidate-start plan and whole-pattern fast-path accelerator for RegularCore Auto mode.
// Work is charged one unit per scanned unit through AcceleratorWork / RunState.
internal sealed class StartScan
{
    internal const int MaxPrefixUnits = 64;
    private const int InitialChunk = 32;

    private readonly char[]? _chars;
    private readonly byte[]? _bytes;
    private readonly SearchValues<char>? _charSet;
    private readonly SearchValues<byte>? _byteSet;
    private readonly SearchValues<string>? _stringSet;
    private readonly bool _isStartAnchored;
    private readonly bool _isEndAnchored;
    private readonly char[]? _requiredChars;
    private readonly byte[]? _requiredBytes;
    private readonly int _requiredEndUnit = -1;

    // Whole-pattern fast-path state
    private readonly CoreFastKind _fastKind;
    private readonly int _groupCount;
    private readonly string[]? _altStrings;
    private readonly CharacterClass? _repeatClass;
    private readonly SearchValues<char>? _repeatSv;
    private readonly bool _repeatSvNegated;
    private readonly int _repeatMin;
    private readonly int _repeatMax;
    private readonly char[]? _suffixChars;
    private readonly bool _suffixAllInSet;
    private readonly CoreSeqStep[]? _seqSteps;
    private readonly int _seqAnchorIndex = -1;
    private readonly int _seqPrefixFixedLen = -1;

    internal int PrefixLength { get; }
    internal bool HasFastPath => _fastKind != CoreFastKind.None;

    private StartScan(
        char[]? chars,
        byte[]? bytes,
        SearchValues<char>? charSet,
        SearchValues<byte>? byteSet,
        SearchValues<string>? stringSet,
        int prefixLength,
        bool isStartAnchored,
        bool isEndAnchored,
        char[]? requiredChars,
        byte[]? requiredBytes,
        CoreFastKind fastKind = CoreFastKind.None,
        int groupCount = 1,
        string[]? altStrings = null,
        CharacterClass? repeatClass = null,
        SearchValues<char>? repeatSv = null,
        bool repeatSvNegated = false,
        int repeatMin = 0,
        int repeatMax = 0,
        char[]? suffixChars = null,
        bool suffixAllInSet = false,
        CoreSeqStep[]? seqSteps = null,
        int seqAnchorIndex = -1,
        int seqPrefixFixedLen = -1,
        int requiredEndUnit = -1)
    {
        _chars = chars;
        _bytes = bytes;
        _charSet = charSet;
        _byteSet = byteSet;
        _stringSet = stringSet;
        PrefixLength = prefixLength;
        _isStartAnchored = isStartAnchored;
        _isEndAnchored = isEndAnchored;
        _requiredChars = requiredChars;
        _requiredBytes = requiredBytes;
        _requiredEndUnit = requiredEndUnit;
        _fastKind = fastKind;
        _groupCount = groupCount;
        _altStrings = altStrings;
        _repeatClass = repeatClass;
        _repeatSv = repeatSv;
        _repeatSvNegated = repeatSvNegated;
        _repeatMin = repeatMin;
        _repeatMax = repeatMax;
        _suffixChars = suffixChars;
        _suffixAllInSet = suffixAllInSet;
        _seqSteps = seqSteps;
        _seqAnchorIndex = seqAnchorIndex;
        _seqPrefixFixedLen = seqPrefixFixedLen;
    }

    internal static StartScan? TryCreate(SemanticProgram program, AcceleratorSwitches switches)
    {
        if (switches == AcceleratorSwitches.None)
        {
            return null;
        }

        var profile = program.Profile;
        var instructions = program.Instructions;
        if (instructions.Length < 3)
        {
            return null;
        }

        var allowLiteral = (switches & AcceleratorSwitches.Literal) != 0;
        var allowTables = (switches & AcceleratorSwitches.Tables) != 0;

        // Analyze anchors at start and end of group 0
        var entry = instructions[0].Opcode == CoreOpcode.CaptureStart && instructions[0].Operand == 0
            ? instructions[0].X
            : 0;
        var bodyEnd = instructions.Length - 2; // CaptureEnd(0) -> Match
        var isStartAnchored = false;
        if (instructions[entry].Opcode == CoreOpcode.AssertStart)
        {
            isStartAnchored = true;
            entry = instructions[entry].X;
        }

        // Check if all transitions into bodyEnd come through a single AssertEnd instruction
        var effectiveEnd = bodyEnd;
        var isEndAnchored = false;
        if (bodyEnd >= 1 && instructions[bodyEnd - 1].Opcode == CoreOpcode.AssertEnd && instructions[bodyEnd - 1].X == bodyEnd)
        {
            var allToAssertEnd = true;
            var assertEndPc = bodyEnd - 1;
            for (var i = 0; i < assertEndPc; i++)
            {
                var inst = instructions[i];
                if (inst.X == bodyEnd || (inst.Opcode == CoreOpcode.Split && inst.Y == bodyEnd))
                {
                    allToAssertEnd = false;
                    break;
                }
            }
            if (allToAssertEnd)
            {
                isEndAnchored = true;
                effectiveEnd = assertEndPc;
            }
        }

        // Extract mandatory literal on the spine (for prefiltering patterns like .*.*=.* or Holmes$)
        char[]? reqChars = null;
        byte[]? reqBytes = null;
        var reqEndUnit = -1;
        if (allowLiteral)
        {
            var reqList = ExtractRequiredLiteral(program, entry, effectiveEnd);
            if (reqList is { Count: > 0 })
            {
                if (profile.InputKind == RegexInputKind.Utf16)
                {
                    reqChars = ToChars(reqList);
                }
                else
                {
                    reqBytes = ToBytes(reqList, profile);
                }
            }
            if (isEndAnchored)
            {
                reqEndUnit = ExtractRequiredEndUnit(program, entry, effectiveEnd);
            }
        }

        // Try whole-pattern fast paths first when Literal/Tables switches are enabled
        if (allowLiteral && program.GroupCount == 1 &&
            TryMatchWholeLiteral(program, entry, effectiveEnd, isStartAnchored, isEndAnchored, reqChars, reqBytes, out var wholeLit))
        {
            return wholeLit;
        }

        if (allowLiteral && allowTables && program.GroupCount == 1 && !isStartAnchored && !isEndAnchored &&
            profile.InputKind == RegexInputKind.Utf16 &&
            TryMatchWholeLiteralAlternation(program, entry, effectiveEnd, out var wholeAlt))
        {
            return wholeAlt;
        }

        if (allowTables && program.GroupCount == 1 && profile.InputKind == RegexInputKind.Utf16 &&
            TryMatchWholeSingleSetRepeat(program, entry, effectiveEnd, isStartAnchored, isEndAnchored, reqChars, out var wholeRepeat))
        {
            return wholeRepeat;
        }

        if (allowLiteral && allowTables && program.GroupCount == 1 && !isStartAnchored && !isEndAnchored &&
            profile.InputKind == RegexInputKind.Utf16 &&
            TryMatchWholeSetRepeatSuffixLiteral(program, entry, effectiveEnd, out var wholeSetSuffix))
        {
            return wholeSetSuffix;
        }

        if (allowLiteral && allowTables && !isStartAnchored && !isEndAnchored &&
            profile.InputKind == RegexInputKind.Utf16 &&
            TryMatchWholeAtomicSequence(program, entry, effectiveEnd, reqChars, out var wholeSeq))
        {
            return wholeSeq;
        }

        // Otherwise build prefix / first-set / required-literal scan
        var prefix = allowLiteral ? ExtractPrefix(program, entry) : null;
        if (prefix is not null)
        {
            if (profile.InputKind == RegexInputKind.Utf16)
            {
                var chars = ToChars(prefix);
                if (chars is not null)
                {
                    return new(chars, null, null, null, null, chars.Length, isStartAnchored, isEndAnchored, reqChars, null, requiredEndUnit: reqEndUnit);
                }
            }
            else
            {
                var bytes = ToBytes(prefix, profile);
                if (bytes is not null)
                {
                    return new(null, bytes, null, null, null, bytes.Length, isStartAnchored, isEndAnchored, null, reqBytes, requiredEndUnit: reqEndUnit);
                }
            }
        }

        if (allowTables)
        {
            var setScan = TryCreateSet(program, entry, isStartAnchored, isEndAnchored, reqChars, reqBytes, reqEndUnit);
            if (setScan is not null)
            {
                return setScan;
            }
        }

        if (isStartAnchored || reqChars is not null || reqBytes is not null || reqEndUnit >= 0)
        {
            return new(null, null, null, null, null, 0, isStartAnchored, isEndAnchored, reqChars, reqBytes, requiredEndUnit: reqEndUnit);
        }

        return null;
    }

    private static char[]? ToChars(List<int> units)
    {
        var chars = new char[units.Count];
        for (var i = 0; i < chars.Length; i++)
        {
            var v = units[i];
            if ((uint)v > 0xFFFF || v is >= 0xD800 and <= 0xDFFF)
            {
                return null;
            }

            chars[i] = (char)v;
        }
        return chars;
    }

    private static byte[]? ToBytes(List<int> units, ProfileDescriptor profile)
    {
        var limit = profile.TextModel == TextModel.RawBytes ? 0xFF : 0x7F;
        var bytes = new byte[units.Count];
        for (var i = 0; i < bytes.Length; i++)
        {
            if ((uint)units[i] > (uint)limit)
            {
                return null;
            }

            bytes[i] = (byte)units[i];
        }
        return bytes;
    }

    private static bool TryMatchWholeLiteral(
        SemanticProgram program, int entry, int effectiveEnd, bool isStartAnchored, bool isEndAnchored,
        char[]? reqChars, byte[]? reqBytes, out StartScan? scan)
    {
        scan = null;
        var instructions = program.Instructions;
        var units = new List<int>();
        var pc = entry;
        for (var steps = 0; steps <= instructions.Length && pc != effectiveEnd; steps++)
        {
            var inst = instructions[pc];
            if (inst.Opcode == CoreOpcode.Literal)
            {
                units.Add(inst.Operand);
                pc = inst.X;
            }
            else if (inst.Opcode == CoreOpcode.Jump)
            {
                pc = inst.X;
            }
            else
            {
                return false;
            }
        }
        if (pc != effectiveEnd || units.Count == 0)
        {
            return false;
        }

        if (program.Profile.InputKind == RegexInputKind.Utf16)
        {
            var chars = ToChars(units);
            if (chars is null)
            {
                return false;
            }

            scan = new(chars, null, null, null, null, chars.Length, isStartAnchored, isEndAnchored, reqChars ?? chars, null,
                fastKind: CoreFastKind.Literal, groupCount: 1);
            return true;
        }
        else
        {
            var bytes = ToBytes(units, program.Profile);
            if (bytes is null)
            {
                return false;
            }

            scan = new(null, bytes, null, null, null, bytes.Length, isStartAnchored, isEndAnchored, null, reqBytes ?? bytes,
                fastKind: CoreFastKind.Literal, groupCount: 1);
            return true;
        }
    }

    private static bool TryMatchWholeLiteralAlternation(
        SemanticProgram program, int entry, int effectiveEnd, out StartScan? scan)
    {
        scan = null;
        var instructions = program.Instructions;
        var alts = new List<string>();
        var firstChars = new HashSet<char>();
        var pc = entry;
        var safety = 0;
        while (safety++ <= instructions.Length)
        {
            var inst = instructions[pc];
            if (inst.Opcode == CoreOpcode.Split)
            {
                if (!TryReadLiteralBranch(instructions, inst.X, effectiveEnd, out var branch) || branch is null)
                {
                    return false;
                }

                alts.Add(branch);
                firstChars.Add(branch[0]);
                pc = inst.Y;
            }
            else
            {
                if (!TryReadLiteralBranch(instructions, pc, effectiveEnd, out var lastBranch) || lastBranch is null)
                {
                    return false;
                }

                alts.Add(lastBranch);
                firstChars.Add(lastBranch[0]);
                break;
            }
        }
        if (alts.Count < 2 || alts.Count > 64)
        {
            return false;
        }

        var altArr = alts.ToArray();
        var stringSet = SearchValues.Create(altArr, StringComparison.Ordinal);
        var charSet = SearchValues.Create(firstChars.ToArray());
        scan = new(null, null, charSet, null, stringSet, 1, false, false, null, null,
            fastKind: CoreFastKind.LiteralAlternation, groupCount: 1, altStrings: altArr);
        return true;
    }

    private static bool TryReadLiteralBranch(ReadOnlySpan<CoreInstruction> instructions, int startPc, int targetEnd, out string? literal)
    {
        literal = null;
        Span<char> buf = stackalloc char[128];
        var len = 0;
        var pc = startPc;
        for (var steps = 0; steps <= instructions.Length && pc != targetEnd; steps++)
        {
            var inst = instructions[pc];
            if (inst.Opcode == CoreOpcode.Literal)
            {
                var v = inst.Operand;
                if ((uint)v > 0xFFFF || v is >= 0xD800 and <= 0xDFFF || len >= buf.Length)
                {
                    return false;
                }

                buf[len++] = (char)v;
                pc = inst.X;
            }
            else if (inst.Opcode == CoreOpcode.Jump)
            {
                pc = inst.X;
            }
            else
            {
                return false;
            }
        }
        if (pc != targetEnd || len == 0)
        {
            return false;
        }

        literal = buf[..len].ToString();
        return true;
    }

    private static bool TryMatchWholeSingleSetRepeat(
        SemanticProgram program, int entry, int effectiveEnd, bool isStartAnchored, bool isEndAnchored,
        char[]? reqChars, out StartScan? scan)
    {
        scan = null;
        var instructions = program.Instructions;
        // Recognize [class]+ : entry: Class(c) -> entry+2; entry+1: Class(c) -> entry+2; entry+2: Split(entry+1, effectiveEnd)
        if (entry + 2 >= instructions.Length)
        {
            return false;
        }

        var i0 = instructions[entry];
        var i1 = instructions[entry + 1];
        var i2 = instructions[entry + 2];
        if (i0.Opcode != CoreOpcode.Class || i1.Opcode != CoreOpcode.Class || i2.Opcode != CoreOpcode.Split)
        {
            return false;
        }

        if (i0.Operand != i1.Operand || i0.X != entry + 2 || i1.X != entry + 2 || i2.X != entry + 1 || i2.Y != effectiveEnd)
        {
            return false;
        }

        var @class = program.Classes[i0.Operand];
        var sv = @class.CreateCharSearchValues(out var svNegated);
        scan = new(null, null, !svNegated ? sv : null, null, null, 1, isStartAnchored, isEndAnchored, reqChars, null,
            fastKind: CoreFastKind.SingleSetRepeat,
            groupCount: 1,
            repeatClass: @class,
            repeatSv: sv,
            repeatSvNegated: svNegated,
            repeatMin: 1,
            repeatMax: int.MaxValue);
        return true;
    }

    private static bool TryMatchWholeSetRepeatSuffixLiteral(
        SemanticProgram program, int entry, int effectiveEnd, out StartScan? scan)
    {
        scan = null;
        var instructions = program.Instructions;
        if (entry + 3 > effectiveEnd)
        {
            return false;
        }

        var i0 = instructions[entry];
        var i1 = instructions[entry + 1];
        var i2 = instructions[entry + 2];
        if (i0.Opcode != CoreOpcode.Class || i1.Opcode != CoreOpcode.Class || i2.Opcode != CoreOpcode.Split)
        {
            return false;
        }

        if (i0.Operand != i1.Operand || i0.X != entry + 2 || i1.X != entry + 2 || i2.X != entry + 1 || i2.Y != entry + 3)
        {
            return false;
        }

        if (!TryReadLiteralBranch(instructions, entry + 3, effectiveEnd, out var suffix) || suffix is null)
        {
            return false;
        }

        var @class = program.Classes[i0.Operand];
        var suffixChars = suffix.ToCharArray();
        var allInSet = true;
        foreach (var c in suffixChars)
        {
            if (!@class.MatchesBmpChar(c))
            { allInSet = false; break; }
        }
        var sv = @class.CreateCharSearchValues(out var svNegated);
        scan = new(null, null, !svNegated ? sv : null, null, null, 1, false, false, suffixChars, null,
            fastKind: CoreFastKind.SetRepeatSuffixLiteral,
            groupCount: 1,
            repeatClass: @class,
            repeatSv: sv,
            repeatSvNegated: svNegated,
            repeatMin: 1,
            repeatMax: int.MaxValue,
            suffixChars: suffixChars,
            suffixAllInSet: allInSet);
        return true;
    }

    private static bool TryMatchWholeAtomicSequence(
        SemanticProgram program, int entry, int effectiveEnd, char[]? reqChars, out StartScan? scan)
    {
        scan = null;
        var instructions = program.Instructions;
        var steps = new List<CoreSeqStep>();
        var pc = entry;
        var activeCapture = 0;
        var capturedStepsInCurrentGroup = 0;

        while (pc < effectiveEnd)
        {
            var inst = instructions[pc];
            if (inst.Opcode == CoreOpcode.CaptureStart)
            {
                if (activeCapture != 0 || inst.Operand <= 0)
                {
                    return false;
                }

                activeCapture = inst.Operand;
                capturedStepsInCurrentGroup = 0;
                pc = inst.X;
                continue;
            }
            if (inst.Opcode == CoreOpcode.CaptureEnd)
            {
                if (activeCapture == 0 || inst.Operand != activeCapture || capturedStepsInCurrentGroup != 1)
                {
                    return false;
                }

                activeCapture = 0;
                pc = inst.X;
                continue;
            }
            // Check for [class]+ (3-instruction pattern)
            if (pc + 2 < effectiveEnd &&
                inst.Opcode == CoreOpcode.Class &&
                instructions[pc + 1].Opcode == CoreOpcode.Class &&
                instructions[pc + 2].Opcode == CoreOpcode.Split &&
                inst.Operand == instructions[pc + 1].Operand &&
                inst.X == pc + 2 &&
                instructions[pc + 1].X == pc + 2 &&
                instructions[pc + 2].X == pc + 1 &&
                instructions[pc + 2].Y == pc + 3)
            {
                steps.Add(new CoreSeqStep(program.Classes[inst.Operand], 1, int.MaxValue, activeCapture));
                if (activeCapture != 0)
                {
                    capturedStepsInCurrentGroup++;
                }

                pc += 3;
                continue;
            }
            // Check for fixed run of Class(c)
            if (inst.Opcode == CoreOpcode.Class)
            {
                var classIdx = inst.Operand;
                var count = 0;
                while (pc < effectiveEnd && instructions[pc].Opcode == CoreOpcode.Class &&
                       instructions[pc].Operand == classIdx && instructions[pc].X == pc + 1)
                {
                    count++;
                    pc++;
                    // Stop if we hit aCaptureEnd or the start of a [class]+ loop
                    if (pc + 1 < effectiveEnd &&
                        instructions[pc].Opcode == CoreOpcode.Class &&
                        instructions[pc].X == pc + 2)
                    {
                        break;
                    }
                }
                if (count == 0)
                {
                    return false;
                }

                steps.Add(new CoreSeqStep(program.Classes[classIdx], count, count, activeCapture));
                if (activeCapture != 0)
                {
                    capturedStepsInCurrentGroup++;
                }

                continue;
            }
            // Check for run of Literal
            if (inst.Opcode == CoreOpcode.Literal)
            {
                var litChars = new List<char>();
                while (pc < effectiveEnd && instructions[pc].Opcode == CoreOpcode.Literal && instructions[pc].X == pc + 1)
                {
                    var v = instructions[pc].Operand;
                    if ((uint)v > 0xFFFF || v is >= 0xD800 and <= 0xDFFF)
                    {
                        return false;
                    }

                    litChars.Add((char)v);
                    pc++;
                }
                if (litChars.Count == 0)
                {
                    return false;
                }

                steps.Add(new CoreSeqStep(litChars.ToArray(), activeCapture));
                if (activeCapture != 0)
                {
                    capturedStepsInCurrentGroup++;
                }

                continue;
            }
            return false;
        }

        if (pc != effectiveEnd || activeCapture != 0 || steps.Count < 2)
        {
            return false;
        }

        // Find anchor literal step
        var anchorIdx = -1;
        var bestLen = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].IsLiteral && steps[i].LiteralChars!.Length >= bestLen)
            {
                bestLen = steps[i].LiteralChars!.Length;
                anchorIdx = i;
            }
        }
        if (anchorIdx < 0)
        {
            return false;
        }

        // Validate prefix before anchorIdx: either all steps 0..anchorIdx-1 are fixed-length (Min == Max),
        // OR anchorIdx == 1 and step 0 is [class]+ where class does NOT contain anchor's first char.
        var prefixFixedLen = 0;
        for (var i = 0; i < anchorIdx; i++)
        {
            if (steps[i].Min != steps[i].Max)
            {
                if (i == 0 && anchorIdx == 1 && !steps[0].IsLiteral &&
                    !steps[0].Class!.MatchesBmpChar(steps[anchorIdx].LiteralChars![0]))
                {
                    prefixFixedLen = -1;
                    break;
                }
                return false;
            }
            prefixFixedLen += steps[i].Min;
        }

        // Validate suffix after anchorIdx: only the very last step may have Min != Max
        for (var i = anchorIdx + 1; i < steps.Count; i++)
        {
            if (steps[i].Min != steps[i].Max && i != steps.Count - 1)
            {
                return false;
            }
        }

        var stepArr = steps.ToArray();
        SearchValues<char>? firstSv = null;
        if (!stepArr[0].IsLiteral)
        {
            var sv = stepArr[0].Class!.CreateCharSearchValues(out var neg);
            if (!neg)
            {
                firstSv = sv;
            }
        }
        scan = new(null, null, firstSv, null, null, 1, false, false, reqChars ?? stepArr[anchorIdx].LiteralChars, null,
            fastKind: CoreFastKind.AtomicSequence,
            groupCount: program.GroupCount,
            seqSteps: stepArr,
            seqAnchorIndex: anchorIdx,
            seqPrefixFixedLen: prefixFixedLen);
        return true;
    }

    private static List<int>? ExtractRequiredLiteral(SemanticProgram program, int entry, int effectiveEnd)
    {
        var instructions = program.Instructions;
        List<int>? best = null;
        List<int>? current = null;
        var pc = entry;
        var safety = 0;
        while (pc < effectiveEnd && safety++ <= instructions.Length)
        {
            var inst = instructions[pc];
            if (inst.Opcode == CoreOpcode.Literal)
            {
                current ??= new List<int>();
                current.Add(inst.Operand);
                if (best is null || current.Count > best.Count)
                {
                    best = new List<int>(current);
                }

                pc = inst.X;
                continue;
            }
            current = null;
            if (inst.Opcode != CoreOpcode.Split)
            {
                if (inst.X <= pc)
                {
                    break;
                }

                pc = inst.X;
                continue;
            }
            // Split(X, Y)
            if (inst.X < pc || inst.Y < pc)
            {
                // Loop exit Split: forward branch is Max(X, Y)
                var next = Math.Max(inst.X, inst.Y);
                if (next <= pc)
                {
                    break;
                }

                pc = next;
            }
            else
            {
                // Forward branch Split (Alternation or Optional): find reconvergence point
                var limit = Math.Max(inst.X, inst.Y);
                for (var u = Math.Min(inst.X, inst.Y); u < limit && u < effectiveEnd; u++)
                {
                    var ui = instructions[u];
                    if (ui.Opcode == CoreOpcode.Match)
                    {
                        break;
                    }

                    if (ui.X > limit)
                    {
                        limit = ui.X;
                    }

                    if (ui.Opcode == CoreOpcode.Split && ui.Y > limit)
                    {
                        limit = ui.Y;
                    }
                }
                if (limit <= pc)
                {
                    break;
                }

                pc = limit;
            }
        }
        return best;
    }

    private static StartScan? TryCreateSet(
        SemanticProgram program, int entry, bool isStartAnchored, bool isEndAnchored,
        char[]? reqChars, byte[]? reqBytes, int reqEndUnit = -1)
    {
        const int MaxSetUnits = 256;
        var instructions = program.Instructions;
        var seen = new bool[instructions.Length];
        var stack = new Stack<int>();
        var values = new SortedSet<int>();
        stack.Push(entry);
        while (stack.Count > 0)
        {
            var pc = stack.Pop();
            if (seen[pc])
            {
                continue;
            }

            seen[pc] = true;
            var instruction = instructions[pc];
            switch (instruction.Opcode)
            {
                case CoreOpcode.Literal:
                    values.Add(instruction.Operand);
                    break;
                case CoreOpcode.Class:
                    var @class = program.Classes[instruction.Operand];
                    if (@class.Negated)
                    {
                        return null;
                    }

                    foreach (var range in @class.Ranges)
                    {
                        if ((long)range.Last - range.First >= MaxSetUnits)
                        {
                            return null;
                        }

                        for (var v = range.First; v <= range.Last; v++)
                        {
                            values.Add(v);
                        }
                    }
                    break;
                case CoreOpcode.Split:
                    stack.Push(instruction.X);
                    stack.Push(instruction.Y);
                    break;
                case CoreOpcode.Jump:
                case CoreOpcode.CaptureStart:
                case CoreOpcode.CaptureEnd:
                case CoreOpcode.ClearCaptures:
                    stack.Push(instruction.X);
                    break;
                default:
                    return null;
            }
            if (values.Count > MaxSetUnits)
            {
                return null;
            }
        }
        if (values.Count == 0)
        {
            return null;
        }

        var profile = program.Profile;
        if (profile.InputKind == RegexInputKind.Utf16)
        {
            var chars = new char[values.Count];
            var i = 0;
            foreach (var value in values)
            {
                if ((uint)value > 0xFFFF || value is >= 0xD800 and <= 0xDFFF)
                {
                    return null;
                }

                chars[i++] = (char)value;
            }
            return new(null, null, SearchValues.Create(chars), null, null, 1, isStartAnchored, isEndAnchored, reqChars, null, requiredEndUnit: reqEndUnit);
        }
        var limit = profile.TextModel == TextModel.RawBytes ? 0xFF : 0x7F;
        var bytes = new byte[values.Count];
        var b = 0;
        foreach (var value in values)
        {
            if ((uint)value > (uint)limit)
            {
                return null;
            }

            bytes[b++] = (byte)value;
        }
        return new(null, null, null, SearchValues.Create(bytes), null, 1, isStartAnchored, isEndAnchored, null, reqBytes, requiredEndUnit: reqEndUnit);
    }

    private static int ExtractRequiredEndUnit(SemanticProgram program, int entry, int effectiveEnd)
    {
        if (effectiveEnd <= entry)
        {
            return -1;
        }

        var instructions = program.Instructions;
        var canReachEnd = effectiveEnd < 256 ? stackalloc bool[effectiveEnd + 1] : new bool[effectiveEnd + 1];
        canReachEnd[effectiveEnd] = true;

        var changed = true;
        while (changed)
        {
            changed = false;
            for (var i = 0; i < effectiveEnd; i++)
            {
                if (canReachEnd[i])
                {
                    continue;
                }

                var inst = instructions[i];
                if (inst.Opcode is CoreOpcode.Split or CoreOpcode.Jump or CoreOpcode.CaptureStart
                    or CoreOpcode.CaptureEnd or CoreOpcode.ClearCaptures or CoreOpcode.AssertStart or CoreOpcode.AssertEnd)
                {
                    var xReach = (uint)inst.X <= (uint)effectiveEnd && canReachEnd[inst.X];
                    var yReach = inst.Opcode == CoreOpcode.Split && (uint)inst.Y <= (uint)effectiveEnd && canReachEnd[inst.Y];
                    if (xReach || yReach)
                    {
                        canReachEnd[i] = true;
                        changed = true;
                    }
                }
            }
        }

        if ((uint)entry <= (uint)effectiveEnd && canReachEnd[entry])
        {
            return -1;
        }

        var reqUnit = -1;
        for (var i = 0; i < effectiveEnd; i++)
        {
            var inst = instructions[i];
            if (inst.Opcode is CoreOpcode.Split or CoreOpcode.Jump or CoreOpcode.CaptureStart
                or CoreOpcode.CaptureEnd or CoreOpcode.ClearCaptures or CoreOpcode.AssertStart or CoreOpcode.AssertEnd)
            {
                continue;
            }
            if ((uint)inst.X <= (uint)effectiveEnd && canReachEnd[inst.X])
            {
                if (inst.Opcode != CoreOpcode.Literal)
                {
                    return -1;
                }

                if (reqUnit < 0)
                {
                    reqUnit = inst.Operand;
                }
                else if (reqUnit != inst.Operand)
                {
                    return -1;
                }
            }
        }

        if (reqUnit < 0)
        {
            return -1;
        }

        var profile = program.Profile;
        if (profile.InputKind == RegexInputKind.Utf16)
        {
            if ((uint)reqUnit > 0xFFFF || reqUnit is >= 0xD800 and <= 0xDFFF)
            {
                return -1;
            }

            return reqUnit;
        }
        var limit = profile.TextModel == TextModel.RawBytes ? 0xFF : 0x7F;
        return (uint)reqUnit <= (uint)limit ? reqUnit : -1;
    }

    private static List<int>? ExtractPrefix(SemanticProgram program, int entry)
    {
        var instructions = program.Instructions;
        var prefix = new List<int>();
        var pc = entry;
        for (var steps = 0; steps <= instructions.Length; steps++)
        {
            var instruction = instructions[pc];
            switch (instruction.Opcode)
            {
                case CoreOpcode.Literal:
                    prefix.Add(instruction.Operand);
                    if (prefix.Count == MaxPrefixUnits)
                    {
                        return prefix;
                    }

                    pc = instruction.X;
                    continue;
                case CoreOpcode.Jump:
                case CoreOpcode.CaptureStart:
                case CoreOpcode.CaptureEnd:
                case CoreOpcode.ClearCaptures:
                    pc = instruction.X;
                    continue;
                default:
                    return prefix.Count > 0 ? prefix : null;
            }
        }
        return null;
    }

    internal bool CheckRequiredPrecondition<TUnit>(ReadOnlySpan<TUnit> input, int startAt, ref RunState state)
        where TUnit : unmanaged
    {
        long rem = input.Length - startAt;
        if (!state.Budget.IsUnconstrained(rem * 2 + 64))
        {
            return true;
        }

        if (_requiredEndUnit >= 0)
        {
            if (input.Length <= startAt)
            {
                state.TryCharge(1, startAt);
                return false;
            }
            if (typeof(TUnit) == typeof(char))
            {
                var chars = MemoryMarshal.Cast<TUnit, char>(input);
                if (chars[^1] != (char)_requiredEndUnit)
                {
                    state.TryCharge(1, startAt);
                    return false;
                }
            }
            else if (typeof(TUnit) == typeof(byte))
            {
                var bytes = MemoryMarshal.Cast<TUnit, byte>(input);
                if (bytes[^1] != (byte)_requiredEndUnit)
                {
                    state.TryCharge(1, startAt);
                    return false;
                }
            }
        }
        if (typeof(TUnit) == typeof(char) && _requiredChars is { } rc)
        {
            var chars = MemoryMarshal.Cast<TUnit, char>(input);
            if (chars.Slice(startAt).IndexOf(rc) < 0)
            {
                state.TryCharge(rem + 1, startAt);
                return false;
            }
        }
        else if (typeof(TUnit) == typeof(byte) && _requiredBytes is { } rb)
        {
            var bytes = MemoryMarshal.Cast<TUnit, byte>(input);
            if (bytes.Slice(startAt).IndexOf(rb) < 0)
            {
                state.TryCharge(rem + 1, startAt);
                return false;
            }
        }
        return true;
    }

    internal bool TryExecuteFastPath<TUnit>(
        ReadOnlySpan<TUnit> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
        where TUnit : unmanaged
    {
        match = default;
        status = RegexSearchStatus.NoMatch;
        if (_fastKind == CoreFastKind.None)
        {
            return false;
        }

        long rem = input.Length - startAt;
        if (!state.Budget.IsUnconstrained(rem * 4 + 256))
        {
            return false;
        }

        if (_fastKind == CoreFastKind.Literal)
        {
            if (typeof(TUnit) == typeof(char) && _chars is { } litChars)
            {
                return ExecuteLiteral(MemoryMarshal.Cast<TUnit, char>(input), startAt, litChars, operation, ref state, groups, out match, out status);
            }

            if (typeof(TUnit) == typeof(byte) && _bytes is { } litBytes)
            {
                return ExecuteLiteral(MemoryMarshal.Cast<TUnit, byte>(input), startAt, litBytes, operation, ref state, groups, out match, out status);
            }

            return false;
        }

        if (typeof(TUnit) != typeof(char) || !state.ValidatedCleanSubject)
        {
            return false;
        }

        var chars = MemoryMarshal.Cast<TUnit, char>(input);
        return _fastKind switch
        {
            CoreFastKind.LiteralAlternation => ExecuteLiteralAlternation(chars, startAt, operation, ref state, groups, out match, out status),
            CoreFastKind.SingleSetRepeat => ExecuteSingleSetRepeat(chars, startAt, operation, ref state, groups, out match, out status),
            CoreFastKind.SetRepeatSuffixLiteral => ExecuteSetRepeatSuffixLiteral(chars, startAt, operation, ref state, groups, out match, out status),
            CoreFastKind.AtomicSequence => ExecuteAtomicSequence(chars, startAt, operation, ref state, groups, out match, out status),
            _ => false
        };
    }

    private bool ExecuteLiteral<T>(
        ReadOnlySpan<T> input, int startAt, ReadOnlySpan<T> lit, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
        where T : unmanaged, IEquatable<T>
    {
        match = default;
        if (_isStartAnchored)
        {
            if (startAt > 0 || input.Length < lit.Length || (_isEndAnchored && input.Length != lit.Length))
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            state.TryCharge(lit.Length + 2, 0);
            if (!input.Slice(0, lit.Length).SequenceEqual(lit))
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, lit.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (_isEndAnchored)
        {
            var pos = input.Length - lit.Length;
            if (pos < startAt)
            {
                state.TryCharge(input.Length - startAt + 1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            state.TryCharge(lit.Length + 2, pos);
            if (!input.Slice(pos, lit.Length).SequenceEqual(lit))
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(pos, lit.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        var idx = input.Slice(startAt).IndexOf(lit);
        if (idx < 0)
        {
            state.TryCharge(input.Length - startAt + 1, startAt);
            status = RegexSearchStatus.NoMatch;
            return true;
        }
        var matchStart = startAt + idx;
        state.TryCharge(idx + lit.Length + 2, startAt);
        PublishGroup0(matchStart, lit.Length, operation, groups, out match);
        status = RegexSearchStatus.Match;
        return true;
    }

    private bool ExecuteLiteralAlternation(
        ReadOnlySpan<char> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var alts = _altStrings!;
        var pos = startAt;
        while (pos < input.Length)
        {
            var idx = input.Slice(pos).IndexOfAny(_stringSet!);
            if (idx < 0)
            {
                state.TryCharge(input.Length - startAt + 1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            pos += idx;
            var rem = input.Slice(pos);
            foreach (var alt in alts)
            {
                if (rem.StartsWith(alt.AsSpan()))
                {
                    state.TryCharge(pos - startAt + alt.Length + 2, startAt);
                    PublishGroup0(pos, alt.Length, operation, groups, out match);
                    status = RegexSearchStatus.Match;
                    return true;
                }
            }
            pos++;
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteSingleSetRepeat(
        ReadOnlySpan<char> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        status = RegexSearchStatus.NoMatch;
        var set = _repeatClass!;
        if (_isStartAnchored && _isEndAnchored)
        {
            if (startAt > 0 || input.Length < _repeatMin || input.Length > _repeatMax)
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            var badIdx = FindFirstNotInSet(input, set, _repeatSv, _repeatSvNegated);
            state.TryCharge(badIdx >= 0 ? badIdx + 1 : input.Length + 2, 0);
            if (badIdx >= 0)
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, input.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (_isStartAnchored)
        {
            if (startAt > 0 || input.Length < _repeatMin)
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            var maxScan = Math.Min(input.Length, _repeatMax);
            var badIdx = FindFirstNotInSet(input.Slice(0, maxScan), set, _repeatSv, _repeatSvNegated);
            var runLen = badIdx < 0 ? maxScan : badIdx;
            state.TryCharge(runLen + 2, 0);
            if (runLen < _repeatMin)
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, runLen, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (_isEndAnchored)
        {
            return false;
        }

        var pos = startAt;
        while (pos <= input.Length - _repeatMin)
        {
            var rel = FindFirstInSet(input.Slice(pos), set, _repeatSv, _repeatSvNegated);
            if (rel < 0)
            {
                break;
            }

            var matchStart = pos + rel;
            if (matchStart > input.Length - _repeatMin)
            {
                break;
            }

            var maxRem = Math.Min(input.Length - matchStart, _repeatMax);
            var badRel = FindFirstNotInSet(input.Slice(matchStart, maxRem), set, _repeatSv, _repeatSvNegated);
            var runLen = badRel < 0 ? maxRem : badRel;
            if (runLen >= _repeatMin)
            {
                state.TryCharge(matchStart - startAt + runLen + 2, startAt);
                PublishGroup0(matchStart, runLen, operation, groups, out match);
                status = RegexSearchStatus.Match;
                return true;
            }
            pos = matchStart + Math.Max(1, runLen);
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteSetRepeatSuffixLiteral(
        ReadOnlySpan<char> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var set = _repeatClass!;
        var lit = _suffixChars.AsSpan();
        var searchPos = startAt + 1;
        while (searchPos <= input.Length - lit.Length)
        {
            var idx = input.Slice(searchPos).IndexOf(lit);
            if (idx < 0)
            {
                break;
            }

            var litPos = searchPos + idx;
            if (!set.MatchesBmpChar(input[litPos - 1]))
            {
                searchPos = litPos + 1;
                continue;
            }
            var matchStart = litPos - 1;
            while (matchStart > startAt && set.MatchesBmpChar(input[matchStart - 1]))
            {
                matchStart--;
            }

            var matchEnd = litPos + lit.Length;
            if (_suffixAllInSet)
            {
                var nextSearch = litPos + 1;
                while (nextSearch <= input.Length - lit.Length)
                {
                    var nextIdx = input.Slice(nextSearch).IndexOf(lit);
                    if (nextIdx < 0)
                    {
                        break;
                    }

                    var nextLitPos = nextSearch + nextIdx;
                    var connected = true;
                    for (var k = matchEnd; k < nextLitPos; k++)
                    {
                        if (!set.MatchesBmpChar(input[k]))
                        { connected = false; break; }
                    }
                    if (!connected)
                    {
                        break;
                    }

                    matchEnd = nextLitPos + lit.Length;
                    nextSearch = nextLitPos + 1;
                }
            }
            state.TryCharge(matchEnd - startAt + 2, startAt);
            PublishGroup0(matchStart, matchEnd - matchStart, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteAtomicSequence(
        ReadOnlySpan<char> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var steps = _seqSteps!;
        var anchorLit = steps[_seqAnchorIndex].LiteralChars.AsSpan();
        var saveCaptures = operation == SearchOperation.Captures;

        if (_seqPrefixFixedLen >= 0)
        {
            var searchPos = startAt + _seqPrefixFixedLen;
            while (searchPos <= input.Length - anchorLit.Length)
            {
                var idx = input.Slice(searchPos).IndexOf(anchorLit);
                if (idx < 0)
                {
                    break;
                }

                var anchorPos = searchPos + idx;
                var matchStart = anchorPos - _seqPrefixFixedLen;

                // Verify prefix steps 0 .. _seqAnchorIndex - 1
                var cur = matchStart;
                var ok = true;
                for (var s = 0; s < _seqAnchorIndex; s++)
                {
                    ref readonly var step = ref steps[s];
                    var stepStart = cur;
                    if (step.IsLiteral)
                    {
                        if (!input.Slice(cur, step.Min).SequenceEqual(step.LiteralChars.AsSpan()))
                        { ok = false; break; }
                        cur += step.Min;
                    }
                    else
                    {
                        var cls = step.Class!;
                        for (var k = 0; k < step.Min; k++)
                        {
                            if (!cls.MatchesBmpChar(input[cur + k]))
                            { ok = false; break; }
                        }
                        if (!ok)
                        {
                            break;
                        }

                        cur += step.Min;
                    }
                    if (saveCaptures && step.CaptureGroup > 0)
                    {
                        groups[step.CaptureGroup] = new(true, stepStart, cur - stepStart);
                    }
                }
                if (!ok)
                {
                    searchPos = anchorPos + 1;
                    continue;
                }

                if (saveCaptures && steps[_seqAnchorIndex].CaptureGroup > 0)
                {
                    groups[steps[_seqAnchorIndex].CaptureGroup] = new(true, anchorPos, anchorLit.Length);
                }

                cur = anchorPos + anchorLit.Length;

                // Verify suffix steps _seqAnchorIndex + 1 .. end
                if (VerifySuffixSteps(input, steps, _seqAnchorIndex + 1, ref cur, saveCaptures, groups))
                {
                    state.TryCharge(cur - startAt + 2, startAt);
                    match = new(matchStart, cur - matchStart);
                    if (saveCaptures)
                    {
                        groups[0] = new(true, matchStart, cur - matchStart);
                    }

                    status = RegexSearchStatus.Match;
                    return true;
                }
                searchPos = anchorPos + 1;
            }
        }
        else
        {
            // _seqAnchorIndex == 1 and step 0 is [class]+
            var cls0 = steps[0].Class!;
            var searchPos = startAt + 1;
            while (searchPos <= input.Length - anchorLit.Length)
            {
                var idx = input.Slice(searchPos).IndexOf(anchorLit);
                if (idx < 0)
                {
                    break;
                }

                var anchorPos = searchPos + idx;
                if (!cls0.MatchesBmpChar(input[anchorPos - 1]))
                {
                    searchPos = anchorPos + 1;
                    continue;
                }
                var matchStart = anchorPos - 1;
                while (matchStart > startAt && cls0.MatchesBmpChar(input[matchStart - 1]))
                {
                    matchStart--;
                }

                if (saveCaptures)
                {
                    if (steps[0].CaptureGroup > 0)
                    {
                        groups[steps[0].CaptureGroup] = new(true, matchStart, anchorPos - matchStart);
                    }

                    if (steps[1].CaptureGroup > 0)
                    {
                        groups[steps[1].CaptureGroup] = new(true, anchorPos, anchorLit.Length);
                    }
                }
                var cur = anchorPos + anchorLit.Length;
                if (VerifySuffixSteps(input, steps, 2, ref cur, saveCaptures, groups))
                {
                    state.TryCharge(cur - startAt + 2, startAt);
                    match = new(matchStart, cur - matchStart);
                    if (saveCaptures)
                    {
                        groups[0] = new(true, matchStart, cur - matchStart);
                    }

                    status = RegexSearchStatus.Match;
                    return true;
                }
                searchPos = anchorPos + 1;
            }
        }

        if (saveCaptures)
        {
            groups[.._groupCount].Clear();
        }

        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private static bool VerifySuffixSteps(
        ReadOnlySpan<char> input, CoreSeqStep[] steps, int startStep,
        ref int cur, bool saveCaptures, Span<RegexCapture> groups)
    {
        for (var s = startStep; s < steps.Length; s++)
        {
            ref readonly var step = ref steps[s];
            var stepStart = cur;
            if (step.IsLiteral)
            {
                var lit = step.LiteralChars.AsSpan();
                if (cur + lit.Length > input.Length || !input.Slice(cur, lit.Length).SequenceEqual(lit))
                {
                    return false;
                }

                cur += lit.Length;
            }
            else if (step.Min == step.Max)
            {
                if (cur + step.Min > input.Length)
                {
                    return false;
                }

                var cls = step.Class!;
                for (var k = 0; k < step.Min; k++)
                {
                    if (!cls.MatchesBmpChar(input[cur + k]))
                    {
                        return false;
                    }
                }
                cur += step.Min;
            }
            else
            {
                // Greedy unbounded step at end of pattern
                var cls = step.Class!;
                var count = 0;
                while (cur < input.Length && cls.MatchesBmpChar(input[cur]))
                {
                    cur++;
                    count++;
                }
                if (count < step.Min)
                {
                    return false;
                }
            }
            if (saveCaptures && step.CaptureGroup > 0)
            {
                groups[step.CaptureGroup] = new(true, stepStart, cur - stepStart);
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFirstInSet(ReadOnlySpan<char> span, CharacterClass set, SearchValues<char>? sv, bool svNegated)
    {
        if (sv is not null)
        {
            return svNegated ? span.IndexOfAnyExcept(sv) : span.IndexOfAny(sv);
        }

        for (var i = 0; i < span.Length; i++)
        {
            if (set.MatchesBmpChar(span[i]))
            {
                return i;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindFirstNotInSet(ReadOnlySpan<char> span, CharacterClass set, SearchValues<char>? sv, bool svNegated)
    {
        if (sv is not null)
        {
            return svNegated ? span.IndexOfAny(sv) : span.IndexOfAnyExcept(sv);
        }

        for (var i = 0; i < span.Length; i++)
        {
            if (!set.MatchesBmpChar(span[i]))
            {
                return i;
            }
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishGroup0(int index, int length, SearchOperation operation, Span<RegexCapture> groups, out RegexMatchRange match)
    {
        match = operation == SearchOperation.Boolean ? default : new(index, length);
        if (operation == SearchOperation.Captures)
        {
            groups[0] = new(true, index, length);
            for (var g = 1; g < _groupCount; g++)
            {
                groups[g] = default;
            }
        }
    }

    // Returns the next offset at or after from where the prefix begins, input.Length when there is none,
    // or -1 after a budget failure (state.Failure then carries the status).
    internal int Next<TUnit>(ReadOnlySpan<TUnit> input, int from, ref RunState state) where TUnit : unmanaged
    {
        if (_isStartAnchored && from > 0)
        {
            if (state.Budget.IsUnconstrained(input.Length - from + 4))
            {
                state.TryCharge(1, from);
                return input.Length;
            }
        }
        if (typeof(TUnit) == typeof(char))
        {
            var chars = MemoryMarshal.Cast<TUnit, char>(input);
            if (_chars is not null)
            {
                return Scan(chars, _chars, from, ref state);
            }

            if (_stringSet is not null && state.Budget.IsUnconstrained(chars.Length - from + 16))
            {
                var idx = chars.Slice(from).IndexOfAny(_stringSet);
                var scanned = idx >= 0 ? idx + 1 : chars.Length - from;
                if (!state.TryCharge(scanned, from))
                {
                    return -1;
                }

                return idx >= 0 ? from + idx : chars.Length;
            }
            if (_charSet is not null)
            {
                return ScanSet(chars, _charSet, from, ref state);
            }
        }
        else if (typeof(TUnit) == typeof(byte))
        {
            var bytes = MemoryMarshal.Cast<TUnit, byte>(input);
            if (_bytes is not null)
            {
                return Scan(bytes, _bytes, from, ref state);
            }

            if (_byteSet is not null)
            {
                return ScanSet(bytes, _byteSet, from, ref state);
            }
        }
        return from;
    }

    private static int Scan<T>(ReadOnlySpan<T> input, T[] prefix, int from, ref RunState state)
        where T : unmanaged, IEquatable<T>
    {
        var remaining = input.Length - from;
        if (remaining > 0 && state.Budget.IsUnconstrained(remaining + 16))
        {
            var found = input.Slice(from).IndexOf(prefix);
            var scanned = found >= 0 ? found + 1 : remaining;
            if (!state.TryCharge(scanned, from))
            {
                return -1;
            }

            return found >= 0 ? from + found : input.Length;
        }
        var position = from;
        var ramp = InitialChunk;
        while (position < input.Length)
        {
            var chunk = AcceleratorWork.TryReserveScan(ref state, Math.Min(input.Length - position, ramp), position);
            if (chunk < 0)
            {
                return -1;
            }

            ramp = Math.Min(ramp * 2, AcceleratorWork.ScanChunkUnits);
            var window = input.Slice(position, Math.Min(input.Length - position, chunk + prefix.Length - 1));
            var found = window.IndexOf(prefix);
            if (found >= 0)
            {
                return position + found;
            }

            position += chunk;
        }
        return input.Length;
    }

    private static int ScanSet<T>(ReadOnlySpan<T> input, SearchValues<T> set, int from, ref RunState state)
        where T : unmanaged, IEquatable<T>
    {
        var remaining = input.Length - from;
        if (remaining > 0 && state.Budget.IsUnconstrained(remaining + 16))
        {
            var found = input.Slice(from).IndexOfAny(set);
            var scanned = found >= 0 ? found + 1 : remaining;
            if (!state.TryCharge(scanned, from))
            {
                return -1;
            }

            return found >= 0 ? from + found : input.Length;
        }
        var position = from;
        var ramp = InitialChunk;
        while (position < input.Length)
        {
            var chunk = AcceleratorWork.TryReserveScan(ref state, Math.Min(input.Length - position, ramp), position);
            if (chunk < 0)
            {
                return -1;
            }

            ramp = Math.Min(ramp * 2, AcceleratorWork.ScanChunkUnits);
            var found = input.Slice(position, chunk).IndexOfAny(set);
            if (found >= 0)
            {
                return position + found;
            }

            position += chunk;
        }
        return input.Length;
    }
}
