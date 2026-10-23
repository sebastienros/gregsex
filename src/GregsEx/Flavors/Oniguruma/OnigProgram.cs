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
        SplitLazy = 35;
}

// The item a Run instruction repeats.
internal static class RunItem
{
    internal const int Char = 0, Set = 1, SetUnit = 2, AnyNL = 3, Any = 4;
}

// The ASCII units a node can start with, whether it can start above ASCII, and whether it can match empty.
internal readonly record struct OnigStartBits(ulong Low, ulong High, bool NonAscii, bool Empty)
{
    internal static readonly OnigStartBits Any = new(ulong.MaxValue, ulong.MaxValue, true, true);
    internal static readonly OnigStartBits None = new(0, 0, false, false);
    internal static readonly OnigStartBits EmptyOnly = new(0, 0, false, true);

    internal OnigStartBits Union(OnigStartBits other) => new(Low | other.Low, High | other.High, NonAscii || other.NonAscii, Empty || other.Empty);

    internal bool IsAny => Empty || (Low == ulong.MaxValue && High == ulong.MaxValue && NonAscii);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool CanStartWith(int c)
        => Empty || (c < 0 ? false : c < 128 ? ((c < 64 ? Low : High) & (1UL << c)) != 0 : NonAscii);
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
    // A case-sensitive literal every match starts with (after zero-width assertions), two units or more.
    internal string? Prefix { get; init; }
    // A case-sensitive literal every match contains.
    internal string? Required { get; init; }
    internal required int MinLength { get; init; }
    // Whether the whole pattern is one run of a set (\s+, [^"\\]+): the matcher then measures it directly.
    internal int RunSet { get; init; } = -1;

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
