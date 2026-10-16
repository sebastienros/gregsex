using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Kernel;

// Persistent continuation links and choice checkpoints use the common frame
// serializer. A completed continuation is reclaimed when no choice owns it;
// otherwise it remains immutable until backtracking or an atomic commit.
internal readonly partial struct Pcre2Kernel : IPcre2VmKernel
{
    private const int Active = 0, Choice = 1, Mode = 2, Floor = 3, FragmentEnd = 5, FragmentFloor = 6;
    private const int Enter = 0, Complete = 1, Failing = 2;
    private const int Sequence = -1, Capture = -2, Atomic = -3, Repeat = -4;
    private const int EnterChoice = 0, RepeatMore = 1, RepeatExit = 2;
    private const int ModuleChoice = 3, ModuleRetry = 10, ModulePayload = 11;
    private const int EmptyPartial = 8, EarliestRead = 9;

    internal static bool BeginCandidate(Pcre2Program program, int scanStart,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!program.IsExecutable)
        {
            throw new ArgumentException("Kernel requires an executable plan.", nameof(program));
        }

        if (scanStart < 0 || scanStart > machine.CandidateStart || machine.CandidateStart > machine.VisibleEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(scanStart));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }
        // Native zero is a denial even if an empty tree could accept immediately.
        if (machine.Limits.MaxWork == 0)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Matchlimit; return false; }
        if (machine.Limits.MaxDepth == 0)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Depthlimit; return false; }
        machine.FrameCount = machine.CaptureCursor = machine.WindowDepth = machine.CallDepth = 0;
        machine.Node = program.Root;
        machine.Position = machine.CandidateStart;
        machine.ReportStart = machine.CandidateStart;
        machine.CaptureTop = 1;
        machine.CaptureLast = 0;
        machine.MarkIndex = -1;
        machine.LastUsedPosition = machine.CandidateStart;
        machine.HasPredicateResult = false;
        machine.PredicateNode = -1;
        machine.NoteolCleared = false;
        machine.PendingFlags |= Pcre2CalloutFlags.Startmatch;
        machine.HasVerbReturn = false;
        machine.VerbReturnFrame = default;
        var header = state.Buffers.States;
        header[Active] = header[Choice] = -1;
        header[Mode] = Enter;
        header[Floor] = scanStart;
        header[Accepted] = 0;
        header[FragmentEnd] = machine.VisibleEnd;
        header[FragmentFloor] = scanStart;
        header[EarliestRead] = machine.CandidateStart;
        header[ModuleRetry] = 0;
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!storage.InitializeCaptures(ref machine, ref state))
        {
            return false;
        }

        if (program.SearchHints is { } hints)
        { SetEmptyPartial(hints.MatchesEmpty || hints.MaximumLookbehind > 0, ref state); return true; }
        return AnalyzeEmptyPartial(program, storage.UnicodeScratch, ref machine, ref state);
    }

    // Core regular emptiness analysis uses two slots per syntactic depth, not
    // one per node. Composition may override this for resolved advanced plans.
    internal static void SetEmptyPartial(bool allowed, ref RunState state) => state.Buffers.States[EmptyPartial] = allowed ? 1 : 0;
    private static bool AnalyzeEmptyPartial(Pcre2Program program, Span<int> scratch, ref Pcre2VmState machine, ref RunState state)
    {
        if (scratch.Length < 2)
        {
            return ScratchFailure(2, ref machine, ref state);
        }

        var depth = 1;
        scratch[0] = program.Root;
        scratch[1] = 0;
        bool result = false, behind = false;
        while (depth > 0)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            int offset = (depth - 1) * 2, id = scratch[offset], cursor = scratch[offset + 1] & int.MaxValue;
            var node = program.Nodes[id];
            if (node.Kind == Pcre2NodeKind.Assertion && program.Assertions[node.Operand].Kind is
                Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind)
            {
                behind = true;
            }

            var accumulator = (scratch[offset + 1] & int.MinValue) != 0;
            if (cursor == 0)
            {
                accumulator = node.Kind == Pcre2NodeKind.Sequence;
            }

            if (cursor < node.Children.Count)
            {
                scratch[offset + 1] = (cursor + 1) | (accumulator ? int.MinValue : 0);
                if (scratch.Length < (depth + 1) * 2)
                {
                    return ScratchFailure((depth + 1) * 2, ref machine, ref state);
                }

                scratch[depth * 2] = program.Children[node.Children.Offset + cursor];
                scratch[depth * 2 + 1] = 0;
                depth++;
                continue;
            }
            result = node.Kind switch
            {
                Pcre2NodeKind.Sequence or Pcre2NodeKind.Alternative => accumulator,
                Pcre2NodeKind.Repeat => node.Min == 0 || accumulator,
                Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun => accumulator,
                Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot or Pcre2NodeKind.CodeUnit or
                    Pcre2NodeKind.Newline or Pcre2NodeKind.Grapheme or Pcre2NodeKind.Subroutine => false,
                _ => true
            };
            depth--;
            if (depth > 0)
            {
                var parentOffset = (depth - 1) * 2;
                var parent = program.Nodes[scratch[parentOffset]];
                var old = (scratch[parentOffset + 1] & int.MinValue) != 0;
                var combined = parent.Kind == Pcre2NodeKind.Sequence ? old && result : old || result;
                scratch[parentOffset + 1] = (scratch[parentOffset + 1] & int.MaxValue) | (combined ? int.MinValue : 0);
            }
        }
        SetEmptyPartial(result || behind, ref state);
        return true;
    }
    private static bool ScratchFailure(int required, ref Pcre2VmState machine, ref RunState state)
    {
        state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed, required);
        return false;
    }

    internal static void CompleteNode(int node, ref Pcre2VmState machine, ref RunState state)
    { machine.Node = node; state.Buffers.States[Mode] = Complete; }
    internal static void Fail(ref RunState state) => state.Buffers.States[Mode] = Failing;
    internal static int ScanStart(ref RunState state) => state.Buffers.States[Floor];

    internal static bool PushModuleChoice(Pcre2Program program, int ownerNode, int payload,
        ref Pcre2VmState machine, ref RunState state)
    {
        if ((uint)ownerNode >= (uint)program.Nodes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ownerNode));
        }

        return PushChoice(new(state.Buffers, machine.Capacity, program.GroupCount), ownerNode, ownerNode,
            ModuleChoice, payload, ref machine, ref state);
    }
    internal static bool TakeModuleRetry(ref RunState state, out int payload)
    {
        payload = state.Buffers.States[ModulePayload];
        var retry = state.Buffers.States[ModuleRetry] != 0;
        state.Buffers.States[ModuleRetry] = 0;
        return retry;
    }

    // Only a Scan continuation changes true_end_subject. Assertion visibility
    // bounds must not turn \z into a match at a lookbehind's temporary bound.
    internal static int TrueEnd(Pcre2Program program, int subjectLength, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position) || machine.FrameCount == 0)
        {
            return subjectLength;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        for (var index = state.Buffers.States[Active]; index >= 0;)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
            {
                return subjectLength;
            }

            if (frame.Kind == Pcre2FrameKind.Scan)
            {
                if (frame.WindowDepth == 0)
                {
                    throw new InvalidOperationException("Scan child requires its prepared window before entry.");
                }

                if (!storage.ReadWindow(frame.WindowDepth - 1, ref machine, ref state, out var window))
                {
                    return subjectLength;
                }

                return window.VisibleEnd;
            }
            index = frame.Auxiliary;
        }
        return subjectLength;
    }

    internal static bool RefreshWindow(Pcre2Program program, ref Pcre2VmState machine, ref RunState state)
    {
        var header = state.Buffers.States;
        if (machine.WindowDepth == 0)
        { machine.VisibleEnd = header[FragmentEnd]; header[Floor] = header[FragmentFloor]; return true; }
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!storage.ReadWindow(machine.WindowDepth - 1, ref machine, ref state, out var window))
        {
            return false;
        }

        machine.VisibleEnd = Math.Min(header[FragmentEnd], window.VisibleEnd);
        header[Floor] = Math.Max(header[FragmentFloor], window.ScanRequiredStart);
        return true;
    }

    internal static bool ReadModuleContinuation(Pcre2Program program, ref Pcre2VmState machine,
        ref RunState state, out int index, out Pcre2Frame frame)
    {
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        frame = default;
        for (index = state.Buffers.States[Active]; index >= 0; index = frame.Auxiliary)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out frame))
            {
                return false;
            }

            if (frame.Kind is Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate)
            {
                return true;
            }
        }
        return false;
    }
    // afterIndex=-1 starts at the current active chain; subsequent calls pass
    // the previously returned index. Retained inactive calls are never predicates.
    internal static bool ReadActiveSubroutine(Pcre2Program program, int afterIndex,
        ref Pcre2VmState machine, ref RunState state, out int index, out Pcre2Frame frame)
    {
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        frame = default;
        index = -1;
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position) || machine.FrameCount == 0)
        {
            return false;
        }

        index = state.Buffers.States[Active];
        if (afterIndex >= 0)
        {
            if (!storage.ReadFrame(afterIndex, ref machine, ref state, out frame))
            {
                return false;
            }

            index = frame.Auxiliary;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(afterIndex, -1);
        }

        while (index >= 0)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out frame))
            {
                return false;
            }

            if (frame.Kind == Pcre2FrameKind.Subroutine && frame.CallActive)
            {
                return true;
            }

            if (frame.Auxiliary >= index)
            {
                throw new InvalidOperationException("Invalid continuation chain.");
            }

            index = frame.Auxiliary;
        }
        return false;
    }
    // Module owns semantic restoration. commit discards its local choices;
    // non-atomic success keeps immutable continuations for later retries.
    internal static bool LeaveModuleChild(Pcre2Program program, bool success, bool commit,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!ReadModuleContinuation(program, ref machine, ref state, out var index, out var frame))
        {
            if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
            {
                return false;
            }

            throw new InvalidOperationException("No module continuation.");
        }
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (commit || !success)
        {
            if (!storage.TruncateFrames(index, ref machine, ref state))
            {
                return false;
            }

            if (!FindChoice(storage, ref machine, ref state))
            {
                return false;
            }
        }
        else if (index == machine.FrameCount - 1 && !storage.PopFrame(ref machine, ref state, out _))
        {
            return false;
        }

        if (frame.Kind == Pcre2FrameKind.Subroutine && index < machine.FrameCount &&
            !storage.SetCallActive(index, false, ref machine, ref state))
        {
            return false;
        }

        machine.CallDepth = frame.CallDepth;
        state.Buffers.States[Active] = frame.Auxiliary;
        if (success)
        {
            CompleteNode(frame.ScopeNode, ref machine, ref state);
        }
        else
        {
            Fail(ref state);
        }

        return true;
    }

    // False predicates return normally through a Predicate boundary. The assertion
    // owner applies source capture policy first; notably a matching negative child
    // used as a false conditional retains its captures (manual2634-2635).
    internal static bool LeaveModulePredicate(Pcre2Program program, bool value, bool commit,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!ReadModuleContinuation(program, ref machine, ref state, out _, out var frame))
        {
            return false;
        }

        if (!LeaveModuleChild(program, true, commit, ref machine, ref state))
        {
            return false;
        }

        machine.HasPredicateResult = true;
        machine.PredicateResult = value;
        machine.PredicateNode = frame.ScopeNode;
        return true;
    }
    internal static bool TakePredicateResult(int assertionNode, ref Pcre2VmState machine, ref RunState state, out bool value)
    {
        value = false;
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if (!machine.HasPredicateResult || machine.PredicateNode != assertionNode)
        {
            return false;
        }

        value = machine.PredicateResult;
        machine.HasPredicateResult = false;
        machine.PredicateNode = -1;
        return true;
    }
    internal static bool UpdateLastUsedPosition(int position, ref Pcre2VmState machine, ref RunState state)
    {
        if (position < 0 || position > machine.SubjectLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        machine.LastUsedPosition = Math.Max(machine.LastUsedPosition, position);
        return true;
    }
    // Restores missing physical windows from immutable continuation snapshots,
    // even when popped cells have since been overwritten by a later scan.
    private static bool RestoreModuleWindows(Pcre2Program program, int active, int depth,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (depth < 0 || depth > machine.Capacity.Windows)
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        while (machine.WindowDepth > depth)
        {
            if (!storage.PopWindow(ref machine, ref state, out _))
            {
                return false;
            }
        }

        while (machine.WindowDepth < depth)
        {
            var found = false;
            for (var index = active; index >= 0;)
            {
                if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
                {
                    return false;
                }

                if (frame.HasSavedWindow && frame.WindowDepth == machine.WindowDepth + 1)
                {
                    if (!storage.PushWindow(frame.SavedWindow, ref machine, ref state))
                    {
                        return false;
                    }

                    found = true;
                    break;
                }
                if (frame.Auxiliary >= index)
                {
                    throw new InvalidOperationException("Invalid continuation chain.");
                }

                index = frame.Auxiliary;
            }
            if (!found)
            {
                throw new InvalidOperationException("Missing suspended module window.");
            }
        }
        return RefreshWindow(program, ref machine, ref state);
    }
    private static bool ReactivateCalls(Pcre2Storage storage, int active, ref Pcre2VmState machine, ref RunState state)
    {
        for (var index = active; index >= 0;)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
            {
                return false;
            }

            if (frame.Kind == Pcre2FrameKind.Subroutine && !frame.CallActive &&
                !storage.SetCallActive(index, true, ref machine, ref state))
            {
                return false;
            }

            if (frame.Auxiliary >= index)
            {
                throw new InvalidOperationException("Invalid continuation chain.");
            }

            index = frame.Auxiliary;
        }
        return true;
    }
    // Used after a module-owned retry choice hands back its retained boundary index.
    // The module supplies source restoration/retry policy; this only restores core context.
    internal static bool ReactivateModuleContinuation(Pcre2Program program, int index,
        ref Pcre2VmState machine, ref RunState state)
    {
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
        {
            return false;
        }

        if (frame.Kind is not (Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate))
        {
            throw new ArgumentException("Not a module continuation.", nameof(index));
        }

        if (!RestoreModuleWindows(program, index, frame.WindowDepth, ref machine, ref state) ||
            !ReactivateCalls(storage, index, ref machine, ref state))
        {
            return false;
        }

        machine.CallDepth = frame.CallDepth + (frame.Kind == Pcre2FrameKind.Subroutine ? 1 : 0);
        machine.Node = frame.ScopeNode;
        state.Buffers.States[Active] = index;
        Fail(ref state);
        return true;
    }
    internal static Pcre2MatchFlags EffectiveContextFlags(Pcre2Program program, Pcre2MatchFlags flags,
        ref Pcre2VmState machine, ref RunState state)
    {
        flags |= program.PatternMatchFlags;
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return flags;
        }

        if (machine.NoteolCleared)
        {
            return flags & ~Pcre2MatchFlags.Noteol;
        }

        if (machine.FrameCount == 0)
        {
            return flags;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        for (var index = state.Buffers.States[Active]; index >= 0;)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
            {
                return flags;
            }

            if (frame.Kind == Pcre2FrameKind.Scan)
            {
                return flags & ~Pcre2MatchFlags.Noteol;
            }

            index = frame.Auxiliary;
        }
        return flags;
    }
    // Scan owners clear NOTEOL after their entry snapshot, keep it cleared across
    // successful inline remainder, and restore that snapshot on source exits.
    internal static bool SetNoteolCleared(bool cleared, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        machine.NoteolCleared = cleared;
        return true;
    }
    // A future module enters its child with this continuation, and receives a
    // Return when that child completes. It owns its frame/window checkpoint.
    internal static bool EnterModuleChild(Pcre2Program program, int child, Pcre2FrameKind kind,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (kind is not (Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!PushContinuation(storage, kind, machine.Node, 0, 0, ref machine, ref state))
        {
            return false;
        }

        machine.Node = child;
        state.Buffers.States[Mode] = Enter;
        return true;
    }

    public static Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchOptions options, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        if (!program.IsExecutable)
        {
            throw new ArgumentException("Kernel requires an executable plan.", nameof(program));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var flags = EffectiveContextFlags(program, options.Flags, ref machine, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
        {
            return Resource();
        }

        if (machine.HasVerbReturn)
        {
            throw new InvalidOperationException("Unconsumed verb return.");
        }

        if (state.Buffers.States[Mode] == Failing)
        {
            return Backtrack(program, ref machine, ref state);
        }

        if (state.Buffers.States[Mode] == Complete)
        {
            return ContinueAfterChild(program, flags, storage, ref machine, ref state);
        }

        var node = program.Nodes[machine.Node];
        switch (node.Kind)
        {
            case Pcre2NodeKind.Sequence:
                if (!PushContinuation(storage, Pcre2FrameKind.Choice, machine.Node, Sequence, 1, ref machine, ref state))
                {
                    return Resource();
                }

                machine.Node = program.Children[node.Children.Offset];
                return Continued(machine.Node);
            case Pcre2NodeKind.Alternative:
                if (!EnterAlternativeBranch(program, machine.Node, 0, storage, ref machine, ref state))
                {
                    return Resource();
                }

                return Continued(machine.Node);
            case Pcre2NodeKind.Capture:
                if (!PushContinuation(storage, Pcre2FrameKind.Choice, machine.Node, Capture, 0, ref machine, ref state))
                {
                    return Resource();
                }

                machine.Node = program.Children[node.Children.Offset];
                return Continued(machine.Node);
            case Pcre2NodeKind.Atomic:
                if (!PushContinuation(storage, Pcre2FrameKind.Atomic, machine.Node, Atomic, 0, ref machine, ref state))
                {
                    return Resource();
                }

                machine.Node = program.Children[node.Children.Offset];
                return Continued(machine.Node);
            case Pcre2NodeKind.Repeat:
                if (node.Quantifier == Pcre2Quantifier.Possessive &&
                    !PushContinuation(storage, Pcre2FrameKind.Atomic, machine.Node, Atomic, 0, ref machine, ref state))
                {
                    return Resource();
                }

                return ScheduleRepeat(program, machine.Node, 0, false, storage, ref machine, ref state);
            case Pcre2NodeKind.Assertion:
            case Pcre2NodeKind.ScanSubstring:
            case Pcre2NodeKind.Backreference:
                return new(Pcre2StepKind.EnterAssertion, machine.Node);
            case Pcre2NodeKind.Subroutine:
            case Pcre2NodeKind.Conditional:
            case Pcre2NodeKind.Define:
                return new(Pcre2StepKind.EnterSubroutine, machine.Node);
            case Pcre2NodeKind.Grapheme:
            case Pcre2NodeKind.ScriptRun:
                return new(Pcre2StepKind.Unicode, machine.Node);
            case Pcre2NodeKind.Callout:
            case Pcre2NodeKind.ResetStart:
            case Pcre2NodeKind.Verb:
                return new(Pcre2StepKind.Control, machine.Node);
            default:
                return Atom<TUnit, TReader>(program, input, data, flags, node, storage, ref machine, ref state);
        }
    }

    private static bool PushContinuation(Pcre2Storage storage, Pcre2FrameKind kind, int node,
        int action, int count, ref Pcre2VmState machine, ref RunState state)
    {
        var index = machine.FrameCount;
        var frame = Snapshot(kind, node, node, action, count, state.Buffers.States[Active], ref machine);
        if (kind is Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate && machine.WindowDepth > 0)
        {
            if (!storage.ReadWindow(machine.WindowDepth - 1, ref machine, ref state, out var window))
            {
                return false;
            }

            frame = frame with { HasSavedWindow = true, SavedWindow = window };
        }
        if (!storage.PushFrame(frame, ref machine, ref state))
        {
            return false;
        }

        state.Buffers.States[Active] = index;
        return true;
    }
    private static Pcre2Frame Snapshot(Pcre2FrameKind kind, int resume, int scope, int action,
        int count, int link, ref Pcre2VmState machine) => new(kind, resume, machine.Position,
        machine.CaptureCursor, machine.WindowDepth, machine.CallDepth, machine.ReportStart,
        machine.MarkIndex, count, scope, action, machine.CandidateStart, machine.CaptureLast,
        machine.CaptureTop, machine.PendingFlags, link, machine.LastUsedPosition, NoteolCleared: machine.NoteolCleared);
    private static bool PushChoice(Pcre2Storage storage, int resume, int scope, int action,
        int count, ref Pcre2VmState machine, ref RunState state)
    {
        // Choice frames retain native coordinate/depth/capture fields. Auxiliary
        // is the persistent continuation link; earlier choices are found by a
        // charged reverse walk, without stealing a semantic snapshot field.
        var frame = Snapshot(Pcre2FrameKind.Choice, resume, scope, action, count,
            state.Buffers.States[Active], ref machine);
        var index = machine.FrameCount;
        if (!storage.PushFrame(frame, ref machine, ref state))
        {
            return false;
        }

        state.Buffers.States[Choice] = index;
        return true;
    }

    private static Pcre2Step ScheduleRepeat(Pcre2Program program, int repeat, int count, bool empty,
        Pcre2Storage storage, ref Pcre2VmState machine, ref RunState state)
    {
        var node = program.Nodes[repeat];
        var exit = count >= node.Min;
        var more = (node.Max < 0 || count < node.Max) && !(empty && exit);
        if (!more)
        {
            if (!exit)
            { Fail(ref state); return Continued(machine.Node); }
            CompleteNode(repeat, ref machine, ref state);
            return Continued(repeat);
        }
        if (exit && node.Quantifier == Pcre2Quantifier.Lazy)
        {
            if (!PushChoice(storage, repeat, repeat, RepeatMore, count, ref machine, ref state))
            {
                return Resource();
            }

            CompleteNode(repeat, ref machine, ref state);
            return Continued(repeat);
        }
        if (exit && !PushChoice(storage, repeat, repeat, RepeatExit, count, ref machine, ref state))
        {
            return Resource();
        }

        if (!PushContinuation(storage, Pcre2FrameKind.Repeat, repeat, Repeat, count, ref machine, ref state))
        {
            return Resource();
        }

        machine.Node = program.Children[node.Children.Offset];
        state.Buffers.States[Mode] = Enter;
        return Continued(machine.Node);
    }

    private static Pcre2Step ContinueAfterChild(Pcre2Program program, Pcre2MatchFlags flags,
        Pcre2Storage storage, ref Pcre2VmState machine, ref RunState state)
    {
        var index = state.Buffers.States[Active];
        Pcre2Frame continuation = default;
        // Alternative checkpoints are mechanically transparent at inline KET.
        // Consume them iteratively in this same transition, preserving existing
        // module Return/terminal occurrences without managed recursion.
        while (index >= 0)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out continuation))
            {
                return Resource();
            }

            if (continuation.Kind != Pcre2FrameKind.Branch)
            {
                break;
            }

            state.Buffers.States[Active] = continuation.Auxiliary;
            if (index == machine.FrameCount - 1 && !storage.PopFrame(ref machine, ref state, out _))
            {
                return Resource();
            }

            CompleteNode(continuation.ScopeNode, ref machine, ref state);
            index = continuation.Auxiliary;
        }
        if (index < 0)
        {
            var empty = machine.Position == machine.ReportStart;
            if (empty && ((flags & Pcre2MatchFlags.Notempty) != 0 ||
                (flags & Pcre2MatchFlags.NotemptyAtstart) != 0 && machine.ReportStart == machine.SearchStart) ||
                ((flags & Pcre2MatchFlags.Endanchored) != 0 || (program.Profile.Flags & Pcre2CompileFlags.Endanchored) != 0) && machine.Position != machine.VisibleEnd)
            { Fail(ref state); return Continued(machine.Node); }
            if (!storage.WriteCapture(0, machine.ReportStart, machine.Position, ref machine, ref state))
            {
                return Resource();
            }

            if (!UpdateLastUsedPosition(machine.Position, ref machine, ref state))
            {
                return Resource();
            }

            state.Buffers.States[Accepted] = 1;
            return new(Pcre2StepKind.Accept, machine.Node);
        }
        if (continuation.Kind is Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate)
        {
            // Assertion KET updates before its position reset (6288/6302), or
            // returns through RETURN_SWITCH (negative/conditional,6331/6231).
            // Subroutine KET continues inline (6272); Scan runs the remainder
            // before RETURN_SWITCH (6337-6347). Predicate/script success also
            // continues inline. Those module owners call the helper only at
            // their actual source RRETURN/ACCEPT events.
            if (continuation.Kind == Pcre2FrameKind.Assertion &&
                program.Assertions[program.Nodes[continuation.ScopeNode].Operand].Kind is not
                    (Pcre2AssertionKind.NegativeAhead or Pcre2AssertionKind.NegativeBehind) &&
                !UpdateLastUsedPosition(machine.Position, ref machine, ref state))
            {
                return Resource();
            }

            machine.Node = continuation.ScopeNode;
            return new(Pcre2StepKind.Return, continuation.ScopeNode);
        }
        state.Buffers.States[Active] = continuation.Auxiliary;
        // If the continuation is topmost, no choice can need this version.
        if (index == machine.FrameCount - 1 && !storage.PopFrame(ref machine, ref state, out _))
        {
            return Resource();
        }

        var parent = program.Nodes[continuation.ScopeNode];
        switch (continuation.ChildCursor)
        {
            case Branch:
                // Ordinary group KET continues inline; no RRETURN notification.
                break;
            case Sequence:
                if (continuation.RepeatCount < parent.Children.Count)
                {
                    if (!PushContinuation(storage, Pcre2FrameKind.Choice, continuation.ScopeNode, Sequence,
                        continuation.RepeatCount + 1, ref machine, ref state))
                    {
                        return Resource();
                    }

                    machine.Node = program.Children[parent.Children.Offset + continuation.RepeatCount];
                    state.Buffers.States[Mode] = Enter;
                    return Continued(machine.Node);
                }
                break;
            case Capture:
                var group = program.Groups[parent.Operand].Number;
                if (!storage.WriteCapture(group, continuation.InputPosition, machine.Position, ref machine, ref state))
                {
                    return Resource();
                }

                machine.CaptureLast = group;
                machine.CaptureTop = Math.Max(machine.CaptureTop, group + 1);
                break;
            case Repeat:
                return ScheduleRepeat(program, continuation.ScopeNode, continuation.RepeatCount + 1,
                    machine.Position == continuation.InputPosition, storage, ref machine, ref state);
            case Atomic:
                if (!storage.TruncateFrames(index, ref machine, ref state))
                {
                    return Resource();
                }

                if (!FindChoice(storage, ref machine, ref state))
                {
                    return Resource();
                }

                break;
            default:
                throw new InvalidOperationException("Unknown core continuation.");
        }
        CompleteNode(continuation.ScopeNode, ref machine, ref state);
        return Continued(machine.Node);
    }

    internal static Pcre2Step Backtrack(Pcre2Program program, ref Pcre2VmState machine, ref RunState state)
    {
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var index = state.Buffers.States[Choice];
        // OP_* character/class/type repeats maximize inside the current native
        // frame. A failed optional atom stops that loop; only a failed RMATCH
        // of its remainder goes through RETURN_SWITCH (match.c1380-1394,
        // 1520-1545). Our atom continuation is not itself that source call.
        var repeatStop = false;
        var current = state.Buffers.States[Active];
        if (current >= 0 && index >= 0)
        {
            if (!storage.ReadFrame(current, ref machine, ref state, out var pending) ||
                !storage.ReadFrame(index, ref machine, ref state, out var retry))
            {
                return Resource();
            }

            if (pending.Kind == Pcre2FrameKind.Repeat && retry.ChildCursor == RepeatExit &&
                pending.ScopeNode == retry.ScopeNode &&
                program.Children[program.Nodes[pending.ScopeNode].Children.Offset] == machine.Node &&
                program.Nodes[pending.ScopeNode].Quantifier != Pcre2Quantifier.Lazy &&
                program.Nodes[machine.Node].Kind is Pcre2NodeKind.Literal or Pcre2NodeKind.Class or
                    Pcre2NodeKind.Dot or Pcre2NodeKind.CodeUnit or Pcre2NodeKind.Newline or
                    Pcre2NodeKind.Grapheme or Pcre2NodeKind.Backreference)
            {
                repeatStop = true;
            }
        }
        for (var active = state.Buffers.States[Active]; active >= 0;)
        {
            if (!storage.ReadFrame(active, ref machine, ref state, out var boundary))
            {
                return Resource();
            }

            if (boundary.Kind is Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate)
            {
                if (index < active)
                {
                    if (!NotifyRmatchReturn(true, ref machine, ref state))
                    {
                        return Resource();
                    }

                    machine.Node = boundary.ScopeNode;
                    return new(Pcre2StepKind.Backtrack, boundary.ScopeNode);
                }
                break;
            }
            active = boundary.Auxiliary;
        }
        // Native's implicit root OP_BRA always RMATCHes the body, even its only
        // branch (5488-5499/5546). Exhaustion first returns from that real body;
        // the subsequent Frdepth=0 exit does not add a second flag (6777).
        if (!repeatStop && !NotifyRmatchReturn(true, ref machine, ref state))
        {
            return Resource();
        }

        if (index < 0)
        {
            if (!NotifyRmatchReturn(false, ref machine, ref state))
            {
                return Resource();
            }

            return new(Pcre2StepKind.Backtrack, machine.Node);
        }
        if (!storage.ReadFrame(index, ref machine, ref state, out var choice) ||
            !storage.RestoreCaptures(choice.CaptureCursor, ref machine, ref state) ||
            !storage.TruncateFrames(index, ref machine, ref state))
        {
            return Resource();
        }

        if (!RestoreModuleWindows(program, choice.Auxiliary, choice.WindowDepth, ref machine, ref state) ||
            !ReactivateCalls(storage, choice.Auxiliary, ref machine, ref state))
        {
            return Resource();
        }

        RestoreEntry(choice, ref machine);
        state.Buffers.States[Active] = choice.Auxiliary;
        if (!FindChoice(storage, ref machine, ref state))
        {
            return Resource();
        }

        machine.Node = choice.ResumeNode;
        state.Buffers.States[Mode] = Enter;
        if (choice.Kind == Pcre2FrameKind.Verb)
        {
            if (machine.HasVerbReturn)
            {
                throw new InvalidOperationException("Unconsumed verb return.");
            }

            machine.HasVerbReturn = true;
            machine.VerbReturnFrame = choice;
            return new(Pcre2StepKind.Control, choice.ScopeNode);
        }
        if (choice.ChildCursor == ModuleChoice)
        {
            state.Buffers.States[ModuleRetry] = 1;
            state.Buffers.States[ModulePayload] = choice.RepeatCount;
            return new(Pcre2StepKind.Return, choice.ScopeNode);
        }
        if (choice.ChildCursor == RepeatExit)
        { CompleteNode(choice.ScopeNode, ref machine, ref state); return Continued(machine.Node); }
        if (choice.ChildCursor == RepeatMore)
        {
            if (!PushContinuation(storage, Pcre2FrameKind.Repeat, choice.ScopeNode, Repeat,
                choice.RepeatCount, ref machine, ref state))
            {
                return Resource();
            }

            machine.Node = program.Children[program.Nodes[choice.ScopeNode].Children.Offset];
            return Continued(machine.Node);
        }
        if (!EnterAlternativeBranch(program, choice.ScopeNode, choice.RepeatCount, storage, ref machine, ref state))
        {
            return Resource();
        }

        return Continued(machine.Node);
    }

    private static Pcre2Step Continued(int node) => new(Pcre2StepKind.Continue, node);
    private static bool FindChoice(Pcre2Storage storage, ref Pcre2VmState machine, ref RunState state)
    {
        for (var index = machine.FrameCount - 1; index >= 0; index--)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
            {
                return false;
            }

            if (frame.Kind == Pcre2FrameKind.Verb || frame.Kind == Pcre2FrameKind.Choice && frame.ChildCursor >= 0)
            { state.Buffers.States[Choice] = index; return true; }
        }
        state.Buffers.States[Choice] = -1;
        return true;
    }
    private static Pcre2Step Resource() => new(Pcre2StepKind.ResourceFailure);
}
