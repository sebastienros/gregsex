using GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2ControlExecutionTests
{
    public static TheoryData<int, string, string, int, int> VerifiedThenCases => ThenRetryTests.NativeCases;

    [Theory, MemberData(nameof(VerifiedThenCases))]
    public void ActualControlCallerBindsVerifiedBehindRetryPolicy(int domain, string pattern, string input, int start, int rc)
    {
        var f = new ControlFixture(pattern, ControlFixture.Profile(domain,
            Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoAutoPossess));
        var pairs = new Pcre2OffsetPair[f.Program.GroupCount];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored };
        var result = domain < 2 ? f.Match([.. input.Select(c => (byte)c)], pairs, options, start: start) :
            f.Match(input, pairs, options, start: start);
        Assert.Equal(rc, result.NativeReturnCode);
        if (rc > 0)
        {
            Assert.Equal(new(true, start, start + 1), pairs[0]);
        }

        if (rc == 3)
        { Assert.False(pairs[1].Participated); Assert.Equal(new(true, 0, 2), pairs[2]); }
        Assert.True(f.Released);
    }

    public static IEnumerable<TheoryDataRow<int, string, string, bool, int, int>> Rules()
    {
        (string Pattern, string Input, bool Match, int Start, int End)[] rules =
        [
            ("a(*PRUNE)b|ac", "ac", false, 0, 0),
            ("a(*COMMIT)b|ac", "ac", false, 0, 0),
            ("a(*SKIP)b|ac", "ac", false, 0, 0),
            ("a(*SKIP:missing)b|ac", "ac", true, 0, 2),
            ("a(*MARK:X)b(*SKIP:X)c|ab", "ab", false, 0, 0),
            ("a(*MARK:X)b(*SKIP:Y)c|ab", "ab", true, 0, 2),
            ("a+(*THEN)b|aa", "aa", true, 0, 2),
            ("(a|a+(*THEN)b)|aa", "aa", true, 0, 2),
            ("(a+(*THEN)b)|aa", "aa", true, 0, 2),
            ("(?!a(*PRUNE)b)ac", "ac", true, 0, 2),
            ("(?=a(*PRUNE)b)ac|ac", "ac", false, 0, 0),
            ("(?(?=a(*PRUNE)b)x|ac)", "ac", true, 0, 2),
            ("(?(DEFINE)(?<r>a(*PRUNE)b))(?&r)c|ac", "ac", true, 0, 2),
            ("(?(DEFINE)(?<r>a(*ACCEPT)b))(?&r)c", "ac", true, 0, 2),
            ("a(*ACCEPT)|ab", "ab", false, 0, 0),
            ("a|ab", "ab", true, 0, 2),
            ("(*sr:a\\x{0}(*ACCEPT))", "a\0", true, 0, 2),
            ("(?!a(*ACCEPT))a", "a", false, 0, 0),
            ("(?=a(b(*ACCEPT))c)ab", "ab", true, 0, 2),
            ("foo\\Kbar", "foobar", true, 3, 6),
            ("a(*FAIL)|ab", "ab", true, 0, 2)
        ];
        for (var domain = 0; domain < 4; domain++)
        {
            foreach (var (Pattern, Input, Match, Start, End) in rules)
            {
                yield return new TheoryDataRow<int, string, string, bool, int, int>(domain, Pattern, Input, Match, Start, End);
            }
        }
    }

    [Theory, MemberData(nameof(Rules))]
    public void ActualSourceCompilationExecutesControlScope(int domain, string pattern, string input, bool matched, int start, int end)
    {
        var f = new ControlFixture(pattern, ControlFixture.Profile(domain));
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 77, 99), f.Program.GroupCount).ToArray();
        var result = f.Ascii(input, pairs, new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Endanchored });
        Assert.Equal(matched ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (matched)
        {
            Assert.Equal(new(true, start, end), pairs[0]);
        }
        else
        {
            Assert.All(pairs, pair => Assert.Equal(new(true, 77, 99), pair));
        }

        Assert.True(f.Released);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WholeAcceptClosesEligibleOpenCapturesAndNotemptyPreservesRetries(int domain)
    {
        var f = new ControlFixture("(a(b(*ACCEPT))c)", ControlFixture.Profile(domain));
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(3, f.Ascii("ab", pairs).NativeReturnCode);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        Assert.Equal(new(true, 0, 2), pairs[1]);
        Assert.Equal(new(true, 1, 2), pairs[2]);
        var g = new ControlFixture("(*ACCEPT)|a", f.Program.Profile);
        Assert.Equal(Pcre2MatchStatus.Match, g.Ascii("a", pairs, new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Notempty }).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefaultPrefilterChangesCommitOutcomeExactly(int domain)
    {
        var pairs = new Pcre2OffsetPair[1];
        var on = new ControlFixture("(*COMMIT)ABC", ControlFixture.Profile(domain));
        Assert.Equal(Pcre2MatchStatus.Match, on.Ascii("DEFABC", pairs).Status);
        Assert.Equal(new(true, 3, 6), pairs[0]);
        var off = new ControlFixture("(*NO_START_OPT)(*COMMIT)ABC", on.Program.Profile);
        Assert.Equal(Pcre2MatchStatus.NoMatch, off.Ascii("DEFABC", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThenInBehindTriesNextTopLevelBranch(int domain)
    {
        var f = new ControlFixture("(?<=a(*THEN)b|ac)x", ControlFixture.Profile(domain,
            Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoAutoPossess));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Ascii("acx", pairs).Status);
        Assert.Equal(new(true, 2, 3), pairs[0]);
    }
}
