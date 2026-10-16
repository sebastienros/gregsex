using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

// Protocol tests invoke real mechanisms at explicit source occurrences. They
// do not supply a substitute production verb/callback policy or executor.
public sealed class Pcre2ControlProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WholeAcceptPublishesActualOpenCaptureClosures(int domain)
    {
        var f = new ControlFixture("(a(b(*ACCEPT))c)", domain);
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.Equal(Pcre2VerbKind.Accept, f.Program.Controls[f.Program.Nodes[m.Node].Operand].Kind);
        // These lexical OP_CLOSEs are source compile6362-6376, not a policy
        // inferred by the kernel from the nearest runtime scope.
        CloseCapture(f, 2, ref m, ref s);
        CloseCapture(f, 1, ref m, ref s);
        Assert.Equal(Pcre2StepKind.Accept, Pcre2Kernel.FinishAccept(f.Program, 0, ref m, ref s).Kind);
        var pairs = new Pcre2OffsetPair[4];
        var result = Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs);
        Assert.Equal(3, result.NativeReturnCode);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        Assert.Equal(new(true, 0, 2), pairs[1]);
        Assert.Equal(new(true, 1, 2), pairs[2]);
        Assert.Equal(default, pairs[3]);
        Assert.Equal(2, m.LastUsedPosition);
        Assert.Equal(Pcre2CalloutFlags.Startmatch, m.PendingFlags);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void ScopedAcceptUnwindsInnerCapturesAndUsesActualAssertionOwner(int domain, bool negative)
    {
        var f = new ControlFixture(negative ? "(?!a(b(*ACCEPT))c)ab" : "(?=a(b(*ACCEPT))c)ab", domain);
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        CloseCapture(f, 1, ref m, ref s);
        Assert.True(Pcre2Kernel.ReadModuleContinuation(f.Program, ref m, ref s, out var target, out var assertion));
        int position = m.Position, cursor = m.CaptureCursor;
        m.MarkIndex = 17;
        m.ReportStart = 1;
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, target, true, ref m, ref s));
        Assert.Equal(position, m.Position);
        Assert.Equal(cursor, m.CaptureCursor);
        Assert.Equal(17, m.MarkIndex);
        Assert.Equal(1, m.ReportStart);
        Assert.True(Pcre2Kernel.ReadActiveContinuation(f.Program, -1, ref m, ref s, out var active, out var frame));
        Assert.Equal(target, active);
        Assert.Equal(assertion, frame);
        // OP_ASSERT_ACCEPT source862/864 occurs before actual assertion Resume.
        Assert.True(Pcre2Kernel.NotifyRmatchReturn(true, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Dispatch(f, "ab", new(Pcre2StepKind.Return, m.Node), ref m, ref s, true).Kind);
        var step = Until(f, "ab", ref m, ref s);
        Assert.Equal(negative ? Pcre2StepKind.Backtrack : Pcre2StepKind.Accept, step.Kind);
        var pairs = new Pcre2OffsetPair[2];
        var result = Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs);
        Assert.Equal(negative ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, result.Status);
        if (!negative)
        {
            Assert.Equal(new(true, 1, 2), pairs[1]);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RecursionAcceptRetainsCurrentPositionAndMarkThenRestoresActualCallerCaptures(int domain)
    {
        var f = new ControlFixture("(?(DEFINE)(?<r>a(*ACCEPT)b))(?&r)c", domain);
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ac", ref m, ref s).Kind);
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out var target, out _));
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, f.Program.GroupCount);
        Assert.True(storage.WriteCapture(1, 0, 1, ref m, ref s));
        m.CaptureTop = 2;
        m.MarkIndex = 5;
        m.ReportStart = 1;
        var watermark = m.LastUsedPosition;
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, target, true, ref m, ref s));
        Assert.Equal(1, m.Position);
        Assert.Equal(1, m.ReportStart);
        Assert.Equal(5, m.MarkIndex);
        Assert.Equal(Pcre2StepKind.Continue, Dispatch(f, "ac", new(Pcre2StepKind.Return, m.Node), ref m, ref s).Kind);
        Assert.Equal(0, m.CallDepth);
        Assert.False(storage.Capture(1).Participated);
        Assert.Equal(watermark, m.LastUsedPosition);
        Assert.Equal(Pcre2CalloutFlags.Startmatch, m.PendingFlags);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "ac", ref m, ref s).Kind);
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(1, Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs).NativeReturnCode);
        Assert.Equal(new(true, 1, 2), pairs[0]);
        Assert.Equal(default, pairs[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptEmptyChecksPreserveActualRetryChoices(bool atStart)
    {
        var f = new ControlFixture("(*ACCEPT)|a");
        var m = f.Machine(1);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "a", ref m, ref s).Kind);
        var flags = atStart ? Pcre2MatchFlags.NotemptyAtstart : Pcre2MatchFlags.Notempty;
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.FinishAccept(f.Program, flags, ref m, ref s).Kind);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "a", ref m, ref s, flags).Kind);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
    }

    [Fact]
    public void AcceptEndanchoredRejectsEntireCandidateWhileEndStillRetries()
    {
        var f = new ControlFixture("a(*ACCEPT)|ab");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.Equal(Pcre2StepKind.Backtrack, Pcre2Kernel.FinishAccept(f.Program, Pcre2MatchFlags.Endanchored, ref m, ref s).Kind);
        Assert.Equal(0, m.FrameCount);
        Assert.Equal(0, m.WindowDepth);
        Assert.Equal(0, m.CallDepth);
        var pairs = new Pcre2OffsetPair[1];
        pairs[0] = new(true, 7, 8);
        Assert.Equal(Pcre2MatchStatus.NoMatch, Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs).Status);
        Assert.Equal(new(true, 7, 8), pairs[0]);
        var ordinary = new ControlFixture("a|ab");
        m = ordinary.Machine(2);
        s = ordinary.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(ordinary.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Accept, Until(ordinary, "ab", ref m, ref s, Pcre2MatchFlags.Endanchored).Kind);
        Assert.Equal(2, m.Position);
    }

    [Theory]
    [InlineData("a+(*THEN)b|aa", 0)]
    [InlineData("(a|a+(*THEN)b)|aa", 1)]
    public void ThenCutsDynamicBranchRetriesIncludingFinalBranchAndPreservesOuterChoice(string pattern, int ordinal)
    {
        var f = new ControlFixture(pattern);
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "aa", ref m, ref s, Pcre2MatchFlags.Endanchored).Kind);
        var verb = m.Node;
        var entryPosition = m.Position;
        Assert.False(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out _));
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, verb, ref m, ref s));
        Pcre2Kernel.CompleteNode(verb, ref m, ref s);
        Assert.Equal(Pcre2StepKind.Control, Until(f, "aa", ref m, ref s, Pcre2MatchFlags.Endanchored).Kind);
        Assert.True(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out var entry));
        Assert.Equal(entryPosition, entry.InputPosition);
        Assert.Equal(verb, entry.ScopeNode);
        Assert.False(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out _));
        var branch = FindBranch(f, ref m, ref s, out var frame);
        Assert.Equal(ordinal, frame.RepeatCount);
        Assert.True(Pcre2Kernel.CutThenScope(f.Program, branch, ref m, ref s));
        // A retry of a+ must not revisit the verb, including final inner branch.
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "aa", ref m, ref s, Pcre2MatchFlags.Endanchored).Kind);
        Assert.Equal(2, m.Position);
    }

    [Fact]
    public void VerbMarkerDoesNotBecomeActiveAndSuccessfulRemainderNeverReturnsIt()
    {
        var f = new ControlFixture("a(*PRUNE)b");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.True(Pcre2Kernel.ReadActiveContinuation(f.Program, -1, ref m, ref s, out var before, out _));
        var verb = m.Node;
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, verb, ref m, ref s));
        Assert.True(Pcre2Kernel.ReadActiveContinuation(f.Program, -1, ref m, ref s, out var after, out _));
        Assert.Equal(before, after);
        Pcre2Kernel.CompleteNode(verb, ref m, ref s);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "ab", ref m, ref s).Kind);
        Assert.False(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out _));
    }

    [Fact]
    public void NewerModuleBoundaryPrecedesOlderVerbAndRealFailureRestoresItsEntry()
    {
        var f = new ControlFixture("a(*MARK:x)(?=b)c");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ac", ref m, ref s).Kind);
        var verb = m.Node;
        m.MarkIndex = 0;
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, verb, ref m, ref s));
        Pcre2Kernel.CompleteNode(verb, ref m, ref s);
        // Actual assertion failure is consumed by its owner before older MARK.
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ac", ref m, ref s).Kind);
        Assert.Equal(verb, m.Node);
        Assert.Equal(0, m.CallDepth);
        Assert.True(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out var frame));
        Assert.Equal(1, frame.InputPosition);
        Assert.Equal(1, m.Position);
        Assert.Equal(0, m.MarkIndex);
        Assert.Equal(Pcre2CalloutFlags.Startmatch | Pcre2CalloutFlags.Backtrack, m.PendingFlags);
    }

    [Fact]
    public void AtomicSuccessCutsItsVerbMarkerBeforeLaterFailure()
    {
        var f = new ControlFixture("(?>a(*PRUNE)b)c|ab");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        var verb = m.Node;
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, verb, ref m, ref s));
        Pcre2Kernel.CompleteNode(verb, ref m, ref s);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "ab", ref m, ref s).Kind);
        Assert.False(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out _));
    }

    [Theory]
    [InlineData("(?=a)(?C1)a", "a", 1, 1)]
    [InlineData("(?!b)(?C1)a", "a", 3, 0)]
    [InlineData("(?(?=a)(?C1)a|b)", "a", 3, 1)]
    [InlineData("(?(DEFINE)(?<r>a))(?&r)(?C1)b", "ab", 1, 0)]
    [InlineData("(*sr:a)(?C1)b", "ab", 1, 0)]
    [InlineData("(a)(*scs:(1)a)(?C1)b", "ab", 1, 0)]
    public void SourceReturnNotificationsMatchNativeFirstCalloutFlagsAndWatermark(string pattern, string input, int flags, int used)
    {
        var f = new ControlFixture(pattern);
        var m = f.Machine(input.Length);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, input, ref m, ref s).Kind);
        Assert.Equal(Pcre2NodeKind.Callout, f.Program.Nodes[m.Node].Kind);
        Assert.Equal((Pcre2CalloutFlags)flags, m.PendingFlags);
        Assert.Equal(used, m.LastUsedPosition);
        // No callback is fabricated or invoked here: controls owns dispatch/clear.
    }

    [Fact]
    public void RootReturnAndCandidateOrPreserveUndeliveredFlags()
    {
        var f = new ControlFixture("b(?C1)");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Backtrack, Until(f, "ab", ref m, ref s).Kind);
        Assert.Equal(Pcre2CalloutFlags.Startmatch | Pcre2CalloutFlags.Backtrack, m.PendingFlags);
        m.CandidateStart = 1;
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.Equal(2, m.Position);
        Assert.Equal((Pcre2CalloutFlags)3, m.PendingFlags);
        m.PendingFlags = 0; // Independently initialized observable state for top-return helper.
        m.Position = 2;
        Assert.True(Pcre2Kernel.NotifyRmatchReturn(false, ref m, ref s));
        Assert.Equal(0, (int)m.PendingFlags);
        Assert.Equal(2, m.LastUsedPosition);
    }

    [Fact]
    public void ChoiceRollbackDoesNotReplayHistoricalStartmatchAfterActualFlagsAreAlreadyConsumed()
    {
        var f = new ControlFixture("a|b");
        var m = f.Machine(1);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Step(f, "b", ref m, ref s).Kind);
        // Explicit accumulator input: the choice contains STARTMATCH, current
        // observable state does not. This tests rollback, not callback policy.
        m.PendingFlags = 0;
        Pcre2Kernel.Fail(ref s);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Backtrack(f.Program, ref m, ref s).Kind);
        Assert.Equal(Pcre2CalloutFlags.Backtrack, m.PendingFlags);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "b", ref m, ref s).Kind);
    }

    [Fact]
    public void UnwindSkipsUnfinishedScriptValidationAndRetainsTargetWindowAndCallState()
    {
        var f = new ControlFixture("(?=(*sr:aα(*ACCEPT)))", 3);
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "aα", ref m, ref s).Kind);
        int target = -1, cursor = -1;
        while (Pcre2Kernel.ReadActiveContinuation(f.Program, cursor, ref m, ref s, out var index, out var frame))
        { cursor = index; if (frame.Kind == Pcre2FrameKind.Assertion) { target = index; break; } }
        Assert.True(target >= 0);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, target, true, ref m, ref s));
        Assert.Equal(2, m.Position);
        Assert.Equal(0, m.LastUsedPosition);
        Assert.True(Pcre2Kernel.NotifyRmatchReturn(true, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Dispatch(f, "aα", new(Pcre2StepKind.Return, m.Node), ref m, ref s, true).Kind);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "aα", ref m, ref s).Kind);
    }

    [Fact]
    public void ChargedActiveWalkRejectsRetainedInactiveCursorAndStickyDenialWithoutPublishing()
    {
        var f = new ControlFixture("a(*PRUNE)b");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        var verb = m.Node;
        var marker = m.FrameCount;
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, verb, ref m, ref s));
        var invalid = false;
        try
        { Pcre2Kernel.ReadActiveContinuation(f.Program, marker, ref m, ref s, out _, out _); }
        catch (ArgumentException) { invalid = true; }
        Assert.True(invalid);
        Exhaust(ref m, ref s);
        var frames = m.FrameCount;
        Assert.False(Pcre2Kernel.ReadActiveContinuation(f.Program, -1, ref m, ref s, out _, out _));
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, m.SourceReturnCode);
        Assert.False(Pcre2Kernel.UnwindToContinuation(f.Program, -1, true, ref m, ref s));
        Assert.Equal(frames, m.FrameCount);
        Assert.False(Pcre2Kernel.TakeVerbReturn(f.Program, ref m, ref s, out _));
        var pairs = new Pcre2OffsetPair[1];
        pairs[0] = new(true, 5, 6);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs).Status);
        Assert.Equal(new(true, 5, 6), pairs[0]);
        m = f.Machine(2);
        s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
    }

    [Fact]
    public void MarkerCapacityAndAcceptOutputPrechargeAreBoundedAndFailureDestinationStaysUntouched()
    {
        var f = new ControlFixture("a(*PRUNE)b", capacity: new(2, 16, 2, 32));
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.True(Pcre2Kernel.PushVerbBoundary(f.Program, m.Node, ref m, ref s));
        Assert.False(Pcre2Kernel.PushVerbBoundary(f.Program, m.Node, ref m, ref s));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, s.Failure.Code);
        var success = new ControlFixture("a(*ACCEPT)");
        m = success.Machine(1);
        s = success.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(success.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(success, "a", ref m, ref s).Kind);
        Assert.Equal(Pcre2StepKind.Accept, Pcre2Kernel.FinishAccept(success.Program, 0, ref m, ref s).Kind);
        Exhaust(ref m, ref s);
        var pairs = new Pcre2OffsetPair[1];
        pairs[0] = new(true, 5, 6);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, Pcre2Kernel.Publish(success.Program, ref m, ref s, pairs).Status);
        Assert.Equal(new(true, 5, 6), pairs[0]);
    }

    [Fact]
    public void ScopedAcceptKeepsSurvivingScanWindowAndActualOwnerRestoresOuterRemainder()
    {
        var f = new ControlFixture("(a)(*scs:(1)(?=a(*ACCEPT)))b");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "ab", ref m, ref s).Kind);
        Assert.True(Pcre2Kernel.ReadModuleContinuation(f.Program, ref m, ref s, out var target, out var frame));
        Assert.Equal(Pcre2NodeKind.Assertion, f.Program.Nodes[frame.ScopeNode].Kind);
        Assert.Equal(1, m.WindowDepth);
        Assert.Equal(1, m.VisibleEnd);
        Assert.True(m.NoteolCleared);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, target, true, ref m, ref s));
        Assert.Equal(1, m.WindowDepth);
        Assert.Equal(1, m.VisibleEnd);
        Assert.Equal(1, m.Position);
        Assert.True(m.NoteolCleared);
        Assert.Equal(0, m.CallDepth);
        Assert.True(Pcre2Kernel.NotifyRmatchReturn(true, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Continue, Dispatch(f, "ab", new(Pcre2StepKind.Return, m.Node), ref m, ref s, true).Kind);
        Assert.Equal(Pcre2StepKind.Accept, Until(f, "ab", ref m, ref s).Kind);
        Assert.Equal(0, m.WindowDepth);
        Assert.Equal(2, m.VisibleEnd);
        Assert.Equal(2, m.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnwindToOuterCallPreservesPositionAndReconcilesNestedCallActivity(bool discard)
    {
        var f = new ControlFixture("(?<r>a(?:(?&r)|(*ACCEPT)))");
        var m = f.Machine(3);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "aaa", ref m, ref s).Kind);
        Assert.Equal(2, m.CallDepth);
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out var inner, out _));
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, inner, ref m, ref s, out var outer, out _));
        int frames = m.FrameCount, cursor = m.CaptureCursor, used = m.LastUsedPosition;
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, outer, discard, ref m, ref s));
        Assert.Equal(1, m.CallDepth);
        Assert.Equal(3, m.Position);
        Assert.Equal(cursor, m.CaptureCursor);
        Assert.Equal(used, m.LastUsedPosition);
        Assert.True(Pcre2Kernel.ReadActiveSubroutine(f.Program, -1, ref m, ref s, out var active, out _));
        Assert.Equal(outer, active);
        Assert.False(Pcre2Kernel.ReadActiveSubroutine(f.Program, active, ref m, ref s, out _, out _));
        if (!discard)
        {
            Assert.Equal(frames, m.FrameCount);
            var storage = new Pcre2Storage(s.Buffers, f.Capacity, f.Program.GroupCount);
            Assert.True(storage.ReadFrame(inner, ref m, ref s, out var retained));
            Assert.False(retained.CallActive);
        }
        else
        {
            Assert.True(m.FrameCount < frames);
        }
    }

    [Fact]
    public void ThenCutUnderStickyWorkDenialCannotMutateTheOwnedBranch()
    {
        var f = new ControlFixture("a+(*THEN)b|aa");
        var m = f.Machine(2);
        var s = f.State(m);
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref m, ref s));
        Assert.Equal(Pcre2StepKind.Control, Until(f, "aa", ref m, ref s).Kind);
        int branch = FindBranch(f, ref m, ref s, out _), frames = m.FrameCount, position = m.Position;
        Exhaust(ref m, ref s);
        Assert.False(Pcre2Kernel.CutThenScope(f.Program, branch, ref m, ref s));
        Assert.Equal(frames, m.FrameCount);
        Assert.Equal(position, m.Position);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, m.SourceReturnCode);
    }

    [Fact]
    public async Task ImmutablePlanHasIndependentPreparedStateAndBorrowsCurrentSubjectEachOperation()
    {
        var plan = new ControlFixture("ab|ac").Program;
        var results = await Task.WhenAll(Task.Run(() => ReusePlan(plan, "ab")), Task.Run(() => ReusePlan(plan, "ac")));
        Assert.All(results, result => Assert.True(result));
    }
    private static bool ReusePlan(Pcre2Program plan, string input)
    {
        var f = new ControlFixture(plan);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var m = f.Machine(2);
            var s = f.State(m);
            if (!Pcre2Kernel.BeginCandidate(plan, 0, ref m, ref s) || Until(f, input, ref m, ref s).Kind != Pcre2StepKind.Accept)
            {
                return false;
            }

            m = f.Machine(2);
            s = f.State(m);
            if (!Pcre2Kernel.BeginCandidate(plan, 0, ref m, ref s) || Until(f, "zz", ref m, ref s).Kind != Pcre2StepKind.Backtrack)
            {
                return false;
            }
        }
        return true;
    }

    private static void Exhaust(ref Pcre2VmState m, ref RunState s) =>
        Assert.True(Pcre2Storage.Charge(ref m, ref s, m.Limits.MaxWork - s.Budget.WorkConsumed, m.Position));
    private static int FindBranch(ControlFixture f, ref Pcre2VmState m, ref RunState s, out Pcre2Frame found)
    {
        var cursor = -1;
        while (Pcre2Kernel.ReadActiveContinuation(f.Program, cursor, ref m, ref s, out var index, out var frame))
        { cursor = index; if (frame.Kind == Pcre2FrameKind.Branch) { found = frame; return index; } }
        found = default;
        Assert.Fail("No dynamic branch continuation.");
        return -1;
    }
    private static void CloseCapture(ControlFixture f, int group, ref Pcre2VmState m, ref RunState s)
    {
        var cursor = -1;
        while (Pcre2Kernel.ReadActiveContinuation(f.Program, cursor, ref m, ref s, out var index, out var frame))
        {
            cursor = index;
            var node = f.Program.Nodes[frame.ScopeNode];
            if (node.Kind != Pcre2NodeKind.Capture || f.Program.Groups[node.Operand].Number != group)
            {
                continue;
            }

            var storage = new Pcre2Storage(s.Buffers, f.Capacity, f.Program.GroupCount);
            Assert.True(storage.WriteCapture(group, frame.InputPosition, m.Position, ref m, ref s));
            m.CaptureLast = group;
            m.CaptureTop = Math.Max(m.CaptureTop, group + 1);
            return;
        }
        Assert.Fail("Missing lexical OP_CLOSE capture.");
    }
    private static Pcre2Step Until(ControlFixture f, string input, ref Pcre2VmState m, ref RunState s, Pcre2MatchFlags flags = 0)
    {
        for (var transitions = 0; transitions < 10000; transitions++)
        {
            var result = Step(f, input, ref m, ref s, flags);
            if (result.Kind != Pcre2StepKind.Continue)
            {
                return result;
            }
        }
        throw new InvalidOperationException("Protocol test transition limit.");
    }
    private static Pcre2Step Step(ControlFixture f, string input, ref Pcre2VmState m, ref RunState s, Pcre2MatchFlags flags = 0) =>
        f.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ?
            Pcre2Vm.Step<byte, Pcre2ByteTextReader>(f.Program, f.Bytes(input), f.Data, new() { Flags = flags }, ref m, ref s) :
            Pcre2Vm.Step<char, Pcre2CharTextReader>(f.Program, input, f.Data, new() { Flags = flags }, ref m, ref s);
    private static Pcre2Step Dispatch(ControlFixture f, string input, Pcre2Step occurrence, ref Pcre2VmState m, ref RunState s, bool force = false) =>
        f.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ?
            Pcre2Vm.Dispatch<byte, Pcre2ByteTextReader>(f.Program, f.Bytes(input), f.Data, 0, occurrence, ref m, ref s, force) :
            Pcre2Vm.Dispatch<char, Pcre2CharTextReader>(f.Program, input, f.Data, 0, occurrence, ref m, ref s, force);

    private sealed class ControlFixture
    {
        internal readonly Pcre2Program Program;
        internal readonly Pcre2ExecutionCapacity Capacity;
        private readonly int[] _states, _captures, _scratch;
        private string? _input; private byte[] _bytes = [];
        internal Pcre2DataView Data => Pcre2Data.Instance.Bind(Program.Profile.CharacterTables);
        internal ControlFixture(string pattern, int domain = 1, Pcre2ExecutionCapacity? capacity = null)
        {
            var flags = Pcre2CompileFlags.NoAutoPossess | Pcre2CompileFlags.NoStartOptimize;
            if (domain >= 2)
            {
                flags |= Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp;
            }

            var profile = new Pcre2Profile(domain % 2 == 0 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, flags: flags);
            var budget = new CompileBudget(new());
            Assert.True(new Pcre2SyntaxParser().TryParse(pattern, profile, Pcre2Data.Instance, ref budget, out var syntax, out var error), error.ToString());
            Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var executable, out error), error.ToString());
            Program = executable!;
            Capacity = capacity ?? new(256, 2048, 32, 512);
            var size = Capacity.Required(Program.GroupCount);
            _states = new int[size.StateSlots];
            _captures = new int[size.CaptureSlots];
            _scratch = new int[size.ScratchSlots];
        }
        internal ControlFixture(Pcre2Program program)
        {
            Program = program;
            Capacity = new(256, 2048, 32, 512);
            var size = Capacity.Required(Program.GroupCount);
            _states = new int[size.StateSlots];
            _captures = new int[size.CaptureSlots];
            _scratch = new int[size.ScratchSlots];
        }
        internal Pcre2VmState Machine(int length) => new(Capacity, new(maxWork: 100000), length);
        internal RunState State(Pcre2VmState m) => new(new(_states, _captures, _scratch), new());
        internal byte[] Bytes(string input)
        {
            if (!ReferenceEquals(_input, input))
            { _input = input; _bytes = System.Text.Encoding.UTF8.GetBytes(input); }
            return _bytes; // nonshipping prepared fixture, never production subject storage
        }
    }
}
