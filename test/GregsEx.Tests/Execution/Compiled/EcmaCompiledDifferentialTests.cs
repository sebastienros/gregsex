using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// The ECMAScript interpreter is the oracle for the compiled ECMAScript backend.
public class EcmaCompiledDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "(the|and|of) ([a-z]+)", "[a-z]+(ing|ed)", "(ERROR|WARN) ([a-z]+)", "(wi|ri)n[a-z]*", "t[a-z]*e", "(a|b)*c",
        "ab(c|d)e", "a(b)*c", "(ab)+c", "(a)(b)(c)", ".b", "[^a]b", "(é|a)b", "(a?)(b?)c", "((a)|b)+c",
        "(a|ab)(c|bcd)(d*)", "x*abc", "[a-c]+d", "(?:ab){2}c", "a.*b", "^a.*c$", "^[a-z ]+$", "(a+)(b+)?c",
        "(a|b)+?c", "[a-c]*?d", "(x)?(y)?z", "(?:(a)|(b))+", "é+b", "😀b", ".*", "a?", "(|a)b", "[^b]+b",
        "(a*)b", "(a|aa)*b", "\\bwin\\w*", "\\Bn\\b", "^\\w+$", "(?:ab){1,3}c", "a{2,}b", "[\\s\\S]b", "\\d+x", "[^\\d\\s]+",
        "(a)|(b)", "k", "\\u212a", "[k-m]+x", "ſ+", "\\bk\\b", "(?:a|b){0,2}c", "a{0}b",
        "([a-z]+)@([a-z]+)\\.com", "(?:(?:a)*)*b", "(a*)*b", "(a|b*)*c", "(a)\\1", "(?=a)a", "(?<!a)b",
    ];

    private static readonly string[] Alphabet =
        ["a", "b", "c", "d", "e", "x", "y", "z", "\n", "\r", "\u2028", "é", "😀", "ab", "abc", "the ", "and ", "of ", "ing", "ed ", "ERROR ", "WARN ", "win", "rin", " ", "q", "K", "k", "\u212a", "\u017f", "S", "1", "_", "@x.com", "A", "B"];

    private static IEnumerable<string> Inputs()
    {
        var random = new Random(20261115);
        yield return "";
        yield return "the cat sat winning and running ERROR failed of tasks";
        for (var i = 0; i < 70; i++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 26);
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

    public static IEnumerable<TheoryDataRow<string, EcmaScriptFlags>> Data()
    {
        foreach (var p in Patterns)
        {
            foreach (var flags in new[]
            {
                EcmaScriptFlags.None, EcmaScriptFlags.IgnoreCase, EcmaScriptFlags.Multiline | EcmaScriptFlags.DotAll,
                EcmaScriptFlags.Unicode, EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase,
                EcmaScriptFlags.Unicode | EcmaScriptFlags.Multiline | EcmaScriptFlags.DotAll, EcmaScriptFlags.Sticky,
                EcmaScriptFlags.Sticky | EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase,
            })
            {
                yield return new TheoryDataRow<string, EcmaScriptFlags>(p, flags);
            }
        }
    }

    private static RegexPattern Parse(string pattern, EcmaScriptFlags flags, RegexMatchLimits? limits = null)
        => RegexPattern.Parse(pattern, new EcmaScriptProfile(flags),
            new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));

    private static RegexPattern? TryCompile(RegexPattern interpreted)
    {
        try
        { return interpreted.Compile(); }
        catch (NotSupportedException) { return null; }
    }

    [Theory]
    [MemberData(nameof(Data))]
    public void CompiledMatchesInterpretedForEveryOperationAndStart(string pattern, EcmaScriptFlags flags)
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
        var oracleSpace = oracle.CreateWorkspace();
        var space = compiled.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[compiled.GroupCount];
        foreach (var input in Inputs())
        {
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(oracleSpace.IsMatch(input, start), space.IsMatch(input, start));
                var expected = oracleSpace.Find(input, start, out var expectedRange);
                var actual = space.Find(input, start, out var actualRange);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedRange, actualRange);
                expected = oracleSpace.FindCaptures(input, start, expectedGroups, out expectedRange);
                actual = space.FindCaptures(input, start, actualGroups, out actualRange);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    public static IEnumerable<TheoryDataRow<string, EcmaScriptFlags>> CommonShapes()
    {
        foreach (var pattern in new[] { "[a-z]+(ing|ed)", "(wi|ri)n[a-z]*s", "\\b[a-z]+ing\\b", "(a|b)*c" })
        {
            foreach (var flags in new[] { EcmaScriptFlags.None, EcmaScriptFlags.IgnoreCase, EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase })
            {
                yield return new TheoryDataRow<string, EcmaScriptFlags>(pattern, flags);
            }
        }
    }

    [Theory]
    [MemberData(nameof(CommonShapes))]
    public void CommonShapesAreLowered(string pattern, EcmaScriptFlags flags) =>
        Assert.NotNull(TryCompile(Parse(pattern, flags)));

    [Fact]
    public void AcceleratedDirectPlansAreDeclined()
    {
        foreach (var pattern in new[] { "(the|and|of) ([a-z]+)", "([a-z]+)=([a-z0-9]+)", "foo", "foo|bar|baz", "(wi|ri)n[a-z]*" })
        {
            Assert.Null(TryCompile(Parse(pattern, EcmaScriptFlags.None)));
        }
    }

    [Fact]
    public void MostCorpusPatternsAreLowered()
    {
        int lowered = 0, total = 0;
        var skipped = new List<string>();
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
            else
            {
                skipped.Add(args.Data.Item1 + "/" + args.Data.Item2);
            }
        }
        Assert.True(lowered * 10 >= total * 7, lowered + "/" + total + " lowered; skipped: " + string.Join(", ", skipped));
    }

    [Fact]
    public void UnmodelledConstructsAreRejected()
    {
        foreach (var pattern in new[] { "(a)\\1", "(?=a)a", "(?<!a)b", "(a*)*b" })
        {
            Assert.Null(TryCompile(Parse(pattern, EcmaScriptFlags.None)));
        }
    }
}
