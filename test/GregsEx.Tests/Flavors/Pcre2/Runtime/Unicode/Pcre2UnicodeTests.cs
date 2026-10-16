using System.Text;
using GregsEx.Contracts.Pcre2;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Unicode;

public sealed class Pcre2UnicodeTests
{
    internal static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    internal static Pcre2MatchResult Match(UnicodeFixture fixture, string input, Pcre2OffsetPair[] pairs,
        int start = 0, Pcre2MatchOptions? options = null, Pcre2MatchLimits? limits = null, int floor = 0, int? end = null) =>
        fixture.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ?
        fixture.Match(fixture.Program.Profile.Utf ? Encoding.UTF8.GetBytes(input) : [.. input.Select(c => checked((byte)c))], pairs, start, options ?? Anchored, limits, floor, end) :
        fixture.Match(input, pairs, start, options ?? Anchored, limits, floor, end);
    internal static AssertionPlan Plan(int domain) => new(new((domain & 1) == 0 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
        domain >= 2 ? Pcre2CompileFlags.Utf : Pcre2CompileFlags.None));

    public static IEnumerable<TheoryDataRow<int, string, int>> Graphemes()
    {
        // Source masks and extuni contextual rules; lengths are scalar-independent
        // native units. Multiscalar supplementary cases run only in UTF domains.
        (string Text, int End)[] raw8 = [("ab", 1), ("\r\nx", 2), ("\0a", 1), ("a\0", 1), ("", 0)];
        (string Text, int End)[] bmp = [("a\u0301b",2),("\u0600a",2),("\u0915\u093eX",2),
            ("\u1100\u1161\u11a8X",3),("\uac00\u11a8X",2),("\uac01\u11a8X",2),
            ("\u200d\u2600",1),("a\u200d\u2600",2),("\u2600\u0301\u200d\u2600X",4)];
        for (var d = 0; d < 4; d++)
        {
            foreach (var x in raw8)
            {
                yield return new TheoryDataRow<int, string, int>(d, x.Text, x.End);
            }

            if (d != 0)
            {
                foreach (var x in bmp)
                {
                    yield return new TheoryDataRow<int, string, int>(d, x.Text, x.End);
                }
            }

            if (d >= 2)
            {
                yield return new TheoryDataRow<int, string, int>(d, "\U0001f1e6\U0001f1e7\U0001f1e8", 4);
                yield return new TheoryDataRow<int, string, int>(d, "\U0001f469\u0301\u200d\U0001f4bbX", 6);
            }
            if (d == 1)
            { yield return new TheoryDataRow<int, string, int>(d, "\ud800\udfff", 1); yield return new TheoryDataRow<int, string, int>(d, "\ud800\u0301x", 2); }
        }
    }
    [Theory, MemberData(nameof(Graphemes))]
    public void ExactNativeCluster(int domain, string input, int end)
    {
        var p = Plan(domain);
        p.Leaf(Pcre2NodeKind.Grapheme);
        var pairs = new Pcre2OffsetPair[1];
        var result = Match(new(p.Build()), input, pairs);
        Assert.Equal(input.Length == 0 ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, result.Status);
        if (input.Length != 0)
        {
            Assert.Equal(new(true, 0, domain == 2 ? Encoding.UTF8.GetByteCount(input.AsSpan(0, end)) : end), pairs[0]);
        }
    }

    public static IEnumerable<TheoryDataRow<int, string, bool>> Scripts()
    {
        (string Text, bool Valid)[] basic = [("", true), ("a", true), ("aA0 !", true), ("\0a", true), ("a\0", true)];
        (string Text, bool Valid)[] bmp = [("a\u0301",true),("a\u03b1",false),("\u0378",true),("\u0378a",false),
            ("\ud800",true),("\ud800a",false),("\u0301 !",true),("\u03b1\u0342",true),("a\u0342",false),
            ("\u6f22\u3042\u30a2",true),("\u6f22\u3105",true),("\uac00\u6f22",true),
            ("\u6f22\uac00\u3042",true),("\uac00\u6f22\u3042",false),
            ("\u3042\u3105",false),("\u6f22a",false),("\u30fc\u3042",true),("\u30fca",false),
            ("1\u0661",false),("\u0661\u0662",true),("\u0661\u06f2",false),("1\uff12",false),
            ("\uff11\uff12",true),("\u0661\u0627",true),("\u0661a",false)];
        for (var d = 0; d < 4; d++)
        {
            foreach (var x in basic)
            {
                yield return new TheoryDataRow<int, string, bool>(d, x.Text, x.Valid);
            }

            if (d != 0)
            {
                foreach (var x in bmp)
                {
                    if (d == 1 || !x.Text.Any(char.IsSurrogate))
                    {
                        yield return new TheoryDataRow<int, string, bool>(d, x.Text, x.Valid);
                    }
                }
            }

            if (d >= 2)
            {
                yield return new TheoryDataRow<int, string, bool>(d, "\U00010400\U00010428", true); // Deseret > compact extension map
                yield return new TheoryDataRow<int, string, bool>(d, "\U00010400a", false);
                yield return new TheoryDataRow<int, string, bool>(d, "\U0001d7ce\U0001d7cf", true);
                yield return new TheoryDataRow<int, string, bool>(d, "\U0001d7ce\U0001d7d8", false);
            }
        }
    }
    [Theory, MemberData(nameof(Scripts))]
    public void NativeScriptAndDigitFamilies(int domain, string input, bool valid)
    {
        foreach (var atomic in new[] { false, true })
        {
            var p = Plan(domain);
            var body = p.Repeat(p.Leaf(Pcre2NodeKind.Dot, flags: p.Profile.Flags | Pcre2CompileFlags.Dotall));
            p.Seq(p.Add(Pcre2NodeKind.ScriptRun, [body], atomic ? 1 : 0), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
            var pairs = new Pcre2OffsetPair[1];
            var result = Match(new(p.Build()), input, pairs);
            Assert.Equal(valid ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
            if (valid)
            {
                Assert.Equal(new(true, 0, domain == 2 ? Encoding.UTF8.GetByteCount(input) : input.Length), pairs[0]);
            }
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RegionalParityUsesOriginalPrefixAndFragmentFloor(int domain)
    {
        const string subject = "\U0001f1e6\U0001f1e7\U0001f1e8";
        var p = Plan(domain);
        p.Leaf(Pcre2NodeKind.Grapheme);
        var fixture = new UnicodeFixture(p.Build());
        var width = domain == 2 ? 4 : 2;
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, Match(fixture, subject, pairs, width).Status);
        Assert.Equal(new(true, width, width * 2), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.Match, Match(fixture, subject, pairs, width, floor: width).Status);
        Assert.Equal(new(true, width, width * 3), pairs[0]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptValidationFailureRespectsTheInnerAtomicCommit(bool atomic)
    {
        var p = Plan(3);
        var body = p.Capture(p.Alt(p.Seq(p.Literal('a'), p.Literal(0x03b1)), p.Literal('a')));
        p.Seq(p.Add(Pcre2NodeKind.ScriptRun, [body], atomic ? 1 : 0), p.Literal(0x03b1), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[2];
        // PCRE2 10.46 atomic-script-reduction.native: ASR commits the
        // inner alternative before validating its script; SR can retry it.
        Assert.Equal(atomic ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, Match(new(p.Build()), "a\u03b1", pairs).Status);
        Assert.Equal(atomic ? default : new Pcre2OffsetPair(true, 0, 1), pairs[1]);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SuccessfulAtomicRunCommitsLocalChoices(bool atomic, bool matches)
    {
        var p = Plan(3);
        var body = p.Capture(p.Repeat(p.Literal('a'), 1, 2));
        p.Seq(p.Add(Pcre2NodeKind.ScriptRun, [body], atomic ? 1 : 0), p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(matches ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, Match(new(p.Build()), "aa", pairs).Status);
        if (matches)
        {
            Assert.Equal(new(true, 0, 1), pairs[1]);
        }
    }
    [Fact]
    public void ResetStartDoesNotRebaseValidatedBody()
    {
        var p = Plan(3);
        var body = p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.ResetStart), p.Literal(0x03b1));
        p.Add(Pcre2NodeKind.ScriptRun, [body]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, Match(new(p.Build()), "a\u03b1", new Pcre2OffsetPair[1]).Status);
    }
    [Fact]
    public void NestedRetainedFramesAndCapturesRetryCorrectly()
    {
        var p = Plan(3);
        var inner = p.Add(Pcre2NodeKind.ScriptRun, [p.Capture(p.Repeat(p.Literal('a'), 1, 3))]);
        var outer = p.Add(Pcre2NodeKind.ScriptRun, [p.Capture(inner, 2)]);
        p.Seq(outer, p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, Match(new(p.Build()), "aaa", pairs).Status);
        Assert.Equal(new(true, 0, 2), pairs[1]);
        Assert.Equal(new(true, 0, 2), pairs[2]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ClusterEndPartialPolicy(int domain)
    {
        var p = Plan(domain);
        p.Leaf(Pcre2NodeKind.Grapheme);
        var fixture = new UnicodeFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Partial, Match(fixture, "a", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.PartialHard }).Status);
        Assert.Equal(Pcre2MatchStatus.Match, Match(fixture, "a", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.PartialSoft }).Status);
        Assert.Equal(Pcre2MatchStatus.Match, Match(fixture, "ab", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.PartialHard }).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, Match(fixture, "", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.PartialHard }).Status);
        var q = Plan(domain);
        q.Seq(q.Literal('a'), q.Leaf(Pcre2NodeKind.Grapheme));
        Assert.Equal(Pcre2MatchStatus.Partial, Match(new(q.Build()), "a", pairs, options: new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.PartialSoft }).Status);
    }
    [Fact]
    public void ScanWindowsAndNonAtomicAssertionRetainScriptRetry()
    {
        var p = Plan(3);
        var capture = p.Capture(p.Repeat(p.Literal('a'), 2, 2));
        var run = p.Add(Pcre2NodeKind.ScriptRun, [p.Capture(p.Repeat(p.Leaf(Pcre2NodeKind.Grapheme), 1, 2), 2)]);
        var scan = p.Scan(run);
        p.Seq(capture, scan, p.Backref([2]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, Match(new(p.Build()), "aaa", pairs).Status);
        Assert.Equal(new(true, 0, 1), pairs[2]);
        var q = Plan(3);
        var script = q.Add(Pcre2NodeKind.ScriptRun, [q.Capture(q.Alt(q.Literal('a'), q.Seq(q.Literal('a'), q.Literal('a'))))]);
        q.Seq(q.Assertion(script, Pcre2AssertionKind.NonAtomicAhead), q.Backref([1]), q.Literal('b'));
        Assert.Equal(Pcre2MatchStatus.Match, Match(new(q.Build()), "aab", new Pcre2OffsetPair[2]).Status);
    }
}
