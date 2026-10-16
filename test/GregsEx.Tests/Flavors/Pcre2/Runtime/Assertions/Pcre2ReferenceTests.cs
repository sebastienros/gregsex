using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2ReferenceTests
{
    private static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ResolvedAbsoluteRelativeAndNamedReferences(int kind)
    {
        var p = new AssertionPlan();
        p.Seq(p.Capture(p.Literal('a')), p.Backref([1], (Pcre2ReferenceKind)kind));
        var pairs = new Pcre2OffsetPair[2];
        var fixture = new AssertionFixture(p.Build());
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("aa", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("ab", pairs, options: Anchored).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsetOptionDiffersFromEmptyParticipation(bool unsetMatches)
    {
        var p = new AssertionPlan(new(flags: unsetMatches ? Pcre2CompileFlags.MatchUnsetBackref : 0));
        var capture = p.Repeat(p.Capture(p.Literal('a')), 0, 1);
        p.Seq(capture, p.Backref([1]), p.Literal('b'));
        var pairs = new Pcre2OffsetPair[2];
        var result = new AssertionFixture(p.Build()).Match("b", pairs, options: Anchored);
        Assert.Equal(unsetMatches ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (unsetMatches)
        {
            Assert.False(pairs[1].Participated);
        }

        p = new();
        p.Seq(p.Capture(p.Leaf(Pcre2NodeKind.Empty)), p.Repeat(p.Backref([1]), 3, -1), p.Literal('b'));
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("b", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 0, 0), pairs[1]);
    }
    [Theory]
    [InlineData("aba", true)]
    [InlineData("abb", false)]
    [InlineData("bb", true)]
    public void DuplicateNameSelectsFirstParticipatingNumber(string input, bool match)
    {
        var p = new AssertionPlan(new(flags: Pcre2CompileFlags.Dupnames));
        p.Seq(p.Repeat(p.Capture(p.Literal('a'), 1), 0, 1), p.Capture(p.Literal('b'), 2), p.Backref([1, 2], Pcre2ReferenceKind.Named));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(match ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match(input, pairs, options: Anchored).Status);
    }
    [Fact]
    public void CaptureRemainsVisibleUntilRepeatedGroupCloses()
    {
        // (a(?:\1)?){2}: the second open capture still exposes the first a.
        var p = new AssertionPlan();
        p.Repeat(p.Capture(p.Seq(p.Literal('a'), p.Repeat(p.Backref([1]), 0, 1))), 2, 2);
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("aaa", pairs, options: Anchored).Status);
        Assert.Equal(new(true, 1, 3), pairs[1]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaselessUtfBackreferenceConsumesDifferentNativeWidth(bool bytes)
    {
        var profile = new Pcre2Profile(width: bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Caseless);
        var p = new AssertionPlan(profile);
        p.Seq(p.Capture(p.Literal(0x023A)), p.Backref([1]));
        var pairs = new Pcre2OffsetPair[2];
        var fixture = new AssertionFixture(p.Build());
        var result = bytes ? fixture.Match([0xC8, 0xBA, 0xE2, 0xB1, 0xA5], pairs, options: Anchored) : fixture.Match("Ⱥⱥ", pairs, options: Anchored);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 0, bytes ? 5 : 2), pairs[0]);
        Assert.Equal(new(true, 0, bytes ? 2 : 1), pairs[1]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Raw16CaselessUsesUnicodeOnlyWithUcp(bool ucp)
    {
        var p = new AssertionPlan(new(flags: Pcre2CompileFlags.Caseless | (ucp ? Pcre2CompileFlags.Ucp : 0)));
        p.Seq(p.Capture(p.Literal(0x023A)), p.Backref([1]));
        Assert.Equal(ucp ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match("Ⱥⱥ", new Pcre2OffsetPair[2], options: Anchored).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackreferenceUnicodeCaseIgnoresImportedAsciiLowerTable(bool ucp)
    {
        var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
        tables['a'] = tables['b'] = 1;
        var p = new AssertionPlan(new(flags: Pcre2CompileFlags.Caseless | (ucp ? Pcre2CompileFlags.Ucp : 0), characterTables: new(tables)));
        p.Seq(p.Capture(p.Literal('a', flags: 0)), p.Backref([1]));
        Assert.Equal(ucp ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, new AssertionFixture(p.Build()).Match("ab", new Pcre2OffsetPair[2], options: Anchored).Status);
    }
    [Theory]
    [InlineData(0, "kK", true)]
    [InlineData(0, "Iı", false)]
    [InlineData(0x80, "kK", false)]
    [InlineData(0x10000, "Iı", true)]
    [InlineData(0x10080, "Iı", false)]
    public void CaselessRestrictionAndTurkishBackreference(int extra, string input, bool expected)
    {
        // Actual enum values are supplied below rather than native extra bits.
        var extras = extra switch { 0x80 => Pcre2ExtraFlags.CaselessRestrict, 0x10000 => Pcre2ExtraFlags.TurkishCasing, 0x10080 => Pcre2ExtraFlags.TurkishCasing | Pcre2ExtraFlags.CaselessRestrict, _ => Pcre2ExtraFlags.None };
        var p = new AssertionPlan(new(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Caseless, extraFlags: extras));
        p.Seq(p.Capture(p.Literal(input[0], flags: Pcre2CompileFlags.Utf)), p.Backref([1]));
        Assert.Equal(expected ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new AssertionFixture(p.Build()).Match(input, new Pcre2OffsetPair[2], options: Anchored).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CasefulBackreferencePreservesSplitCodeUnitCaptures(bool bytes)
    {
        var p = new AssertionPlan(new(width: bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, flags: Pcre2CompileFlags.Utf));
        p.Seq(p.Capture(p.Leaf(Pcre2NodeKind.CodeUnit)), p.Leaf(Pcre2NodeKind.CodeUnit), p.Backref([1]));
        var fixture = new AssertionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        // 8-bit two-byte scalar then another lead; 16-bit pair then another high.
        var result = bytes ? fixture.Match([0xC3, 0xA9, 0xC3, 0xA9], pairs, options: Anchored) : fixture.Match("😀😀", pairs, options: Anchored);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 0, 1), pairs[1]);
        Assert.Equal(new(true, 0, 3), pairs[0]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackreferencePartialChecksMatchedPrefixBeforeEnd(bool hard)
    {
        var p = new AssertionPlan();
        p.Seq(p.Capture(p.Seq(p.Literal('a'), p.Literal('b'))), p.Backref([1]));
        var fixture = new AssertionFixture(p.Build());
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored | (hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft) };
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Partial, fixture.Match("aba", pairs, options: options).Status);
        Assert.Equal(new(true, 0, 3), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, fixture.Match("abx", pairs, options: options).Status);
        Assert.Equal(Pcre2MatchStatus.Match, fixture.Match("abab", pairs, options: options).Status);
    }
}
