using GregsEx.Tests.Contracts;
using Xunit;

namespace GregsEx.Tests.Integration.Core;

// Hand-authored RegularCore-1 contract witnesses. BCL is used only in the explicitly
// common ASCII family below; no ambient Unicode or flavor compatibility is inferred.
public class CoreBehaviorTests
{
    public static TheoryData<string, string, int, int> Matches => new()
    {
        { "", "", 0, 0 }, { "a", "ba", 1, 1 }, { "a|aa", "aa", 0, 1 },
        { "aa|a", "aa", 0, 2 }, { "a*", "aaa", 0, 3 }, { "a*?", "aaa", 0, 0 },
        { "a+?", "aaa", 0, 1 }, { "a{2,3}", "aaaa", 0, 3 }, { "a{2,3}?", "aaaa", 0, 2 },
        { "a{2,}", "aaaa", 0, 4 }, { "a?", "x", 0, 0 }, { "(?:ab)+", "xabab", 1, 4 },
        { "[a-c]+", "xabc", 1, 3 }, { "[^a-c]+", "abcxyz", 3, 3 },
        { "[-a]+", "--a", 0, 3 }, { "[a-]+", "a--", 0, 3 },
        { "[\\[\\]]+", "x[]", 1, 2 }, { "\\d+", "x12", 1, 2 },
        { "\\w+", "!Ab_12", 1, 5 }, { "\\s+", "x\t\n\r ", 1, 4 },
        { "\\D+", "12ab", 2, 2 }, { "\\W+", "ab!?", 2, 2 }, { "\\S+", " ab", 1, 2 },
        { "^a$", "a", 0, 1 }, { "a$", "ba", 1, 1 }, { "\\x61\\u0062", "xab", 1, 2 },
        { "\\u{1F600}", "x😀", 1, 2 }, { "[😀-🙏]", "x😁", 1, 2 },
        { "(a?)*", "a", 0, 1 }, { "(a?)+", "a", 0, 1 }, { "(a|)b", "b", 0, 1 },
        { "(ab|a)b", "ab", 0, 2 }, { "(a|aa)*b", "aaaab", 0, 5 },
        { "\\0", "x\0", 1, 1 }, { "\\.\\^\\$", "x.^$", 1, 3 }
    };

    [Theory, MemberData(nameof(Matches))]
    public void RealParserAndAllOperationsAgree(string source, string input, int index, int length)
    {
        var p = RegexPattern.Parse(source, new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        Assert.True(p.IsMatch(input));
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch(input));
        Assert.Equal(RegexSearchStatus.Match, w.Find(input, 0, out var range));
        Assert.Equal(new(index, length), range);
        var groups = new RegexCapture[p.GroupCount + 1];
        groups[^1] = new(true, 999, 123);
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures(input, 0, groups, out var captured));
        Assert.Equal(range, captured);
        Assert.Equal(new(true, index, length), groups[0]);
        Assert.Equal(new(true, 999, 123), groups[^1]);
        Assert.Equal(default, w.LastFailure);
    }

    [Theory]
    [InlineData("a", "bbb")]
    [InlineData("^a", "ba")]
    [InlineData("a$", "a\n")]
    [InlineData(".", "\n")]
    [InlineData("\\d", "١")]
    [InlineData("\\w", "é")]
    [InlineData("\\s", "\u00A0")]
    [InlineData("a{2}", "a")]
    [InlineData("[a-z]", "A")]
    public void MissIsDistinctFromFailure(string source, string input)
    {
        var p = RegexPattern.Parse(source, new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        var groups = new RegexCapture[p.GroupCount + 1];
        Array.Fill(groups, new(true, 4, 2));
        Assert.False(p.IsMatch(input));
        Assert.Equal(RegexSearchStatus.NoMatch, w.IsMatch(input));
        Assert.Equal(RegexSearchStatus.NoMatch, w.FindCaptures(input, 0, groups, out var range));
        Assert.Equal(default, range);
        Assert.All(groups[..p.GroupCount], g => Assert.Equal(default, g));
        Assert.Equal(new(true, 4, 2), groups[^1]);
        Assert.Equal(default, w.LastFailure);
    }

    [Theory]
    [InlineData("[", RegexDiagnosticCode.InvalidSyntax)]
    [InlineData("a**", RegexDiagnosticCode.InvalidSyntax)]
    [InlineData("\\q", RegexDiagnosticCode.InvalidSyntax)]
    [InlineData("\\1", RegexDiagnosticCode.UnsupportedFeature)]
    [InlineData("\\p{L}", RegexDiagnosticCode.UnsupportedFeature)]
    [InlineData("(?=a)", RegexDiagnosticCode.UnsupportedFeature)]
    [InlineData("a++", RegexDiagnosticCode.UnsupportedFeature)]
    [InlineData("\\uD800", RegexDiagnosticCode.InvalidPatternEncoding)]
    public void ExpectedDiagnosticsSurviveThrowingAndTryFactories(string source, RegexDiagnosticCode code)
    {
        Assert.False(RegexPattern.TryParse(source.AsSpan(), new RegularCoreProfile(), out var p, out var d));
        Assert.Null(p);
        Assert.Equal(code, d.Code);
        var e = Assert.Throws<RegexParseException>(() => RegexPattern.Parse(source, new RegularCoreProfile()));
        Assert.Equal(d, e.Diagnostic);
    }

    [Fact]
    public void FrozenInterchangeRunsThroughRealFactoriesAndSearches()
    {
        using var stream = typeof(CoreFixture).Assembly.GetManifestResourceStream("GregsEx.CoreFixtures.json")!;
        var manifest = CoreFixtureManifest.Read(stream);
        Assert.Equal(12, manifest.Cases.Length);
        foreach (var f in manifest.Cases)
        {
            var profile = new RegularCoreProfile(f.Mode, f.Flags);
            var options = new RegexCompileOptions(f.InputKind);
            bool ok;
            RegexPattern? p;
            RegexDiagnostic d;
            if (f.PatternUnit == RegexOffsetUnit.Bytes)
            {
                ok = RegexPattern.TryParse(f.Pattern.Select(v => (byte)v).ToArray(), profile, out p, out d, options);
            }
            else
            {
                ok = RegexPattern.TryParse(f.Pattern.Select(v => (char)v).ToArray(), profile, out p, out d, options);
            }

            if (f.Operation == "Parse")
            {
                Assert.Equal(f.ExpectedDiagnostic == RegexDiagnosticCode.None, ok);
                Assert.Equal(f.ExpectedDiagnostic, d.Code);
                Assert.Equal(f.ExpectedDiagnosticOffset, d.Offset);
                Assert.Equal(f.ExpectedDiagnosticLength, d.Length);
                Assert.Equal(f.PatternUnit, d.LocationUnit);
                continue;
            }
            Assert.True(ok, f.Id);
            using var w = p!.CreateWorkspace();
            var groups = new RegexCapture[f.DestinationLength ?? 0];
            RegexSearchStatus status;
            RegexMatchRange range = default;
            if (f.InputKind == RegexInputKind.Utf16)
            {
                var input = f.Input.Select(v => (char)v).ToArray();
                status = f.Operation switch
                {
                    "Boolean" => w.IsMatch(input, f.StartAt),
                    "Range" => w.Find(input, f.StartAt, out range),
                    _ => w.FindCaptures(input, f.StartAt, groups, out range)
                };
            }
            else
            {
                var input = f.Input.Select(v => (byte)v).ToArray();
                status = f.Operation switch
                {
                    "Boolean" => w.IsMatch(input, f.StartAt),
                    "Range" => w.Find(input, f.StartAt, out range),
                    _ => w.FindCaptures(input, f.StartAt, groups, out range)
                };
            }
            Assert.Equal(f.ExpectedStatus, status);
            Assert.Equal(f.ExpectedRange ?? default, range);
            if (f.ExpectedGroups is { } expected)
            {
                Assert.Equal(expected, groups);
            }

            Assert.Equal(f.ExpectedFailure ?? RegexExecutionFailureCode.None, w.LastFailure.Code);
            if (f.ExpectedFailureOffset is { } offset)
            {
                Assert.Equal(offset, w.LastFailure.Offset);
            }

            if (f.ExpectedRequiredCapacity is { } count)
            {
                Assert.Equal(count, w.LastFailure.RequiredCapacity);
            }
        }
    }

    [Fact]
    public void CapturesClearPerIterationAndPreserveUnsetVersusEmpty()
    {
        // First-visit closure prunes a zero-progress return to an already visited
        // repetition split. The preceding consuming iteration's snapshot wins for
        // star/plus; distinct finite copies still execute their final empty body.
        // The initial plus expectation (final empty) failed and is retained in the
        // report; these expectations follow the reviewed rule, not BCL semantics.
        foreach (var (source, input, expected) in new (string, string, RegexCapture[])[]
        {
            ("((a)|b)*", "ab", [new(true,0,2),new(true,1,1),default]),
            ("(a?)*", "a", [new(true,0,1),new(true,0,1)]),
            ("(a?)+", "a", [new(true,0,1),new(true,0,1)]),
            ("(a?)(b)?", "", [new(true,0,0),new(true,0,0),default]),
            ("(a?){2}", "a", [new(true,0,1),new(true,1,0)])
        })
        {
            var p = RegexPattern.Parse(source, new RegularCoreProfile());
            using var w = p.CreateWorkspace();
            var groups = new RegexCapture[p.GroupCount];
            Assert.Equal(RegexSearchStatus.Match, w.FindCaptures(input, 0, groups, out _));
            Assert.Equal(expected, groups);
        }
    }

    [Fact]
    public void PatternBorrowingBackendAndDotAllAreExplicit()
    {
        char[] source = ['a'];
        var profile = new RegularCoreProfile();
        Assert.True(RegexPattern.TryParse(source, profile, out var p, out var d, new(executionMode: RegexExecutionMode.Auto)));
        source[0] = 'z';
        Assert.True(p!.IsMatch("a"));
        Assert.False(p.IsMatch("z"));
        Assert.Same(profile, p.Profile);
        Assert.Equal(RegexExecutionBackend.Interpreted, p.ExecutionBackend);
        Assert.Equal(default, d);
        Assert.True(RegexPattern.Parse(".", new RegularCoreProfile(flags: RegularCoreFlags.DotAll)).IsMatch("\n"));
        Assert.True(RegexPattern.TryParse([255], new RegularCoreProfile(RegularCoreMode.RawBytes), out p, out _, new(RegexInputKind.Bytes)));
        Assert.True(p!.IsMatch([255]));
    }

    [Fact]
    public void FactoriesEnforceProfilePatternDomainAndMetadataLimits()
    {
        Assert.Throws<ArgumentNullException>(() => RegexPattern.Parse(null!, new RegularCoreProfile()));
        Assert.Throws<ArgumentNullException>(() => RegexPattern.Parse("a", null!));
        Assert.False(RegexPattern.TryParse("a".AsSpan(), new RegularCoreProfile(RegularCoreMode.RawBytes), out _, out var d));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, d.Code);
        Assert.False(RegexPattern.TryParse("a"u8, new RegularCoreProfile(), out _, out d));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, d.Code);
        Assert.Equal(RegexOffsetUnit.Bytes, d.LocationUnit);
        var p = RegexPattern.Parse("", new RegularCoreProfile());
        Assert.False(RegexPattern.TryParse("".AsSpan(), new RegularCoreProfile(), out _, out d,
            new(limits: new(maxPlanBytes: p.RetainedBytes - 1))));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Code);
        Assert.True(RegexPattern.TryParse("".AsSpan(), new RegularCoreProfile(), out _, out _,
            new(limits: new(maxPlanBytes: p.RetainedBytes))));
    }

    [Fact]
    public void DeliberateCommonAsciiSubsetAgreesWithBcl()
    {
        string[] sources = ["a+", "(ab|a)b?", "([a-c]+)([0-9]?)", "a{1,3}?", "b|ab", "^a+b$"];
        string[] inputs = ["", "a", "ab", "aab", "aaa", "abc12", "zabab", "cc2", "bb", "ba"];
        foreach (var source in sources)
        {
            var p = RegexPattern.Parse(source, new RegularCoreProfile());
            using var w = p.CreateWorkspace();
            var oracle = new System.Text.RegularExpressions.Regex(source);
            var captures = new RegexCapture[p.GroupCount];
            foreach (var input in inputs)
            {
                for (var start = 0; start <= input.Length; start++)
                {
                    var expected = oracle.Match(input, start);
                    var status = w.FindCaptures(input, start, captures, out var range);
                    Assert.Equal(expected.Success ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, status);
                    if (!expected.Success)
                    {
                        continue;
                    }

                    Assert.Equal(new(expected.Index, expected.Length), range);
                    for (var i = 0; i < captures.Length; i++)
                    { var g = expected.Groups[i]; Assert.Equal(g.Success ? new(true, g.Index, g.Length) : default, captures[i]); }
                }
            }
        }
    }
}
