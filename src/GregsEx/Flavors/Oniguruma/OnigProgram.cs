#nullable enable
using System.Buffers;
using System.Runtime.CompilerServices;

namespace GregsEx.Flavors.Oniguruma;

// Instruction opcodes; operands follow the opcode in OnigProgram.Code (see OnigCompiler for each layout).
internal static class Op
{
    internal const int Match = 0, Char = 1, CharIC2 = 2, Str = 3, Set = 4, SetUnit = 5, AnyNL = 6, Any = 7,
        Anchor = 8, Keep = 9, Jmp = 10, Split = 11, Alt = 12, Run = 13, SaveOpen = 14, SaveClose = 15,
        RetIfCalled = 16, LoopInit = 17, LoopHead = 18, LoopTail = 19, AtomicStart = 20, AtomicEnd = 21,
        LookStart = 22, LookEnd = 23, BehindStart = 24, BehindEnd = 25, AssertSet = 26, AssertStr = 27,
        Backref = 28, Call = 29, CondRef = 30, CondStart = 31, CondEnd = 32, Absent = 33, Fail = 34,
        SplitLazy = 35, AssertLiterals = 37;
}

// The item a Run instruction repeats.
internal static class RunItem
{
    internal const int Char = 0, Set = 1, SetUnit = 2, AnyNL = 3, Any = 4;
}

// The ASCII units a node can start with, whether it can start above ASCII, whether it can match empty (and then
// says nothing about the next unit), and whether it can match at the end of the input (end anchors in follow sets).
internal readonly record struct OnigStartBits(ulong Low, ulong High, bool NonAscii, bool Empty, bool End = false)
{
    internal static readonly OnigStartBits Any = new(ulong.MaxValue, ulong.MaxValue, true, true, true);
    internal static readonly OnigStartBits None = new(0, 0, false, false);
    internal static readonly OnigStartBits EmptyOnly = new(0, 0, false, true);

    internal OnigStartBits Union(OnigStartBits other)
        => new(Low | other.Low, High | other.High, NonAscii || other.NonAscii, Empty || other.Empty, End || other.End);

    internal OnigStartBits Intersect(OnigStartBits other)
        => new(Low & other.Low, High & other.High, NonAscii && other.NonAscii, false, End && other.End);

    internal bool IsAny => Empty || (Low == ulong.MaxValue && High == ulong.MaxValue && NonAscii);

    // Whether no unit can start both: a greedy run of one followed by the other never needs to give units back.
    internal bool DisjointUnits(OnigStartBits other)
        => (Low & other.Low) == 0 && (High & other.High) == 0 && !(NonAscii && other.NonAscii);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CanStartWith(int c)
        => Empty || (c < 0 ? End : c < 128 ? ((c < 64 ? Low : High) & (1UL << c)) != 0 : NonAscii);
}

internal enum OnigStartAnchor : byte { None, BufferStart, SearchStart, LineStart }

// An immutable compiled pattern: instructions, sets, literals and the search prefilters.
internal sealed class OnigProgram
{
    internal required int[] Code { get; init; }
    internal required OnigCharSet[] Sets { get; init; }
    internal required string Literals { get; init; }
    internal required OnigStartBits[] Bits { get; init; }
    // Including group zero.
    internal required int GroupCount { get; init; }
    internal required int LoopCount { get; init; }
    internal required bool UsesCalls { get; init; }
    internal required bool NeedsUndo { get; init; }
    internal required string?[] GroupNames { get; init; }

    internal required OnigStartAnchor StartAnchor { get; init; }
    internal required OnigStartBits StartBits { get; init; }
    // Units a match can start with when StartBits excludes some: IndexOfAny over FirstUnits, or, when non-ASCII units
    // can start one too, IndexOfAnyExcept over the ASCII units that cannot.
    internal SearchValues<char>? FirstUnits { get; init; }
    internal SearchValues<char>? NotFirstAscii { get; init; }
    // ASCII units one of which precedes every match start (a leading look-behind such as (?<=^|[\t &;])), with
    // PrecededAtStart when a match may also start at the start of the input; null when unknown.
    internal SearchValues<char>? PrecededBy { get; init; }
    internal bool PrecededAtStart { get; init; }
    // A case-sensitive literal every match starts with (after zero-width assertions), two units or more.
    internal string? Prefix { get; init; }
    // A case-sensitive literal every match contains.
    internal string? Required { get; init; }
    // Units of which every match contains one (at or after its start), when no literal is required: IndexOfAny over
    // RequiredUnits, or IndexOfAnyExcept over the ASCII units outside a set that also admits non-ASCII units.
    internal SearchValues<char>? RequiredUnits { get; init; }
    internal SearchValues<char>? NotRequiredAscii { get; init; }
    internal required int MinLength { get; init; }
    // The pc of an unbounded greedy or possessive run every match starts with (after SaveOpen), or -1. When an
    // attempt at s fails after that run reached e, every start in (s, e] fails too: its run reaches the same e and
    // tries a subset of the same continuations, so the search resumes after e (as Oniguruma does).
    internal int LeadingRun { get; init; } = -1;
    internal OnigLiteralTable[] Tables { get; init; } = [];

    internal const int MaxCallDepth = 20;

    // Register layout: per group start and end, per group open start, keep start, call depth, loop bank base, the call
    // frames (group, return) and one bank (count, start) of loop registers per call level.
    internal int OpenBase => 2 * GroupCount;
    internal int KeepRegister => 3 * GroupCount;
    internal int DepthRegister => 3 * GroupCount + 1;
    internal int BankRegister => 3 * GroupCount + 2;
    internal int FrameBase => 3 * GroupCount + 3;
    internal int FirstBank => FrameBase + (UsesCalls ? 2 * MaxCallDepth : 0);
    internal int RegisterCount => FirstBank + 2 * LoopCount * (UsesCalls ? MaxCallDepth + 1 : 1);
}

// The literals of a look-around over alternatives such as (?<=^until| until|\tuntil) or (?!nocorrect |readonly$):
// each literal may require a line start before it (^) or a line end after it ($). Literals are bucketed by the unit
// next to the assertion's position (the first unit ahead, the last unit behind), so a test reads one bucket.
internal sealed class OnigLiteralTable
{
    internal const byte LineStartBefore = 1, LineEndAfter = 2;
    private const int NonAsciiKey = 128, EmptyKey = 129;

    private readonly string[] _literals;
    private readonly byte[] _anchors;
    private readonly int[][] _buckets;

    internal OnigLiteralTable(IReadOnlyList<(string Literal, byte Anchors)> entries, bool behind)
    {
        _literals = entries.Select(e => e.Literal).ToArray();
        _anchors = entries.Select(e => e.Anchors).ToArray();
        var lists = new List<int>[130];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = [];
        }

        for (var i = 0; i < _literals.Length; i++)
        {
            var literal = _literals[i];
            var key = literal.Length == 0 ? EmptyKey : behind ? literal[^1] : literal[0];
            lists[key < 128 || key == EmptyKey ? key : NonAsciiKey].Add(i);
        }
        _buckets = lists.Select(l => l.ToArray()).ToArray();
    }

    internal int Count => _literals.Length;

    internal bool Matches(ReadOnlySpan<char> input, int pos, bool behind)
    {
        var key = behind ? (pos > 0 ? input[pos - 1] : -1) : (pos < input.Length ? input[pos] : -1);
        if (key >= 0)
        {
            foreach (var i in _buckets[key < 128 ? key : NonAsciiKey])
            {
                if (Test(input, pos, i, behind))
                {
                    return true;
                }
            }
        }

        foreach (var i in _buckets[EmptyKey])
        {
            if (Test(input, pos, i, behind))
            {
                return true;
            }
        }
        return false;
    }

    private bool Test(ReadOnlySpan<char> input, int pos, int index, bool behind)
    {
        var literal = _literals[index].AsSpan();
        int start, end;
        if (behind)
        {
            if (pos < literal.Length || !input.Slice(pos - literal.Length, literal.Length).SequenceEqual(literal))
            {
                return false;
            }

            (start, end) = (pos - literal.Length, pos);
        }
        else
        {
            if (!input[pos..].StartsWith(literal))
            {
                return false;
            }

            (start, end) = (pos, pos + literal.Length);
        }

        var anchors = _anchors[index];
        if ((anchors & LineStartBefore) != 0 && !(start == 0 || (start < input.Length && input[start - 1] == '\n')))
        {
            return false;
        }

        return (anchors & LineEndAfter) == 0 || end == input.Length || input[end] == '\n';
    }
}
