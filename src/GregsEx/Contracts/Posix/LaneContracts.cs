#nullable enable
using GregsEx.IR;

namespace GregsEx.Contracts.Posix;

internal enum PosixNodeKind { Empty, Literal, Class, Any, Sequence, Alternative, Repeat, Capture, Backreference, Start, End }

// Bottom-up OWNED tree; no shared children. X/Y=-1 when unused, Value=0
// unless Literal/Class/Capture/Backreference. Repeat Max=-1 means unbounded;
// finite counts <=255. Source is original pattern units, never transcoded.
internal readonly record struct PosixNode(PosixNodeKind Kind, int X, int Y, int Value,
    int Min, int Max, SourceRange Source);

// Alphabet excludes NUL. Sets are normalized once, INCLUDING IgnoreCase
// expansion before complement and REG_NEWLINE exclusion for negative lists.
internal readonly record struct PosixByteSet(ulong A, ulong B, ulong C, ulong D)
{
    internal bool Contains(byte value) => (((value >> 6) switch
    { 0 => A, 1 => B, 2 => C, _ => D }) & (1UL << (value & 63))) != 0;
}

internal enum PosixCharacterClass { Alnum, Alpha, Blank, Cntrl, Digit, Graph, Lower, Print, Punct, Space, Upper, Xdigit }

// Compile boundary only; executor binds immutable spans once, no virtual
// class/case calls inside character loops and no native provider in production.
internal interface IPosixLocaleData
{
    PosixLocale Identity { get; }
    ReadOnlySpan<ushort> ClassMasks { get; } // 256 entries, bit order enum above
    ReadOnlySpan<byte> FoldToLower { get; } // 256 entries, ASCII A-Z -> a-z
    ReadOnlySpan<byte> CollationOrdinals { get; } // 256 entries, ordinal identity
    bool TryGetClass(ReadOnlySpan<byte> name, out PosixCharacterClass value);
    bool TryGetCollatingElement(ReadOnlySpan<byte> name, out byte value); // exactly one nonNUL byte
    // Equivalence for this provider is a singleton; no multi-character elements.
}

internal interface IPosixCompiler
{
    bool TryCompile(ReadOnlySpan<byte> pattern, PosixProfile profile, RegexInputKind inputKind,
        IPosixLocaleData locale, ref CompileBudget budget,
        out PosixProgram? program, out PosixRegexDiagnostic diagnostic);
    // Convenience units 1..255 map one-to-one; all source spans remain UTF-16.
    bool TryCompile(ReadOnlySpan<char> pattern, PosixProfile profile, RegexInputKind inputKind,
        IPosixLocaleData locale, ref CompileBudget budget,
        out PosixProgram? program, out PosixRegexDiagnostic diagnostic);
}

// Logical ceilings are enforced even with oversized buffers. Defaults are
// safety choices, not POSIX constants. Smaller positive internal test bindings
// force history/frame failure independently from physical array capacity.
internal readonly record struct PosixExecutionCapacity
{
    internal int Frames { get; }
    internal int HistoryEvents { get; }
    internal static PosixExecutionCapacity Default => new(4096, 65536);
    internal PosixExecutionCapacity(int frames, int historyEvents)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(historyEvents);
        Frames = frames;
        HistoryEvents = historyEvents;
    }
    internal bool IsValid => Frames > 0 && HistoryEvents > 0;
}

internal interface IPosixExecutor
{
    PosixProgram Program { get; }
    RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, PosixExecutionCapacity capacity);
    // Original nonNUL byte input; validate ENTIRE subject in SAME RunState
    // before any successful result. startAt lower bound never slices anchors.
    // One boundary per invocation. All histories, reference verification and
    // winner publication charged; no leftmost-first dominance/deduplication.
    // Captures are full GroupCount here, regardless of typed nmatch/NoSub;
    // adapter performs partial/native publication after complete success.
    RegexSearchStatus Search(ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        PosixExecutionFlags flags, PosixExecutionCapacity capacity, ref RunState state,
        Span<RegexCapture> groups, out RegexMatchRange match);
}

internal interface IPosixExecutorFactory
{
    bool TryCreate(PosixProgram program, IPosixLocaleData locale, ref CompileBudget budget,
        out IPosixExecutor? executor, out RegexDiagnostic diagnostic);
}
