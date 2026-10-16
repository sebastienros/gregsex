using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// Interpreted mode is the oracle: the compiled backend must reach the identical result for every operation.
public class CompiledDifferentialTests
{
    // Patterns whose programs have no hand-written accelerated path, so Compile() lowers them.
    private static readonly string[] Patterns =
    [
        "(the|and|of) ([a-z]+)", "[a-z]+(ing|ed)", "(ERROR|WARN) ([a-z]+)", "(wi|ri)n[a-z]*", "t[a-z]*e", "(a|b)*c",
        "ab(c|d)e", "a(b)*c", "(ab)+c", "(a)(b)(c)", ".b", "[^a]b", "(é|a)b", "(a?)(b?)c", "((a)|b)+c",
        "(a|ab)(c|bcd)(d*)", "x*abc", "[a-c]+d", "(?:ab){2}c", "a.*b", "^a.*c$", "^[a-z ]+$", "(a+)(b+)?c",
        "(a|b)+?c", "[a-c]*?d", "(x)?(y)?z", "(?:(a)|(b))+", "é+b", "😀b", ".*", "a?", "(|a)b", "[^b]+b",
        "([a-c])\\1?", "(a*)b", "(a|aa)*b",
    ];

    private static readonly string[] Alphabet =
        ["a", "b", "c", "d", "e", "x", "y", "z", "\n", "é", "😀", "ab", "abc", "the ", "and ", "of ", "ing", "ed ", "ERROR ", "WARN ", "win", "rin", " ", "q"];

    private static IEnumerable<string> Inputs(bool lone)
    {
        var random = new Random(20261114);
        yield return "";
        yield return "the cat sat winning and running ERROR failed of tasks";
        for (var i = 0; i < 60; i++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 26);
            for (var p = 0; p < parts; p++)
            {
                builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            if (lone && i % 12 == 0)
            {
                builder.Append('\uD800');
            }

            yield return builder.ToString();
        }
    }

    public static IEnumerable<TheoryDataRow<string, RegularCoreMode, RegularCoreFlags>> Data()
    {
        foreach (var p in Patterns)
        {
            foreach (var mode in new[] { RegularCoreMode.UnicodeScalars, RegularCoreMode.Utf16CodeUnits })
            {
                foreach (var flags in new[] { RegularCoreFlags.None, RegularCoreFlags.DotAll })
                {
                    yield return new TheoryDataRow<string, RegularCoreMode, RegularCoreFlags>(p, mode, flags);
                }
            }
        }
    }

    private static RegexPattern Parse(string pattern, RegularCoreMode mode, RegularCoreFlags flags, RegexExecutionMode execution, RegexMatchLimits? limits = null)
        => RegexPattern.Parse(pattern, new RegularCoreProfile(mode, flags),
            new RegexCompileOptions(RegexInputKind.Utf16, execution, defaultMatchLimits: limits));

    private static RegexPattern? TryCompile(RegexPattern interpreted)
    {
        try
        { return interpreted.Compile(); }
        catch (NotSupportedException) { return null; }
    }

    [Theory]
    [MemberData(nameof(Data))]
    public void CompiledMatchesInterpretedForEveryOperationAndStart(string pattern, RegularCoreMode mode, RegularCoreFlags flags)
    {
        RegexPattern oracle;
        try
        { oracle = Parse(pattern, mode, flags, RegexExecutionMode.Interpreted); }
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
        foreach (var input in Inputs(lone: true))
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

    [Fact]
    public void MostCorpusPatternsAreLowered()
    {
        var lowered = 0;
        var skipped = new List<string>();
        foreach (var pattern in Patterns)
        {
            RegexPattern oracle;
            try
            { oracle = Parse(pattern, RegularCoreMode.UnicodeScalars, RegularCoreFlags.None, RegexExecutionMode.Interpreted); }
            catch (RegexParseException) { skipped.Add(pattern + " (parse)"); continue; }
            if (TryCompile(oracle) is not null)
            {
                lowered++;
            }
            else
            {
                skipped.Add(pattern);
            }
        }
        Assert.True(lowered >= Patterns.Length / 2, "Only " + lowered + " lowered; skipped: " + string.Join(", ", skipped));
    }
}
