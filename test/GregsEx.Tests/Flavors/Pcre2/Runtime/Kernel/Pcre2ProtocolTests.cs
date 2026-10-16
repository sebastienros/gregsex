using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

// Nonshipping protocol drivers; no substitute assertion/subroutine engine.
public sealed class Pcre2ProtocolTests
{
    [Fact]
    public void ScanNoteolScopeSurvivesInlineRemainderAndRestoresOnEarlierChoiceAndCandidate()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 1);
        var s = f.State(m.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.True(Pcre2Kernel.PushModuleChoice(f.Program, f.Program.Root, 7, ref m, ref s));
        Assert.True(Pcre2Kernel.SetNoteolCleared(true, ref m, ref s));
        // No Scan remains active, but its restored-window remainder keeps NOTEOL clear.
        Assert.Equal(Pcre2MatchFlags.Notbol, Pcre2Kernel.EffectiveContextFlags(f.Program,
            Pcre2MatchFlags.Notbol | Pcre2MatchFlags.Noteol, ref m, ref s));
        Pcre2Kernel.Fail(ref s);
        Assert.Equal(Pcre2StepKind.Return, Pcre2Kernel.Backtrack(f.Program, ref m, ref s).Kind);
        Assert.False(m.NoteolCleared);
        Assert.Equal(Pcre2MatchFlags.Noteol,
            Pcre2Kernel.EffectiveContextFlags(f.Program, Pcre2MatchFlags.Noteol, ref m, ref s));
        Assert.True(Pcre2Kernel.SetNoteolCleared(true, ref m, ref s));
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.False(m.NoteolCleared);
    }
    [Fact]
    public void RetainingRestorePreservesOriginalUndoCheckpointsAndRawOpenRegisters()
    {
        var p = new KernelPlan();
        p.Capture(p.Leaf(Pcre2NodeKind.Empty));
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 4);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 2);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.True(storage.WriteCapture(1, 0, 1, ref m, ref s));
        var caller = m.CaptureCursor;
        Assert.True(storage.WriteCapture(1, 2, -1, ref m, ref s));
        var local = m.CaptureCursor;
        Assert.True(storage.WriteCapture(1, 3, 4, ref m, ref s));
        var original = m.CaptureCursor;
        Assert.True(storage.RestoreCapturesRetaining(caller, ref m, ref s));
        Assert.Equal((0, 1), storage.Registers(1));
        Assert.Equal(original + 2, m.CaptureCursor);
        Assert.True(storage.RestoreCaptures(local, ref m, ref s));
        Assert.Equal((2, -1), storage.Registers(1));
        Assert.False(storage.Capture(1).Participated);
        Assert.True(storage.RestoreCaptures(caller, ref m, ref s));
        Assert.Equal((0, 1), storage.Registers(1));
        Assert.True(storage.RestoreCaptures(0, ref m, ref s));
        Assert.Equal((-1, -1), storage.Registers(1));
    }
    [Fact]
    public void RetainingRestoreCapacityDenialDoesNotDiscardUndoAndWorkspaceCanRestart()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build(), new(8, 2, 2, 16));
        var m = new Pcre2VmState(f.Capacity, new(), 1);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.True(storage.WriteCapture(0, 0, 0, ref m, ref s));
        Assert.True(storage.WriteCapture(0, 0, 1, ref m, ref s));
        Assert.False(storage.RestoreCapturesRetaining(0, ref m, ref s));
        Assert.Equal(2, m.CaptureCursor);
        Assert.Equal((0, 1), storage.Registers(0));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, s.Failure.Code);
        Pcre2KernelOperation.ReleaseTransient(ref m);
        var restarted = f.State(m.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref restarted));
        Assert.Equal((-1, -1), storage.Registers(0));
    }
    [Fact]
    public void NonAtomicCallReturnLeavesChoicesButRestoresLogicalDepthAndWatermark()
    {
        var p = new KernelPlan();
        var child = p.Alt(p.Literal('a'), p.Literal('b'));
        var owner = p.Assertion(child);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 4);
        var s = f.State(m.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        // Source peer owns call target semantics. This driver exercises its shared boundary.
        m.Node = owner;
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, child, Pcre2FrameKind.Subroutine, ref m, ref s));
        Assert.Equal(1, m.CallDepth);
        Assert.True(Pcre2Kernel.ReadModuleContinuation(f.Program, ref m, ref s, out var call, out var entered));
        Assert.Equal(0, entered.RecurseLastUsedPosition);
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out var activeIndex, out _));
        Assert.Equal(call, activeIndex);
        Assert.Equal(Pcre2StepKind.Continue, Step(f, ref m, ref s).Kind); // creates child alternative
        Assert.Equal(Pcre2StepKind.Continue, Step(f, ref m, ref s).Kind); // consumes a, but no scalar-read watermark update
        Assert.Equal(0, m.LastUsedPosition);
        Assert.Equal(Pcre2StepKind.Return, Step(f, ref m, ref s).Kind);
        Assert.Equal(0, m.LastUsedPosition);
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, true, false, ref m, ref s));
        Assert.Equal(0, m.CallDepth);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
        Assert.True(storage.ReadFrame(call, ref m, ref s, out var inactive));
        Assert.False(inactive.CallActive);
        Assert.False(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out _, out _));
        // A later failed remainder advances the global watermark, and local reentry never rolls it back.
        m.Position = 3;
        Pcre2Kernel.Fail(ref s);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Backtrack(f.Program, ref m, ref s).Kind);
        Assert.Equal(1, m.CallDepth);
        Assert.Equal(0, m.Position);
        Assert.Equal(3, m.LastUsedPosition);
        Assert.True(storage.ReadFrame(call, ref m, ref s, out var active));
        Assert.True(active.CallActive);
        Assert.Equal(0, active.RecurseLastUsedPosition);
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out _, out var current));
        Assert.False(m.Position == current.InputPosition && m.LastUsedPosition == current.RecurseLastUsedPosition);
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, false, true, ref m, ref s));
        Assert.Equal(0, m.CallDepth);
    }
    [Theory]
    [InlineData(0)] // Subroutine KET6272 continues inline
    [InlineData(1)] // Predicate/conditional KET6274 continues inline
    [InlineData(2)] // Scan KET6337 runs outer remainder before returning
    [InlineData(3)] // Successful script KET validates then continues inline
    public void InlineModuleSuccessDoesNotManufactureSourceLastUsedReturn(int kind)
    {
        var kinds = new[] { Pcre2FrameKind.Subroutine, Pcre2FrameKind.Predicate, Pcre2FrameKind.Scan, Pcre2FrameKind.ScriptRun };
        var p = new KernelPlan();
        var child = p.Literal('a');
        var owner = p.Assertion(child);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 10);
        var s = f.State(m.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        if (kinds[kind] == Pcre2FrameKind.Scan)
        {
            var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
            Assert.True(storage.PushWindow(new(2, 0, owner, 0, 0, 0), ref m, ref s));
        }
        m.Node = owner;
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, child, kinds[kind], ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Step(f, ref m, ref s).Kind);
        Assert.Equal(Pcre2StepKind.Return, Step(f, ref m, ref s).Kind);
        Assert.Equal(1, m.Position);
        Assert.Equal(0, m.LastUsedPosition);
        // Actual module RRETURN/control event may explicitly update, retaining
        // the common work/cancel budget; ordinary successful KET does not.
        Assert.True(Pcre2Kernel.UpdateLastUsedPosition(m.Position, ref m, ref s));
        Assert.Equal(1, m.LastUsedPosition);
    }
    [Fact]
    public void NestedInactiveAndActiveCallsPopWithoutCountingRetainedFrames()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 1);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        var outer = Frame(0);
        Assert.True(storage.PushFrame(outer, ref m, ref s));
        var inner = Frame(1);
        Assert.True(storage.PushFrame(inner, ref m, ref s));
        Assert.Equal(2, m.CallDepth);
        Assert.True(storage.SetCallActive(1, false, ref m, ref s));
        m.CallDepth = 1;
        Assert.True(storage.PushFrame(Frame(1), ref m, ref s));
        Assert.Equal(2, m.CallDepth);
        Assert.True(storage.PopFrame(ref m, ref s, out _));
        Assert.Equal(1, m.CallDepth);
        Assert.True(storage.PopFrame(ref m, ref s, out var inactive));
        Assert.False(inactive.CallActive);
        Assert.Equal(1, m.CallDepth);
        Assert.True(storage.PopFrame(ref m, ref s, out _));
        Assert.Equal(0, m.CallDepth);
    }
    [Fact]
    public void SuspendedNestedScanRestoresOverwrittenWindowsTrueEndAndContextFlags()
    {
        var p = new KernelPlan();
        var child = p.Alt(p.Literal('a'), p.Literal('b'));
        var owner = p.Assertion(child);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 10);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        var outer = new Pcre2Window(8, 1, owner, 0, 0, 0);
        Assert.True(storage.PushWindow(outer, ref m, ref s));
        m.Node = owner;
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, child, Pcre2FrameKind.Scan, ref m, ref s));
        var inner = new Pcre2Window(4, 2, owner, 2, m.FrameCount, 0);
        Assert.True(storage.PushWindow(inner, ref m, ref s));
        m.Node = owner;
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, child, Pcre2FrameKind.Scan, ref m, ref s));
        Assert.True(Pcre2Kernel.RefreshWindow(f.Program, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Step(f, ref m, ref s).Kind); // retain inner choice
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, true, false, ref m, ref s));
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, true, false, ref m, ref s));
        // Both popped cells are deliberately overwritten by unrelated subsequent windows.
        Assert.True(storage.PushWindow(new(9, 0, owner, 7, 0, 0), ref m, ref s));
        Assert.True(storage.PushWindow(new(7, 0, owner, 6, 0, 0), ref m, ref s));
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(Pcre2Kernel.RefreshWindow(f.Program, ref m, ref s));
        Assert.Equal(Pcre2MatchFlags.Noteol, Pcre2Kernel.EffectiveContextFlags(f.Program, Pcre2MatchFlags.Noteol, ref m, ref s));
        m.Position = 6;
        Pcre2Kernel.Fail(ref s);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Backtrack(f.Program, ref m, ref s).Kind);
        Assert.Equal(2, m.WindowDepth);
        Assert.Equal(4, m.VisibleEnd);
        Assert.Equal(2, Pcre2Kernel.ScanStart(ref s));
        Assert.Equal(4, Pcre2Kernel.TrueEnd(f.Program, 10, ref m, ref s));
        Assert.True(storage.ReadWindow(0, ref m, ref s, out var first));
        Assert.Equal(outer, first);
        Assert.True(storage.ReadWindow(1, ref m, ref s, out var second));
        Assert.Equal(inner, second);
        Assert.Equal(Pcre2MatchFlags.PartialSoft, Pcre2KernelOperation.EffectiveFlags(f.Program, Pcre2MatchFlags.Noteol, Pcre2MatchFlags.PartialSoft, ref m, ref s));
        Assert.Equal(6, m.LastUsedPosition);
        Assert.Equal(0, m.CallDepth);
        Assert.True(Pcre2Kernel.ReactivateModuleContinuation(f.Program, 1, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Backtrack, Step(f, ref m, ref s).Kind);
        Assert.Equal(2, m.WindowDepth);
        Assert.Equal(4, Pcre2Kernel.TrueEnd(f.Program, 10, ref m, ref s));
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, true, false, ref m, ref s));
        Assert.True(storage.PopWindow(ref m, ref s, out _));
        Assert.True(Pcre2Kernel.LeaveModuleChild(f.Program, true, false, ref m, ref s));
        Assert.Equal(Pcre2MatchFlags.Noteol, Pcre2Kernel.EffectiveContextFlags(f.Program, Pcre2MatchFlags.Noteol, ref m, ref s));
        Assert.Equal(10, Pcre2Kernel.TrueEnd(f.Program, 10, ref m, ref s));
    }
    [Fact]
    public void FalseNegativePredicateRetainsChildCapturesAndReturnsWithoutCallDepth()
    {
        var p = new KernelPlan();
        var child = p.Capture(p.Literal('a'));
        var assertion = p.Assertion(child, Pcre2AssertionKind.NegativeAhead);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 1);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 2);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        m.Node = assertion;
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, assertion, Pcre2FrameKind.Predicate, ref m, ref s));
        Assert.True(Pcre2Kernel.EnterModuleChild(f.Program, child, Pcre2FrameKind.Assertion, ref m, ref s));
        Pcre2Step step;
        do
        { step = Step(f, ref m, ref s); } while (step.Kind == Pcre2StepKind.Continue);
        Assert.Equal(Pcre2StepKind.Return, step.Kind);
        Assert.Equal(0, m.LastUsedPosition);
        Assert.Equal(new(true, 0, 1), storage.Capture(1));
        // The real negative/conditional KET6231/6331 notification belongs to
        // assertion Resume before its eptr reset, not generic kernel Return.
        Assert.True(Pcre2Kernel.NotifyRmatchReturn(true, ref m, ref s));
        Assert.Equal(1, m.LastUsedPosition);
        m.Position = 0;
        Assert.True(Pcre2Kernel.LeaveModulePredicate(f.Program, false, true, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Return, Step(f, ref m, ref s).Kind);
        Assert.Equal(0, m.CallDepth);
        Assert.True(Pcre2Kernel.TakePredicateResult(assertion, ref m, ref s, out var value));
        Assert.False(value);
        Assert.False(Pcre2Kernel.TakePredicateResult(assertion, ref m, ref s, out _));
        Assert.Equal(new(true, 0, 1), storage.Capture(1));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialProbeUsesEarliestContextAndOneOperationBudget(bool hard)
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 2, startAt: 2);
        var s = f.State(m.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Pcre2Kernel.SetEmptyPartial(false, ref s);
        var flags = hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft;
        Assert.False(Pcre2Kernel.RememberPartial(f.Program, flags, ref m, ref s));
        Assert.False(m.HasPartial);
        Assert.True(Pcre2Kernel.RecordEarliestRead(1, ref m, ref s));
        var before = s.Budget.WorkConsumed;
        Assert.Equal(hard, Pcre2Kernel.RememberPartial(f.Program, flags, ref m, ref s));
        Assert.True(m.HasPartial);
        Assert.Equal(2, m.PartialStart);
        Assert.Equal(2, m.PartialEnd);
        Assert.True(s.Budget.WorkConsumed > before);
    }
    [Fact]
    public void RetainingRestoreAndSuspendedRetryHonorFiniteWorkWithoutDiscardingOriginalState()
    {
        var p = new KernelPlan();
        var child = p.Alt(p.Literal('a'), p.Literal('b'));
        p.Assertion(child);
        var f = new KernelFixture(p.Build());
        var m = new Pcre2VmState(f.Capacity, new(), 2);
        var s = f.State(m.Limits);
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, 1);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.True(storage.WriteCapture(0, 0, 1, ref m, ref s));
        var cursor = m.CaptureCursor;
        // A second machine shares the prepared buffers with an imposed exhausted
        // smaller native ceiling; it is solely a deterministic protocol denial probe.
        var denied = new Pcre2VmState(f.Capacity, new(maxWork: 1), 2) { CaptureCursor = cursor };
        Assert.False(storage.RestoreCapturesRetaining(0, ref denied, ref s));
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, denied.SourceReturnCode);
        Assert.Equal(cursor, denied.CaptureCursor);
        Assert.Equal((0, 1), storage.Registers(0));
        Assert.False(Pcre2Kernel.UpdateLastUsedPosition(2, ref denied, ref s));
        Assert.Equal(0, denied.LastUsedPosition);
    }
    [Fact]
    public void ContextHelpersPollCancellationBeforeZeroWorkAndDoNotMutateOnDenial()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var m = new Pcre2VmState(f.Capacity, new(maxWork: 0), 0);
        var s = f.State(m.Limits, new() { CancellationToken = cancellation.Token });
        Assert.False(Pcre2Kernel.UpdateLastUsedPosition(0, ref m, ref s));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, s.Failure.Code);
        Assert.Equal(0, m.LastUsedPosition);
        Assert.False(Pcre2Kernel.RememberPartial(f.Program, Pcre2MatchFlags.PartialHard, ref m, ref s, true));
        Assert.False(m.HasPartial);
    }
    private static Pcre2Frame Frame(int depth) => new(Pcre2FrameKind.Subroutine, 0, 0, 0, 0, depth, 0, -1, 0, 0, 0, 0, 0, 1, default, -1);
    private static Pcre2Step Step(KernelFixture f, ref Pcre2VmState m, ref RunState s) => Pcre2Kernel.Step<char, Pcre2CharTextReader>(
        f.Program, "abcdefghij", Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables), default, ref m, ref s);
}
