using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Assertions;
using GregsEx.Execution.Pcre2.Unicode;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Unicode;

internal sealed class UnicodeFixture
{
    internal readonly Pcre2Program Program;
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly int[] States, Captures, Scratch;
    internal UnicodeFixture(Pcre2Program program, Pcre2ExecutionCapacity? capacity = null)
    {
        Program = program;
        Capacity = capacity ?? new(512, 8192, 32, 512);
        var sizes = Capacity.Required(program.GroupCount);
        States = new int[sizes.StateSlots];
        Captures = new int[sizes.CaptureSlots];
        Scratch = new int[sizes.ScratchSlots];
    }
    internal int LastWatermark, LastPosition;
    internal Pcre2MatchResult Match(string input, Span<Pcre2OffsetPair> pairs, int start = 0,
        Pcre2MatchOptions options = default, Pcre2MatchLimits? limits = null, int floor = 0, int? visibleEnd = null) =>
        Match<char, Pcre2CharTextReader>(input, pairs, start, options, limits, floor, visibleEnd);
    internal Pcre2MatchResult Match(byte[] input, Span<Pcre2OffsetPair> pairs, int start = 0,
        Pcre2MatchOptions options = default, Pcre2MatchLimits? limits = null, int floor = 0, int? visibleEnd = null) =>
        Match<byte, Pcre2ByteTextReader>(input, pairs, start, options, limits, floor, visibleEnd);
    private Pcre2MatchResult Match<TUnit, TReader>(ReadOnlySpan<TUnit> input, Span<Pcre2OffsetPair> pairs, int start,
        Pcre2MatchOptions options, Pcre2MatchLimits? ceiling, int floor, int? visibleEnd)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(ceiling ?? new()), input.Length, Program.PatternLimits, start);
        var state = Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
        {
            return Pcre2Kernel.Failure(ref machine, ref state);
        }

        var maxBehind = Program.Assertions.ToArray().Select(a => Math.Max(0, a.MaximumLength)).DefaultIfEmpty(0).Max();
        var validation = default(TReader).Validate(input, start, maxBehind, Program.Profile, options.Flags, Pcre2ValidationPolicy.NativeWindow, ref state);
        if (!validation.Valid)
        {
            return state.Failure.Code != RegexExecutionFailureCode.None ? Pcre2Kernel.Failure(ref machine, ref state) :
            new(Pcre2MatchStatus.Error, validation.NativeError, validation.Failure.Offset, Failure: validation.Failure);
        }

        machine.VisibleEnd = visibleEnd ?? input.Length;
        var data = Pcre2Data.Instance.Bind(Program.Profile.CharacterTables);
        while (true)
        {
            if (!Pcre2Kernel.BeginCandidate(Program, floor, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            while (true)
            {
                var step = Pcre2Kernel.Step<TUnit, TReader>(Program, input, data, options, ref machine, ref state);
                if (step.Kind == Pcre2StepKind.Unicode)
                {
                    step = Pcre2UnicodeOps.Step<TUnit, TReader>(Program, input, data, options.Flags, ref machine, ref state);
                }
                else if (step.Kind == Pcre2StepKind.Control && Program.Nodes[machine.Node].Kind == Pcre2NodeKind.ResetStart)
                {
                    // Nonshipping source \K event; actual controls are another owner.
                    machine.ReportStart = machine.Position;
                    Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
                    step = new(Pcre2StepKind.Continue, machine.Node);
                }
                else if (step.Kind == Pcre2StepKind.EnterAssertion)
                {
                    step = Pcre2AssertionOps.Step<TUnit, TReader>(Program, input, data, options.Flags, ref machine, ref state);
                }
                else if (step.Kind is Pcre2StepKind.Return or Pcre2StepKind.Backtrack)
                {
                    if (Pcre2Kernel.ReadModuleContinuation(Program, ref machine, ref state, out _, out var frame))
                    {
                        if (frame.Kind == Pcre2FrameKind.ScriptRun)
                        {
                            step = Pcre2UnicodeOps.Resume<TUnit, TReader>(Program, input, data, step.Kind, ref machine, ref state);
                        }
                        else
                        {
                            step = Pcre2AssertionOps.Resume<TUnit, TReader>(Program, input, step.Kind, ref machine, ref state);
                        }
                    }
                    else if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
                    {
                        step = new(Pcre2StepKind.ResourceFailure);
                    }
                }
                LastWatermark = machine.LastUsedPosition;
                LastPosition = machine.Position;
                if (step.Kind == Pcre2StepKind.Continue)
                {
                    continue;
                }

                if (step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.Partial or Pcre2StepKind.ResourceFailure)
                {
                    return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
                }

                Assert.Equal(Pcre2StepKind.Backtrack, step.Kind);
                break;
            }
            if ((options.Flags & Pcre2MatchFlags.Anchored) != 0 || (Program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0 || machine.CandidateStart == machine.VisibleEnd)
            {
                return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
            }

            var next = default(TReader).AdvanceAfterEmpty(input, machine.CandidateStart, Program.Profile, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            machine.CandidateStart = next;
        }
    }
}
