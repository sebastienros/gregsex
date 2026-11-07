using GregsEx.Flavors.Oniguruma;
using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

// An alternation whose branches start with different units jumps over the branches that cannot start at the position,
// or fails when none can (Op.AltSwitch). The same branches nested two at a time, a|(?:b|(?:c|...)), keep the order a
// leftmost-first search tries them in but never get a jump, so in the same pattern both forms must give the same
// status, match, captures and work from every start. Each case has a branch (#x) only a jump past eight tests reaches.
public sealed class AltSwitchTests
{
    private static readonly string[] Nine = ["a1", "b1", "c1", "d1", "e1", "f1", "g1", "h1", "#x"];

    public static TheoryData<string, string[], string> Cases => new()
    {
        // Keywords out of order, a shared prefix where the shorter one comes first.
        { "({0})", ["select", "insert", "update", "delete", "from", "where", "set", "sets", "union", "order", "#x"], "x update sets; from where SELECT ordering #x" },
        // Ignore-case, multi-code-point folds (ss, ﬃ), word boundaries.
        { "({0})", ["(?i:\\bstrasse\\b)", "(?i:affine)", "(?i:offset)", "(?i:class)", "(?i:file)", "(?i:boss)", "(?i:list)", "(?i:fist)", "#x"], "Straße AFFINE oﬃce BOSS fiſt LIST Class #x" },
        // A branch that matches empty, end anchors, a line start, non-ASCII starts, and a branch any unit can start that
        // the chain enters and leaves at the end of the input.
        { "({0})", ["alpha", "beta", "gamma", "delta", "zeta", "é+", "ω", "^z", "#x", "[\\s\\S]q", "$", "(?:)"], "zeta beta\néé ω gamma #x" },
        // Captures inside branches, a backreference to them, a nested long alternation.
        { "({0})", ["(a)b", "(c)d", "(e)f", "(g)h", "(i)j", "(k)(?:l|m|n|o|p|q|r|s|t|u)", "(u)v\\1", "(w)x", "#x"], "ab cd kq ku uvu uvx wx gh #x" },
        // Look-arounds, lazy and possessive repeats.
        { "({0})", ["(?<=k)a", "(?<=z)b", "c(?=d)", "e+?f", "g++h", "i*j", "(?=m)mn", "o{2,3}", "#x"], "kazbcdeeefgggghiijmnooo#x" },
        // \G ties a branch to the search start.
        { "({0})", ["\\Ga", "\\Gb", "c", "d", "\\Ge", "f", "g", "h", "#x"], "abcdefgh ab#x" },
        // A unit before the alternation, so attempts reach units no branch can start with: the jump fails at once, where
        // entering the last branch (a look-ahead) would count work.
        { "-({0})", [.. Nine, "(?=z+y)z"], "-. -zzy -a1 -#x -zq -!" },
        // Surrogate pairs, a lone high surrogate at the end, non-ASCII starts.
        { "({0})", [.. Nine, "\U0001F600", "é"], "x\U0001F600y é #x \uD83D" },
        // The alternation ends a variable-length look-behind body: the jump reads the end of the look-behind's text.
        { "(?<=(x)(?:{0}))q", ["a", "b", "c", "d", "e", "f", "g", "h", "#x", "$", "yy"], "xqr xaq x#xq xq xyyq" },
        // A called group holding the alternation, called again after it; recursion through the last branch.
        { "(?<k>(?:{0}))-\\g<k>", [.. Nine, "z+"], "a1-b1 #x-#x c1-zzz h1-q" },
        { "(?<p>\\((?:{0})*\\))", ["a", "b", "c", "d", "e", "f", "g", "h", "#x", "\\g<p>"], "(a(b)(#x(c)))((h)" },
        // An atomic group, a conditional, \K, a counted loop and a lazy loop over the alternation.
        { "(?>{0})1", [.. Nine, "a"], "a1 a11 #x1 h11" },
        { "(z)?(?(1)(?:{0})|y)", [.. Nine, "q"], "za1 y zq z#x zz" },
        { "({0})", ["a\\Kb", "b", "c", "d", "e", "f", "g", "h", "#x", "x\\Ky"], "ab xy #x h" },
        { "(?:({0})){2,3}", [.. Nine, "q"], "a1b1 qqq #xa1c1d1 h1" },
        { "(?:({0}))+?;", [.. Nine, "q"], "a1b1; qq; #x;" },
        // Exponential without the failure memo, which needs MemoSlots to know the switch's width: (a|aa)* over 40 a's.
        { "^(?:({0}))*;", ["a", "b", "c", "d", "e", "f", "g", "h", "#x", "aa"], new string('a', 40) + "!;" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ALongAlternationMatchesAsItsBranchesNestedTwoAtATime(string template, string[] branches, string input)
    {
        var flat = template.Replace("{0}", string.Join("|", branches));
        var nested = template.Replace("{0}", Nest(branches, 0));
        // The outer alternation has a switch only in the flat form (a long alternation inside a branch has one in both).
        Assert.Equal(Program(nested).AltSwitches.Length + 1, Program(flat).AltSwitches.Length);

        // The default budget, and one small enough that searches stop on it: the budget exit must also agree.
        foreach (var maxWork in new long[] { 10_000_000, 25 })
        {
            var options = new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: maxWork));
            var a = OnigurumaPattern.Parse(flat, RegexProfiles.OnigurumaTextMate, options).CreateWorkspace();
            var b = OnigurumaPattern.Parse(nested, RegexProfiles.OnigurumaTextMate, options).CreateWorkspace();
            var ga = new RegexCapture[16];
            var gb = new RegexCapture[16];
            for (var start = 0; start <= input.Length; start++)
            {
                var sa = a.FindCaptures(input, start, ga, out var ma);
                var sb = b.FindCaptures(input, start, gb, out var mb);
                Assert.Equal(sb, sa);
                Assert.Equal(mb, ma);
                Assert.Equal(gb, ga);
                Assert.Equal(b.LastWork, a.LastWork);
            }
        }
    }

    // Each switch table belongs to its own alternation, also when one alternation is nested in another's branch.
    [Theory]
    [InlineData("ez", 2)]
    [InlineData("i1", 2)]
    [InlineData("ea", -1)]
    public void NestedAlternationsKeepTheirOwnSwitch(string input, int length)
    {
        const string pattern = "(?:a1|b1|c1|d1|e(?:p|q|r|s|t|u|v|w|x|y|z)|f1|g1|h1|i1)";
        Assert.Equal(2, Program(pattern).AltSwitches.Length);
        var found = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate).CreateWorkspace().Find(input, 0, out var match);
        Assert.Equal(length < 0 ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match, found);
        if (length >= 0)
        {
            Assert.Equal(length, match.Length);
        }
    }

    // A jump that passes fewer than AltSwitchMinSkip tests for every unit is not worth its dispatch.
    [Theory]
    [InlineData("(?:a|b|c|d|e|f|g|h)")]
    [InlineData("(?:\\w+|x|y|z|w|v|u|t|s|r)")]
    [InlineData("(?:a|.|b|c|d|e|f|g|h|i|j)")]
    public void AlternationsThatSkipLittleGetNoSwitch(string pattern)
        => Assert.DoesNotContain(Op.AltSwitch, Program(pattern).Code);

    [Fact]
    public void TheShortestAlternationWithASwitchPassesEightTests()
        => Assert.Contains(Op.AltSwitch, Program("(?:a|b|c|d|e|f|g|h|i)").Code);

    private static OnigProgram Program(string pattern) => OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate).Program;

    private static string Nest(string[] branches, int at)
        => at == branches.Length - 1 ? branches[at] : branches[at] + "|(?:" + Nest(branches, at + 1) + ")";
}
