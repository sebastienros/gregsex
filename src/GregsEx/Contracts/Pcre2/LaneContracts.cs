#nullable enable
using GregsEx.IR;

namespace GregsEx.Contracts.Pcre2;

internal enum Pcre2PlanStage { Syntax, Executable }
internal enum Pcre2NodeKind
{
    Empty, Literal, Class, Dot, CodeUnit, Newline, Grapheme, Sequence, Alternative,
    Repeat, Capture, Backreference, Start, End, AbsoluteStart, AbsoluteEnd,
    BeforeFinalNewline, StartOffset, WordBoundary, NotWordBoundary, Atomic,
    Assertion, ScanSubstring, ScriptRun, Subroutine, Conditional, Define,
    Callout, ResetStart, Verb
}
internal enum Pcre2Quantifier { Greedy, Lazy, Possessive }
internal enum Pcre2ClassKind { Intervals, Property, TableClass, Union, Intersection, Subtraction, SymmetricDifference, Complement }
internal enum Pcre2PropertyKind { MajorCategory, ParticularCategory, Script, ScriptExtensions, Binary, BidiClass, Special }
internal enum Pcre2ReferenceKind { Absolute, Relative, Named, WholePattern }
internal enum Pcre2AssertionKind { PositiveAhead, NegativeAhead, PositiveBehind, NegativeBehind, NonAtomicAhead, NonAtomicBehind }
internal enum Pcre2ConditionKind { CaptureSet, Recursion, Assertion, Version, AnyRecursion }
internal enum Pcre2VerbKind { Fail, Accept, Commit, Prune, Skip, Then, Mark }
internal readonly record struct Pcre2PropertyKey(Pcre2PropertyKind Kind, int Id);
internal readonly record struct Pcre2Interval(int First, int Last);
internal readonly record struct Pcre2Slice(int Offset, int Count);
internal readonly record struct Pcre2ClassNode(Pcre2ClassKind Kind, int Left, int Right,
    Pcre2Slice Intervals, Pcre2PropertyKey Property, int TableClass);

// Children are bottom-up structural node IDs; recursion is a reference target,
// not a structural cycle. Operand indexes the kind-specific metadata table.
internal readonly record struct Pcre2Node(Pcre2NodeKind Kind, Pcre2Slice Children,
    int Operand, int Min, int Max, Pcre2Quantifier Quantifier,
    Pcre2CompileFlags Flags, Pcre2ExtraFlags ExtraFlags, SourceRange Source);
internal readonly record struct Pcre2GroupDefinition(int Number, int ParentDefinition, int EntryNode);
internal readonly record struct Pcre2Name(string Name, Pcre2Slice Numbers);
internal readonly record struct Pcre2Reference(Pcre2ReferenceKind Kind, int Number, int NameIndex,
    Pcre2Slice Candidates, int TargetDefinition);
internal readonly record struct Pcre2Assertion(Pcre2AssertionKind Kind, int MinimumLength, int MaximumLength, bool VariableLength = false);
internal readonly record struct Pcre2LookbehindBranch(int AssertionNode, int BranchNode, int MaximumLength);
// -1 denotes an absent native code-unit hint. UTF8 stores leading bytes; width16
// bitmap bit255 denotes code unit255 and every wider unit. Inference is lowering.
internal readonly record struct Pcre2SearchHints(int MinimumLength, int MaximumLookbehind, bool MatchesEmpty,
    bool StartLine = false, int FirstCodeUnit = -1, bool FirstCodeUnitCaseless = false,
    int RequiredCodeUnit = -1, bool RequiredCodeUnitCaseless = false, int? MaximumBackreference = null);
internal readonly record struct Pcre2Condition(Pcre2ConditionKind Kind, int ReferenceIndex,
    int AssertionNode, int VersionMajor, int VersionMinor, bool AtLeast);
internal readonly record struct Pcre2CalloutSite(int Number, bool Automatic, bool IsString,
    Pcre2Slice StringUnits, int PatternPosition, int NextItemLength, int StringSourceOffset);
internal readonly record struct Pcre2Control(Pcre2VerbKind Kind, int MarkIndex);
// Source uint32 limits are INFO provenance; finite managed limits stay separate.
internal readonly record struct Pcre2PatternLimits(long? MatchWork, int? Depth, long? HeapBytes,
    uint? NativeMatchLimit = null, uint? NativeDepthLimit = null, uint? NativeHeapLimit = null);

// All views are borrowed until Pcre2Program.TryCreate copies under CompileBudget.
internal readonly ref struct Pcre2PlanData
{
    internal readonly ReadOnlySpan<Pcre2Node> Nodes;
    internal readonly ReadOnlySpan<int> Children;
    internal readonly ReadOnlySpan<Pcre2ClassNode> Classes;
    internal readonly ReadOnlySpan<Pcre2Interval> Intervals;
    internal readonly ReadOnlySpan<Pcre2GroupDefinition> Groups;
    internal readonly ReadOnlySpan<Pcre2Name> Names;
    internal readonly ReadOnlySpan<int> Numbers;
    internal readonly ReadOnlySpan<Pcre2Reference> References;
    internal readonly ReadOnlySpan<Pcre2Assertion> Assertions;
    internal readonly ReadOnlySpan<Pcre2Condition> Conditions;
    internal readonly ReadOnlySpan<Pcre2CalloutSite> Callouts;
    internal readonly ReadOnlySpan<Pcre2Control> Controls;
    internal readonly ReadOnlySpan<Pcre2Slice> Marks;
    internal readonly ReadOnlySpan<Pcre2Slice> ScanReferences;
    internal readonly ReadOnlySpan<int> Units;
    // Native encoded boundary -> original pattern source boundary, including end.
    internal readonly ReadOnlySpan<int> SourceMap;
    internal readonly ReadOnlySpan<Pcre2LookbehindBranch> LookbehindBranches;
    internal readonly bool HasLookbehindMetadata;
    internal Pcre2PlanData(ReadOnlySpan<Pcre2Node> nodes, ReadOnlySpan<int> children = default,
        ReadOnlySpan<Pcre2ClassNode> classes = default, ReadOnlySpan<Pcre2Interval> intervals = default,
        ReadOnlySpan<Pcre2GroupDefinition> groups = default, ReadOnlySpan<Pcre2Name> names = default,
        ReadOnlySpan<int> numbers = default, ReadOnlySpan<Pcre2Reference> references = default,
        ReadOnlySpan<Pcre2Assertion> assertions = default, ReadOnlySpan<Pcre2Condition> conditions = default,
        ReadOnlySpan<Pcre2CalloutSite> callouts = default, ReadOnlySpan<Pcre2Control> controls = default,
        ReadOnlySpan<Pcre2Slice> marks = default, ReadOnlySpan<Pcre2Slice> scanReferences = default,
        ReadOnlySpan<int> units = default, ReadOnlySpan<int> sourceMap = default,
        ReadOnlySpan<Pcre2LookbehindBranch> lookbehindBranches = default, bool hasLookbehindMetadata = false)
    {
        Nodes = nodes;
        Children = children;
        Classes = classes;
        Intervals = intervals;
        Groups = groups;
        Names = names;
        Numbers = numbers;
        References = references;
        Assertions = assertions;
        Conditions = conditions;
        Callouts = callouts;
        Controls = controls;
        Marks = marks;
        ScanReferences = scanReferences;
        Units = units;
        SourceMap = sourceMap;
        LookbehindBranches = lookbehindBranches;
        HasLookbehindMetadata = hasLookbehindMetadata;
    }
}

// Direct immutable table views are bound once. No virtual call per character.
internal readonly record struct Pcre2UnicodeRecord(int ParticularCategory, int Script, int BidiClass, int GraphemeBreak,
    int OtherCaseDelta, int CaseSetOffset, int CaseSetCount, int ScriptSetOffset, int ScriptSetCount,
    int BooleanSetOffset, int BooleanSetCount, int DigitSet);
internal readonly record struct Pcre2SimpleCase(int CodePoint, int Lower, int Upper, int Title);
internal readonly ref struct Pcre2DataView
{
    internal readonly ReadOnlySpan<ushort> Stage1;
    internal readonly ReadOnlySpan<ushort> Stage2;
    internal readonly ReadOnlySpan<Pcre2UnicodeRecord> Records;
    internal readonly ReadOnlySpan<int> CaseSets;
    internal readonly ReadOnlySpan<uint> ScriptSets;
    internal readonly ReadOnlySpan<uint> BooleanSets;
    internal readonly ReadOnlySpan<Pcre2SimpleCase> OutputCases;
    internal readonly ReadOnlySpan<byte> LowByteTables;
    internal Pcre2DataView(ReadOnlySpan<ushort> stage1, ReadOnlySpan<ushort> stage2,
        ReadOnlySpan<Pcre2UnicodeRecord> records, ReadOnlySpan<int> caseSets,
        ReadOnlySpan<uint> scriptSets, ReadOnlySpan<uint> booleanSets,
        ReadOnlySpan<Pcre2SimpleCase> outputCases, ReadOnlySpan<byte> lowByteTables)
    {
        Stage1 = stage1;
        Stage2 = stage2;
        Records = records;
        CaseSets = caseSets;
        ScriptSets = scriptSets;
        BooleanSets = booleanSets;
        OutputCases = outputCases;
        LowByteTables = lowByteTables;
    }
}
internal interface IPcre2Data
{
    string DataId { get; }
    string SourceDigest { get; }
    bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value, out Pcre2PropertyKey property);
    bool IsValidGroupName(ReadOnlySpan<char> name, bool utf);
    Pcre2DataView Bind(Pcre2CharacterTables tables);
}
internal interface IPcre2SyntaxParser
{
    bool TryParse(ReadOnlySpan<char> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic);
    bool TryParse(ReadOnlySpan<byte> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic);
}
internal interface IPcre2Lowerer
{
    bool TryLower(Pcre2Program syntax, IPcre2Data data, ref CompileBudget budget,
        out Pcre2Program? executable, out Pcre2Diagnostic diagnostic);
}
internal interface IPcre2Compiler
{
    bool TryCompile(ReadOnlySpan<char> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? program, out Pcre2Diagnostic diagnostic);
    bool TryCompile(ReadOnlySpan<byte> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? program, out Pcre2Diagnostic diagnostic);
}

internal enum Pcre2ValidationPolicy { NativeWindow, WholeSubjectProjection }
internal enum Pcre2ReadStatus { End, Value, InvalidBarrier }
internal readonly record struct Pcre2ReadResult(Pcre2ReadStatus Status, int Value, int Width);
internal readonly record struct Pcre2ValidationResult(bool Valid, int NativeError = 0, RegexExecutionFailure Failure = default);
#if !GREGSEX_GENERATOR
internal interface IPcre2TextReader<TUnit> where TUnit : unmanaged
{
    Pcre2ValidationResult Validate(ReadOnlySpan<TUnit> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state);
    Pcre2ReadResult Read(ReadOnlySpan<TUnit> input, int offset, int visibleEnd, bool codeUnit,
        Pcre2Profile profile, ref RunState state);
    Pcre2ReadResult ReadPrevious(ReadOnlySpan<TUnit> input, int offset,
        Pcre2Profile profile, ref RunState state);
    int AdvanceAfterEmpty(ReadOnlySpan<TUnit> input, int offset, Pcre2Profile profile, ref RunState state);
}
#endif

internal readonly record struct Pcre2ExecutionCapacity
{
    internal int Frames { get; }
    internal int CaptureEvents { get; }
    internal int Windows { get; }
    internal int UnicodeScratch { get; }
    internal static Pcre2ExecutionCapacity Default => new(4096, 65536, 4096, 512);
    internal Pcre2ExecutionCapacity(int frames, int captureEvents, int windows, int unicodeScratch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unicodeScratch);
        Frames = frames;
        CaptureEvents = captureEvents;
        Windows = windows;
        UnicodeScratch = unicodeScratch;
    }
    internal RegexWorkspaceCapacity Required(int groupCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groupCount);
        return new(checked(64 + Frames * Pcre2Frame.Slots + Windows * Pcre2Window.Slots),
            checked(groupCount * 2), checked(CaptureEvents * Pcre2CaptureUndo.Slots + UnicodeScratch));
    }
    // Pair staging is a separate prepared typed array, never aliased with caller
    // short output or integer registers. Conservative logical16 bytes/pair.
    internal Pcre2StorageRequirements RequiredStorage(int groupCount)
    {
        var integers = Required(groupCount);
        return new(integers, groupCount, checked(integers.RequiredBytes + 32L + 16L * groupCount));
    }
}
internal readonly record struct Pcre2StorageRequirements(RegexWorkspaceCapacity Integers, int CallbackPairSlots, long RequiredBytes);
internal enum Pcre2FrameKind { Choice, Repeat, Atomic, Assertion, Scan, Subroutine, ScriptRun, Verb, Predicate, Branch }
// Internal layout v3: legacy named slots0..15, watermark16, saved-window tag17,
// window18..23, active-call tag24, NOTEOL scope25. No persisted/public ABI exists.
// Reprepare from Required()/RequiredStorage(); v1-sized buffers are rejected.
internal readonly record struct Pcre2Frame(Pcre2FrameKind Kind, int ResumeNode, int InputPosition,
    int CaptureCursor, int WindowDepth, int CallDepth, int ReportStart, int MarkIndex,
    int RepeatCount, int ScopeNode, int ChildCursor, int StartMatch, int LastCapture,
    int CaptureTop, Pcre2CalloutFlags PendingFlags, int Auxiliary,
    int RecurseLastUsedPosition = 0, bool HasSavedWindow = false, Pcre2Window SavedWindow = default, bool CallActive = true,
    bool NoteolCleared = false)
{
    internal const int LayoutVersion = 3;
    internal const int Slots = 26;
}
// Undo slots: group, previousStart, previousEnd, previousParticipation(0/1).
internal readonly record struct Pcre2CaptureUndo(int Group, int Start, int End, bool Participated)
{
    internal const int Slots = 4;
}
// Temporary end does not replace original input beginning. ScanRequiredStart
// is a candidate bound, not a sliced subject. Continuation returns to core loop.
internal readonly record struct Pcre2Window(int VisibleEnd, int ScanRequiredStart, int ReturnNode,
    int SavedPosition, int FrameCheckpoint, int CaptureCheckpoint)
{
    internal const int Slots = 6;
}
internal enum Pcre2StepKind
{
    Continue, Backtrack, EnterAssertion, EnterSubroutine, Unicode,
    Control, Return, Accept, Partial, SourceAbort, ResourceFailure
}
internal readonly record struct Pcre2Step(Pcre2StepKind Kind, int Node = -1, int NativeReturnCode = 0);
internal enum Pcre2ExecutionEvent
{
    CandidateStart, EnterNode, Backtrack, AssertionReturn,
    SubroutineReturn, ResetStart, CandidateComplete, SearchComplete
}

// One state per invocation. All modules mutate this same state/budget; never
// restart a RunState in assertions, calls, validation or host iteration.
internal ref struct Pcre2VmState
{
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly Pcre2OperationLimits Limits;
    internal readonly int SearchStart, SubjectLength;
    internal int Node, Position, CandidateStart, ReportStart, VisibleEnd;
    internal int FrameCount, CaptureCursor, WindowDepth, CallDepth, MarkIndex;
    internal int CaptureTop, CaptureLast, CalloutCount;
    // Global source last_used_ptr is monotonic within a candidate, never rollback.
    internal int LastUsedPosition;
    internal bool HasPredicateResult, PredicateResult;
    internal int PredicateNode;
    internal Pcre2CalloutFlags PendingFlags;
    // Explicit, once-only remainder-return occurrence. No serialized frame slot
    // or private header is needed; the entry is an inline invocation value.
    internal bool HasVerbReturn;
    internal Pcre2Frame VerbReturnFrame;
    internal bool HasPartial;
    internal bool NoteolCleared;
    internal int PartialStart, PartialEnd, SourceReturnCode;
    internal Pcre2VmState(Pcre2ExecutionCapacity capacity, Pcre2MatchLimits limits, int subjectLength,
        Pcre2PatternLimits patternLimits = default, int startAt = 0)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfNegative(subjectLength);
        if (startAt < 0 || startAt > subjectLength)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }

        this = default;
        Capacity = capacity;
        Limits = Pcre2OperationLimits.Resolve(limits, patternLimits);
        SearchStart = startAt;
        SubjectLength = subjectLength;
        Position = startAt;
        CandidateStart = startAt;
        ReportStart = startAt;
        LastUsedPosition = startAt;
        PredicateNode = -1;
        VisibleEnd = subjectLength;
        MarkIndex = -1;
        CaptureTop = 1;
    }
}
internal readonly record struct Pcre2OperationLimits(long MaxWork, int MaxDepth, long MaxWorkspaceBytes,
    int MaxCaptureEvents, int MaxCallouts)
{
    internal static Pcre2OperationLimits Resolve(Pcre2MatchLimits limits, Pcre2PatternLimits pattern) => new(
        Math.Min(limits.MaxWork, pattern.MatchWork ?? limits.MaxWork), Math.Min(limits.MaxDepth, pattern.Depth ?? limits.MaxDepth),
        Math.Min(limits.MaxWorkspaceBytes, pattern.HeapBytes ?? limits.MaxWorkspaceBytes), limits.MaxCaptureEvents, limits.MaxCallouts);
}
// Module calls are statically bound by the composition owner to concrete struct
// implementations. Never box these modules or invoke Step through an interface
// instance inside a consumed-character loop. IPcre2Executor is the once/search boundary.
#if !GREGSEX_GENERATOR
internal interface IPcre2VmKernel
{
#if NET8_0_OR_GREATER
    static abstract Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchOptions options, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>;
    // Publish only the initialized logical slice. Native failure leaves caller
    // pairs unchanged, partial publishes group0 only; rc0 denotes short success.
    static abstract Pcre2MatchResult Publish(Pcre2Program program, ref Pcre2VmState machine, ref RunState state,
        Span<Pcre2OffsetPair> pairs);
#endif
}
internal interface IPcre2AssertionOps
{
#if NET8_0_OR_GREATER
    static abstract Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>;
#endif
}
internal interface IPcre2RecursionOps
{
#if NET8_0_OR_GREATER
    static abstract Pcre2Step Step(Pcre2Program program, Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state);
#endif
}
internal interface IPcre2UnicodeOps
{
#if NET8_0_OR_GREATER
    static abstract Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>;
#endif
}
internal interface IPcre2Controls
{
#if NET8_0_OR_GREATER
    static abstract Pcre2Step Notify(Pcre2ExecutionEvent occurrence, Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2CharCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs, ref Pcre2VmState machine, ref RunState state);
    static abstract Pcre2Step Notify(Pcre2ExecutionEvent occurrence, Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs, ref Pcre2VmState machine, ref RunState state);
#endif
}
#endif
internal interface IPcre2Executor
{
    Pcre2Program Program { get; }
    Pcre2StorageRequirements GetRequiredCapacity(Pcre2ExecutionCapacity capacity);
    Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Pcre2MatchOptions options,
        Pcre2CharCallbacks? callbacks, Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy,
        ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs);
    Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Pcre2MatchOptions options,
        Pcre2ByteCallbacks? callbacks, Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy,
        ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs);
}
internal interface IPcre2ExecutorFactory
{
    bool TryCreate(Pcre2Program program, IPcre2Data data, ref CompileBudget budget,
        out IPcre2Executor? executor, out Pcre2Diagnostic diagnostic);
}
internal interface IPcre2Host
{
    // Uses the same outer RunState/limits through every match and callback.
    Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<char> input, int startAt,
        ReadOnlySpan<char> replacement, Span<char> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs);
    Pcre2SubstituteResult Substitute(IPcre2Executor executor, ReadOnlySpan<byte> input, int startAt,
        ReadOnlySpan<byte> replacement, Span<byte> destination, Pcre2SubstituteFlags flags,
        Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks, Pcre2HostLimits limits,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs, Pcre2MatchResult precomputedResult,
        Pcre2ExecutionCapacity capacity, ref RunState state, Span<Pcre2OffsetPair> callbackPairs);
}
