using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

public sealed class Pcre2ContinuationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ModuleBoundaryReturnsBeforeOuterChoiceRollback(bool success)
    {
        var p = new KernelPlan();
        var child = p.Literal('a');
        var assertion = p.Assertion(child);
        p.Alt(assertion, p.Literal('b'));
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = fixture.State(machine.Limits);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Step(fixture.Program, data, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.EnterAssertion, Step(fixture.Program, data, ref machine, ref state));
        Assert.True(Pcre2Kernel.EnterModuleChild(fixture.Program, child, Pcre2FrameKind.Assertion, ref machine, ref state));
        // This nonshipping driver executes only the child kernel and owns the
        // lookaround response, not a fake production assertion implementation.
        if (!success)
        {
            Pcre2Kernel.Fail(ref state);
        }
        else
        {
            Assert.Equal(Pcre2StepKind.Continue, Step(fixture.Program, data, ref machine, ref state));
        }

        Assert.Equal(success ? Pcre2StepKind.Return : Pcre2StepKind.Backtrack, Step(fixture.Program, data, ref machine, ref state));
        Assert.True(Pcre2Kernel.ReadModuleContinuation(fixture.Program, ref machine, ref state, out _, out var frame));
        Assert.Equal(assertion, frame.ScopeNode);
        Assert.Equal(0, frame.InputPosition);
        machine.Position = frame.InputPosition;
        Assert.True(Pcre2Kernel.LeaveModuleChild(fixture.Program, success, true, ref machine, ref state));
        if (!success)
        {
            Assert.Equal(Pcre2StepKind.Continue, Step(fixture.Program, data, ref machine, ref state)); // resumes b outer choice
            Assert.Equal(Pcre2StepKind.Continue, Step(fixture.Program, data, ref machine, ref state)); // b misses on a
            Assert.Equal(Pcre2StepKind.Backtrack, Step(fixture.Program, data, ref machine, ref state));
        }
        else
        {
            Assert.Equal(Pcre2StepKind.Accept, Step(fixture.Program, data, ref machine, ref state));
        }
    }
    [Fact]
    public void NestedWindowRollbackRestoresVisibleEndAndFloor()
    {
        var p = new KernelPlan();
        p.Alt(p.Literal('a'), p.Literal('b'));
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 4);
        var state = fixture.State(machine.Limits);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "abcd", data, default, ref machine, ref state).Kind);
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, 1);
        Assert.True(storage.PushWindow(new(3, 1, 0, 0, 0, 0), ref machine, ref state));
        Assert.True(Pcre2Kernel.RefreshWindow(fixture.Program, ref machine, ref state));
        Assert.Equal(3, machine.VisibleEnd);
        Assert.Equal(1, Pcre2Kernel.ScanStart(ref state));
        Assert.True(storage.PushWindow(new(2, 2, 0, 0, 0, 0), ref machine, ref state));
        Assert.True(Pcre2Kernel.RefreshWindow(fixture.Program, ref machine, ref state));
        Assert.Equal(2, machine.VisibleEnd);
        Assert.Equal(2, Pcre2Kernel.ScanStart(ref state));
        Pcre2Kernel.Fail(ref state);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Backtrack(fixture.Program, ref machine, ref state).Kind);
        Assert.Equal(0, machine.WindowDepth);
        Assert.Equal(4, machine.VisibleEnd);
        Assert.Equal(0, Pcre2Kernel.ScanStart(ref state));
    }
    [Fact]
    public void FragmentFloorPreventsWordBoundaryRewindAndEndStaysOriginal()
    {
        var p = new KernelPlan();
        p.Seq(p.Leaf(Pcre2NodeKind.WordBoundary), p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 4, startAt: 2) { VisibleEnd = 3 };
        var state = fixture.State(machine.Limits);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 2, ref machine, ref state));
        Pcre2Step step;
        do
        { step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "xxab", data, new() { Flags = Pcre2MatchFlags.Noteol }, ref machine, ref state); }
        while (step.Kind == Pcre2StepKind.Continue);
        Assert.Equal(Pcre2StepKind.Backtrack, step.Kind);
        Assert.Equal(3, machine.Position); // \z must not accept temporary end
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
    }
    [Fact]
    public void OpenRepeatedCapturePreservesPreviousCompletedRegister()
    {
        var p = new KernelPlan();
        var op = p.Leaf(Pcre2NodeKind.Grapheme);
        p.Repeat(p.Capture(p.Seq(p.Literal('a'), op)), 2, 2);
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 2);
        var state = fixture.State(machine.Limits);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, 2);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        var hits = 0;
        while (true)
        {
            var step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "aa", data, default, ref machine, ref state);
            if (step.Kind == Pcre2StepKind.Accept)
            {
                break;
            }

            if (step.Kind == Pcre2StepKind.Unicode)
            {
                Assert.Equal(hits == 0 ? default : new Pcre2OffsetPair(true, 0, 1), storage.Capture(1));
                hits++;
                // Nonshipping continuation probe supplies an empty child result,
                // solely to inspect the pending capture; no Unicode engine exists here.
                Pcre2Kernel.CompleteNode(op, ref machine, ref state);
            }
            else
            {
                Assert.Equal(Pcre2StepKind.Continue, step.Kind);
            }
        }
        Assert.Equal(2, hits);
        Assert.Equal(new(true, 1, 2), storage.Capture(1));
    }
    [Fact]
    public void ModuleChoiceReturnsOpaqueRetryAndRestoresFrozenSnapshots()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Grapheme);
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = fixture.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        machine.MarkIndex = 2;
        Assert.True(Pcre2Kernel.PushModuleChoice(fixture.Program, 0, 123, ref machine, ref state));
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, 1);
        Assert.True(storage.ReadFrame(0, ref machine, ref state, out var choice));
        Assert.Equal(0, choice.StartMatch);
        machine.MarkIndex = 3;
        machine.Position = 1;
        Pcre2Kernel.Fail(ref state);
        Assert.Equal(Pcre2StepKind.Return, Pcre2Kernel.Backtrack(fixture.Program, ref machine, ref state).Kind);
        Assert.Equal(2, machine.MarkIndex);
        Assert.Equal(0, machine.Position);
        Assert.True(Pcre2Kernel.TakeModuleRetry(ref state, out var payload));
        Assert.Equal(123, payload);
        Assert.False(Pcre2Kernel.TakeModuleRetry(ref state, out _));
    }
    private static Pcre2StepKind Step(Pcre2Program program, Pcre2DataView data, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Kernel.Step<char, Pcre2CharTextReader>(program, "a", data, default, ref machine, ref state).Kind;
}
