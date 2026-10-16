using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

public sealed class Pcre2KernelTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void OrderedAlternatives(bool shortFirst, int end)
    {
        var p = new KernelPlan();
        int longBranch = p.Seq(p.Literal('a'), p.Literal('b')), shortBranch = p.Literal('a');
        p.Alt(shortFirst ? shortBranch : longBranch, shortFirst ? longBranch : shortBranch);
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("ab", pairs).Status);
        Assert.Equal(new(true, 0, end), pairs[0]);
    }
    [Theory]
    [InlineData((int)Pcre2Quantifier.Greedy, 3)]
    [InlineData((int)Pcre2Quantifier.Lazy, 1)]
    [InlineData((int)Pcre2Quantifier.Possessive, 3)]
    public void RepeatPriority(int priorityValue, int end)

    {
        var priority = (Pcre2Quantifier)priorityValue;
        var p = new KernelPlan();
        p.Repeat(p.Literal('a'), 1, -1, priority);
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aaab", pairs).Status);
        Assert.Equal(new(true, 0, end), pairs[0]);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void PossessiveCut(bool possessive, bool success)
    {
        var p = new KernelPlan();
        p.Seq(p.Repeat(p.Literal('a'), 0, -1,
            possessive ? Pcre2Quantifier.Possessive : Pcre2Quantifier.Greedy), p.Literal('a'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, fixture.Match("aaa", pairs).Status);
        if (success)
        {
            Assert.Equal(new(true, 0, 3), pairs[0]);
        }
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void AtomicCut(bool atomic, bool success)
    {
        var p = new KernelPlan();
        var choice = p.Alt(p.Seq(p.Literal('a'), p.Literal('b')), p.Literal('a'));
        p.Seq(atomic ? p.Atomic(choice) : choice, p.Literal('b'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, fixture.Match("ab", pairs).Status);
    }
    [Fact]
    public void CaptureRollbackAfterLaterFailure()
    {
        var p = new KernelPlan();
        var first = p.Capture(p.Literal('a'));
        p.Seq(p.Alt(first, p.Literal('a')), p.Literal('b'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("ab", pairs).Status);
        Assert.True(pairs[1].Participated);
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("ac", pairs).Status);
        var q = new KernelPlan();
        var captured = q.Seq(q.Capture(q.Literal('a')), q.Literal('x'));
        q.Alt(captured, q.Seq(q.Literal('a'), q.Literal('b')));
        var other = new KernelFixture(q.Build());
        Assert.Equal(Pcre2MatchStatus.Match, other.Match("ab", pairs).Status);
        Assert.False(pairs[1].Participated);
    }
    [Fact]
    public void RepeatedCaptureKeepsFinalSuccessfulIteration()
    {
        var p = new KernelPlan();
        p.Seq(p.Repeat(p.Capture(p.Literal('a')), 1), p.Literal('a'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(2, fixture.Match("aaa", pairs).NativeReturnCode);
        Assert.Equal(new(true, 1, 2), pairs[1]);
    }
    [Fact]
    public void EmptyRepeatTerminatesAndCanBacktrackIntoConsumingAlternative()
    {
        var p = new KernelPlan();
        p.Seq(p.Repeat(p.Alt(p.Leaf(Pcre2NodeKind.Empty), p.Literal('a'))), p.Literal('b'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aaab", pairs).Status);
        Assert.Equal(new(true, 0, 4), pairs[0]);
        var q = new KernelPlan();
        q.Repeat(q.Leaf(Pcre2NodeKind.Empty), 3, 7);
        Assert.Equal(Pcre2MatchStatus.Match, new KernelFixture(q.Build()).Match("", pairs).Status);
    }
    [Theory]
    [InlineData(Pcre2MatchFlags.Notempty, Pcre2MatchStatus.NoMatch)]
    [InlineData(Pcre2MatchFlags.NotemptyAtstart, Pcre2MatchStatus.Match)]
    public void PatternNotemptyDirectivesRemainEffective(Pcre2MatchFlags flags, Pcre2MatchStatus expected)
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var fixture = new KernelFixture(p.Build(flags));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(expected, fixture.Match("x", pairs).Status);
        if (expected == Pcre2MatchStatus.Match)
        {
            Assert.Equal(new(true, 1, 1), pairs[0]);
        }

        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("x", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored }).Status);
    }
    [Fact]
    public void OriginalSearchStartForGAndEmptyRetries()
    {
        var p = new KernelPlan();
        p.Seq(p.Leaf(Pcre2NodeKind.StartOffset), p.Literal('a'));
        var pairs = new Pcre2OffsetPair[1];
        var fixture = new KernelFixture(p.Build());
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("xaa", pairs, 1).Status);
        Assert.Equal(new(true, 1, 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("xxa", pairs, 1).Status);
    }
    [Theory]
    [InlineData(Pcre2Newline.Lf, "x\na", 2)]
    [InlineData(Pcre2Newline.Cr, "x\ra", 2)]
    [InlineData(Pcre2Newline.Crlf, "x\r\na", 3)]
    [InlineData(Pcre2Newline.Any, "x\u2028a", 2)]
    [InlineData(Pcre2Newline.Anycrlf, "x\ra", 2)]
    [InlineData(Pcre2Newline.Nul, "x\0a", 2)]
    public void MultilineBeginningUsesOriginalSubject(Pcre2Newline newline, string input, int start)
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Multiline, newline: newline));
        p.Seq(p.Leaf(Pcre2NodeKind.Start), p.Literal('a'), p.Leaf(Pcre2NodeKind.End));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match(input, pairs, start).Status);
        Assert.Equal(new(true, start, start + 1), pairs[0]);
    }
    [Theory]
    [InlineData(false, Pcre2MatchStatus.NoMatch)]
    [InlineData(true, Pcre2MatchStatus.Match)]
    public void CircumflexAfterFinalNewline(bool alt, Pcre2MatchStatus expected)
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Multiline | (alt ? Pcre2CompileFlags.AltCircumflex : 0)));
        p.Leaf(Pcre2NodeKind.Start);
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(expected, fixture.Match("x\n", pairs, 2, new() { Flags = Pcre2MatchFlags.Anchored }).Status);
    }
    [Theory]
    [InlineData((int)Pcre2NodeKind.End, Pcre2MatchFlags.None, true)]
    [InlineData((int)Pcre2NodeKind.End, Pcre2MatchFlags.Noteol, false)]
    [InlineData((int)Pcre2NodeKind.BeforeFinalNewline, Pcre2MatchFlags.Noteol, true)]
    [InlineData((int)Pcre2NodeKind.AbsoluteEnd, Pcre2MatchFlags.None, false)]
    public void EndingNewlinePolicy(int kindValue, Pcre2MatchFlags flags, bool success)
    {
        var kind = (Pcre2NodeKind)kindValue;
        var p = new KernelPlan();
        p.Seq(p.Literal('a'), p.Leaf(kind));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch,
            new KernelFixture(p.Build()).Match("a\n", pairs, options: new() { Flags = flags }).Status);
    }
    [Fact]
    public void MultilineDollarBeforeNewlineIsCompleteUnderHardPartial()
    {
        // OP_DOLLM6519–6545 only polls partial at end_subject, not before a
        // complete newline. Nonmultiline OP_DOLL/OP_EODN has a different rule.
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Multiline));
        p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.End));
        var pairs = new Pcre2OffsetPair[1];
        var result = new KernelFixture(p.Build()).Match("a\n", pairs, options: new() { Flags = Pcre2MatchFlags.PartialHard });
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
    }
    [Fact]
    public void BsrCrLfIsAtomic()
    {
        var p = new KernelPlan();
        p.Seq(p.Leaf(Pcre2NodeKind.Newline), p.Literal('\n'));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.NoMatch, new KernelFixture(p.Build()).Match("\r\n", pairs,
            options: new() { Flags = Pcre2MatchFlags.Anchored }).Status);
    }
    [Theory]
    [InlineData(Pcre2CodeUnitWidth.Sixteen, false)]
    [InlineData(Pcre2CodeUnitWidth.Sixteen, true)]
    [InlineData(Pcre2CodeUnitWidth.Eight, false)]
    [InlineData(Pcre2CodeUnitWidth.Eight, true)]
    public void FourDomainsKeepNativeOffsetsAndNul(Pcre2CodeUnitWidth width, bool utf)
    {
        var p = new KernelPlan(new(width, utf ? Pcre2CompileFlags.Utf : 0));
        p.Seq(p.Literal(0), p.Leaf(Pcre2NodeKind.Dot));
        var pairs = new Pcre2OffsetPair[1];
        var fixture = new KernelFixture(p.Build());
        var result = width == Pcre2CodeUnitWidth.Sixteen ? fixture.Match(utf ? "\0😀" : new string(['\0', '\uD800']), pairs) :
            fixture.Match(utf ? [0, 0xF0, 0x9F, 0x98, 0x80] : [0, 0xFF], pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 0, utf ? width == Pcre2CodeUnitWidth.Eight ? 5 : 3 : 2), pairs[0]);
    }
    [Fact]
    public void DefinedUtfCodeUnitSequenceCanSplitCaptures()
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf));
        p.Seq(p.Capture(p.Leaf(Pcre2NodeKind.CodeUnit)), p.Leaf(Pcre2NodeKind.CodeUnit));
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, new KernelFixture(p.Build()).Match("😀", pairs).Status);
        Assert.Equal(new(true, 0, 1), pairs[1]);
        Assert.Equal(new(true, 0, 2), pairs[0]);
    }
    [Fact]
    public void ShortAndExcessVectorsHaveNativePublicationSemantics()
    {
        var p = new KernelPlan();
        p.Capture(p.Literal('a'));
        var fixture = new KernelFixture(p.Build());
        var shortPairs = new Pcre2OffsetPair[1];
        Assert.Equal(0, fixture.Match("a", shortPairs).NativeReturnCode);
        Assert.Equal(new(true, 0, 1), shortPairs[0]);
        Pcre2OffsetPair sentinel = new(true, 7, 8);
        Pcre2OffsetPair[] full = [sentinel, sentinel, sentinel];
        Assert.Equal(2, fixture.Match("a", full).NativeReturnCode);
        Assert.Equal(sentinel, full[2]);
        full[0] = sentinel;
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("b", full).Status);
        Assert.Equal(sentinel, full[0]);
    }
    [Theory]
    [InlineData(false, Pcre2MatchStatus.Match)]
    [InlineData(true, Pcre2MatchStatus.Partial)]
    public void PartialHardPreemptsLaterSuccessWhileSoftAllowsIt(bool hard, Pcre2MatchStatus status)
    {
        var p = new KernelPlan();
        p.Alt(p.Seq(p.Literal('a'), p.Literal('b')), p.Literal('a'));
        var fixture = new KernelFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(status, fixture.Match("a", pairs, options: new() { Flags = hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft }).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
    }
    [Theory]
    [InlineData(0, 100, 100000, -47)]
    [InlineData(10000, 0, 100000, -53)]
    [InlineData(10000, 100, 0, -63)]
    public void NativeZeroDenials(long work, int depth, long heap, int error)
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        Pcre2OffsetPair sentinel = new(true, 7, 8);
        Pcre2OffsetPair[] pairs = [sentinel];
        var result = new KernelFixture(p.Build()).Match("", pairs, limits: new(work, depth, heap));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(error, result.NativeReturnCode);
        Assert.Equal(sentinel, pairs[0]);
    }
    [Fact]
    public void CancellationPrecedesZeroWorkAndMalformedInput()
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf));
        p.Literal('a');
        var pairs = new Pcre2OffsetPair[1];
        var result = new KernelFixture(p.Build()).Match(new string(['\uD800']), pairs,
            options: new() { CancellationToken = new(true) }, limits: new(maxWork: 0));
        Assert.Equal(Pcre2MatchStatus.Cancelled, result.Status);
        Assert.Equal(0, result.Failure.WorkConsumed);
    }
    [Fact]
    public void DeadlinePrecedesZeroWork()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(maxWork: 0), 0);
        var state = fixture.State(machine.Limits, new() { Timeout = TimeSpan.FromTicks(1) });
        // Deliberately expire the deadline instead of assuming immediate 100 ns clock advancement.
        Thread.Sleep(1);
        Assert.False(Pcre2KernelOperation.PollEntry(fixture.Program, ref machine, ref state));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, state.Failure.Code);
    }
    [Fact]
    public void TextAndVmUseOneReducedBudget()
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf));
        p.Literal('a');
        var fixture = new KernelFixture(p.Build(limits: new(MatchWork: 2, Depth: null, HeapBytes: null)));
        var pairs = new Pcre2OffsetPair[1];
        var result = fixture.Match("xxxxa", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(2, result.Failure.WorkConsumed);
    }
    [Fact]
    public void FrameAndUndoCapacityFailuresLeavePreparedStorageReusable()
    {
        var p = new KernelPlan();
        p.Seq(p.Repeat(p.Capture(p.Literal('a'))), p.Literal('b'));
        var fixture = new KernelFixture(p.Build(), new(4, 3, 1, 16));
        var states = fixture.States;
        var scratch = fixture.Scratch;
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, fixture.Match("aaaaac", pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("b", pairs).Status);
        Assert.Same(states, fixture.States);
        Assert.Same(scratch, fixture.Scratch);
    }
    [Fact]
    public void SharedPlanWithSeparateWorkspacesIsConcurrentSafe()
    {
        var p = new KernelPlan();
        p.Repeat(p.Literal('a'), 1);
        var program = p.Build();
        Parallel.For(0, 16, i =>
        {
            var fixture = new KernelFixture(program);
            var pairs = new Pcre2OffsetPair[1];
            Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aaa", pairs).Status);
            Assert.Equal(new(true, 0, 3), pairs[0]);
        });
    }
    [Fact]
    public void ModuleOpcodesDispatchWithoutSuccessfulSubstitutes()
    {
        // Grapheme has no structural operands and is independently valid.
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Grapheme);
        var fixture = new KernelFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = fixture.State(machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        var step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "a", Pcre2Data.Instance.Bind(p.Profile.CharacterTables), default, ref machine, ref state);
        Assert.Equal(Pcre2StepKind.Unicode, step.Kind);
        Assert.Equal(0, machine.Position);
        Assert.Equal(Pcre2MatchStatus.NoMatch, Pcre2Kernel.Publish(fixture.Program, ref machine, ref state, []).Status);
    }
}
