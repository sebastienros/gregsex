#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Assertions;

internal readonly partial struct Pcre2AssertionOps
{
    // Source ASSERTBACK/ASSERTBACK_NOT treats THEN as failure of this branch,
    // not another VREVERSE length. Controls supplies the lexical branch and
    // actual assertion scope after its verb return; scheduling stays here.
    internal static Pcre2Step ResumeAfterThen<TUnit, TReader>(Pcre2Program program,
        ReadOnlySpan<TUnit> input, int currentTopLevelBranchNode,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        Require(program, input.Length, ref machine);
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        if (!Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out var index, out var frame))
        {
            return Denied(ref machine, ref state) ? Resource() : throw new InvalidOperationException("Missing lookbehind continuation.");
        }

        if (frame.ScopeNode != machine.Node || frame.Kind is not (Pcre2FrameKind.Assertion or Pcre2FrameKind.Predicate) ||
            program.Nodes[frame.ScopeNode].Kind != Pcre2NodeKind.Assertion ||
            !Behind(program.Assertions[program.Nodes[frame.ScopeNode].Operand].Kind))
        {
            throw new InvalidOperationException("THEN retry requires its active lookbehind owner.");
        }

        var ordinal = BranchOrdinal(program, frame.ScopeNode, currentTopLevelBranchNode, ref machine, ref state);
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }
        // Validate before cutting. This removes all current lengths and inner
        // choices, reconstructing entry windows/calls through the shared kernel.
        if (!Pcre2Kernel.UnwindToContinuation(program, index, true, ref machine, ref state))
        {
            return Resource();
        }

        if (!NextBranch<TUnit, TReader>(program, input, frame.ScopeNode, ordinal + 1, frame.InputPosition,
            ref machine, ref state, out var nextOrdinal, out var start))
        {
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            return Resume<TUnit, TReader>(program, input, Pcre2StepKind.Backtrack, ref machine, ref state);
        }
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!storage.RestoreCaptures(frame.CaptureCursor, ref machine, ref state) ||
            !Pcre2Kernel.SetNoteolCleared(frame.NoteolCleared, ref machine, ref state))
        {
            return Resource();
        }

        machine.Position = frame.InputPosition;
        machine.ReportStart = frame.ReportStart;
        machine.MarkIndex = frame.MarkIndex;
        machine.CaptureTop = frame.CaptureTop;
        machine.CaptureLast = frame.LastCapture;
        if (!Pcre2Kernel.LeaveModuleChild(program, false, true, ref machine, ref state))
        {
            return Resource();
        }

        machine.Node = frame.ScopeNode;
        return ScheduleBehind<TUnit, TReader>(program, input, nextOrdinal, start, frame.InputPosition,
            frame.Kind == Pcre2FrameKind.Predicate, ref machine, ref state);
    }

    private static int BranchCount(Pcre2Program program, int owner)
    {
        var child = program.Children[program.Nodes[owner].Children.Offset];
        return program.Nodes[child].Kind == Pcre2NodeKind.Alternative ? program.Nodes[child].Children.Count : 1;
    }
    private static int BranchNode(Pcre2Program program, int owner, int ordinal)
    {
        var child = program.Children[program.Nodes[owner].Children.Offset];
        return program.Nodes[child].Kind == Pcre2NodeKind.Alternative ?
            program.Children[program.Nodes[child].Children.Offset + ordinal] : child;
    }
    private static int BranchOrdinal(Pcre2Program program, int owner, int branch,
        ref Pcre2VmState machine, ref RunState state)
    {
        for (var ordinal = 0; ordinal < BranchCount(program, owner); ordinal++)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return -1;
            }

            if (BranchNode(program, owner, ordinal) == branch)
            {
                return ordinal;
            }
        }
        throw new InvalidOperationException("Retry is not a top-level lookbehind branch.");
    }
    private static int BranchMaximum(Pcre2Program program, int owner, int ordinal,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!program.HasLookbehindMetadata)
        {
            return program.Assertions[program.Nodes[owner].Operand].MaximumLength;
        }

        var branches = program.LookbehindBranches;
        int low = 0, high = branches.Length;
        while (low < high)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return -1;
            }

            var mid = low + (high - low) / 2;
            if (branches[mid].AssertionNode < owner)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        var branch = branches[low + ordinal];
        if (branch.AssertionNode != owner || branch.BranchNode != BranchNode(program, owner, ordinal))
        {
            throw new InvalidOperationException("Invalid lookbehind metadata.");
        }

        return branch.MaximumLength;
    }
    private static bool Variable(Pcre2Program program, Pcre2Assertion assertion) =>
        program.HasLookbehindMetadata ? assertion.VariableLength : assertion.MinimumLength != assertion.MaximumLength;

    private static bool Rewind<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input, int origin,
        int maximum, int minimum, ref Pcre2VmState machine, ref RunState state, out int start)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        start = origin;
        int count = 0, floor = Pcre2Kernel.ScanStart(ref state);
        while (count < maximum && start > floor)
        {
            var read = TReader.ReadPrevious(input, start, program.Profile, ref state);
            if (Denied(ref machine, ref state))
            {
                return false;
            }

            if (read.Status != Pcre2ReadStatus.Value || start - read.Width < floor)
            {
                break;
            }

            start -= read.Width;
            count++;
        }
        return count >= minimum;
    }
    private static bool NextBranch<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int owner, int first, int origin, ref Pcre2VmState machine, ref RunState state,
        out int ordinal, out int start)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var assertion = program.Assertions[program.Nodes[owner].Operand];
        start = origin;
        for (ordinal = first; ordinal < BranchCount(program, owner); ordinal++)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, origin))
            {
                return false;
            }

            var maximum = BranchMaximum(program, owner, ordinal, ref machine, ref state);
            if (Denied(ref machine, ref state))
            {
                return false;
            }

            var minimum = Variable(program, assertion) ? assertion.MinimumLength : maximum;
            if (Rewind<TUnit, TReader>(program, input, origin, maximum, minimum, ref machine, ref state, out start))
            {
                return true;
            }

            if (Denied(ref machine, ref state))
            {
                return false;
            }
        }
        return false;
    }
    private static Pcre2Step ScheduleBehind<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int ordinal, int start, int origin, bool predicate, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        int owner = machine.Node, branch = BranchNode(program, owner, ordinal);
        var assertion = program.Assertions[program.Nodes[owner].Operand];
        int nextStart = origin, nextOrdinal = ordinal;
        var more = false;
        if (Variable(program, assertion))
        {
            if (!Rewind<TUnit, TReader>(program, input, origin, assertion.MinimumLength, assertion.MinimumLength,
                ref machine, ref state, out var minimumStart))
            {
                return Resource();
            }

            if (start < minimumStart)
            {
                var read = TReader.Read(input, start, origin, false, program.Profile, ref state);
                if (Denied(ref machine, ref state))
                {
                    return Resource();
                }

                if (read.Status != Pcre2ReadStatus.Value)
                {
                    throw new InvalidOperationException("Invalid rewind boundary.");
                }

                nextStart = start + read.Width;
                more = true;
            }
        }
        if (!more)
        {
            more = NextBranch<TUnit, TReader>(program, input, owner, ordinal + 1, origin, ref machine, ref state, out nextOrdinal, out nextStart);
        }

        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        machine.Position = origin;
        if (!Pcre2Kernel.EnterModuleChild(program, branch, predicate ? Pcre2FrameKind.Predicate : Pcre2FrameKind.Assertion,
            ref machine, ref state))
        {
            return Resource();
        }

        if (more)
        {
            // The common boundary preserves the original assertion endpoint.
            // The choice preserves the next native start and a validated node ID.
            machine.Position = nextStart;
            if (!Pcre2Kernel.PushModuleChoice(program, owner, BranchNode(program, owner, nextOrdinal), ref machine, ref state))
            {
                return Resource();
            }
        }
        machine.Position = start;
        if (!Pcre2Kernel.RecordEarliestRead(start, ref machine, ref state))
        {
            return Resource();
        }

        return Continued(machine.Node);
    }
}
