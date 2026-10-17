#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Recursion;

// Entry and return are deliberately separate: the same source call site can be
// entered while an older invocation of that site is still active.
internal readonly struct Pcre2RecursionOps : IPcre2RecursionOps
{
    public static Pcre2Step Step(Pcre2Program program, Pcre2MatchFlags flags,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (!program.IsExecutable)
        {
            throw new ArgumentException("Executable plan required.", nameof(program));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        var node = program.Nodes[machine.Node];
        switch (node.Kind)
        {
            case Pcre2NodeKind.Define:
                Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
                return Continued(machine.Node);
            case Pcre2NodeKind.Subroutine:
                var target = TargetNumber(program, machine.Node);
                if ((flags & Pcre2MatchFlags.DisableRecurseloopCheck) == 0)
                {
                    var after = -1;
                    while (Pcre2Kernel.ReadActiveSubroutine(program, after, ref machine, ref state, out var index, out var call))
                    {
                        if (TargetNumber(program, call.ScopeNode) == target)
                        {
                            // Source checks only the nearest call of this target.
                            if (call.InputPosition == machine.Position && call.RecurseLastUsedPosition == machine.LastUsedPosition)
                            {
                                machine.SourceReturnCode = (int)Pcre2ErrorCode.Recurseloop;
                                return new(Pcre2StepKind.SourceAbort, machine.Node, machine.SourceReturnCode);
                            }
                            break;
                        }
                        after = index;
                    }
                    if (Failed(ref machine, ref state))
                    {
                        return Resource();
                    }
                }
                var reference = program.References[node.Operand];
                // A called group's outer capture is not exported; inner captures
                // remain available inside the call and are restored on return.
                var child = reference.Kind == Pcre2ReferenceKind.WholePattern ? program.Root :
                    program.Children[program.Nodes[program.Groups[reference.TargetDefinition].EntryNode].Children.Offset];
                return Pcre2Kernel.EnterModuleChild(program, child, Pcre2FrameKind.Subroutine, ref machine, ref state)
                    ? Continued(machine.Node) : Resource();
            case Pcre2NodeKind.Conditional:
                var condition = program.Conditions[node.Operand];
                if (condition.Kind == Pcre2ConditionKind.Assertion)
                {
                    return Pcre2Kernel.EnterModuleChild(program, condition.AssertionNode, Pcre2FrameKind.Predicate, ref machine, ref state)
                        ? Continued(machine.Node) : Resource();
                }

                var value = EvaluateCondition(program, condition, ref machine, ref state);
                return Failed(ref machine, ref state) ? Resource() : SelectBranch(program, value, ref machine, ref state);
            default:
                throw new ArgumentException("Node is not a recursion or condition operation.", nameof(program));
        }
    }

    // Composition routes kernel Return/Backtrack by the owning ScopeNode kind.
    // It must not turn an exhausted outer candidate Backtrack into a module return.
    internal static Pcre2Step Resume(Pcre2Program program, Pcre2Step occurrence,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (occurrence.Kind is not (Pcre2StepKind.Return or Pcre2StepKind.Backtrack))
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        if (!Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out var index, out var frame))
        {
            if (Failed(ref machine, ref state))
            {
                return Resource();
            }

            throw new InvalidOperationException("Missing recursion/condition boundary.");
        }
        if (frame.ScopeNode != occurrence.Node || frame.ScopeNode != machine.Node)
        {
            throw new InvalidOperationException("Return does not own the active boundary.");
        }

        var node = program.Nodes[frame.ScopeNode];
        var success = occurrence.Kind == Pcre2StepKind.Return;
        if (node.Kind == Pcre2NodeKind.Conditional && frame.Kind == Pcre2FrameKind.Predicate)
        {
            if (!success)
            {
                throw new InvalidOperationException("An assertion predicate must return explicit truth, including false.");
            }

            var assertion = program.Conditions[node.Operand].AssertionNode;
            if (!Pcre2Kernel.TakePredicateResult(assertion, ref machine, ref state, out var value))
            {
                if (Failed(ref machine, ref state))
                {
                    return Resource();
                }

                throw new InvalidOperationException("Missing scoped assertion truth.");
            }
            if (!Pcre2Kernel.LeaveModuleChild(program, true, true, ref machine, ref state))
            {
                return Resource();
            }

            return SelectBranch(program, value, ref machine, ref state);
        }
        if (node.Kind is not (Pcre2NodeKind.Subroutine or Pcre2NodeKind.Conditional) ||
            frame.Kind != (node.Kind == Pcre2NodeKind.Subroutine ? Pcre2FrameKind.Subroutine : Pcre2FrameKind.ScriptRun))
        {
            throw new InvalidOperationException("Boundary belongs to another module.");
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        if (!success || node.Kind == Pcre2NodeKind.Subroutine)
        {
            // Only surviving choices need the inverse events. Otherwise ordinary
            // undo can reclaim the call's events immediately.
            var retain = success && index != machine.FrameCount - 1;
            if (!(retain ? storage.RestoreCapturesRetaining(frame.CaptureCursor, ref machine, ref state) :
                storage.RestoreCaptures(frame.CaptureCursor, ref machine, ref state)))
            {
                return Resource();
            }

            machine.CaptureTop = frame.CaptureTop;
            machine.CaptureLast = frame.LastCapture;
        }
        if (!success)
        {
            machine.Position = frame.InputPosition;
            machine.ReportStart = frame.ReportStart;
            machine.MarkIndex = frame.MarkIndex;
        }
        // Successful routine/conditional KET continues inline. No watermark update
        // here: core RRETURN failure/end/assertion sites and controls ACCEPT own it.
        return Pcre2Kernel.LeaveModuleChild(program, success, false, ref machine, ref state)
            ? Continued(machine.Node) : Resource();
    }

    internal static int CurrentRecursion(Pcre2Program program, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Kernel.ReadActiveSubroutine(program, -1, ref machine, ref state, out _, out var call)
            ? TargetNumber(program, call.ScopeNode) : -1;

    private static int TargetNumber(Pcre2Program program, int callNode)
    {
        var reference = program.References[program.Nodes[callNode].Operand];
        return reference.Kind == Pcre2ReferenceKind.WholePattern ? 0 : program.Numbers[reference.Candidates.Offset];
    }
    private static bool EvaluateCondition(Pcre2Program program, Pcre2Condition condition,
        ref Pcre2VmState machine, ref RunState state)
    {
        if (condition.Kind == Pcre2ConditionKind.Version)
        {
            return condition.AtLeast ? condition.VersionMajor < 10 || condition.VersionMajor == 10 && condition.VersionMinor <= 46 :
                condition.VersionMajor == 10 && condition.VersionMinor == 46;
        }

        var current = condition.Kind is Pcre2ConditionKind.Recursion or Pcre2ConditionKind.AnyRecursion
            ? CurrentRecursion(program, ref machine, ref state) : -1;
        if (condition.Kind == Pcre2ConditionKind.AnyRecursion)
        {
            return current >= 0;
        }

        var reference = program.References[condition.ReferenceIndex];
        if (condition.Kind == Pcre2ConditionKind.Recursion && reference.Kind == Pcre2ReferenceKind.WholePattern)
        {
            return current == 0;
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        foreach (var group in program.Numbers.Slice(reference.Candidates.Offset, reference.Candidates.Count))
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (condition.Kind == Pcre2ConditionKind.Recursion ? current == group : group < machine.CaptureTop && storage.Registers(group).Start >= 0)
            {
                return true;
            }
        }
        return false;
    }
    private static Pcre2Step SelectBranch(Pcre2Program program, bool value, ref Pcre2VmState machine, ref RunState state)
    {
        var node = program.Nodes[machine.Node];
        var first = program.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? 1 : 0;
        var branch = first + (value ? 0 : 1);
        if (branch == node.Children.Count)
        {
            Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
            return Continued(machine.Node);
        }
        // ScriptRun is a non-call module continuation here; ScopeNode is a
        // Conditional, so composition routes its returns to this module.
        return Pcre2Kernel.EnterModuleChild(program, program.Children[node.Children.Offset + branch],
            Pcre2FrameKind.ScriptRun, ref machine, ref state) ? Continued(machine.Node) : Resource();
    }
    private static bool Failed(ref Pcre2VmState machine, ref RunState state) =>
        machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;
    private static Pcre2Step Resource() => new(Pcre2StepKind.ResourceFailure);
    private static Pcre2Step Continued(int node) => new(Pcre2StepKind.Continue, node);
}
