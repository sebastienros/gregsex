using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// The XSD and XPath interpreters are the oracle for the compiled XML backend.
public class XmlCompiledDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "[a-z]+(ing|ed)", "(wi|ri)n[a-z]*", "(a|b)*c", "ab(c|d)e", "a(b)*c", "(ab)+c", "(a)(b)(c)", ".b", "[^a]b", "(é|a)b",
        "(a?)(b?)c", "((a)|b)+c", "(a|ab)(c|bcd)(d*)", "x*abc", "[a-c]+d", "(ab){2}c", "a.*b", "(a+)(b+)?c",
        "(x)?(y)?z", "é+b", "(a*)b", "(a|aa)*b", "[a-z]+@[a-z]+\\.com", "\\d+x", "[^\\d\\s]+", "\\w+", "\\p{L}+\\d", "\\i\\c*",
        "(ab){1,3}c", "a{2,}b", "[a-c-[b]]+d", "a?", "(|a)b", "a|b", "[0-9]{3}-[0-9]{4}", "\\s*foo\\s*", ".*", "x|y|z|ing",
        "(a|b)+", "a{0}b", "[\\p{IsBasicLatin}]+", "k", "\\u212a", "(a)\\1", "(?:a)(b)", "^a.*c$", "^a", "c$", "a.b", "[a-z]+$",
    ];

    private static readonly string[] Alphabet =
        ["a", "b", "c", "d", "e", "x", "y", "z", "\n", "\r", "\t", "é", "😀", "ab", "abc", " ", "q", "K", "k", "\u212a", "\u017f", "S", "1", "_", "@x.com", "A", "B", "ing", "foo", "123-4567", "\u0001"];

    private static IEnumerable<string> Inputs()
    {
        var random = new Random(20261116);
        yield return "";
        yield return "winning";
        yield return "abc";
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

    public static IEnumerable<TheoryDataRow<string>> XsdData()
    {
        foreach (var p in Patterns)
        {
            yield return new TheoryDataRow<string>(p);
        }
    }

    public static IEnumerable<TheoryDataRow<string, XPathFlags>> XPathData()
    {
        foreach (var p in Patterns)
        {
            foreach (var flags in new[]
            {
                XPathFlags.None, XPathFlags.IgnoreCase, XPathFlags.Multiline, XPathFlags.DotAll,
                XPathFlags.Multiline | XPathFlags.DotAll | XPathFlags.IgnoreCase, XPathFlags.IgnoreWhitespace, XPathFlags.Quote,
            })
            {
                yield return new TheoryDataRow<string, XPathFlags>(p, flags);
            }
        }
    }

    private static RegexPattern Parse(string pattern, RegexProfile profile)
        => RegexPattern.Parse(pattern, profile, new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));

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

    private static void Compare(RegexPattern oracle, RegexPattern compiled, bool xsd)
    {
        var oracleSpace = oracle.CreateWorkspace();
        var space = compiled.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[compiled.GroupCount];
        foreach (var input in Inputs())
        {
            foreach (var start in xsd ? new[] { 0, input.Length } : [0, input.Length / 2, input.Length])
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
    [MemberData(nameof(XsdData))]
    public void XsdCompiledMatchesInterpreted(string pattern)
    {
        RegexPattern oracle;
        try
        { oracle = Parse(pattern, new XsdProfile()); }
        catch (RegexParseException) { return; }
        var compiled = TryCompile(oracle);
        if (compiled is null)
        {
            return;
        }

        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Compare(oracle, compiled, xsd: true);
    }

    [Theory]
    [MemberData(nameof(XPathData))]
    public void XPathCompiledMatchesInterpreted(string pattern, XPathFlags flags)
    {
        RegexPattern oracle;
        try
        { oracle = Parse(pattern, new XPathProfile(flags)); }
        catch (RegexParseException) { return; }
        var compiled = TryCompile(oracle);
        if (compiled is null)
        {
            return;
        }

        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Compare(oracle, compiled, xsd: false);
    }

    [Fact]
    public void CommonShapesAreLowered()
    {
        foreach (var pattern in new[] { "[a-z]+(ing|ed)", "(a|b)*c", "[0-9]{3}-[0-9]{4}", "(a)(b)(c)" })
        {
            Assert.NotNull(TryCompile(Parse(pattern, new XsdProfile())));
            Assert.NotNull(TryCompile(Parse(pattern, new XPathProfile())));
            Assert.NotNull(TryCompile(Parse(pattern, new XPathProfile(XPathFlags.IgnoreCase))));
        }
    }

    [Fact]
    public void MostCorpusPatternsAreLowered()
    {
        int lowered = 0, total = 0;
        var skipped = new List<string>();
        var cases = XPathData().Select(row => (Pattern: row.Data.Item1, Profile: (RegexProfile)new XPathProfile(row.Data.Item2), Suffix: "/" + row.Data.Item2))
            .Concat(XsdData().Select(row => (Pattern: row.Data, Profile: (RegexProfile)new XsdProfile(), Suffix: "/xsd")));
        foreach (var (pattern, profile, suffix) in cases)
        {
            RegexPattern oracle;
            try
            { oracle = Parse(pattern, profile); }
            catch (RegexParseException) { continue; }
            total++;
            if (TryCompile(oracle) is not null)
            {
                lowered++;
            }
            else
            {
                skipped.Add(pattern + suffix);
            }
        }
        Assert.True(lowered * 10 >= total * 7, lowered + "/" + total + " lowered; skipped: " + string.Join(", ", skipped));
    }

    [Fact]
    public void UnmodelledConstructsAreRejected()
    {
        Assert.Null(TryCompile(Parse("(a)\\1", new XPathProfile())));
        Assert.Null(TryCompile(Parse("(a*)*b", new XPathProfile())));
    }

    [Fact]
    public void InvalidAndAstralTextDelegatesToTheInterpreter()
    {
        var oracle = Parse("[a-z]+", new XPathProfile());
        var compiled = oracle.Compile();
        foreach (var text in new[] { "ab\u0001cd", "ab\uD800cd", "a😀b", "abc\uFFFEd" })
        {
            var expected = Describe(() => oracle.CreateWorkspace().IsMatch(text));
            Assert.Equal(expected, Describe(() => compiled.CreateWorkspace().IsMatch(text)));
        }
    }
}
