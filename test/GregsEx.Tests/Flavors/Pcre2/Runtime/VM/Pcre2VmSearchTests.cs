using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.VM;

public sealed class Pcre2VmSearchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void GraphemeRegionalParityReadsValidPrefixBeforeNativeValidationWindow(bool bytes, bool invalidProfile)
    {
        // extuni receives original start_subject, not check_subject. Starting
        // at RI2 in RI1/RI2/RI3 therefore consumes RI2 alone (odd left parity).
        var p = new VmPlan(Pcre2VmCompositionTests.Profile(bytes ? 1 : 3,
            invalidProfile ? Pcre2CompileFlags.MatchInvalidUtf : 0));
        p.Leaf(Pcre2NodeKind.Grapheme);
        var f = new VmFixture(p.Build(hints: new(1, 0, false)));
        var pairs = new Pcre2OffsetPair[1];
        var result = bytes ? f.Match("🇦🇧🇨"u8.ToArray(), pairs,
            new() { Flags = Pcre2MatchFlags.Anchored }, start: 4) :
            f.Match("\ud83c\udde6\ud83c\udde7\ud83c\udde8", pairs, new() { Flags = Pcre2MatchFlags.Anchored }, start: 2);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(bytes ? new(true, 4, 8) : new(true, 2, 4), pairs[0]);
    }

    [Fact]
    public void CandidateLoopRetriesMixedScriptThenKeepsNativeOffsets()
    {
        var p = new VmPlan(Pcre2VmCompositionTests.Profile(3));
        p.Script(p.Repeat(p.Leaf(Pcre2NodeKind.Grapheme), 2, 2));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("αab", pairs).Status);
        Assert.Equal(new(true, 1, 3), pairs[0]);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("αab", pairs, Pcre2VmCompositionTests.Anchored).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialAcrossRecursionAndCandidatesUsesEarliestStart(bool hard)
    {
        var p = new VmPlan();
        p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
        var f = new VmFixture(p.Build());
        Pcre2OffsetPair[] pairs = [new(true, 7, 9), new(true, 5, 6)];
        var result = f.Match("xxaa", pairs, new() { Flags = hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft });
        Assert.Equal(Pcre2MatchStatus.Partial, result.Status);
        Assert.Equal(new(true, 2, 4), pairs[0]);
        Assert.Equal(new(true, 5, 6), pairs[1]);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("xxaabb", pairs).Status);
        Assert.Equal(new(true, 2, 6), pairs[0]);
    }

    [Fact]
    public void InvalidUtfFragmentsRetainOriginalStartTrueEndAndBehindFloor()
    {
        var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.MatchInvalidUtf | Pcre2CompileFlags.Ucp);
        var p = new VmPlan(profile);
        p.Script(p.Seq(p.Lit('a'), p.Lit('a'), p.Lit('b'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match([97, 98, 255, 97, 97, 98], pairs).Status);
        Assert.Equal(new(true, 3, 6), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match([97, 97, 98, 255, 120], pairs).Status);
        var q = new VmPlan(profile);
        q.Seq(q.Assertion(q.Lit('a'), Pcre2AssertionKind.PositiveBehind, 1, 1), q.Leaf(Pcre2NodeKind.Grapheme));
        Assert.Equal(Pcre2MatchStatus.NoMatch, new VmFixture(q.Build()).Match([97, 255, 98], pairs).Status);
        // Actual required prefix read uses the checked barrier even when native
        // validation begins after the bad unit; no unsafe/native equality claim.
        Assert.Equal(Pcre2MatchStatus.NoMatch, new VmFixture(q.Build()).Match([97, 255, 98], pairs, start: 2).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FragmentRestartDiscardsPartialButNeverReturnsTerminalEmptyFragment(bool hard)
    {
        var p = new VmPlan(new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.MatchInvalidUtf));
        p.Seq(p.Lit('a'), p.Lit('b'));
        var f = new VmFixture(p.Build());
        Pcre2OffsetPair[] pairs = [new(true, 7, 8)];
        var options = new Pcre2MatchOptions { Flags = hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft };
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match([97, 255], pairs, options).Status);
        Assert.Equal(new(true, 7, 8), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match([97, 255, 97, 98], pairs, options).Status);
        Assert.Equal(new(true, 2, 4), pairs[0]);
    }

    [Theory]
    [InlineData((int)Pcre2Newline.Lf, "a\nb")]
    [InlineData((int)Pcre2Newline.Cr, "a\rb")]
    [InlineData((int)Pcre2Newline.Crlf, "a\r\nb")]
    [InlineData((int)Pcre2Newline.Any, "a\u2028b")]
    [InlineData((int)Pcre2Newline.Anycrlf, "a\rb")]
    [InlineData((int)Pcre2Newline.Nul, "a\0b")]
    public void FirstlineConstrainsCandidateRatherThanConsumption(int newline, string input)
    {
        var p = new VmPlan(new(flags: Pcre2CompileFlags.Firstline | Pcre2CompileFlags.Utf, newline: (Pcre2Newline)newline));
        p.Script(p.Lit('b'));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match(input, pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs, start: input.Length - 1).Status);
        var q = new VmPlan(p.Profile);
        q.Seq(q.Lit('a'), q.Repeat(q.Leaf(Pcre2NodeKind.Grapheme), 1, 2), q.Lit('b'));
        Assert.Equal(Pcre2MatchStatus.Match, new VmFixture(q.Build()).Match(input, pairs).Status);
    }

    [Fact]
    public void ExplicitCrLfMetadataControlsSourceBumpalongInterior()
    {
        // (?<=\r)\n source HASCRORLF must be supplied, not guessed from class
        // membership. Same direct tree demonstrates the candidate-policy hook.
        var p = new VmPlan(new(newline: Pcre2Newline.Crlf));
        p.Seq(p.Assertion(p.Lit(13), Pcre2AssertionKind.PositiveBehind, 1, 1), p.Lit(10));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("\r\n", pairs, hasExplicitCrOrLf: true).Status);
        Assert.Equal(new(true, 1, 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("\r\n", pairs, hasExplicitCrOrLf: false).Status);
    }

    [Fact]
    public void OffsetLimitAdmissionAndReuseCannotPublishStaleAcceptance()
    {
        var p = new VmPlan(new(flags: Pcre2CompileFlags.UseOffsetLimit));
        p.Lit('a');
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("xa", pairs, new() { OffsetLimit = 1 }).Status);
        Assert.Equal(new(true, 1, 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("xa", pairs, new() { OffsetLimit = 0 }, start: 1).Status);
        Assert.Equal(new(true, 1, 2), pairs[0]);
        var q = new VmPlan();
        q.Lit('a');
        Assert.Equal((int)Pcre2ErrorCode.Badoffsetlimit, new VmFixture(q.Build()).Match("a", pairs, new() { OffsetLimit = 0 }).NativeReturnCode);
    }

    [Fact]
    public void NotemptyAtstartUsesOriginalOperationOffsetAcrossCandidates()
    {
        var p = new VmPlan();
        p.Script(p.Leaf(Pcre2NodeKind.Empty));
        var f = new VmFixture(p.Build(patternFlags: Pcre2MatchFlags.NotemptyAtstart));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("abc", pairs, start: 1).Status);
        Assert.Equal(new(true, 2, 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("abc", pairs, Pcre2VmCompositionTests.Anchored, start: 1).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeWidthValidationAndShortCapturePublication(int domain)
    {
        var p = new VmPlan(Pcre2VmCompositionTests.Profile(domain));
        p.Capture(p.Leaf(Pcre2NodeKind.Grapheme), 1);
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        var result = domain < 2 ? f.Match("é"u8.ToArray(), pairs) : f.Match("\ud83d\ude00", pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(0, result.NativeReturnCode);
        Assert.Equal(new(true, 0, domain % 2 == 1 ? 2 : 1), pairs[0]);
        if (domain % 2 == 1)
        {
            var bad = domain < 2 ? f.Match([0xff], pairs) : f.Match("\ud800", pairs);
            Assert.Equal(Pcre2MatchStatus.Error, bad.Status);
            Assert.True(bad.NativeReturnCode < 0);
            var split = domain < 2 ? f.Match("é"u8.ToArray(), pairs, start: 1) : f.Match("\ud83d\ude00", pairs, start: 1);
            Assert.Equal((int)Pcre2ErrorCode.Badutfoffset, split.NativeReturnCode);
        }
        Assert.True(f.Released);
    }
}
