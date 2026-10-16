namespace GregsEx.Contracts.Acceleration;

/// <summary>Every optional execution strategy layered over a flavor's reference executor.</summary>
internal enum AcceleratorKind
{
    /// <summary>The reference executor runs unmodified.</summary>
    None = 0,
    /// <summary>Literal prefix/required-literal candidate skipping before the reference executor.</summary>
    Literal,
    /// <summary>Deterministic one-pass capture execution for eligible anchored regular plans.</summary>
    OnePass,
    /// <summary>Visited-state bounded regular backtracker.</summary>
    BoundedBacktracker,
    /// <summary>Bounded lazy DFA with assertion-context-preserving capture recovery.</summary>
    LazyDfa,
    /// <summary>Static table/SearchValues class scanning inside the reference or another accelerator.</summary>
    Tables,
    /// <summary>Delegates produced by explicit or guarded runtime compilation.</summary>
    Compiled
}

[Flags]
internal enum AcceleratorSwitches
{
    None = 0,
    Literal = 1,
    OnePass = 2,
    BoundedBacktracker = 4,
    LazyDfa = 8,
    Tables = 16,
    Compiled = 32,
    // Interpreter-level accelerators; none emits code, so all are NativeAOT-safe.
    Semantic = Literal | OnePass | BoundedBacktracker | LazyDfa | Tables,
    All = Semantic | Compiled
}

/// <summary>Why an accelerator is not used for a pattern or a search. Never an error by itself.</summary>
internal enum AcceleratorFallbackReason
{
    None = 0,
    SwitchedOff,
    Ineligible,
    UnsupportedAssertionContext,
    BuildBudgetExceeded,
    InputWindowTooLarge,
    CacheExhausted,
    NotBeneficial,
    CompilationUnavailable,
    CompilerRejected
}

internal readonly record struct AcceleratorAdmission(AcceleratorKind Kind, AcceleratorFallbackReason Reason, long RetainedBytes)
{
    internal bool IsAdmitted => Kind != AcceleratorKind.None && Reason == AcceleratorFallbackReason.None;
    internal static AcceleratorAdmission Rejected(AcceleratorKind kind, AcceleratorFallbackReason reason)
        => new(kind, reason, 0);
}

#if !NETSTANDARD2_0
// An accelerated executor owns its prebuilt immutable data and always retains the reference
// executor it decorates. Semantics, status, capture and failure reporting are the reference's;
// an accelerator may only change how quickly the same result is reached.
//
// Fallback contract:
//  * A search that cannot continue (cache exhaustion, window too large for a bounded visited set)
//    delegates the ORIGINAL (input, startAt, operation) to Reference with the SAME RunState, so
//    work, timeout and cancellation are cumulative across the accelerated and reference attempts.
//  * Resumption in the middle of a search is allowed only where a lane proves it.
//  * A work, time, cancellation, capacity or invalid-input outcome is never converted to NoMatch.
internal interface IAcceleratedExecutor : IRegularExecutor
{
    AcceleratorKind Kind { get; }
    IRegularExecutor Reference { get; }
}
#endif

// Work accounting for accelerated scans. The reference executors charge at least one unit for every
// start position and every consumed input unit, so charging one unit per scanned unit never exceeds
// a reference charge for the same region. Scans are chunked so cancellation and deadlines are polled
// at least once per ScanChunkUnits input units and a long prefilter cannot hide excessive work.
internal static class AcceleratorWork
{
    internal const int ScanChunkUnits = 1024;
    internal const long ScanUnitCost = 1;

    // Returns the number of units the caller may scan before the next poll, or -1 after a failure;
    // state.Failure then carries the status to report. remaining is the unscanned window length.
    internal static int TryReserveScan(ref RunState state, int remaining, int offset)
    {
        var chunk = Math.Min(remaining, ScanChunkUnits);
        if (chunk <= 0)
        {
            return 0;
        }

        return state.TryCharge(chunk * ScanUnitCost, offset) ? chunk : -1;
    }
}

/// <summary>Frozen expression-lowering limits, checked before FastExpressionCompiler runs.</summary>
internal readonly record struct LoweringLimits(int MaxNodes, int MaxDepth, int MaxEstimatedCodeBytes)
{
    internal static LoweringLimits Default { get; } = new(MaxNodes: 8192, MaxDepth: 64, MaxEstimatedCodeBytes: 256 * 1024);
    internal bool IsValid => MaxNodes > 0 && MaxDepth > 0 && MaxEstimatedCodeBytes > 0;
}

// Direct span delegates. The lowered tree takes spans, a RunState and caller-owned output as parameters;
// closures may capture only immutable plan data. DynamicInvoke, object[] arguments, boxing, input copies and
// captured spans or workspaces are prohibited. ref-struct parameters are legal in custom delegate types.
internal delegate RegexSearchStatus Utf16SearchKernel(ReadOnlySpan<char> input, int startAt,
    SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match);

internal delegate RegexSearchStatus ByteSearchKernel(ReadOnlySpan<byte> input, int startAt,
    SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match);

#if !NETSTANDARD2_0
// Plan-to-expression lowering plus the guarded FastExpressionCompiler adapter. Implementations live in
// Execution/Compiled and never appear in a public signature. A false result is a known ineligibility
// (explicit Compile reports NotSupportedException; Auto keeps the interpreted/accelerated executor).
// Defective trees, invalid emitted code and resource exhaustion are failures, never fallbacks.
internal interface ICompiledPlanLowering
{
    bool TryLower(IRegularExecutor reference, in LoweringLimits limits,
        out IAcceleratedExecutor? compiled, out AcceleratorFallbackReason reason);
}
#endif
