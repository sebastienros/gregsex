using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Assertions;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Recursion;

namespace GregsEx.Execution.Pcre2.Controls;

internal interface IPcre2CalloutDriver<TUnit, TCallbacks> where TUnit : unmanaged where TCallbacks : class
{
    static abstract Pcre2Step Invoke(Pcre2Program program, ReadOnlySpan<TUnit> input,
        TCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state);
}
internal readonly struct Pcre2CharCalloutDriver : IPcre2CalloutDriver<char, Pcre2CharCallbacks>
{
    public static Pcre2Step Invoke(Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2CharCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Callouts.Invoke(program, input, callbacks, callbackPairs, ref controls, ref machine, ref state);
}
internal readonly struct Pcre2ByteCalloutDriver : IPcre2CalloutDriver<byte, Pcre2ByteCallbacks>
{
    public static Pcre2Step Invoke(Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Callouts.Invoke(program, input, callbacks, callbackPairs, ref controls, ref machine, ref state);
}

internal readonly partial struct Pcre2Controls
{
    internal static Pcre2Step RunCandidate<TUnit, TReader, TCallbacks, TDriver>(Pcre2ControlPlan plan,
        ReadOnlySpan<TUnit> input, Pcre2DataView data, Pcre2MatchOptions options, TCallbacks? callbacks,
        Span<Pcre2OffsetPair> callbackPairs, ref Pcre2ControlState controls,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
        where TCallbacks : class where TDriver : struct, IPcre2CalloutDriver<TUnit, TCallbacks>
    {
        while (true)
        {
            var step = Pcre2Kernel.Step<TUnit, TReader>(plan.Program, input, data, options, ref machine, ref state);
            step = step.Kind == Pcre2StepKind.Control
                ? Step<TUnit, TReader, TCallbacks, TDriver>(plan, input, data, options, callbacks, callbackPairs, ref controls, ref machine, ref state)
                : Pcre2Vm.Dispatch<TUnit, TReader>(plan.Program, input, data, options.Flags, step, ref machine, ref state);
            if (step.Kind != Pcre2StepKind.Continue)
            {
                return step;
            }
        }
    }

    private static Pcre2Step Step<TUnit, TReader, TCallbacks, TDriver>(Pcre2ControlPlan plan,
        ReadOnlySpan<TUnit> input, Pcre2DataView data, Pcre2MatchOptions options, TCallbacks? callbacks,
        Span<Pcre2OffsetPair> callbackPairs, ref Pcre2ControlState controls,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
        where TCallbacks : class where TDriver : struct, IPcre2CalloutDriver<TUnit, TCallbacks>
    {
        var program = plan.Program;
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        var node = program.Nodes[machine.Node];
        if (node.Kind == Pcre2NodeKind.Callout)
        {
            return TDriver.Invoke(program, input, callbacks, callbackPairs, ref controls, ref machine, ref state);
        }

        if (node.Kind == Pcre2NodeKind.ResetStart)
        {
            machine.ReportStart = machine.Position;
            Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
            return Continued(machine.Node);
        }
        if (node.Kind != Pcre2NodeKind.Verb)
        {
            throw new InvalidOperationException("Control occurrence has no control owner.");
        }

        var verb = program.Controls[node.Operand];
        if (Pcre2Kernel.TakeVerbReturn(program, ref machine, ref state, out var entry))
        {
            if (verb.Kind is Pcre2VerbKind.Mark or Pcre2VerbKind.Accept or Pcre2VerbKind.Fail)
            { Pcre2Kernel.Fail(ref state); return Continued(machine.Node); }
            controls.Signal = verb.Kind switch
            {
                Pcre2VerbKind.Commit => Pcre2ControlSignal.Commit,
                Pcre2VerbKind.Prune => Pcre2ControlSignal.Prune,
                Pcre2VerbKind.Skip => verb.MarkIndex >= 0 ? Pcre2ControlSignal.SkipArgument : Pcre2ControlSignal.Skip,
                Pcre2VerbKind.Then => Pcre2ControlSignal.Then,
                _ => throw new InvalidOperationException("Unknown verb remainder owner.")
            };
            controls.VerbNode = entry.ScopeNode;
            controls.ArgumentMarkIndex = verb.MarkIndex;
            controls.SkipPosition = entry.InputPosition;
            controls.OriginRecursion = Pcre2RecursionOps.CurrentRecursion(program, ref machine, ref state);
            return Denied(ref machine, ref state) ? Resource() :
                Propagate<TUnit, TReader>(plan, input, data, options.Flags, ref controls, ref machine, ref state);
        }
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }
        // ACCEPT/FAIL arguments are source pre-MARK; SKIP:argument is not MARK.
        if (verb.MarkIndex >= 0 && verb.Kind != Pcre2VerbKind.Skip)
        { machine.MarkIndex = controls.NomatchMarkIndex = verb.MarkIndex; }
        if (verb.Kind == Pcre2VerbKind.Skip && verb.MarkIndex >= 0)
        {
            controls.SkipArgumentCount++;
            if (controls.SkipArgumentCount <= controls.IgnoreSkipArguments)
            { Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state); return Continued(machine.Node); }
        }
        if (verb.Kind == Pcre2VerbKind.Accept)
        {
            if (verb.MarkIndex >= 0 && !Pcre2Kernel.PushVerbBoundary(program, machine.Node, ref machine, ref state))
            {
                return Resource();
            }

            return Accept<TUnit, TReader>(plan, input, data, options.Flags, ref machine, ref state);
        }
        if (verb.Kind == Pcre2VerbKind.Fail)
        {
            if (verb.MarkIndex >= 0 && !Pcre2Kernel.PushVerbBoundary(program, machine.Node, ref machine, ref state))
            {
                return Resource();
            }

            Pcre2Kernel.Fail(ref state);
            return Continued(machine.Node);
        }
        if (!Pcre2Kernel.PushVerbBoundary(program, machine.Node, ref machine, ref state))
        {
            return Resource();
        }

        Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
        return Continued(machine.Node);
    }

    private static Pcre2Step Accept<TUnit, TReader>(Pcre2ControlPlan plan, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var program = plan.Program;
        var acceptNode = machine.Node;
        var assertionAccept = plan.IsAssertionAccept(acceptNode, ref machine, ref state);
        var recursion = Pcre2RecursionOps.CurrentRecursion(program, ref machine, ref state);
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        // compile.c6362 CLOSE follows lexical open-capture order, same assertion
        // level only. match.c831 suppresses every CLOSE inside a recursion.
        if (recursion < 0)
        {
            for (var parent = plan.Parent(acceptNode); parent >= 0; parent = plan.Parent(parent))
            {
                if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
                {
                    return Resource();
                }

                var lexical = program.Nodes[parent];
                if (lexical.Kind is Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring)
                {
                    break;
                }

                if (lexical.Kind != Pcre2NodeKind.Capture)
                {
                    continue;
                }

                var after = -1;
                while (Pcre2Kernel.ReadActiveContinuation(program, after, ref machine, ref state, out var index, out var frame))
                {
                    after = index;
                    if (frame.ScopeNode != parent || program.Nodes[frame.ScopeNode].Kind != Pcre2NodeKind.Capture)
                    {
                        continue;
                    }

                    var group = program.Groups[lexical.Operand].Number;
                    if (!storage.WriteCapture(group, frame.InputPosition, machine.Position, ref machine, ref state))
                    {
                        return Resource();
                    }

                    machine.CaptureLast = group;
                    machine.CaptureTop = Math.Max(machine.CaptureTop, group + 1);
                    break;
                }
                if (Denied(ref machine, ref state))
                {
                    return Resource();
                }
            }
        }
        var cursor = -1;
        while (Pcre2Kernel.ReadActiveContinuation(program, cursor, ref machine, ref state, out var target, out var boundary))
        {
            cursor = target;
            var kind = program.Nodes[boundary.ScopeNode].Kind;
            if (assertionAccept ? kind is not (Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring) : boundary.Kind != Pcre2FrameKind.Subroutine)
            {
                continue;
            }

            if (assertionAccept)
            {
                var negative = kind == Pcre2NodeKind.Assertion && program.Assertions[program.Nodes[boundary.ScopeNode].Operand].Kind is
                    Pcre2AssertionKind.NegativeAhead or Pcre2AssertionKind.NegativeBehind;
                if (!negative && boundary.Kind != Pcre2FrameKind.Predicate &&
                    !Pcre2Kernel.NotifyRmatchReturn(true, ref machine, ref state))
                {
                    return Resource();
                }
            }
            if (!Pcre2Kernel.UnwindToContinuation(program, target, true, ref machine, ref state))
            {
                return Resource();
            }

            return Pcre2Vm.Dispatch<TUnit, TReader>(program, input, data, flags,
                new(Pcre2StepKind.Return, boundary.ScopeNode), ref machine, ref state, assertionAccept);
        }
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        if (assertionAccept)
        {
            // A lexical ASSERT_ACCEPT can escape a called definition without an
            // enclosing assertion. Preserve the source internal signed return.
            machine.SourceReturnCode = -999;
            return new(Pcre2StepKind.SourceAbort, acceptNode, -999);
        }
        return Pcre2Kernel.FinishAccept(program, flags, ref machine, ref state);
    }

    private static Pcre2Step Propagate<TUnit, TReader>(Pcre2ControlPlan plan, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, ref Pcre2ControlState controls,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var program = plan.Program;
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        while (true)
        {
            int scopeIndex = -1, thenIndex = -1, cursor = -1;
            Pcre2Frame scope = default;
            while (Pcre2Kernel.ReadActiveContinuation(program, cursor, ref machine, ref state, out var index, out var frame))
            {
                cursor = index;
                if (controls.Signal == Pcre2ControlSignal.Then && frame.Kind == Pcre2FrameKind.Branch &&
                    plan.IsWithin(controls.VerbNode, frame.ScopeNode, ref machine, ref state))
                { thenIndex = index; break; }
                if (frame.Kind is Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Subroutine or Pcre2FrameKind.ScriptRun or Pcre2FrameKind.Predicate)
                { scopeIndex = index; scope = frame; break; }
            }
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (thenIndex >= 0)
            {
                if (!Pcre2Kernel.CutThenScope(program, thenIndex, ref machine, ref state))
                {
                    return Resource();
                }

                controls.Signal = Pcre2ControlSignal.None;
                return Continued(machine.Node);
            }
            // MARK remainder frames may survive inline success and remain real
            // source callers. Inspect only the interval before this module exit.
            if (controls.Signal == Pcre2ControlSignal.SkipArgument)
            {
                for (var index = machine.FrameCount - 1; index > scopeIndex; index--)
                {
                    if (!storage.ReadFrame(index, ref machine, ref state, out var mark))
                    {
                        return Resource();
                    }

                    if (mark.Kind != Pcre2FrameKind.Verb)
                    {
                        continue;
                    }

                    var marked = program.Controls[program.Nodes[mark.ScopeNode].Operand];
                    if (marked.MarkIndex < 0 || marked.Kind is not (Pcre2VerbKind.Mark or Pcre2VerbKind.Accept or Pcre2VerbKind.Fail))
                    {
                        continue;
                    }

                    if (!Pcre2Marks.SameSkipName(program, marked.MarkIndex, controls.ArgumentMarkIndex, ref machine, ref state))
                    { if (Denied(ref machine, ref state)) { return Resource(); } continue; }
                    controls.Signal = Pcre2ControlSignal.Skip;
                    controls.SkipPosition = mark.InputPosition;
                    break;
                }
            }
            if (scopeIndex < 0)
            {
                if (!Pcre2Kernel.UnwindToContinuation(program, -1, true, ref machine, ref state))
                {
                    return Resource();
                }

                Pcre2Kernel.Fail(ref state);
                return new(Pcre2StepKind.Backtrack, machine.Node);
            }
            var owner = program.Nodes[scope.ScopeNode];
            var assertion = owner.Kind == Pcre2NodeKind.Assertion;
            var negative = assertion && program.Assertions[owner.Operand].Kind is Pcre2AssertionKind.NegativeAhead or Pcre2AssertionKind.NegativeBehind;
            if (assertion && controls.Signal == Pcre2ControlSignal.Then && program.Assertions[owner.Operand].Kind is
                Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind)
            {
                var branch = program.Children[owner.Children.Offset];
                var body = program.Nodes[branch];
                if (body.Kind == Pcre2NodeKind.Alternative)
                {
                    branch = -1;
                    foreach (var candidate in program.Children.Slice(body.Children.Offset, body.Children.Count))
                    {
                        if (plan.IsWithin(controls.VerbNode, candidate, ref machine, ref state))
                        { branch = candidate; break; }
                        if (Denied(ref machine, ref state))
                        {
                            return Resource();
                        }
                    }
                    if (branch < 0)
                    {
                        throw new InvalidOperationException("THEN has no lexical top-level behind branch.");
                    }
                }
                if (!Pcre2Kernel.UnwindToContinuation(program, scopeIndex, false, ref machine, ref state))
                {
                    return Resource();
                }

                controls.Signal = Pcre2ControlSignal.None;
                return Pcre2AssertionOps.ResumeAfterThen<TUnit, TReader>(program, input, branch, ref machine, ref state);
            }
            var caught = scope.Kind == Pcre2FrameKind.Subroutine &&
                Pcre2RecursionOps.CurrentRecursion(program, ref machine, ref state) == controls.OriginRecursion ||
                (assertion || owner.Kind == Pcre2NodeKind.ScanSubstring) && controls.Signal == Pcre2ControlSignal.Then ||
                assertion && (negative || scope.Kind == Pcre2FrameKind.Predicate) &&
                    controls.Signal is Pcre2ControlSignal.Commit or Pcre2ControlSignal.Prune or Pcre2ControlSignal.Skip;
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (caught)
            {
                if (!Pcre2Kernel.UnwindToContinuation(program, scopeIndex, true, ref machine, ref state))
                {
                    return Resource();
                }

                controls.Signal = Pcre2ControlSignal.None;
                return Pcre2Vm.Dispatch<TUnit, TReader>(program, input, data, flags,
                    new(Pcre2StepKind.Backtrack, scope.ScopeNode), ref machine, ref state);
            }
            // Propagating source returns do not choose a conditional branch or
            // validate an unfinished script. Restore that caller's saved fields
            // through shared capture/window/continuation services, then continue.
            if (!storage.RestoreCaptures(scope.CaptureCursor, ref machine, ref state))
            {
                return Resource();
            }

            machine.Position = scope.Kind == Pcre2FrameKind.Scan ? scope.SavedWindow.SavedPosition : scope.InputPosition;
            machine.ReportStart = scope.ReportStart;
            machine.MarkIndex = scope.MarkIndex;
            machine.CaptureTop = scope.CaptureTop;
            machine.CaptureLast = scope.LastCapture;
            if (!Pcre2Kernel.SetNoteolCleared(scope.NoteolCleared, ref machine, ref state) ||
                !Pcre2Kernel.UnwindToContinuation(program, scope.Auxiliary, true, ref machine, ref state))
            {
                return Resource();
            }
        }
    }

    private static bool Denied(ref Pcre2VmState machine, ref RunState state) =>
        machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;
    private static Pcre2Step Resource() => new(Pcre2StepKind.ResourceFailure);
    private static Pcre2Step Continued(int node) => new(Pcre2StepKind.Continue, node);
}
