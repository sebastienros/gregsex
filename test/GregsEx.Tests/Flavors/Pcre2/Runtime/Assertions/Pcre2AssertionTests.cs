using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Assertions;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2AssertionTests
{
    private static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(5, false)]
    public void LookaroundPolarityPositionAndCapturePolicy(int kind, bool hit)
    {
        var p = new AssertionPlan();
        var behind = kind is 2 or 3 or 5;
        var assertion = p.Assertion(p.Capture(p.Literal('a')), (Pcre2AssertionKind)kind, behind ? 1 : 0, behind ? 1 : 0);
        p.Seq(assertion, p.Literal(behind ? 'b' : 'a'));
        var fixture = new AssertionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        var result = fixture.Match(behind ? (hit ? "ab" : "xb") : (hit ? "a" : "x"), pairs, behind ? 1 : 0, Anchored);
        var negative = kind is 1 or 3;
        var expected = negative ? !hit : hit;
        // Ahead negative cannot match x followed by literal a in this fixture.
        if (kind == 1)
        {
            expected = false;
        }

        Assert.Equal(expected ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (expected)
        { Assert.Equal(new(true, behind ? 1 : 0, behind ? 2 : 1), pairs[0]); Assert.Equal(!negative, pairs[1].Participated); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAtomicAheadRetriesFinalCapture(bool nonAtomic)
    {
        var p = new AssertionPlan();
        var body = p.Capture(p.Repeat(p.Literal('a'), 1, 2));
        var assertion = p.Assertion(body, nonAtomic ? Pcre2AssertionKind.NonAtomicAhead : Pcre2AssertionKind.PositiveAhead);
        p.Seq(assertion, p.Backref([1]), p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[2];
        var result = new AssertionFixture(p.Build()).Match("aa", pairs, options: Anchored);
        Assert.Equal(nonAtomic ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (nonAtomic)
        {
            Assert.Equal(new(true, 0, 1), pairs[1]);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAtomicBehindRetriesLengthsBeforeBranches(bool nonAtomic)
    {
        var p = new AssertionPlan();
        var body = p.Capture(p.Repeat(p.Literal('a'), 1, 2));
        var assertion = p.Assertion(body, nonAtomic ? Pcre2AssertionKind.NonAtomicBehind : Pcre2AssertionKind.PositiveBehind, 1, 2, true);
        p.Seq(assertion, p.Backref([1]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[2];
        var result = new AssertionFixture(p.Build()).Match("aaa", pairs, 2, Anchored);
        Assert.Equal(nonAtomic ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (nonAtomic)
        {
            Assert.Equal(new(true, 1, 2), pairs[1]);
        }
    }
    [Theory]
    [InlineData("ab", 1)]
    [InlineData("aab", 2)]
    [InlineData("b", -1)]
    [InlineData("xb", -1)]
    public void VariableBehindBoundsAndOriginalSearchStart(string subject, int start)
    {
        var p = new AssertionPlan();
        var behind = p.Assertion(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), Pcre2AssertionKind.PositiveBehind, 1, 2, true);
        p.Seq(behind, p.Literal('b'));
        var pairs = new Pcre2OffsetPair[2];
        var result = new AssertionFixture(p.Build()).Match(subject, pairs);
        Assert.Equal(start >= 0 ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (start >= 0)
        {
            Assert.Equal(new(true, 0, start), pairs[1]);
        }
    }
    [Fact]
    public void FixedBranchesUseTheirOwnWidthsAndOrder()
    {
        var p = new AssertionPlan();
        int first = p.Capture(p.Literal('a'), 1), second = p.Capture(p.Seq(p.Literal('a'), p.Literal('a')), 2);
        var behind = p.Assertion(p.Alt(first, second), Pcre2AssertionKind.PositiveBehind, 1, 2, branchMaxima: [1, 2]);
        p.Seq(behind, p.Literal('b'));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("aab", pairs, 2, Anchored).Status);
        Assert.Equal(new(true, 1, 2), pairs[1]);
        Assert.False(pairs[2].Participated);
    }
    [Fact]
    public void NegativeBehindExhaustsAllLengthsAndDiscardsFailedCapture()
    {
        var p = new AssertionPlan();
        var body = p.Seq(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), p.Literal('x'));
        var behind = p.Assertion(body, Pcre2AssertionKind.NegativeBehind, 2, 3, true);
        p.Seq(behind, p.Literal('b'));
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("aab", pairs, 2, Anchored).Status);
        Assert.False(pairs[1].Participated);
    }
    [Fact]
    public void BehindDoesNotFabricateTrueEndAndNestedAheadSeesBeyondEndpoint()
    {
        var p = new AssertionPlan();
        var ahead = p.Assertion(p.Literal('b'));
        var behind = p.Assertion(p.Seq(p.Literal('a'), ahead), Pcre2AssertionKind.PositiveBehind, 1, 1);
        p.Seq(behind, p.Literal('b'));
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("ab", new Pcre2OffsetPair[1], 1, Anchored).Status);
        p = new();
        behind = p.Assertion(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), Pcre2AssertionKind.PositiveBehind, 1, 1);
        p.Seq(behind, p.Literal('b'));
        Assert.Equal(Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("ab", new Pcre2OffsetPair[1], 1, Anchored).Status);
    }
    [Fact]
    public void FragmentFloorCannotSupplyLookbehindCharacters()
    {
        var p = new AssertionPlan();
        var behind = p.Assertion(p.Literal('a'), Pcre2AssertionKind.PositiveBehind, 1, 1);
        p.Seq(behind, p.Literal('b'));
        var fixture = new AssertionFixture(p.Build());
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("ab", new Pcre2OffsetPair[1], 1, Anchored, floor: 1).Status);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("ab", new Pcre2OffsetPair[1], 1, Anchored).Status);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FourDomainsKeepNativeBackreferenceAndBehindOffsets(int domain)
    {
        bool bytes = domain < 2, utf = domain is 1 or 3;
        var p = new AssertionPlan(new(width: bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, flags: utf ? Pcre2CompileFlags.Utf : 0));
        var cp = utf ? 0x1F600 : 'a';
        var capture = p.Capture(p.Literal(cp));
        var behind = p.Assertion(p.Backref([1]), Pcre2AssertionKind.PositiveBehind, 1, 1);
        p.Seq(capture, behind, p.Backref([1]));
        var fixture = new AssertionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        var result = bytes ? fixture.Match(utf ? [0xF0, 0x9F, 0x98, 0x80, 0xF0, 0x9F, 0x98, 0x80] : [97, 97], pairs, options: Anchored) : fixture.Match(utf ? "😀😀" : "aa", pairs, options: Anchored);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        var width = utf ? (bytes ? 4 : 2) : 1;
        Assert.Equal(new(true, 0, width), pairs[1]);
        Assert.Equal(new(true, 0, width * 2), pairs[0]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativePredicateFalseRetainsMatchedChildCaptures(bool hit)
    {
        var p = new AssertionPlan();
        p.Assertion(p.Capture(p.Literal('a')), Pcre2AssertionKind.NegativeAhead);
        var fixture = new AssertionFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = Pcre2KernelOperation.CreateRunState(new(fixture.States, fixture.Captures, fixture.Scratch), machine.Limits, default);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        var input = hit ? "a" : "b";
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        Assert.Equal(Pcre2StepKind.Continue, Pcre2AssertionOps.EnterPredicate<char, Pcre2CharTextReader>(fixture.Program, input, ref machine, ref state).Kind);
        Pcre2Step step;
        do
        { step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, input, data, default, ref machine, ref state); } while (step.Kind == Pcre2StepKind.Continue);
        Assert.Equal(hit ? Pcre2StepKind.Return : Pcre2StepKind.Backtrack, step.Kind);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2AssertionOps.Resume<char, Pcre2CharTextReader>(fixture.Program, input, step.Kind, ref machine, ref state).Kind);
        Assert.True(Pcre2Kernel.TakePredicateResult(fixture.Program.Root, ref machine, ref state, out var value));
        Assert.Equal(!hit, value);
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, 2);
        Assert.Equal(hit, storage.Capture(1).Participated);
        Assert.Equal(0, machine.Position);
        Assert.Equal(0, machine.CaptureLast);
        Assert.False(Pcre2Kernel.TakePredicateResult(fixture.Program.Root, ref machine, ref state, out _));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchedPredicateRaisesWatermarkBeforePositionRestoration(bool negative)
    {
        var p = new AssertionPlan();
        p.Assertion(p.Seq(p.Literal('a'), p.Literal('b')), negative ? Pcre2AssertionKind.NegativeAhead : Pcre2AssertionKind.PositiveAhead);
        var fixture = new AssertionFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 2);
        var state = Pcre2KernelOperation.CreateRunState(new(fixture.States, fixture.Captures, fixture.Scratch), machine.Limits, default);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        Pcre2AssertionOps.EnterPredicate<char, Pcre2CharTextReader>(fixture.Program, "ab", ref machine, ref state);
        Pcre2Step step;
        do
        { step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "ab", data, default, ref machine, ref state); } while (step.Kind == Pcre2StepKind.Continue);
        Assert.Equal(Pcre2StepKind.Return, step.Kind);
        Assert.Equal(0, machine.LastUsedPosition);
        Assert.Equal(2, machine.Position);
        Pcre2AssertionOps.Resume<char, Pcre2CharTextReader>(fixture.Program, "ab", step.Kind, ref machine, ref state);
        Assert.Equal(2, machine.LastUsedPosition);
        Assert.Equal(0, machine.Position);
        Assert.True(Pcre2Kernel.TakePredicateResult(fixture.Program.Root, ref machine, ref state, out var value));
        Assert.Equal(!negative, value);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReturnControlStateFollowsStandaloneAndPredicateCapturePolicy(bool negative, bool predicate)
    {
        var p = new AssertionPlan();
        p.Assertion(p.Capture(p.Literal('a')), negative ? Pcre2AssertionKind.NegativeAhead : Pcre2AssertionKind.PositiveAhead);
        var fixture = new AssertionFixture(p.Build());
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = Pcre2KernelOperation.CreateRunState(new(fixture.States, fixture.Captures, fixture.Scratch), machine.Limits, default);
        var data = Pcre2Data.Instance.Bind(p.Profile.CharacterTables);
        Assert.True(Pcre2Kernel.BeginCandidate(fixture.Program, 0, ref machine, ref state));
        if (predicate)
        {
            Pcre2AssertionOps.EnterPredicate<char, Pcre2CharTextReader>(fixture.Program, "a", ref machine, ref state);
        }
        else
        {
            Pcre2AssertionOps.Step<char, Pcre2CharTextReader>(fixture.Program, "a", data, ref machine, ref state);
        }

        Pcre2Step step;
        do
        { step = Pcre2Kernel.Step<char, Pcre2CharTextReader>(fixture.Program, "a", data, default, ref machine, ref state); } while (step.Kind == Pcre2StepKind.Continue);
        machine.ReportStart = 1;
        machine.MarkIndex = 17; // Controls-owned effects, supplied at their handoff boundary.
        Assert.True(Pcre2Kernel.SetNoteolCleared(true, ref machine, ref state));
        Pcre2AssertionOps.Resume<char, Pcre2CharTextReader>(fixture.Program, "a", step.Kind, ref machine, ref state);
        Assert.Equal(!predicate && !negative ? 1 : 0, machine.ReportStart);
        Assert.Equal(predicate || !negative ? 17 : -1, machine.MarkIndex);
        Assert.Equal(!predicate && !negative ? 1 : 0, machine.CaptureLast);
        Assert.Equal(!predicate && !negative, machine.NoteolCleared);
        var storage = new Pcre2Storage(state.Buffers, fixture.Capacity, 2);
        Assert.Equal(predicate || !negative, storage.Capture(1).Participated);
        Assert.Equal(0, machine.Position);
    }
}
