using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Accelerated.OnePass;

// Interpreted mode is the oracle: Auto one-pass / atomic-sequence and bounded fallback paths must reach the identical result.
public sealed class OnePassDifferentialTests
{
    private static readonly string[] Patterns =
    [
        "^([a-z0-9_-]{3,24}):([0-9]{1,5})/([a-z0-9./_-]{1,64})$",
        "([a-zA-Z_][a-zA-Z0-9_]*)=([a-zA-Z0-9_./:-]+)",
        "([0-9]{4})-([0-9]{2})-([0-9]{2}) (INFO|WARN|ERROR) ([a-z_]+)=([0-9]+)",
        "^[a-z]+:[0-9]+$",
        "([a-z]+)=([0-9]+)",
        "^([a-z]{2,6})-([0-9]{2,4})$",
        // Ineligible / ambiguous shapes that must fall back cleanly to the reference interpreter:
        "(a|ab)(b|bc)",
        "((a+)+)b",
        "(a*)a+",
    ];

    private static readonly string[] Corpus =
    [
        "",
        "svc_auth-01:8080/api/v1/health",
        "svc_auth-01:8080/api/v1/health!",
        "ab:80/ok",
        "host=db01.internal port=5432 user=admin_1 timeout=30",
        "2026-10-14 INFO request_ms=42 2026-10-15 ERROR retry_count=3",
        "alpha:123",
        "alpha:123x",
        "foo=123 bar=456 baz=789",
        "abc-12",
        "abbbc",
        "aaaaab",
        "aaaaa",
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
    public void InvalidInputAndTightBudgetsPreserveExactDiagnostics()
    {
        const string pattern = "([a-zA-Z_][a-zA-Z0-9_]*)=([a-zA-Z0-9_./:-]+)";
        var oracle = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Interpreted).CreateWorkspace();
        var auto = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Auto).CreateWorkspace();
        foreach (var bad in new[] { "key=val\uD800", "\uDC00key=val", "x\uD800y=1" })
        {
            Assert.Equal(RegexSearchStatus.InvalidInput, oracle.IsMatch(bad));
            Assert.Equal(RegexSearchStatus.InvalidInput, auto.IsMatch(bad));
        }

        var tightAuto = Parse(pattern, RegexInputKind.Utf16, RegexExecutionMode.Auto, new RegexMatchLimits(16)).CreateWorkspace();
        var longInput = new string(' ', 4096) + "key=value";
        var status = tightAuto.Find(longInput, 0, out _);
        Assert.Equal(RegexSearchStatus.LimitExceeded, status);
    }
}
