using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Assertions;

// The composition loop supplies the kernel occurrence explicitly on returns.
// No module-private header, retained subject, or recursive matcher is needed.
internal readonly partial struct Pcre2AssertionOps : IPcre2AssertionOps
{
    public static Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit> =>
        Step<TUnit, TReader>(program, input, data, default, ref machine, ref state);

    internal static Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        Require(program, input.Length, ref machine);
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        return program.Nodes[machine.Node].Kind switch
        {
            Pcre2NodeKind.Backreference => Backreference<TUnit, TReader>(program, input, data, flags, ref machine, ref state),
            Pcre2NodeKind.Assertion => EnterAssertion<TUnit, TReader>(program, input, false, ref machine, ref state),
            Pcre2NodeKind.ScanSubstring => EnterScan(program, ref machine, ref state),
            _ => throw new ArgumentException("Not an assertion module opcode.", nameof(program))
        };
    }

    // Recursion owns the enclosing conditional. Its Predicate continuation enters
    // this assertion; the result is consumed once with TakePredicateResult(node).
    internal static Pcre2Step EnterPredicate<TUnit, TReader>(Pcre2Program program,
        ReadOnlySpan<TUnit> input, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        Require(program, input.Length, ref machine);
        if (program.Nodes[machine.Node].Kind != Pcre2NodeKind.Assertion)
        {
            throw new ArgumentException("Predicate requires an assertion.", nameof(program));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        var kind = program.Assertions[program.Nodes[machine.Node].Operand].Kind;
        if (kind is Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind)
        {
            throw new ArgumentException("Conditional assertions must be atomic.", nameof(program));
        }

        return EnterAssertion<TUnit, TReader>(program, input, true, ref machine, ref state);
    }

    internal static Pcre2Step Resume<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2StepKind occurrence, ref Pcre2VmState machine, ref RunState state, bool forceAtomic = false)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        Require(program, input.Length, ref machine);
        if (occurrence is not (Pcre2StepKind.Return or Pcre2StepKind.Backtrack))
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return Resource();
        }

        var retry = Pcre2Kernel.TakeModuleRetry(ref state, out var branch);
        if (!Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out _, out var frame))
        {
            return Denied(ref machine, ref state) ? Resource() : throw new InvalidOperationException("Missing assertion continuation.");
        }

        if (frame.ScopeNode != machine.Node || frame.Kind is not (Pcre2FrameKind.Assertion or Pcre2FrameKind.Scan or Pcre2FrameKind.Predicate))
        {
            throw new InvalidOperationException("Assertion return has a different owner.");
        }

        var node = program.Nodes[frame.ScopeNode];
        var predicate = frame.Kind == Pcre2FrameKind.Predicate;
        if (retry)
        {
            if (node.Kind != Pcre2NodeKind.Assertion || !Behind(program.Assertions[node.Operand].Kind))
            {
                throw new InvalidOperationException("Unexpected assertion retry payload.");
            }

            var nextStart = machine.Position;
            var ordinal = BranchOrdinal(program, frame.ScopeNode, branch, ref machine, ref state);
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            machine.Position = frame.InputPosition;
            if (!Pcre2Kernel.LeaveModuleChild(program, false, true, ref machine, ref state))
            {
                return Resource();
            }

            machine.Node = frame.ScopeNode;
            return ScheduleBehind<TUnit, TReader>(program, input, ordinal, nextStart, frame.InputPosition,
                predicate, ref machine, ref state);
        }

        var matched = occurrence == Pcre2StepKind.Return;
        var scan = node.Kind == Pcre2NodeKind.ScanSubstring;
        var assertion = scan ? default : program.Assertions[node.Operand];
        // VREVERSE accepts only an endpoint at the original assertion position.
        // Nested lookaheads still see the original consumption and true ends.
        if (matched && !scan && Behind(assertion.Kind) && machine.Position != frame.InputPosition && !forceAtomic)
        { Pcre2Kernel.Fail(ref state); return Continued(machine.Node); }
        // Predicate boundaries deliberately do not receive core's standalone
        // Assertion update. Conditional assertion RRETURN raises last_used
        // before the assertion pointer is restored (6231/6302/6776).
        var negative = !scan && Negative(assertion.Kind);
        // Source conditional/negative KET6231/6331 RRETURNs before eptr reset.
        // Positive assertion KET6302 and scan KET6337 continue inline.
        if (matched && (predicate || negative) &&
            !Pcre2Kernel.NotifyRmatchReturn(true, ref machine, ref state))
        {
            return Resource();
        }

        var value = matched != negative;
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var retain = matched && (!negative || predicate);
        if (!retain)
        {
            if (!storage.RestoreCaptures(frame.CaptureCursor, ref machine, ref state))
            {
                return Resource();
            }

            machine.ReportStart = frame.ReportStart;
            machine.MarkIndex = frame.MarkIndex;
            machine.CaptureTop = frame.CaptureTop;
            machine.CaptureLast = frame.LastCapture;
        }
        // Conditional assertion KET copies the ovector/top and mark into its
        // caller, not the child's reset-start field (match.c6226-6229).
        if (predicate)
        { machine.ReportStart = frame.ReportStart; machine.CaptureLast = frame.LastCapture; }
        // Scan clears NOTEOL across its inline, restored-window remainder.
        // Real predicate/negative/failure RRETURN exits restore caller options.
        if ((!retain || predicate) && !Pcre2Kernel.SetNoteolCleared(frame.NoteolCleared, ref machine, ref state))
        {
            return Resource();
        }

        if (scan)
        {
            if (!frame.HasSavedWindow || frame.WindowDepth == 0)
            {
                throw new InvalidOperationException("Scan requires its saved window.");
            }

            machine.Position = frame.SavedWindow.SavedPosition;
            while (machine.WindowDepth >= frame.WindowDepth)
            {
                if (!storage.PopWindow(ref machine, ref state, out _))
                {
                    return Resource();
                }
            }

            if (!Pcre2Kernel.RefreshWindow(program, ref machine, ref state))
            {
                return Resource();
            }
        }
        else
        {
            machine.Position = frame.InputPosition;
        }

        var atomic = forceAtomic || !scan && assertion.Kind is not (Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind);
        var left = predicate ? Pcre2Kernel.LeaveModulePredicate(program, value, true, ref machine, ref state) :
            Pcre2Kernel.LeaveModuleChild(program, value, atomic, ref machine, ref state);
        return left ? Continued(machine.Node) : Resource();
    }

    private static Pcre2Step EnterAssertion<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        bool predicate, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var owner = machine.Node;
        var node = program.Nodes[owner];
        var assertion = program.Assertions[node.Operand];
        if (Behind(assertion.Kind))
        {
            if (NextBranch<TUnit, TReader>(program, input, owner, 0, machine.Position,
                ref machine, ref state, out var ordinal, out var start))
            {
                return ScheduleBehind<TUnit, TReader>(program, input, ordinal, start, machine.Position, predicate, ref machine, ref state);
            }

            if (Denied(ref machine, ref state))
            {
                return Resource();
            }
            // Keep predicate truth scoped, including an unavailable positive branch.
            if (predicate)
            {
                if (!Pcre2Kernel.EnterModuleChild(program, owner, Pcre2FrameKind.Predicate, ref machine, ref state) ||
                    !Pcre2Kernel.LeaveModulePredicate(program, Negative(assertion.Kind), true, ref machine, ref state))
                {
                    return Resource();
                }
            }
            else if (Negative(assertion.Kind))
            {
                Pcre2Kernel.CompleteNode(owner, ref machine, ref state);
            }
            else
            {
                Pcre2Kernel.Fail(ref state);
            }

            return Continued(owner);
        }
        return Pcre2Kernel.EnterModuleChild(program, program.Children[node.Children.Offset],
            predicate ? Pcre2FrameKind.Predicate : Pcre2FrameKind.Assertion, ref machine, ref state) ? Continued(machine.Node) : Resource();
    }

    private static Pcre2Step EnterScan(Pcre2Program program, ref Pcre2VmState machine, ref RunState state)
    {
        var node = program.Nodes[machine.Node];
        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        var slice = program.ScanReferences[node.Operand];
        Pcre2OffsetPair capture = default;
        foreach (var reference in program.References.Slice(slice.Offset, slice.Count))
        {
            if (Select(program, reference, storage, ref machine, ref state, out capture))
            {
                break;
            }

            if (Denied(ref machine, ref state))
            {
                return Resource();
            }
        }
        if (!capture.Participated)
        { Pcre2Kernel.Fail(ref state); return Continued(machine.Node); }
        if (capture.Start < Pcre2Kernel.ScanStart(ref state) || capture.End > machine.SubjectLength || capture.End < capture.Start)
        {
            throw new InvalidOperationException("Invalid scan capture coordinates.");
        }

        var window = new Pcre2Window(capture.End, Pcre2Kernel.ScanStart(ref state), machine.Node,
            machine.Position, machine.FrameCount, machine.CaptureCursor);
        if (!storage.PushWindow(window, ref machine, ref state) ||
            !Pcre2Kernel.RefreshWindow(program, ref machine, ref state))
        {
            return Resource();
        }

        machine.Position = capture.Start;
        if (!Pcre2Kernel.EnterModuleChild(program, program.Children[node.Children.Offset], Pcre2FrameKind.Scan,
            ref machine, ref state) || !Pcre2Kernel.SetNoteolCleared(true, ref machine, ref state))
        {
            return Resource();
        }

        return Continued(machine.Node);
    }

    private static bool Select(Pcre2Program program, Pcre2Reference reference, Pcre2Storage storage,
        ref Pcre2VmState machine, ref RunState state, out Pcre2OffsetPair capture)
    {
        capture = default;
        foreach (var group in program.Numbers.Slice(reference.Candidates.Offset, reference.Candidates.Count))
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            if (group >= machine.CaptureTop)
            {
                continue;
            }

            capture = storage.Capture(group);
            if (capture.Participated)
            {
                return true;
            }
        }
        return false;
    }
    private static bool Behind(Pcre2AssertionKind kind) => kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind;
    private static bool Negative(Pcre2AssertionKind kind) => kind is Pcre2AssertionKind.NegativeAhead or Pcre2AssertionKind.NegativeBehind;
    private static bool Denied(ref Pcre2VmState machine, ref RunState state) => machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;
    private static void Require(Pcre2Program program, int length, ref Pcre2VmState machine)
    {
        if (!program.IsExecutable || length != machine.SubjectLength)
        {
            throw new ArgumentException("Assertions require a validated executable plan and original subject.", nameof(program));
        }
    }
    private static Pcre2Step Continued(int node) => new(Pcre2StepKind.Continue, node);
    private static Pcre2Step Resource() => new(Pcre2StepKind.ResourceFailure);
}
