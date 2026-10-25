using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// The PCRE2 interpreter is the oracle for the compiled PCRE2 backend.
public class Pcre2CompiledDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "[a-z]+(ing|ed)", "(wi|ri)n[a-z]*", "(a|b)*c", "ab(c|d)e", "a(b)*c", "(ab)+c", "(a)(b)(c)", ".b", "[^a]b", "(é|a)b",
        "(a?)(b?)c", "((a)|b)+c", "(a|ab)(c|bcd)(d*)", "x*abc", "[a-c]+d", "(ab){2}c", "a.*b", "(a+)(b+)?c", "(x)?(y)?z",
        "é+b", "(a*)b", "(a|aa)*b", "[a-z]+@[a-z]+\\.com", "\\d+x", "[^\\d\\s]+", "\\w+", "\\p{L}+\\d", "\\bfoo\\b", "\\Bo+",
        "(ab){1,3}c", "a{2,}b", "a?", "(|a)b", "a|b", "[0-9]{3}-[0-9]{4}", "\\s*foo\\s*", ".*", "x|y|z|ing", "(a|b)+", "a{0}b",
        "[[:alpha:]]+", "[[:^digit:]]+", "k", "\\x{212a}s", "(?i)k+s", "(?s).b", "(?m)^a", "(?m)c$", "^a.*c$", "^a", "c$", "c\\Z", "c\\z",
        "\\Aa", "a.b", "[a-z]+$", "(?-i)K", "(?i:a)b", "(a)|(b)|(c)", "(?|(a)|(b))c", "a+?b", "(a+?)(b*?)c", "(?:a|b)+?c", "a{2,3}?",
        "(?x) a b c", "[\\w-]+", "[^\\W\\d]+", "\\h+x", "\\H+", "(a)(?:b)(c)?", "((a)(b))+", "[\\x{100}-\\x{200}]+", "\\p{Lu}\\p{Ll}+",
        "(?<n>a)(?<m>b)", "a(?#comment)b", "(a+)+b", "(?=a)a", "(a)\\1", "a++b", "(?>a+)b", "a\\Kb", "\\Ga", "\\R", "\\C", "(*UTF)a",
        "a++a", "[ab]++b", "a*+a", "\\w++\\d", "x*+y", "[a-c]++d", "^a|^b", "^a|b", "(?s).*c", "(^a)|(^b)", "^(a|b)+c",
        "(?=\\[)", "(?=[a-z]+ing)[a-z]+", "(?!the)[a-z]+", "(?<=the )[a-z]+", "(?<!\\w)[a-z]+", "(?=(a))a\\1", "(?=(a|ab))(a|ab)c",
        "(?!(a))(b)", "(?<=(ab))c", "(?<=a(b)|(c))d", "(?<=ab|c)d", "(?<=^|\\s)[a-z]+", "(?<=a|bc)d", "(?<=a+)e", "(?<=a{2,3})e",
        "(?=.*ed)[a-z]+", "a(?=b(?=c))", "a(?!b(?!c))", "(?<=(?<=a)b)c", "x(?=y)|z", "(?:(?=a)|b)c", "(?<=\\b)e", "(?=$)", "(?<=é)b",
        "(?<=😀)b", "(?<=[^a])b", "(?<=.)b", "(?=(?:a|b){2})[ab]+", "\\b(?=[a-z]{3}\\b)[a-z]+", "\\Ga", "\\G[a-z]+", "\\Ga|b", "(?:\\Ga|b)c",
        "\\G(a)|(b)", "a\\Gb", "(?=a\\G)a", "(*positive_lookahead:a)a", "(*nla:a)[a-z]", "(*plb:a)b", "(*nlb:a)b", "(?*a)b", "(?<*a)b",
        "(?<=a(?=b))b", "(?(?=a)ab|cd)",
    ];

    private static readonly Pcre2CompileFlags[] Modes =
    [
        Pcre2CompileFlags.None, Pcre2CompileFlags.Caseless, Pcre2CompileFlags.Multiline, Pcre2CompileFlags.Dotall,
        Pcre2CompileFlags.Utf, Pcre2CompileFlags.Utf | Pcre2CompileFlags.Caseless,
        Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.Caseless,
        Pcre2CompileFlags.Ucp, Pcre2CompileFlags.Multiline | Pcre2CompileFlags.Dotall | Pcre2CompileFlags.Caseless,
        Pcre2CompileFlags.DollarEndonly, Pcre2CompileFlags.Anchored, Pcre2CompileFlags.NoAutoPossess, Pcre2CompileFlags.Ungreedy, Pcre2CompileFlags.Extended,
    ];

    private static readonly string[] Alphabet =
        ["a", "b", "c", "d", "e", "x", "y", "z", "\n", "\r", "\t", "é", "😀", "ab", "abc", " ", "q", "K", "k", "\u212a", "\u017f", "S", "s", "1", "_", "@x.com", "A", "B", "ing", "foo", "123-4567", "\u0001", "Ā", "\u0101"];

    private static IEnumerable<string> Inputs()
    {
        var random = new Random(20261117);
        yield return "";
        yield return "winning";
        yield return "abc";
        yield return "abc\n";
        yield return "abc\n\n";
        yield return "a\nb\nc";
        for (var i = 0; i < 70; i++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 24);
            for (var p = 0; p < parts; p++)
            {
                builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            if (i % 12 == 0)
            {
                builder.Append('\uD800');
            }

            yield return builder.ToString();
        }
    }

    public static IEnumerable<TheoryDataRow<string, Pcre2CompileFlags>> Data()
    {
        foreach (var p in Patterns)
        {
            foreach (var flags in Modes)
            {
                yield return new TheoryDataRow<string, Pcre2CompileFlags>(p, flags);
            }
        }
    }

    private static RegexPattern Parse(string pattern, Pcre2CompileFlags flags)
        => RegexPattern.Parse(pattern, new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, flags), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));

    private static RegexPattern? TryCompile(RegexPattern interpreted)
    {
        try
        { return interpreted.Compile(); }
        catch (NotSupportedException) { return null; }
    }

    private static string Describe<T>(Func<T> call)
    {
        try
        { return call()!.ToString()!; }
        catch (Exception e) { return e.GetType().Name; }
    }

    private static void Compare(RegexPattern oracle, RegexPattern compiled)
    {
        var oracleSpace = oracle.CreateWorkspace();
        var space = compiled.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[compiled.GroupCount];
        foreach (var input in Inputs())
        {
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(Describe(() => oracleSpace.IsMatch(input, start)), Describe(() => space.IsMatch(input, start)));
                RegexMatchRange expectedRange = default, actualRange = default;
                Assert.Equal(Describe(() => oracleSpace.Find(input, start, out expectedRange)), Describe(() => space.Find(input, start, out actualRange)));
                Assert.Equal(expectedRange, actualRange);
                Array.Clear(expectedGroups);
                Array.Clear(actualGroups);
                expectedRange = default;
                actualRange = default;
                Assert.Equal(Describe(() => oracleSpace.FindCaptures(input, start, expectedGroups, out expectedRange)),
                    Describe(() => space.FindCaptures(input, start, actualGroups, out actualRange)));
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Data))]
    public void CompiledMatchesInterpreted(string pattern, Pcre2CompileFlags flags)
    {
        RegexPattern oracle;
        try
        { oracle = Parse(pattern, flags); }
        catch (RegexParseException) { return; }
        var compiled = TryCompile(oracle);
        if (compiled is null)
        {
            return;
        }

        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Compare(oracle, compiled);
    }

    [Fact]
    public void MostCorpusPatternsAreLowered()
    {
        int lowered = 0, total = 0;
        foreach (var args in Data())
        {
            RegexPattern oracle;
            try
            { oracle = Parse(args.Data.Item1, args.Data.Item2); }
            catch (RegexParseException) { continue; }
            total++;
            if (TryCompile(oracle) is not null)
            {
                lowered++;
            }
        }
        Assert.True(lowered * 10 >= total * 4, lowered + "/" + total + " lowered");
    }

    [Fact]
    public void CommonShapesAreLowered()
    {
        foreach (var pattern in new[] { "[a-z]+(ing|ed)", "(a|b)*c\\d", "[0-9]{3}-[0-9]{4}x|y", "(a|b)(c)d*e", "(?i)(foo|bar)+z", "\\bfoo\\b[a-z]", "^a.*c$" })
        {
            foreach (var flags in new[] { Pcre2CompileFlags.None, Pcre2CompileFlags.Utf, Pcre2CompileFlags.Multiline | Pcre2CompileFlags.Caseless })
            {
                var pat = Parse(pattern, flags);
                try
                { pat.Compile(); }
                catch (NotSupportedException e) { Assert.Fail(pattern + "/" + flags + ": " + e.Message); }
            }
        }
    }

    [Fact]
    public void LookaroundsAreLowered()
    {
        foreach (var pattern in new[] { "(?=\\[)", "(?=a)a", "(?<!a)b", "(?<=the )[a-z]+", "a(?!b(?!c))", "(?<=(ab))c", "(?<=ab|c)d", "\\Ga", "\\Ga|b" })
        {
            foreach (var flags in new[] { Pcre2CompileFlags.None, Pcre2CompileFlags.Caseless, Pcre2CompileFlags.Utf, Pcre2CompileFlags.Multiline })
            {
                var pat = Parse(pattern, flags);
                try
                { pat.Compile(); }
                catch (NotSupportedException e) { Assert.Fail(pattern + "/" + flags + ": " + e.Message); }
            }
        }
    }

    // Focused lookaround and \G semantics, checked against the interpreter and against known PCRE2 results.
    public static IEnumerable<TheoryDataRow<string, string, int, string?>> LookaroundCases()
    {
        yield return new("(?=(a))a", "xa", 0, "1:1|1:1");
        yield return new("(?!(a))(b)", "ab", 0, "1:1|-|1:1");
        yield return new("(?=(a|ab))(a|ab)c", "abc", 0, "0:3|0:1|0:2");
        yield return new("(?<=(ab))c", "abc", 0, "2:1|0:2");
        yield return new("(?<=(ab))c", "abc", 2, "2:1|0:2");
        yield return new("(?<=a(b)|(c))d", "abd cd", 0, "2:1|1:1|-");
        yield return new("(?<=a(b)|(c))d", "abd cd", 3, "5:1|-|4:1");
        yield return new("(?<=ab|c)d", "cd abd", 0, "1:1");
        yield return new("(?<=ab|c)d", "xd abd", 0, "5:1");
        yield return new("(?<=^|\\s)[a-z]+", " the cat", 0, "1:3");
        yield return new("(?<=the )[a-z]+", "the cat", 4, "4:3");
        yield return new("(?<!a)b", "ab b", 0, "3:1");
        yield return new("(?<=a)b", "b", 0, null);
        yield return new("a(?=b(?=c))", "abd abc", 0, "4:1");
        yield return new("a(?!b(?!c))", "abd abc", 0, "4:1");
        yield return new("(?<=(?<=a)b)c", "abc bc", 0, "2:1");
        yield return new("(?:(?=a)|b)c", "ac bc c", 0, "3:2");
        yield return new("(?=$)", "ab", 0, "2:0");
        yield return new("(?=\\[)", "x[y]", 2, null);
        yield return new("\\Ga", "ba", 0, null);
        yield return new("\\Ga", "ba", 1, "1:1");
        yield return new("\\Ga|b", "cab", 0, "2:1");
        yield return new("\\Ga|b", "ab", 0, "0:1");
        yield return new("(?:\\Ga|b)c", "acbc", 0, "0:2");
        yield return new("(?:\\Ga|b)c", "acbc", 1, "2:2");
        yield return new("a\\Gb", "ab", 0, null);
        yield return new("a\\Gb", "ab", 1, null);
        yield return new("(?=a\\G)a", "ba", 1, null);
        yield return new("(?=\\Ga)a", "ba", 1, "1:1");
    }

    [Theory]
    [MemberData(nameof(LookaroundCases))]
    public void LookaroundKernelsMatchKnownResults(string pattern, string input, int startAt, string? expected)
    {
        var oracle = Parse(pattern, Pcre2CompileFlags.None);
        var compiled = oracle.Compile();
        foreach (var pat in new[] { oracle, compiled })
        {
            var ws = pat.CreateWorkspace();
            var groups = new RegexCapture[pat.GroupCount];
            var status = ws.FindCaptures(input, startAt, groups, out var range);
            string? actual = null;
            if (status == RegexSearchStatus.Match)
            {
                actual = range.Index + ":" + range.Length;
                for (var g = 1; g < groups.Length; g++)
                {
                    actual += "|" + (groups[g].Participated ? groups[g].Index + ":" + groups[g].Length : "-");
                }
            }
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void UnmodelledConstructsAreRejected()
    {
        foreach (var pattern in new[] { "(a)\\1", "a++b", "(?>a+)b", "a\\Kb", "\\R", "(a|b)(?1)", "(?(1)a|b)", "(*SKIP)a", "(?*a)b", "(?<*a)b", "(?<=a+)b", "(?(?=a)ab|cd)" })
        {
            RegexPattern oracle;
            try
            { oracle = Parse(pattern, Pcre2CompileFlags.None); }
            catch (RegexParseException) { continue; }
            Assert.Throws<NotSupportedException>(() => oracle.Compile());
        }
        Assert.Throws<NotSupportedException>(() => Parse("a", Pcre2CompileFlags.Anchored).Compile());
        Assert.Throws<NotSupportedException>(() => RegexPattern.Parse("ab+c", new Pcre2Profile(Pcre2CodeUnitWidth.Eight), new RegexCompileOptions(RegexInputKind.Bytes, RegexExecutionMode.Interpreted)).Compile());
        Assert.Throws<NotSupportedException>(() => RegexPattern.Parse("a\\nb+c", new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, newline: Pcre2Newline.Crlf), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted)).Compile());
    }
}
