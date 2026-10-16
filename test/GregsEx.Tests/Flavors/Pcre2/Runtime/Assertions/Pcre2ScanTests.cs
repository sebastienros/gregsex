using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2ScanTests
{
    private static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    [Theory]
    [InlineData("ab", true)]
    [InlineData("ba", false)]
    [InlineData("a", false)]
    public void ScanAnchorsAtCaptureAndRestoresOriginalPosition(string input, bool hit)
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Repeat(p.Leaf(Pcre2NodeKind.Dot), 1, 2));
        var scan = p.Scan(p.Seq(p.Capture(p.Literal('a'), 2), p.Literal('b'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)));
        p.Seq(capture, scan);
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(hit ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match(input, pairs, options: Anchored).Status);
        if (hit)
        { Assert.Equal(new(true, 0, 2), pairs[0]); Assert.Equal(new(true, 0, 1), pairs[2]); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanIsNonAtomicAndRetriesChildCaptures(bool noteol)
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Seq(p.Literal('a'), p.Literal('a')));
        var scan = p.Scan(p.Capture(p.Repeat(p.Literal('a'), 1, 2), 2));
        p.Seq(capture, scan, p.Backref([2]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[3];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored | (noteol ? Pcre2MatchFlags.Noteol : 0) };
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("aaa", pairs, options: options).Status);
        Assert.Equal(new(true, 0, 1), pairs[2]);
        Assert.Equal(new(true, 0, 3), pairs[0]);
    }
    [Fact]
    public void ScanChangesBothEndsClearsNoteolAndPreservesBeginningAndLookbehind()
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Literal('b'));
        var behind = p.Assertion(p.Literal('a'), Pcre2AssertionKind.PositiveBehind, 1, 1);
        var scan = p.Scan(p.Seq(behind, p.Literal('b'), p.Leaf(Pcre2NodeKind.End), p.Leaf(Pcre2NodeKind.AbsoluteEnd)));
        p.Seq(p.Literal('a'), capture, scan, p.Literal('c'));
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("abc", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Noteol }).Status);
        Assert.Equal(new(true, 0, 3), pairs[0]);
        p = new();
        capture = p.Capture(p.Literal('b'));
        scan = p.Scan(p.Seq(p.Leaf(Pcre2NodeKind.AbsoluteStart), p.Literal('b')));
        p.Seq(p.Literal('a'), capture, scan);
        Assert.Equal(Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("ab", pairs, options: Anchored).Status);
    }
    [Fact]
    public void LaterScanOverwritesPoppedWindowBeforeEarlierScanRetry()
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Seq(p.Literal('a'), p.Literal('a')));
        var first = p.Scan(p.Capture(p.Repeat(p.Literal('a'), 1, 2), 2));
        var second = p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), 2);
        p.Seq(capture, first, second, p.Backref([2]));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("aaa", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 0, 1), pairs[2]);
    }
    [Fact]
    public void NestedScanRestoresOuterEndThenOriginalEnd()
    {
        var p = new AssertionPlan();
        var outer = p.Capture(p.Seq(p.Literal('a'), p.Literal('b')));
        var innerCapture = p.Capture(p.Literal('a'), 2);
        var inner = p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), 2);
        var scan = p.Scan(p.Seq(innerCapture, inner, p.Literal('b'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)));
        p.Seq(outer, scan, p.Literal('c'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("abc", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 0, 1), pairs[2]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsetScanAlwaysFailsEvenWhenUnsetBackrefsMatch(bool unset)
    {
        var p = new AssertionPlan(new(flags: unset ? Pcre2CompileFlags.MatchUnsetBackref : 0));
        p.Seq(p.Repeat(p.Capture(p.Literal('a')), 0, 1), p.Scan(p.Leaf(Pcre2NodeKind.Empty)));
        Assert.Equal(Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("", new Pcre2OffsetPair[2], options: Anchored).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScanSelectsFirstSetReferenceWithoutRetryingSelection(bool named)
    {
        var p = new AssertionPlan(new(flags: Pcre2CompileFlags.Dupnames));
        int one = p.Repeat(p.Capture(p.Literal('a'), 1), 0, 1), two = p.Capture(p.Literal('b'), 2);
        var scan = p.Scan(p.Literal('b'), named ? [new[] { 1, 2 }] : [new[] { 1 }, new[] { 2 }], named);
        p.Seq(one, two, scan);
        var fixture = new AssertionFixture(p.Build());
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("ab", new Pcre2OffsetPair[3], options: Anchored).Status);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("b", new Pcre2OffsetPair[3], options: Anchored).Status);
    }
    [Fact]
    public void ScanKeepsNoteolClearedThroughItsRestoredWindowRemainder()
    {
        var p = new AssertionPlan();
        p.Seq(p.Capture(p.Literal('a')), p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.End))), p.Leaf(Pcre2NodeKind.End));
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("a", new Pcre2OffsetPair[2], options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Noteol }).Status);
    }
    [Fact]
    public void ScanFailureAndOuterChoiceRestoreOriginalNoteol()
    {
        var p = new AssertionPlan();
        var capture = p.Capture(p.Literal('a'));
        var branch = p.Seq(capture, p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.End))), p.Literal('b'));
        p.Alt(branch, p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.End)));
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Noteol };
        Assert.Equal(Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("a", new Pcre2OffsetPair[2], options: options).Status);
        p = new();
        capture = p.Capture(p.Literal('a'));
        var negative = p.Assertion(p.Seq(p.Scan(p.Literal('a')), p.Literal('b')), Pcre2AssertionKind.NegativeAhead);
        p.Seq(capture, negative, p.Leaf(Pcre2NodeKind.End));
        Assert.Equal(Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("a", new Pcre2OffsetPair[2], options: options).Status);
    }
}
