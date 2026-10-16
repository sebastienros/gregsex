using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;

namespace GregsEx.Flavors.Pcre2.Runtime.Kernel;

// Composition creates this state once, before Bind. Host retries keep the
// same RunState (including start time); candidates call BeginCandidate only.
internal static class Pcre2KernelOperation
{
    internal static RunState CreateRunState(WorkspaceBuffers buffers, Pcre2OperationLimits limits,
        Pcre2MatchOptions options = default)
    {
        // For a zero denial only initial polling can run. The one-unit positive
        // constructor is never passed to text/VM loops: PollEntry denies first.
        return new(buffers, Math.Max(1, limits.MaxWork), options.CancellationToken, options.Timeout);
    }
    internal static bool PollEntry(Pcre2Program program, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        if (machine.Limits.MaxWork == 0)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Matchlimit; return false; }
        if (machine.Limits.MaxDepth == 0)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Depthlimit; return false; }
        if (machine.Capacity.RequiredStorage(program.GroupCount).RequiredBytes > machine.Limits.MaxWorkspaceBytes)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Heaplimit; return false; }
        if (!state.Buffers.Capacity.Contains(machine.Capacity.Required(program.GroupCount)))
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        // No text inspection or candidate initialization precedes this poll.
        return true;
    }
    // Native rejects this before initializing match data. Only the error code
    // is source-defined: retain neutral host fields and never publish pairs.
    internal static Pcre2MatchResult BadOffset() => new(Pcre2MatchStatus.Error, (int)Pcre2ErrorCode.Badoffset, 0);

    internal static Pcre2MatchFlags EffectiveFlags(Pcre2Program program, Pcre2MatchFlags invocation,
        Pcre2MatchFlags fragment = default) => program.PatternMatchFlags | invocation | fragment;

    internal static Pcre2MatchFlags EffectiveFlags(Pcre2Program program, Pcre2MatchFlags invocation,
        Pcre2MatchFlags fragment, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Kernel.EffectiveContextFlags(program, invocation | fragment, ref machine, ref state);

    internal static bool ApplyFragment(Pcre2TextWindow window, ref Pcre2VmState machine, ref RunState state)
    {
        if (!state.TryCharge(0, machine.Position) || machine.SourceReturnCode != 0 &&
            machine.SourceReturnCode != (int)Pcre2ErrorCode.Partial)
        {
            return false;
        }
        // Source FRAGMENT_RESTART resets the remembered partial, not the shared
        // budget, original SearchStart or compile-time NOTEMPTY directives.
        machine.HasPartial = false;
        machine.PartialStart = machine.PartialEnd = 0;
        machine.SourceReturnCode = 0;
        if (!window.Available || !window.Validation.Valid)
        {
            return false;
        }

        machine.CandidateStart = machine.Position = window.CandidateStart;
        machine.VisibleEnd = window.VisibleEnd;
        return true;
    }

    internal static void ReleaseTransient(ref Pcre2VmState machine)
    {
        // Teardown does not charge attempted rollback after a sticky failure.
        // Next BeginCandidate initializes logical registers before reuse.
        machine.FrameCount = machine.CaptureCursor = machine.WindowDepth = machine.CallDepth = 0;
        machine.HasPredicateResult = false;
        machine.PredicateNode = -1;
    }
}
