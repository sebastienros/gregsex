using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Flavors.Pcre2.Tables;

namespace GregsEx.Execution.Pcre2.Unicode;

// PCRE2 10.46 pcre2_extuni.c/pcre2_script_run.c; retained BSD notice is
// Public/Pcre2/PCRE2-NOTICE.txt. No ambient Unicode data or subject is retained.
internal readonly partial struct Pcre2UnicodeOps : IPcre2UnicodeOps
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

        var node = program.Nodes[machine.Node];
        if (node.Kind == Pcre2NodeKind.ScriptRun)
        {
            return Pcre2Kernel.EnterModuleChild(program, program.Children[node.Children.Offset],
                Pcre2FrameKind.ScriptRun, ref machine, ref state) ? Continued(machine.Node) : Resource();
        }

        if (node.Kind != Pcre2NodeKind.Grapheme)
        {
            throw new ArgumentException("Not a Unicode opcode.", nameof(program));
        }

        return Grapheme<TUnit, TReader>(program, input, data, flags, ref machine, ref state);
    }

    // Composition dispatches Return/Backtrack by the nearest module's frame kind.
    // Invalid script endpoints fail inside the child, allowing its ordinary
    // retained choices to run before this boundary itself is exhausted.
    internal static Pcre2Step Resume<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2StepKind occurrence, ref Pcre2VmState machine, ref RunState state)
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

        if (!Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out _, out var frame))
        {
            return Denied(ref machine, ref state) ? Resource() : throw new InvalidOperationException("Missing script continuation.");
        }

        if (frame.Kind != Pcre2FrameKind.ScriptRun || frame.ScopeNode != machine.Node)
        {
            throw new InvalidOperationException("Script return has a different owner.");
        }

        if (occurrence == Pcre2StepKind.Return)
        {
            var valid = Script<TUnit, TReader>(program, input, frame.InputPosition, machine.Position, data, ref machine, ref state);
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (!valid)
            {
                if (program.Nodes[frame.ScopeNode].Operand == 1)
                {
                    // Atomic script runs commit their child before the script
                    // check; a rejected run cannot shorten or choose another arm.
                    var failedStorage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
                    if (!failedStorage.RestoreCaptures(frame.CaptureCursor, ref machine, ref state) ||
                        !Pcre2Kernel.SetNoteolCleared(frame.NoteolCleared, ref machine, ref state))
                    {
                        return Resource();
                    }

                    machine.Position = frame.InputPosition;
                    machine.ReportStart = frame.ReportStart;
                    machine.MarkIndex = frame.MarkIndex;
                    machine.CaptureTop = frame.CaptureTop;
                    machine.CaptureLast = frame.LastCapture;
                    return Pcre2Kernel.LeaveModuleChild(program, false, true, ref machine, ref state) ? Continued(machine.Node) : Resource();
                }
                Pcre2Kernel.Fail(ref state);
                return Continued(machine.Node);
            }
            // Script KET continues inline: do not raise last_used_ptr here.
            return Pcre2Kernel.LeaveModuleChild(program, true, program.Nodes[frame.ScopeNode].Operand == 1,
                ref machine, ref state) ? Continued(machine.Node) : Resource();
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
        return Pcre2Kernel.LeaveModuleChild(program, false, true, ref machine, ref state) ? Continued(machine.Node) : Resource();
    }

    private static Pcre2Step Grapheme<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var position = machine.Position;
        var first = TReader.Read(input, position, machine.VisibleEnd, false, program.Profile, ref state);
        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        if (first.Status != Pcre2ReadStatus.Value)
        {
            if (first.Status == Pcre2ReadStatus.End &&
                Pcre2Kernel.RememberPartial(program, flags, ref machine, ref state))
            {
                return new(Pcre2StepKind.Partial);
            }

            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            Pcre2Kernel.Fail(ref state);
            return Continued(machine.Node);
        }
        int leftStart = position, left = Pcre2CharacterOps.Record(first.Value, data).GraphemeBreak;
        position += first.Width;
        var wasEpZwj = false;
        while (position < machine.VisibleEnd)
        {
            var next = TReader.Read(input, position, machine.VisibleEnd, false, program.Profile, ref state);
            if (Denied(ref machine, ref state))
            {
                return Resource();
            }

            if (next.Status != Pcre2ReadStatus.Value)
            { Pcre2Kernel.Fail(ref state); return Continued(machine.Node); }
            var right = Pcre2CharacterOps.Record(next.Value, data).GraphemeBreak;
            if ((Pcre2Data.GraphemeNoBreakMasks[left] & (1u << right)) == 0)
            {
                break;
            }

            if (left == (int)Pcre2GraphemeBreak.gbZWJ && right == (int)Pcre2GraphemeBreak.gbExtended_Pictographic && !wasEpZwj)
            {
                break;
            }

            if (left == (int)Pcre2GraphemeBreak.gbRegional_Indicator && right == (int)Pcre2GraphemeBreak.gbRegional_Indicator)
            {
                int cursor = leftStart, count = 0, floor = Pcre2Kernel.ScanStart(ref state);
                while (cursor > floor)
                {
                    var previous = TReader.ReadPrevious(input, cursor, program.Profile, ref state);
                    if (Denied(ref machine, ref state))
                    {
                        return Resource();
                    }

                    if (previous.Status != Pcre2ReadStatus.Value || cursor - previous.Width < floor)
                    {
                        break;
                    }

                    cursor -= previous.Width;
                    if (Pcre2CharacterOps.Record(previous.Value, data).GraphemeBreak != (int)Pcre2GraphemeBreak.gbRegional_Indicator)
                    {
                        break;
                    }

                    count++;
                }
                if ((count & 1) != 0)
                {
                    break;
                }
            }
            wasEpZwj = left == (int)Pcre2GraphemeBreak.gbExtended_Pictographic && right == (int)Pcre2GraphemeBreak.gbZWJ;
            if (right != (int)Pcre2GraphemeBreak.gbExtend || left != (int)Pcre2GraphemeBreak.gbExtended_Pictographic)
            {
                left = right;
            }

            leftStart = position;
            position += next.Width;
        }
        machine.Position = position;
        // Source CHECK_PARTIAL follows even a successfully consumed final cluster.
        if (position >= machine.VisibleEnd && Pcre2Kernel.RememberPartial(program, flags, ref machine, ref state))
        {
            return new(Pcre2StepKind.Partial);
        }

        if (Denied(ref machine, ref state))
        {
            return Resource();
        }

        Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
        return Continued(machine.Node);
    }

    private static bool Denied(ref Pcre2VmState machine, ref RunState state) => machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;
    private static void Require(Pcre2Program program, int length, ref Pcre2VmState machine)
    {
        if (!program.IsExecutable || length != machine.SubjectLength)
        {
            throw new ArgumentException("Unicode operations require an executable plan and original subject.", nameof(program));
        }
    }
    private static Pcre2Step Continued(int node) => new(Pcre2StepKind.Continue, node);
    private static Pcre2Step Resource() => new(Pcre2StepKind.ResourceFailure);
}
