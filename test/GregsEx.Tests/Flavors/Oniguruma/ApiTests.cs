using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

public class ApiTests
{
    private static OnigurumaPattern Pattern(string source, OnigurumaProfile? profile = null)
        => OnigurumaPattern.Parse(source, profile ?? RegexProfiles.OnigurumaTextMate);

    private static (RegexSearchStatus Status, RegexCapture[] Groups) Find(string source, string input, int startAt = 0, OnigurumaProfile? profile = null)
    {
        var pattern = Pattern(source, profile);
        var groups = new RegexCapture[pattern.GroupCount];
        var status = pattern.CreateWorkspace().FindCaptures(input, startAt, groups, out _);
        return (status, groups);
    }

    [Fact]
    public void ProfilesCarryTheirIdentity()
    {
        Assert.Equal("Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1", RegexProfiles.Oniguruma.Id);
        Assert.Equal("Oniguruma-6.9.10-Unicode16.0.0-4ef89209", RegexProfiles.Oniguruma.DataId);
        Assert.Equal(OnigurumaOptions.CaptureGroup, RegexProfiles.OnigurumaTextMate.Options);
        Assert.Throws<ArgumentException>(() => new OnigurumaProfile(OnigurumaOptions.CaptureGroup | OnigurumaOptions.DontCaptureGroup));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OnigurumaProfile((OnigurumaOptions)64));
    }

    [Theory]
    [InlineData("(a", RegexDiagnosticCode.InvalidSyntax, 0)]
    [InlineData("a)", RegexDiagnosticCode.InvalidSyntax, 1)]
    [InlineData("*a", RegexDiagnosticCode.InvalidSyntax, 0)]
    [InlineData("[z-a]", RegexDiagnosticCode.InvalidSyntax, 1)]
    [InlineData("\\k<missing>", RegexDiagnosticCode.InvalidSyntax, 0)]
    [InlineData("\\p{Nope}", RegexDiagnosticCode.InvalidSyntax, 0)]
    [InlineData("(?<n>a|\\g<n>b)", RegexDiagnosticCode.InvalidSyntax, 0)]
    [InlineData("(?{code})", RegexDiagnosticCode.UnsupportedFeature, 0)]
    [InlineData("(?~|a|b)", RegexDiagnosticCode.UnsupportedFeature, 0)]
    public void DiagnosticsLocateTheProblem(string source, RegexDiagnosticCode code, int offset)
    {
        Assert.False(OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out var diagnostic));
        Assert.Null(pattern);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(offset, diagnostic.Offset);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.LocationUnit);
        Assert.Throws<RegexParseException>(() => OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate));
    }

    [Fact]
    public void LimitsAreDiagnosed()
    {
        var limits = new OnigurumaCompileOptions(new RegexCompileLimits(maxPatternUnits: 16, maxNestingDepth: 2, maxCaptureGroups: 2));
        Assert.False(OnigurumaPattern.TryParse("abcdefghijklmnopq", RegexProfiles.Oniguruma, out _, out var d, limits));
        Assert.Equal(RegexDiagnosticCode.PatternLimitExceeded, d.Code);
        Assert.False(OnigurumaPattern.TryParse("(?:(?:a))", RegexProfiles.Oniguruma, out _, out d, limits));
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, d.Code);
        Assert.False(OnigurumaPattern.TryParse("()()", RegexProfiles.Oniguruma, out _, out d, limits));
        Assert.Equal(RegexDiagnosticCode.CaptureLimitExceeded, d.Code);
    }

    [Fact]
    public void CapturesReportParticipation()
    {
        var (status, groups) = Find("(a)|(b)", "xb");
        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexCapture(true, 1, 1), groups[0]);
        Assert.False(groups[1].Participated);
        Assert.Equal(new RegexCapture(true, 1, 1), groups[2]);
    }

    [Fact]
    public void NamedGroupsAndCaptureOptions()
    {
        // ONIG_OPTION_NONE: with a named group, unnamed groups do not capture.
        var plain = Pattern("(a)(?<n>b)(c)", RegexProfiles.Oniguruma);
        Assert.Equal(2, plain.GroupCount);
        Assert.Equal("n", plain.GetGroupName(1));
        Assert.Equal([1], plain.GetGroupNumbers("n").ToArray());
        Assert.False(OnigurumaPattern.TryParse("(a)(?<n>b)\\1", RegexProfiles.Oniguruma, out _, out _));

        // ONIG_OPTION_CAPTURE_GROUP (TextMate): every group captures, numbered left to right.
        var textMate = Pattern("(a)(?<n>b)(c)");
        Assert.Equal(4, textMate.GroupCount);
        Assert.Equal([2], textMate.GetGroupNumbers("n").ToArray());

        var none = Pattern("(a)(b)", new OnigurumaProfile(OnigurumaOptions.DontCaptureGroup));
        Assert.Equal(1, none.GroupCount);
    }

    [Fact]
    public void StartOffsetDoesNotHideLookBehindOrMoveBufferAnchors()
    {
        Assert.Equal(RegexSearchStatus.Match, Find("(?<=a)b", "ab", 1).Status);
        Assert.Equal(RegexSearchStatus.NoMatch, Find("\\Ab", "ab", 1).Status);
        var (status, groups) = Find("\\Gb", "abb", 1);
        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(1, groups[0].Index);
        Assert.Equal(RegexSearchStatus.NoMatch, Find("\\Gb", "acb", 1).Status);
    }

    [Fact]
    public void LookBehindBodiesStopAtTheirPosition()
    {
        // As Oniguruma's right range: [\t ]*+ cannot take the space after "while".
        var (status, groups) = Find("(?<=(?:^while)[\\t ]*+)", "while x");
        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexCapture(true, 5, 0), groups[0]);
    }

    [Fact]
    public void LineAnchorsFollowOniguruma()
    {
        Assert.Equal(new RegexCapture(true, 1, 0), Find("$", "a\nb").Groups[0]);
        Assert.Equal(new RegexCapture(true, 2, 0), Find("^b", "a\nb").Groups[0] with { Length = 0 });
        Assert.Equal(RegexSearchStatus.NoMatch, Find("\\n^", "a\n").Status);
        Assert.Equal(new RegexCapture(true, 1, 0), Find("\\Z", "a\n").Groups[0]);
    }

    [Fact]
    public void UnicodeClassesUseOnigurumaData()
    {
        Assert.Equal(new RegexCapture(true, 0, 5), Find("\\w+", "héllo world").Groups[0]);
        Assert.Equal(new RegexCapture(true, 4, 3), Find("\\p{Greek}+", "abc αβγ").Groups[0]);
        Assert.Equal(new RegexCapture(true, 0, 3), Find("\\h+", "0aFg").Groups[0]);
        Assert.Equal(new RegexCapture(true, 0, 7), Find("(?i)straße", "STRASSE").Groups[0]);
        Assert.Equal(new RegexCapture(true, 0, 2), Find("\\X", "éx").Groups[0]);
    }

    [Fact]
    public void BacktrackingWorkIsBounded()
    {
        var pattern = OnigurumaPattern.Parse("(a+)+b", RegexProfiles.Oniguruma,
            new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: 10_000)));
        var workspace = pattern.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.LimitExceeded, workspace.IsMatch(new string('a', 30) + "c"));
        Assert.True(workspace.LastWork > 10_000);
        Assert.Throws<RegexExecutionException>(() => pattern.IsMatch(new string('a', 30) + "c"));
    }

    [Fact]
    public void ShortDestinationsAndOffsetsAreRejected()
    {
        var pattern = Pattern("(a)(b)");
        var workspace = pattern.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, workspace.FindCaptures("ab", 0, new RegexCapture[2], out var match));
        Assert.Equal(default, match);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Find("ab", 3, out _));
    }

    [Fact]
    public void WarmSearchesDoNotAllocate()
    {
        var pattern = Pattern("(?<![_$[:alnum:]])(?:(?<=\\.\\.\\.)|(?<!\\.))(export|declare)\\s+([_$[:alpha:]][_$[:alnum:]]*)(?=\\s*[(<])");
        var workspace = pattern.CreateWorkspace();
        var groups = new RegexCapture[pattern.GroupCount];
        const string input = "  export function foo(a, b) { return a + b; }  export declare bar<T>()";
        // Warm up over the same offsets, so the backtracking stacks reach the size these searches need.
        for (var i = 0; i < 100; i++)
        {
            workspace.FindCaptures(input, i % 20, groups, out _);
        }

        var matches = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            matches += workspace.FindCaptures(input, i % 20, groups, out _) == RegexSearchStatus.Match ? 1 : 0;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100, matches);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void StackedQuantifiersCompileInLinearTime()
    {
        // Nested greedy quantifiers reduce as Oniguruma reduces them; others nest counters, not copies of the body.
        var stars = Pattern("a" + new string('*', 60_000), RegexProfiles.Oniguruma);
        Assert.Equal(RegexSearchStatus.Match, stars.CreateWorkspace().IsMatch("aaa"));
        var ranges = string.Concat(Enumerable.Repeat("{1,2}", 1_000));
        Assert.False(OnigurumaPattern.TryParse("a" + ranges, RegexProfiles.Oniguruma, out _, out var d));
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, d.Code);
        var deep = new OnigurumaCompileOptions(new RegexCompileLimits(maxNestingDepth: 4096));
        var nested = OnigurumaPattern.Parse("a" + string.Concat(Enumerable.Repeat("{1,2}", 200)), RegexProfiles.Oniguruma, deep);
        Assert.Equal(RegexSearchStatus.Match, nested.CreateWorkspace().IsMatch("a"));
        // Deeper than a thread pool stack holds: compiled on a stack sized for the tree, not a stack overflow.
        var unlimited = new OnigurumaCompileOptions(new RegexCompileLimits(maxPatternUnits: 1 << 20, maxNestingDepth: 1_000_000, maxCaptureGroups: 1_000_000));
        var deeper = OnigurumaPattern.Parse("a" + string.Concat(Enumerable.Repeat("{1,2}", 4_000)), RegexProfiles.Oniguruma, unlimited);
        Assert.Equal(RegexSearchStatus.Match, deeper.CreateWorkspace().IsMatch("a"));
        Assert.False(OnigurumaPattern.TryParse(string.Concat(Enumerable.Repeat("(?:", 100_000)) + new string(')', 100_000), RegexProfiles.Oniguruma, out _, out d, unlimited));
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, d.Code);
    }

    [Fact]
    public void MultiCodePointFoldsExpandLinearly()
    {
        var pattern = Pattern("(?i)" + new string('s', 50));
        Assert.Equal(RegexSearchStatus.Match, pattern.CreateWorkspace().IsMatch(string.Concat(Enumerable.Repeat("\u00DF", 25))));
        Assert.Equal(RegexSearchStatus.Match, pattern.CreateWorkspace().IsMatch(new string('S', 50)));
    }
}
