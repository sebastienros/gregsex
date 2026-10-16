using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Accelerated.Dfa;

// Interpreted mode is the oracle: Auto set-repeat / suffix-literal / required-tail plans and Thompson fallback must match.
public sealed class DfaDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "[a-z]+ing",
        "^start.*end$",
        "(a+b+|a+c+)d",
        "(alpha|beta|gamma|delta|epsilon|zeta|eta|theta)",
        "[a-z]{3,12}",
        "[0-9]+ms",
        // Non-fast-path NFA shapes that execute via Thompson NFA + StartScan prefilter:
        "(?:ab|ac)+d",
        "[a-c]+x[d-f]+",
    ];

    private static readonly string[] Corpus =
    [
        "",
        "running jumping swimming nothing",
        "start middle content end",
        "start middle content en",
        "aaaaabbbbbd and aaaaacccccd",
        "prefix delta middle theta suffix",
        "latency 12ms then 340ms",
        "abacabacd and abcxdff",
    ];

    public static IEnumerable<TheoryDataRow<string>> PatternData() => Patterns.Select(p => new TheoryDataRow<string>(p));

    private static RegexPattern Parse(string pattern, RegexInputKind kind, RegexExecutionMode mode, RegexMatchLimits? limits = null)
        => RegexPattern.Parse(pattern, new RegularCoreProfile(), new RegexCompileOptions(kind, mode, defaultMatchLimits: limits));

    [Theory]
    [MemberData(nameof(PatternData))]
    public void Utf16AutoMatchesInterpretedAcrossOperationsAndOffsets(string pattern)
    {
        var oracle = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Interpreted);
        var auto = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Auto);
        var oracleSpace = oracle.CreateWorkspace();
        var autoSpace = auto.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[auto.GroupCount];

        foreach (var input in Corpus)
        {
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(oracleSpace.IsMatch(input, start), autoSpace.IsMatch(input, start));
                Assert.Equal(oracleSpace.Find(input, start, out var expectedRange), autoSpace.Find(input, start, out var actualRange));
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(oracleSpace.FindCaptures(input, start, expectedGroups, out expectedRange),
                    autoSpace.FindCaptures(input, start, actualGroups, out actualRange));
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PatternData))]
    public void Utf8AutoMatchesInterpretedAcrossOperationsAndOffsets(string pattern)
    {
        var oracle = Parse(pattern, RegexInputKind.Utf8, RegexExecutionMode.Interpreted);
        var auto = Parse(pattern, RegexInputKind.Utf8, RegexExecutionMode.Auto);
        var oracleSpace = oracle.CreateWorkspace();
        var autoSpace = auto.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[auto.GroupCount];

        foreach (var text in Corpus)
        {
            var input = Encoding.UTF8.GetBytes(text);
            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                Assert.Equal(oracleSpace.IsMatch(input, start), autoSpace.IsMatch(input, start));
                Assert.Equal(oracleSpace.FindCaptures(input, start, expectedGroups, out var expectedRange),
                    autoSpace.FindCaptures(input, start, actualGroups, out var actualRange));
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Fact]
    public void InvalidInputAndCancellationPreserveExactDiagnostics()
    {
        var oracle = Parse("[a-z]+ing", RegexInputKind.Utf16, RegexExecutionMode.Interpreted).CreateWorkspace();
        var auto = Parse("[a-z]+ing", RegexInputKind.Utf16, RegexExecutionMode.Auto).CreateWorkspace();
        foreach (var bad in new[] { "running\uD800", "\uDC00testing", "walk\uD800ing" })
        {
            Assert.Equal(RegexSearchStatus.InvalidInput, oracle.IsMatch(bad));
            Assert.Equal(RegexSearchStatus.InvalidInput, auto.IsMatch(bad));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled,
            auto.IsMatch(new string('a', 32_768) + "ing", 0, new RegexMatchOptions { CancellationToken = cts.Token }));
    }
}
