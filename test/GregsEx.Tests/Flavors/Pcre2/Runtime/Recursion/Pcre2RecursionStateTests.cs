using System.Text;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Recursion;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Recursion;

public sealed class Pcre2RecursionStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LoopWatermarkAndNearestMatchingTarget(int change)
    {
        var p = new RecursionPlan();
        var call = p.Call();
        var f = new RecursionFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 4);
        var state = f.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        Assert.Equal(1, machine.CallDepth);
        if (change == 1)
        {
            Assert.True(Pcre2Kernel.UpdateLastUsedPosition(3, ref machine, ref state));
        }

        if (change == 2)
        {
            machine.Position = 1;
        }

        machine.Node = call;
        var result = Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state);
        Assert.Equal(change == 0 ? Pcre2StepKind.SourceAbort : Pcre2StepKind.Continue, result.Kind);
        if (change == 0)
        {
            Assert.Equal((int)Pcre2ErrorCode.Recurseloop, machine.SourceReturnCode);
        }
        else
        {
            Assert.Equal(2, machine.CallDepth);
            // The newer matching call is authoritative even when an older call
            // has the current entry position but a different watermark.
            machine.Node = call;
            Assert.Equal(Pcre2StepKind.SourceAbort, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        }
    }
    [Fact]
    public void MutualLoopIsSourceError()
    {
        var p = new RecursionPlan();
        int first = p.Capture(p.Call(2), 1), second = p.Capture(p.Call(1), 2);
        p.Seq(p.Define(p.Seq(first, second)), p.Call(1));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal((int)Pcre2ErrorCode.Recurseloop, f.Match("", pairs).NativeReturnCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulCallPreservesControlButFailureRestoresCaller(bool success)
    {
        var p = new RecursionPlan();
        var call = p.Call();
        var f = new RecursionFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 4, startAt: 1);
        var state = f.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        machine.MarkIndex = 7;
        machine.ReportStart = 1;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        machine.Position = 3;
        machine.ReportStart = 2;
        machine.MarkIndex = 8;
        machine.Node = call;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Resume(f.Program, new(success ? Pcre2StepKind.Return : Pcre2StepKind.Backtrack, call), ref machine, ref state).Kind);
        Assert.Equal(success ? 3 : 1, machine.Position);
        Assert.Equal(success ? 2 : 1, machine.ReportStart);
        Assert.Equal(success ? 8 : 7, machine.MarkIndex);
        Assert.Equal(1, machine.SearchStart);
        Assert.Equal(1, machine.CandidateStart);
        Assert.Equal(0, machine.CallDepth);
        Assert.Equal(1, machine.LastUsedPosition); // successful inline KET is not RRETURN
    }
    [Fact]
    public void RawOpenCaptureConditionalAndSnapshotRestore()
    {
        var p = new RecursionPlan();
        var def = p.Define(p.Capture(p.Lit('a'), 1));
        var cond = p.Condition(Pcre2ConditionKind.CaptureSet, p.Lit('b'), p.Lit('c'), p.Reference(1));
        p.Seq(def, cond);
        var f = new RecursionFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 2);
        var state = f.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        var storage = new Pcre2Storage(state.Buffers, f.Capacity, 2);
        Assert.True(storage.WriteCapture(1, 0, -1, ref machine, ref state));
        machine.CaptureTop = 2;
        machine.Node = cond;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        Assert.Equal('b', f.Program.Nodes[machine.Node].Operand);
        Assert.Equal(0, machine.CallDepth);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CurrentRecursionDoesNotManufactureConditionalCalls(int kind)
    {
        var p = new RecursionPlan();
        var reference = kind == 1 ? p.Reference(0, Pcre2ReferenceKind.WholePattern) : kind == 2 ? p.Reference(1) : kind == 3 ? p.Reference(1, Pcre2ReferenceKind.Named, 1, 2) : -1;
        var conditional = p.Condition(kind == 0 ? Pcre2ConditionKind.AnyRecursion : Pcre2ConditionKind.Recursion, p.Lit('y'), p.Lit('n'), reference);
        int g2 = p.Capture(conditional, 2), g1 = p.Capture(p.Lit('a'), 1), call = p.Call(2);
        p.Seq(p.Define(p.Seq(g1, g2)), call);
        var f = new RecursionFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 2);
        var state = f.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        machine.Node = call;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        Assert.Equal(2, Pcre2RecursionOps.CurrentRecursion(f.Program, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        Assert.Equal(kind is 0 or 3 ? 'y' : 'n', f.Program.Nodes[machine.Node].Operand);
        Assert.Equal(1, machine.CallDepth);
    }
    [Fact]
    public void ScopedPredicateFalseWithMissingElseIsEmpty()
    {
        var p = new RecursionPlan();
        var assertion = p.Assertion(p.Lit('a'));
        p.Predicate(assertion, p.Lit('x'));
        var f = new RecursionFixture(p.Build()) { PredicateDriver = true };
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("b", pairs).Status);
        Assert.Equal(new(true, 0, 0), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("a", pairs).Status);
    }
    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void NativeUnitPositionsAndOriginalSearchStartSurviveCalls(int width, bool utf)
    {
        var p = new RecursionPlan(new((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0));
        var cp = utf ? 0x1f600 : 0xe9;
        p.Seq(p.Define(p.Capture(p.Lit(cp), 1)), p.Repeat(p.Call(1), 2, 2));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Pcre2MatchResult r;
        int end;
        if (width == 8)
        {
            var input = utf ? Encoding.UTF8.GetBytes("xx😀😀") : [120, 120, 233, 233];
            end = input.Length;
            r = f.Match(input, pairs, start: 2);
        }
        else
        {
            var input = utf ? "xx😀😀" : "xxéé";
            end = input.Length;
            r = f.Match(input, pairs, start: 2);
        }
        Assert.Equal(Pcre2MatchStatus.Match, r.Status);
        Assert.Equal(new(true, 2, end), pairs[0]);
        Assert.Equal(default, pairs[1]);
        Assert.Equal(2, r.StartChar);
    }
    [Fact]
    public void WindowsAndNamedSnapshotsSurviveCallReturn()
    {
        var p = new RecursionPlan();
        var call = p.Call();
        var f = new RecursionFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 5, startAt: 2);
        var state = f.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 1, ref machine, ref state));
        var storage = new Pcre2Storage(state.Buffers, f.Capacity, 1);
        var window = new Pcre2Window(4, 1, call, 2, 0, 0);
        Assert.True(storage.PushWindow(window, ref machine, ref state));
        Assert.True(Pcre2Kernel.RefreshWindow(f.Program, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Step(f.Program, 0, ref machine, ref state).Kind);
        Assert.True(Pcre2Kernel.ReadModuleContinuation(f.Program, ref machine, ref state, out _, out var frame));
        Assert.Equal(window, frame.SavedWindow);
        Assert.Equal(2, frame.StartMatch);
        Assert.Equal(2, frame.RecurseLastUsedPosition);
        machine.Node = call;
        machine.Position = 3;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2RecursionOps.Resume(f.Program, new(Pcre2StepKind.Return, call), ref machine, ref state).Kind);
        Assert.Equal(4, machine.VisibleEnd);
        Assert.Equal(1, machine.WindowDepth);
        Assert.Equal(1, Pcre2Kernel.ScanStart(ref state));
    }
}
