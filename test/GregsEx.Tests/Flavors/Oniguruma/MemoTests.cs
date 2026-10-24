using System.Text;
using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

// The failure memo (OnigCompiler.MemoSlots): searches that backtrack over ambiguous alternations under a star stay
// polynomial, and every result is the one the plain backtracker finds.
public class MemoTests
{
    private const string Words = "x|exec|w|words|ww|quotewords|v|val|q|single|qq|double|s|scalar|a|array|h|hash|f|function|c|closure|b|blackslash|regexp|substr|trans|codes|p|path";

    private static OnigurumaPattern Pattern(string source) => OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate);

    [Fact]
    public void AmbiguousStarsStayPolynomial()
    {
        // Without the memo each two more units multiply the work by about 2.4: n = 26 took 3.2 million units.
        var workspace = Pattern(@"q(w|ww)*\s*:?(w|ww)*\s*\(\(").CreateWorkspace();
        foreach (var n in new[] { 26, 60, 200 })
        {
            Assert.Equal(RegexSearchStatus.NoMatch, workspace.IsMatch("q" + new string('w', n) + "x(("));
            Assert.True(workspace.LastWork < 200 * n, $"n = {n}: {workspace.LastWork}");
        }

        Assert.Equal(RegexSearchStatus.Match, workspace.Find("q" + new string('w', 200) + "((", 0, out var match));
        Assert.Equal(new RegexMatchRange(0, 203), match);
    }

    [Fact]
    public void Perl6QuoteRuleFinishesOnTheFuzzLine()
    {
        // TextMateSharp's grammar fuzzer: the Perl 6 q/Q rule reached the 10 million unit budget on a 772-unit line.
        var line = "wwqq" + new string('w', 20) + "quotewords" + new string('w', 60) + "{ww[[wwww((wwwwww wwww]ww" + new string('w', 300) + new string(']', 300);
        var workspace = Pattern($@"(q|Q)({Words})*\s*:?({Words})*\s*\(\(").CreateWorkspace();
        Assert.Equal(RegexSearchStatus.NoMatch, workspace.IsMatch(line));
        Assert.True(workspace.LastWork < 1_000_000, $"{workspace.LastWork}");
    }

    [Theory]
    [InlineData(@"(a|aa)*b", true)]
    [InlineData(@"(?:a|ab)+c", true)]
    [InlineData(@"(a|b)*?c", true)]
    [InlineData(@"x\s*(a|aa)*$", true)]
    [InlineData(@"(a|aa){2,}b", false)]
    [InlineData(@"(a|aa){0,5}b", false)]
    [InlineData(@"(a|aa|)*b", false)]
    [InlineData(@"(a|aa)*\1", false)]
    [InlineData(@"(?=a+b)(a|aa)*b", false)]
    [InlineData(@"(?>a|aa)*b", false)]
    [InlineData(@"(a|aa)*+b", false)]
    [InlineData(@"(?<x>a|b\g<x>)", false)]
    public void TheMemoAppliesOnlyWhereResumesDependOnPcAndPosition(string source, bool memo)
        => Assert.Equal(memo, Pattern(source).Program.MemoSlots is not null);

    [Fact]
    public void MemoMatchesThePlainBacktracker()
    {
        // Patterns over a small alphabet with captures, ambiguous alternations, stars, lazy and greedy runs and
        // anchors, searched from every start with the memo from the first backtrack and without it.
        var random = new Random(20261024);
        var compared = 0;
        for (var i = 0; i < 3000; i++)
        {
            var source = Node(random, 3);
            if (!OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out _) || pattern!.Program.MemoSlots is null)
            {
                continue;
            }

            var memo = pattern.CreateWorkspace();
            memo.Matcher.MemoAfter = 0;
            var plain = pattern.CreateWorkspace();
            plain.Matcher.MemoAfter = long.MaxValue;
            var a = new RegexCapture[pattern.GroupCount];
            var b = new RegexCapture[pattern.GroupCount];
            for (var s = 0; s < 6; s++)
            {
                var input = Subject(random);
                for (var start = 0; start <= input.Length; start++)
                {
                    var expected = plain.FindCaptures(input, start, b, out _);
                    var actual = memo.FindCaptures(input, start, a, out _);
                    var context = $"{source} on \"{input}\" from {start}";
                    Assert.True(expected == actual, $"{context}: {actual}, expected {expected}");
                    if (expected == RegexSearchStatus.Match)
                    {
                        Assert.True(a.AsSpan().SequenceEqual(b), context);
                    }

                    compared++;
                }
            }
        }

        Assert.True(compared > 20000, $"{compared} searches");
    }

    private static string Subject(Random random)
    {
        var builder = new StringBuilder();
        var length = random.Next(0, 14);
        for (var i = 0; i < length; i++)
        {
            builder.Append("aab \n"[random.Next(5)]);
        }
        return builder.ToString();
    }

    private static string Node(Random random, int depth)
    {
        if (depth == 0)
        {
            return Atom(random);
        }

        switch (random.Next(8))
        {
            case 0:
            case 1:
                return Node(random, depth - 1) + Node(random, depth - 1);
            case 2:
                return "(" + Node(random, depth - 1) + "|" + Node(random, depth - 1) + ")";
            case 3:
                return "(?:" + Node(random, depth - 1) + "|" + Node(random, depth - 1) + "|" + Node(random, depth - 1) + ")";
            case 4:
                return "(" + Plain(random, depth - 1) + ")" + (random.Next(3) switch { 0 => "*", 1 => "+", _ => "*?" });
            case 5:
                return "(?:" + Plain(random, depth - 1) + ")" + (random.Next(2) == 0 ? "*" : "+?");
            case 6:
                return Atom(random) + (random.Next(4) switch { 0 => "*", 1 => "+", 2 => "?", _ => "*?" });
            default:
                return Atom(random);
        }
    }

    // A body that cannot match empty.
    private static string Plain(Random random, int depth)
    {
        var body = Node(random, depth);
        return random.Next(2) == 0 ? body + "a" : "b|" + body + "a";
    }

    private static string Atom(Random random) => random.Next(10) switch
    {
        0 => "a",
        1 => "aa",
        2 => "b",
        3 => "ab",
        4 => @"\s",
        5 => "[ab]",
        6 => "$",
        7 => "^",
        8 => @"\b",
        _ => ".",
    };
}
