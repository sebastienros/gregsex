using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Accelerated.Literals;

// Interpreted mode is the oracle: Auto (candidate-start skipping) must reach the identical result.
public class LiteralPrefixDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "abc", "ab(c|d)e", "a(b)*c", "(ab)+c", "ab[a-c]+", "abc$", "ab.", "abab(ab)?", "é[a-c]", "😀b",
        "(a)(b)(c)", "abc|abd", "x*abc", "^ab", "a?bc", "ab*", "(?:ab){2}c", "a", "needle",
        "[a-c]+d", "(a|b|x)c", "[a-c]x|e", "a|b", "[^a]b", ".b", "[a-e]*", "(é|a)b", "[a-b]{2}c",
    ];

    private static readonly string[] Alphabet = ["a", "b", "c", "d", "e", "x", "\n", "é", "😀", "ab", "abc"];

    private static RegexPattern Parse(string pattern, RegexInputKind kind, RegexExecutionMode mode, RegexMatchLimits? limits = null)
        => RegexPattern.Parse(pattern, new RegularCoreProfile(), new RegexCompileOptions(kind, mode, defaultMatchLimits: limits));

    private static IEnumerable<string> Inputs()
    {
        var random = new Random(20261014);
        yield return "";
        yield return "needle in a haystack needle";
        for (var i = 0; i < 80; i++)
        {
            var builder = new StringBuilder();
            var parts = random.Next(0, 24);
            for (var p = 0; p < parts; p++)
            {
                builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            yield return builder.ToString();
        }
    }

    public static IEnumerable<TheoryDataRow<string>> PatternData() => Patterns.Select(p => new TheoryDataRow<string>(p));

    [Theory]
    [MemberData(nameof(PatternData))]
    public void Utf16AutoMatchesInterpretedForEveryOperationAndStart(string pattern)
    {
        var oracle = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Interpreted);
        var auto = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Auto);
        var oracleSpace = oracle.CreateWorkspace();
        var autoSpace = auto.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[auto.GroupCount];
        foreach (var input in Inputs())
        {
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(oracleSpace.IsMatch(input, start), autoSpace.IsMatch(input, start));
                var expected = oracleSpace.Find(input, start, out var expectedRange);
                var actual = autoSpace.Find(input, start, out var actualRange);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedRange, actualRange);
                expected = oracleSpace.FindCaptures(input, start, expectedGroups, out expectedRange);
                actual = autoSpace.FindCaptures(input, start, actualGroups, out actualRange);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PatternData))]
    public void Utf8AutoMatchesInterpretedForEveryOperationAndStart(string pattern)
    {
        var oracle = Parse(pattern, RegexInputKind.Utf8, RegexExecutionMode.Interpreted);
        var auto = Parse(pattern, RegexInputKind.Utf8, RegexExecutionMode.Auto);
        var oracleSpace = oracle.CreateWorkspace();
        var autoSpace = auto.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[auto.GroupCount];
        foreach (var text in Inputs())
        {
            var input = Encoding.UTF8.GetBytes(text);
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(oracleSpace.IsMatch(input, start), autoSpace.IsMatch(input, start));
                var expected = oracleSpace.FindCaptures(input, start, expectedGroups, out var expectedRange);
                var actual = autoSpace.FindCaptures(input, start, actualGroups, out var actualRange);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Fact]
    public void InvalidInputIsReportedExactlyLikeTheOracleEvenWhenNoCandidateExists()
    {
        var oracle = Parse("abc", RegexInputKind.Utf16, RegexExecutionMode.Interpreted).CreateWorkspace();
        var auto = Parse("abc", RegexInputKind.Utf16, RegexExecutionMode.Auto).CreateWorkspace();
        foreach (var input in new[] { "xxx\uD800", "abc\uDC00", "\uD800abc", "zzzzabc\uD800" })
        {
            Assert.Equal(RegexSearchStatus.InvalidInput, oracle.IsMatch(input));
            Assert.Equal(RegexSearchStatus.InvalidInput, auto.IsMatch(input));
        }
        var pair = "x\U0001F600abc";
        Assert.Equal(oracle.IsMatch(pair, 2), auto.IsMatch(pair, 2));
        Assert.Equal(RegexSearchStatus.InvalidInput, auto.IsMatch(pair, 2));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(300)]
    [InlineData(2000)]
    [InlineData(5000)]
    public void AutoNeverFailsALimitTheOracleSurvivesAndNeverTurnsALimitIntoNoMatch(long maxWork)
    {
        var limits = new RegexMatchLimits(maxWork);
        var input = string.Concat(Enumerable.Repeat("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", 40)) + "needle";
        var oracle = Parse("needle", RegexInputKind.Utf16, RegexExecutionMode.Interpreted, limits).CreateWorkspace();
        var auto = Parse("needle", RegexInputKind.Utf16, RegexExecutionMode.Auto, limits).CreateWorkspace();
        var expected = oracle.Find(input, 0, out var expectedRange);
        var actual = auto.Find(input, 0, out var actualRange);
        if (expected == RegexSearchStatus.Match)
        {
            Assert.Equal(RegexSearchStatus.Match, actual);
            Assert.Equal(expectedRange, actualRange);
        }
        else
        {
            Assert.Equal(RegexSearchStatus.LimitExceeded, expected);
            Assert.True(actual is RegexSearchStatus.LimitExceeded or RegexSearchStatus.Match, actual.ToString());
            if (actual == RegexSearchStatus.Match)
            {
                Assert.Equal(new RegexMatchRange(input.Length - 6, 6), actualRange);
            }
        }
    }

    [Fact]
    public void CancellationIsPolledAcrossLongScans()
    {
        var auto = Parse("needle", RegexInputKind.Utf16, RegexExecutionMode.Auto).CreateWorkspace();
        var input = new string('x', 100_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled,
            auto.IsMatch(input, 0, new RegexMatchOptions { CancellationToken = cts.Token }));
    }

    [Fact]
    public void LongInputsAcrossChunkBoundariesFindEveryOccurrence()
    {
        var oracle = Parse("abcdefg", RegexInputKind.Utf16, RegexExecutionMode.Interpreted,
            new RegexMatchLimits(100_000_000)).CreateWorkspace();
        var auto = Parse("abcdefg", RegexInputKind.Utf16, RegexExecutionMode.Auto,
            new RegexMatchLimits(100_000_000)).CreateWorkspace();
        for (var gap = 1018; gap <= 1030; gap++)
        {
            var input = new string('z', gap) + "abcdefg" + new string('z', gap) + "abcdefg";
            for (var start = 0; start <= input.Length; start += 511)
            {
                Assert.Equal(oracle.Find(input, start, out var expected), auto.Find(input, start, out var actual));
                Assert.Equal(expected, actual);
            }
        }
    }
}
