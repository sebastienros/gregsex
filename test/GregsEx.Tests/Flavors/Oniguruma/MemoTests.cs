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
    [InlineData(@"(?=a+b)(a|aa)*b", true)]
    [InlineData(@"(a|aa)*(?!b)", true)]
    [InlineData(@"(?<=a|bb)(a|aa)*b", true)]
    [InlineData(@"(?>a|aa)*b", true)]
    [InlineData(@"(?<x>a|b)(?:\g<x>c|ac)*d", true)]
    [InlineData(@"(?<t><(?:[^<>]|\g<t>)+>)?(a|aa)*b", true)]
    [InlineData(@"(?:(a)b|aa)?(a|aa)*b", true)]
    [InlineData(@"(?:(a)b|aa){0,2}(a|aa)*b", false)]
    [InlineData(@"(?=(a|aa)*b)c", false)]
    [InlineData(@"(?<x>a|b\g<x>)", false)]
    [InlineData(@"(a|aa)*b|x\g<0>", false)]
    [InlineData(@"(?<x>a)(?:\g<x>|)*b", false)]
    public void TheMemoAppliesOnlyWhereResumesDependOnPcAndPosition(string source, bool memo)
        => Assert.Equal(memo, Pattern(source).Program.MemoSlots is not null);

    [Fact]
    public void SlotsStayWithinTwoBytes()
    {
        // A slot is a short: a program with more resume points than that has no memo and still matches.
        var small = Pattern("(?:" + string.Join("|", Enumerable.Range(0, 100).Select(i => $"k{i}")) + ")*x").Program;
        Assert.IsType<short[]>((object?)small.MemoSlots);
        // About two slots per branch: 16,500 branches need more than 32,768.
        var large = Pattern("(?:" + string.Join("|", Enumerable.Repeat("ab", 16_500)) + ")*x");
        Assert.Null(large.Program.MemoSlots);
        Assert.Equal(RegexSearchStatus.Match, large.CreateWorkspace().Find("zz ababx", 0, out var match));
        Assert.Equal(new RegexMatchRange(3, 5), match);
    }

    [Fact]
    public void CSharpLocalDeclarationStaysLinearOnDottedNames()
    {
        // csharp.tmLanguage's local-variable-declaration (MIT, dotnet/csharp-tmLanguage): calls and look-aheads. Every
        // letter can start a match, and without the memo each start inside a dotted name re-explores the rest of it.
        var workspace = Pattern(LocalVariableDeclaration).CreateWorkspace();
        long last = 0;
        foreach (var n in new[] { 20, 80, 320 })
        {
            var line = string.Join(".", Enumerable.Repeat("Identifier", n)) + "(argument);";
            Assert.Equal(RegexSearchStatus.NoMatch, workspace.IsMatch(line));
            Assert.True(workspace.LastWork < 120L * line.Length, $"n = {n}: {workspace.LastWork} for {line.Length} units");
            last = workspace.LastWork;
        }

        Assert.True(last > 0);
        Assert.Equal(RegexSearchStatus.Match, workspace.Find("    List<int> values = new();", 0, out var match));
        Assert.Equal(new RegexMatchRange(4, 17), match);
    }

    [Theory]
    [InlineData("result = service.Client.Load(id);")]
    [InlineData("        options.Database.UseSqlServer(connection.Value);")]
    public void OrdinaryLinesStartTheMemo(string line)
    {
        // Lines of everyday code backtrack a few hundred units in this rule: the memo must already be on for them.
        var pattern = Pattern(LocalVariableDeclaration);
        var plain = pattern.CreateWorkspace();
        plain.Matcher.MemoAfter = long.MaxValue;
        var memo = pattern.CreateWorkspace();
        Assert.Equal(plain.IsMatch(line), memo.IsMatch(line));
        Assert.True(memo.LastWork < plain.LastWork, $"{memo.LastWork} with the default threshold, {plain.LastWork} without the memo");
    }

#if !GREGSEX_NETSTANDARD_ASSET
    [Fact]
    public void KernelsKeepTheirBudgetWhereTheMemoSkipsTheBacktracking()
    {
        // All the backtracking is inside the look-ahead, which has no memo slots: handing the search to the interpreter
        // early would repeat the kernel's work, and here run out of it where the kernel finishes.
        var compiled = Pattern(@"b?(?=(?:a|b)*;)").Compile();
        Assert.Equal(RegexSearchStatus.Match, compiled.CreateWorkspace().IsMatch(new string('a', 3300) + " ;"));
    }
#endif

    [Fact]
    public void StartsInsideANameMeetItsContinuationOnce()
    {
        // TypeScript.tmLanguage's template-call (MIT, microsoft/TypeScript-TmLanguage): each start inside "constructor"
        // runs the name to the same end, whose continuation an earlier start already tried. 1,064 units without
        // skipping it on arrival, 855 with (the bound sits between them); 1,601 without the memo.
        var pattern = Pattern(TemplateCall);
        var memo = pattern.CreateWorkspace();
        var plain = pattern.CreateWorkspace();
        plain.Matcher.MemoAfter = long.MaxValue;
        const string line = "  constructor(private readonly http: HttpClient, private readonly store: Store<AppState>) {}";
        Assert.Equal(plain.IsMatch(line), memo.IsMatch(line));
        Assert.True(memo.LastWork < 960, $"{memo.LastWork}, {plain.LastWork} without the memo");
    }

    private const string TemplateCall = """([_$[:alpha:]][_$[:alnum:]]*)?\s*(?=(<\s*(((keyof|infer|typeof|readonly)\s+)|(([_$[:alpha:]][_$[:alnum:]]*|(\{([^\{\}]|(\{([^\{\}]|\{[^\{\}]*\})*\}))*\})|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(\[([^\[\]]|(\[([^\[\]]|\[[^\[\]]*\])*\]))*\])|(\'([^\'\\]|\\.)*\')|(\"([^\"\\]|\\.)*\")|(\`([^\`\\]|\\.)*\`))(?=\s*([\<\>\,\.\[]|=>|&(?!&)|\|(?!\|)))))([^<>\(]|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(?<==)\>|\<\s*(((keyof|infer|typeof|readonly)\s+)|(([_$[:alpha:]][_$[:alnum:]]*|(\{([^\{\}]|(\{([^\{\}]|\{[^\{\}]*\})*\}))*\})|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(\[([^\[\]]|(\[([^\[\]]|\[[^\[\]]*\])*\]))*\])|(\'([^\'\\]|\\.)*\')|(\"([^\"\\]|\\.)*\")|(\`([^\`\\]|\\.)*\`))(?=\s*([\<\>\,\.\[]|=>|&(?!&)|\|(?!\|)))))(([^<>\(]|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(?<==)\>|\<\s*(((keyof|infer|typeof|readonly)\s+)|(([_$[:alpha:]][_$[:alnum:]]*|(\{([^\{\}]|(\{([^\{\}]|\{[^\{\}]*\})*\}))*\})|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(\[([^\[\]]|(\[([^\[\]]|\[[^\[\]]*\])*\]))*\])|(\'([^\'\\]|\\.)*\')|(\"([^\"\\]|\\.)*\")|(\`([^\`\\]|\\.)*\`))(?=\s*([\<\>\,\.\[]|=>|&(?!&)|\|(?!\|)))))([^<>\(]|(\(([^\(\)]|(\(([^\(\)]|\([^\(\)]*\))*\)))*\))|(?<==)\>)*(?<!=)\>))*(?<!=)\>)*(?<!=)>\s*)`)""";

    private const string LocalVariableDeclaration = """
        (?x)
        (?:
          (?:(\bref)\s+(?:(\breadonly)\s+)?)?(\bvar\b)| # ref local
          (?<type_name>
            (?:
              (?:ref\s+(?:readonly\s+)?)?   # ref local
              (?:
                (?:(?<identifier>@?[_[:alpha:]][_[:alnum:]]*)\s*\:\:\s*)? # alias-qualification
                (?<name_and_type_args> # identifier + type arguments (if any)
                  \g<identifier>\s*
                  (?<type_args>\s*<(?:[^<>]|\g<type_args>)+>\s*)?
                )
                (?:\s*\.\s*\g<name_and_type_args>)* | # Are there any more names being dotted into?
                (?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))
              )
              (?:\s*\*\s*)* # pointer suffix?
              (?:\s*\?\s*)? # nullable suffix?
              (?:\s* # array suffix?
                \[
                  (?:\s*,\s*)* # commata for multi-dimensional arrays
                \]
                \s*
                (?:\?)? # arrays can be nullable reference types
                \s*
              )*
            )
          )
        )\s+
        (\g<identifier>)\s*
        (?!=>)
        (?=,|;|=|\))
        """;

    [Fact]
    public void MemoMatchesThePlainBacktrackerAroundLooksAtomicsAndCalls()
    {
        // As MemoMatchesThePlainBacktracker, with look-arounds, atomic groups and calls of a group defined first.
        var random = new Random(20261107);
        var compared = 0;
        var withMemo = 0;
        for (var i = 0; i < 4000; i++)
        {
            var source = Definition(random) + Node(random, 3, regions: true);
            if (!OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out _) || pattern!.Program.MemoSlots is null)
            {
                continue;
            }

            withMemo++;
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

        Assert.True(withMemo > 1000, $"{withMemo} patterns with a memo");
        Assert.True(compared > 20000, $"{compared} searches");
    }

    // One pattern per rule that keeps a resume out of the memo: giving that resume a slot changes the result here.
    [Theory]
    // Inside a called group: the same body returns to different callers.
    [InlineData(@"(?<g>a|ab)?(?<h>b)?\g<g>$", "aa bab")]
    [InlineData(@"(?<g>a|ab)?(?<h>b)?\g<g>$", "aa\n\naaaab")]
    // Inside an atomic group or a possessive loop: its end drops alternatives that never failed.
    [InlineData(@"(b|aa)*+aa\s", "aa baa\n baab")]
    [InlineData(@"(?>.|[^;])*+\s", "bbabb ab")]
    [InlineData(@"((?>ab|a)++a)+?", "a\naaaaa\nab ")]
    // Inside a look-ahead: its end continues from where it was entered.
    [InlineData(@"(?!(?:.a)*)", "a;  aa a a ")]
    [InlineData(@"(?!(^a)*)", "aaab\nb")]
    // Inside a look-behind: its body ends at the position it was entered from.
    [InlineData(@"[ab]*(?<=(?:a|bb)(b))", "\nabb a \naa\n")]
    [InlineData(@"(?:b|ba)?(?<=(?:a|bb)(b))", " aabb ;b;\nb")]
    // A counted loop outside them: its count steers the resume (no memo at all).
    [InlineData(@"(?:.+a){2}", "a  a\naa;baab")]
    [InlineData(@"(?:(b|.a){0,2}a)+?", "bbb;aaa\n")]
    // A loop whose body can match empty: where the iteration started steers the resume (no memo at all).
    [InlineData(@"(?:ab|a;|ba|bb|b;|;a|;b| a| b|\s?|aa)*", "abb;abba \naa")]
    public void ResumesKeptOutOfTheMemoKeepThePlainResult(string source, string input)
    {
        var pattern = Pattern(source);
        var plain = pattern.CreateWorkspace();
        plain.Matcher.MemoAfter = long.MaxValue;
        var a = new RegexCapture[pattern.GroupCount];
        var b = new RegexCapture[pattern.GroupCount];
        // The memo starts at a backtrack: each threshold starts it at another point of the search.
        for (var after = 0; after <= 48; after++)
        {
            var memo = pattern.CreateWorkspace();
            memo.Matcher.MemoAfter = after;
            for (var start = 0; start <= input.Length; start++)
            {
                var expected = plain.FindCaptures(input, start, b, out _);
                var actual = memo.FindCaptures(input, start, a, out _);
                Assert.True(expected == actual, $"from {start}, memo after {after}: {actual}, expected {expected}");
                Assert.True(a.AsSpan().SequenceEqual(b), $"from {start}, memo after {after}");
            }
        }
    }

    [Fact]
    public void LongLinesStartTheMemoOnlyForSearchesThatEarnIt()
    {
        // The memo is cleared when a search starts it, and its size grows with the line: on a 40,000-unit line a search
        // that backtracks a few hundred units must not clear 200 KB first, while one that backtracks for long still
        // gets its memo and stays linear.
        var pattern = Pattern(LocalVariableDeclaration);
        const string statement = "service.Client.Load(id); int value = other.Thing.Get(x); ";
        var ordinary = string.Concat(Enumerable.Repeat(statement, 40_000 / statement.Length));
        var memoBytes = 8L * (((long)pattern.Program.MemoSlotCount * (ordinary.Length + 1) + 63) >> 6);
        var workspace = pattern.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.Match, workspace.Find(ordinary, 0, out var match));
        Assert.Equal(new RegexMatchRange(25, 10), match);
        Assert.True(workspace.LastWork > workspace.Matcher.MemoAfter, $"{workspace.LastWork} units");
        Assert.True(workspace.Matcher.ReservedBytes < memoBytes, $"{workspace.Matcher.ReservedBytes} bytes reserved, the memo takes {memoBytes}");

        var dotted = string.Join(".", Enumerable.Repeat("Identifier", ordinary.Length / 11)) + "(argument);";
        Assert.Equal(RegexSearchStatus.NoMatch, workspace.IsMatch(dotted));
        Assert.True(workspace.LastWork < 120L * dotted.Length, $"{workspace.LastWork} for {dotted.Length} units");
        Assert.True(workspace.Matcher.ReservedBytes >= memoBytes, $"{workspace.Matcher.ReservedBytes} bytes reserved, the memo takes {memoBytes}");
    }

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

    // regions: also look-arounds, atomic groups and calls of a group g the caller defines.
    private static string Node(Random random, int depth, bool regions = false)
    {
        if (depth == 0)
        {
            return regions && random.Next(4) == 0 ? @"\g<g>" : Atom(random);
        }

        switch (random.Next(regions ? 14 : 8))
        {
            case 0:
            case 1:
                return Node(random, depth - 1, regions) + Node(random, depth - 1, regions);
            case 2:
                return "(" + Node(random, depth - 1, regions) + "|" + Node(random, depth - 1, regions) + ")";
            case 3:
                return "(?:" + Node(random, depth - 1, regions) + "|" + Node(random, depth - 1, regions) + "|" + Node(random, depth - 1, regions) + ")";
            case 4:
                return "(" + Plain(random, depth - 1, regions) + ")" + (random.Next(3) switch { 0 => "*", 1 => "+", _ => "*?" });
            case 5:
                return "(?:" + Plain(random, depth - 1, regions) + ")" + (random.Next(2) == 0 ? "*" : "+?");
            case 6:
                return Atom(random) + (random.Next(4) switch { 0 => "*", 1 => "+", 2 => "?", _ => "*?" });
            case 7 when regions:
                return "(" + Plain(random, depth - 1, regions) + ")?";
            case 8:
                return (random.Next(2) == 0 ? "(?=" : "(?!") + Node(random, depth - 1, regions) + ")";
            case 9:
                // A captured alternation keeps a look-behind body (BehindStart); the others become set and literal tests.
                return (random.Next(2) == 0 ? "(?<=" : "(?<!") + (random.Next(4) switch { 0 => "a", 1 => "ab|b", 2 => "(a|ab)b", _ => @"\s" }) + ")";
            case 10:
                return "(?>" + Node(random, depth - 1, regions) + ")";
            case 11:
                return @"(?:\g<g>" + Plain(random, depth - 1, regions) + ")" + (random.Next(2) == 0 ? "*" : "?");
            case 12:
                // Loops whose count matters: they keep the memo only inside a call, look-around or atomic group.
                return "(?:" + Plain(random, depth - 1, regions) + ")" + (random.Next(2) == 0 ? "{0,2}" : "{2,}");
            case 13:
                return "(?:" + Node(random, depth - 1, regions) + "|)*";
            default:
                return Atom(random);
        }
    }

    // The group g that region patterns call: plain, recursive, defined inside a look-ahead, or only defined ({0}).
    private static string Definition(Random random) => random.Next(4) switch
    {
        0 => "(?<g>" + Plain(random, 1) + ")?",
        1 => @"(?<g>a\g<g>?b|" + Plain(random, 1) + ")?",
        2 => "(?=(?<g>" + Plain(random, 1) + "))",
        _ => "(?:(?<g>" + Plain(random, 1) + ")){0}",
    };

    // A body that cannot match empty.
    private static string Plain(Random random, int depth, bool regions = false)
    {
        var body = Node(random, depth, regions);
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
