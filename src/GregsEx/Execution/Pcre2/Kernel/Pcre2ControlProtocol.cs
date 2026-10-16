using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Kernel;

internal readonly partial struct Pcre2Kernel
{
    private const int Branch = -5;

    internal static bool ReadActiveContinuation(Pcre2Program program, int afterIndex,
        ref Pcre2VmState machine, ref RunState state, out int index, out Pcre2Frame frame)
    {
        frame = default;
        index = -1;
        ArgumentOutOfRangeException.ThrowIfLessThan(afterIndex, -1);

        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        // Validate the cursor against logical ancestry, including suspended
        // retained frames. Physical retention alone never proves activity.
        var cursor = state.Buffers.States[Active];
        var next = afterIndex == -1;
        while (cursor >= 0)
        {
            if (!storage.ReadFrame(cursor, ref machine, ref state, out frame))
            {
                return false;
            }

            if (frame.Auxiliary < -1 || frame.Auxiliary >= cursor)
            {
                throw new InvalidOperationException("Invalid continuation chain.");
            }

            if (next)
            { index = cursor; return true; }
            next = cursor == afterIndex;
            cursor = frame.Auxiliary;
        }
        if (!next)
        {
            throw new ArgumentException("Cursor is not an active continuation.", nameof(afterIndex));
        }

        frame = default;
        return false;
    }

    internal static bool PushVerbBoundary(Pcre2Program program, int verbNode,
        ref Pcre2VmState machine, ref RunState state)
    {
        if ((uint)verbNode >= (uint)program.Nodes.Length || program.Nodes[verbNode].Kind != Pcre2NodeKind.Verb)
        {
            throw new ArgumentOutOfRangeException(nameof(verbNode));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        if (machine.HasVerbReturn)
        {
            throw new InvalidOperationException("Unconsumed verb return.");
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var index = machine.FrameCount;
        var frame = Snapshot(Pcre2FrameKind.Verb, verbNode, verbNode, 0, 0,
            state.Buffers.States[Active], ref machine);
        if (!storage.PushFrame(frame, ref machine, ref state))
        {
            return false;
        }

        state.Buffers.States[Choice] = index;
        return true;
    }

    internal static bool TakeVerbReturn(Pcre2Program program,
        ref Pcre2VmState machine, ref RunState state, out Pcre2Frame entry)
    {
        entry = default;
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position) || !machine.HasVerbReturn)
        {
            return false;
        }

        if (machine.VerbReturnFrame.Kind != Pcre2FrameKind.Verb ||
            machine.Node != machine.VerbReturnFrame.ScopeNode ||
            program.Nodes[machine.Node].Kind != Pcre2NodeKind.Verb)
        {
            throw new InvalidOperationException("Verb return has a different owner.");
        }

        entry = machine.VerbReturnFrame;
        machine.HasVerbReturn = false;
        machine.VerbReturnFrame = default;
        return true;
    }

    internal static bool UnwindToContinuation(Pcre2Program program, int targetIndex,
        bool discardChoices, ref Pcre2VmState machine, ref RunState state)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(targetIndex, -1);

        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!ValidateActiveTarget(storage, targetIndex, ref machine, ref state, out var target))
        {
            return false;
        }

        if (discardChoices)
        {
            if (!storage.TruncateFrames(targetIndex + 1, ref machine, ref state) ||
                !FindChoice(storage, ref machine, ref state))
            {
                return false;
            }
        }
        else
        {
            for (var index = state.Buffers.States[Active]; index != targetIndex;)
            {
                if (!storage.ReadFrame(index, ref machine, ref state, out var removed))
                {
                    return false;
                }

                if (removed.Kind == Pcre2FrameKind.Subroutine && removed.CallActive &&
                    !storage.SetCallActive(index, false, ref machine, ref state))
                {
                    return false;
                }

                index = removed.Auxiliary;
            }
        }
        var depth = targetIndex < 0 ? 0 : target.WindowDepth;
        if (!RestoreModuleWindows(program, targetIndex, depth, ref machine, ref state) ||
            !ReactivateCalls(storage, targetIndex, ref machine, ref state))
        {
            return false;
        }

        machine.CallDepth = targetIndex < 0 ? 0 : target.CallDepth + (target.Kind == Pcre2FrameKind.Subroutine ? 1 : 0);
        state.Buffers.States[Active] = targetIndex;
        if (targetIndex >= 0)
        {
            machine.Node = target.ScopeNode;
        }

        return true;
    }

    private static bool ValidateActiveTarget(Pcre2Storage storage, int targetIndex,
        ref Pcre2VmState machine, ref RunState state, out Pcre2Frame target)
    {
        target = default;
        for (var index = state.Buffers.States[Active]; index >= 0;)
        {
            if (!storage.ReadFrame(index, ref machine, ref state, out var frame))
            {
                return false;
            }

            if (frame.Auxiliary < -1 || frame.Auxiliary >= index)
            {
                throw new InvalidOperationException("Invalid continuation chain.");
            }

            if (index == targetIndex)
            { target = frame; return true; }
            index = frame.Auxiliary;
        }
        if (targetIndex >= 0)
        {
            throw new ArgumentException("Target is not an active continuation.", nameof(targetIndex));
        }

        return true;
    }

    internal static Pcre2Step FinishAccept(Pcre2Program program, Pcre2MatchFlags flags,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        flags = EffectiveContextFlags(program, flags, ref machine, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
        {
            return Resource();
        }

        var empty = machine.Position == machine.ReportStart;
        if (empty && ((flags & Pcre2MatchFlags.Notempty) != 0 ||
            (flags & Pcre2MatchFlags.NotemptyAtstart) != 0 && machine.ReportStart == machine.SearchStart))
        { Fail(ref state); return Continued(machine.Node); }
        if (((flags & Pcre2MatchFlags.Endanchored) != 0 ||
            (program.Profile.Flags & Pcre2CompileFlags.Endanchored) != 0) && machine.Position < machine.VisibleEnd)
        {
            // OP_ACCEPT returns directly, rejecting this candidate; OP_END
            // instead RRETURNs NOMATCH and keeps its alternatives.
            if (!UnwindToContinuation(program, -1, true, ref machine, ref state))
            {
                return Resource();
            }

            Fail(ref state);
            return new(Pcre2StepKind.Backtrack, machine.Node);
        }
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!storage.WriteCapture(0, machine.ReportStart, machine.Position, ref machine, ref state) ||
            !UpdateLastUsedPosition(machine.Position, ref machine, ref state))
        {
            return Resource();
        }

        state.Buffers.States[Accepted] = 1;
        return new(Pcre2StepKind.Accept, machine.Node);
    }

    internal static bool CutThenScope(Pcre2Program program, int branchIndex,
        ref Pcre2VmState machine, ref RunState state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(branchIndex);

        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!ValidateActiveTarget(storage, branchIndex, ref machine, ref state, out var branch))
        {
            return false;
        }

        if (branch.Kind != Pcre2FrameKind.Branch || program.Nodes[branch.ScopeNode].Kind != Pcre2NodeKind.Alternative ||
            program.Nodes[branch.ScopeNode].Children.Count < 2)
        {
            throw new ArgumentException("Target is not an alternative branch.", nameof(branchIndex));
        }

        if (!storage.RestoreCaptures(branch.CaptureCursor, ref machine, ref state) ||
            !storage.TruncateFrames(branchIndex, ref machine, ref state) ||
            !RestoreModuleWindows(program, branch.Auxiliary, branch.WindowDepth, ref machine, ref state) ||
            !ReactivateCalls(storage, branch.Auxiliary, ref machine, ref state) ||
            !FindChoice(storage, ref machine, ref state))
        {
            return false;
        }

        RestoreEntry(branch, ref machine);
        state.Buffers.States[Active] = branch.Auxiliary;
        machine.Node = branch.ScopeNode;
        Fail(ref state);
        return true;
    }

    private static void RestoreEntry(Pcre2Frame entry, ref Pcre2VmState machine)
    {
        machine.CallDepth = entry.CallDepth;
        machine.NoteolCleared = entry.NoteolCleared;
        machine.Position = entry.InputPosition;
        machine.ReportStart = entry.ReportStart;
        machine.MarkIndex = entry.MarkIndex;
        machine.CaptureTop = entry.CaptureTop;
        machine.CaptureLast = entry.LastCapture;
        // entry.PendingFlags is historical storage, not observable rollback state.
    }

    internal static bool NotifyRmatchReturn(bool hasCaller, ref Pcre2VmState machine, ref RunState state)
    {
        if (!UpdateLastUsedPosition(machine.Position, ref machine, ref state))
        {
            return false;
        }

        if (hasCaller)
        {
            machine.PendingFlags |= Pcre2CalloutFlags.Backtrack;
        }

        return true;
    }

    private static bool EnterAlternativeBranch(Pcre2Program program, int alternative, int ordinal,
        Pcre2Storage storage, ref Pcre2VmState machine, ref RunState state)
    {
        var node = program.Nodes[alternative];
        var next = ordinal + 1;
        if (next < node.Children.Count && !PushChoice(storage,
            program.Children[node.Children.Offset + next], alternative, EnterChoice, next, ref machine, ref state))
        {
            return false;
        }

        if (node.Children.Count > 1 && !PushContinuation(storage, Pcre2FrameKind.Branch, alternative,
            Branch, ordinal, ref machine, ref state))
        {
            return false;
        }

        machine.Node = program.Children[node.Children.Offset + ordinal];
        state.Buffers.States[Mode] = Enter;
        return true;
    }
}
