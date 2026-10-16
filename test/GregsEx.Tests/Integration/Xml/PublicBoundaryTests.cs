using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Integration;
using Xunit;

namespace GregsEx.Tests.Integration.Xml;

public sealed class PublicBoundaryTests
{
    private static RegexProfile Profile(bool xpath) => xpath ? new XPathProfile() : new XsdProfile();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImmutablePatternNativeOffsetsAndIndependentWorkspaces(bool xpath)
    {
        var profile = Profile(xpath);
        var source = "(😀)(a+)".ToCharArray();
        Assert.True(RegexPattern.TryParse(source, profile, out var pattern, out _, new(RegexInputKind.Utf8, RegexExecutionMode.Auto)));
        Array.Fill(source, 'x');
        Assert.Equal(RegexExecutionBackend.Interpreted, pattern!.ExecutionBackend);
        Assert.Equal(profile is XsdProfile ? 1 : 3, pattern.GroupCount);
        Parallel.For(0, 20, i =>
        {
            using var w = pattern.CreateWorkspace();
            var groups = new RegexCapture[pattern.GroupCount + 1];
            groups[^1] = new(true, 99, 99);
            Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("😀aaa"u8, 0, groups, out var range));
            Assert.Equal(new(0, 7), range);
            Assert.Equal(new(true, 0, 7), groups[0]);
            Assert.Equal(new(true, 99, 99), groups[^1]);
            if (profile is XPathProfile)
            { Assert.Equal(new(true, 0, 4), groups[1]); Assert.Equal(new(true, 4, 3), groups[2]); }
            Assert.Equal(RegexSearchStatus.NoMatch, w.FindCaptures("z"u8, 0, groups, out _));
            Assert.All(groups.Take(pattern.GroupCount), g => Assert.Equal(default, g));
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionPrecedenceReuseAndTransaction(bool xpath)
    {
        var profile = Profile(xpath);
        var p = RegexPattern.Parse("(a)", profile);
        using var w = p.CreateWorkspace(new());
        var cancelled = new RegexMatchOptions { CancellationToken = new(true) };
        var shortGroups = new RegexCapture[p.GroupCount - 1];
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, w.FindCaptures("\0", 0, shortGroups, out _, cancelled));
        Assert.Equal(0, w.LastFailure.WorkConsumed);
        var groups = Enumerable.Repeat(new RegexCapture(true, 8, 9), p.GroupCount + 1).ToArray();
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures("\0", 0, groups, out _, cancelled));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        Assert.All(groups.Take(p.GroupCount), g => Assert.Equal(default, g));
        Assert.Equal(new(true, 8, 9), groups[^1]);
        w.Reserve(p.CaptureCapacity);
        var before = w.Capacity;
        Assert.Equal(RegexSearchStatus.Cancelled, w.FindCaptures("\0", 0, groups, out _, cancelled));
        Assert.Equal(RegexSearchStatus.InvalidInput, w.FindCaptures("a\0", 0, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, w.LastFailure.Code);
        Assert.Equal(1, w.LastFailure.Offset);
        Assert.Equal(RegexSearchStatus.InvalidInput, w.FindCaptures(['a', (char)0xD800], 0, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, w.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures("a", 0, groups, out _, new() { Limits = new(maxWork: 1) }));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, w.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("a", 0, groups, out _));
        Assert.Equal(default, w.LastFailure);
        Assert.Equal(before, w.Capacity);
        Assert.Throws<RegexExecutionException>(() => w.Reserve(new(int.MaxValue, int.MaxValue, int.MaxValue)));
        Assert.Equal(before, w.Capacity);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("a"));
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.IsMatch("a"));
        using var grow = p.CreateWorkspace(new(allowGrowth: true));
        Assert.Equal(RegexSearchStatus.Match, grow.FindCaptures("a", 0, groups, out _));
        Assert.Equal(p.CaptureCapacity, grow.Capacity);
    }
    [Fact]
    public void PriorNoGrowthCapacityFailureRetainsIdentityAtReservedMemoryCeiling()
    {
        RegexProfile[] profiles = [new RegularCoreProfile(), new EcmaScriptProfile()];
        foreach (var profile in profiles)
        {
            using var w = RegexPattern.Parse("a", profile).CreateWorkspace(new());
            Assert.Equal(RegexSearchStatus.LimitExceeded, w.IsMatch("a", options: new()
            { Limits = new(maxWorkspaceBytes: w.ReservedBytes) }));
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        }
    }
    [Fact]
    public void XsdStartArgumentWinsBeforeDestinationCapacityAndCancellation()
    {
        using var w = RegexPattern.Parse("a", new XsdProfile()).CreateWorkspace(new());
        Assert.Throws<ArgumentOutOfRangeException>(() => w.FindCaptures("a", 1, [], out _, new() { CancellationToken = new(true) }));
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, w.FindCaptures("a", 0, [], out _));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceBytesAndResolvedCeilingAdmittedBeforeInput(bool xpath)
    {
        var profile = Profile(xpath);
        var p = RegexPattern.Parse("a", profile);
        var need = RegexWorkspace.MetadataBytes + p.CaptureCapacity.RequiredBytes;
        var bounded = RegexPattern.Parse("a", profile, new(defaultMatchLimits: new(maxWorkspaceBytes: need - 1)));
        Assert.Throws<RegexExecutionException>(() => bounded.CreateWorkspace());
        using var w = bounded.CreateWorkspace(new(allowGrowth: true));
        var before = w.Capacity;
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.IsMatch("\0"));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, w.LastFailure.Code);
        Assert.Equal(before, w.Capacity);
        using var prepared = p.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.LimitExceeded, prepared.IsMatch("\0", options: new() { Limits = new(maxWorkspaceBytes: need - 1) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, prepared.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.Match, prepared.IsMatch("a"));
    }
    [Fact]
    public void PublicFrameCeilingMatchesBindingAndSpareBuffersDoNotRaiseIt()
    {
        var p = RegexPattern.Parse("(a|b)+", new XPathProfile());
        var budget = new CompileBudget(new());
        Assert.True(XPathBinding.TryCreate("(a|b)+", new(), RegexInputKind.Utf16, ref budget, out var b, out _));
        Assert.Equal(b!.RequiredCapacity(SearchOperation.Captures), p.CaptureCapacity);
        using var w = p.CreateWorkspace();
        var cap = p.CaptureCapacity;
        w.Reserve(new(cap.StateSlots + 100_000, cap.CaptureSlots + 100_000, cap.ScratchSlots + 100_000));
        var groups = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures(new string('a', 4200), 0, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("ab", 0, groups, out _));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublicCompileWrapperLimitsAreCumulative(bool xpath)
    {
        var profile = Profile(xpath);
        var p = RegexPattern.Parse("(a)+", profile);
        Assert.True(p.RetainedBytes > XmlUnicodeData.RetainedBytes + 320);
        Assert.False(RegexPattern.TryParse("(a)+".AsSpan(), profile, out var result, out var d,
            new(limits: new(maxPlanBytes: p.RetainedBytes - 1))));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Code);
        Assert.False(RegexPattern.TryParse("a".AsSpan(), profile, out result, out d,
            new(limits: new(maxCompileMemoryBytes: XmlUnicodeData.RetainedBytes + 319))));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, d.Code);
        Assert.False(RegexPattern.TryParse("a".AsSpan(), profile, out result, out d, new(limits: new(maxCompileWork: 1))));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Code);
    }
    [Theory]
    [InlineData("a\\x41", 1, 2)]
    [InlineData("[\\]", 0, 3)]
    [InlineData("😀\\1", 2, 2)]
    public void ExactXsdParserCoordinates(string source, int offset, int length)
    {
        Assert.False(RegexPattern.TryParse(source.AsSpan(), new XsdProfile(), out _, out var d));
        Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, offset, length, RegexOffsetUnit.Utf16CodeUnits), d);
    }
    [Fact]
    public void XPathOriginalBoundsFullValidationAndSurrogateBoundaries()
    {
        using var w = RegexPattern.Parse("^a", new XPathProfile()).CreateWorkspace();
        Assert.Equal(RegexSearchStatus.NoMatch, w.Find("ba", 1, out _));
        Assert.Equal(RegexSearchStatus.InvalidInput, w.IsMatch("a\0"));
        using var scalar = RegexPattern.Parse(".", new XPathProfile()).CreateWorkspace();
        Assert.Equal(RegexSearchStatus.InvalidInput, scalar.Find("😀", 1, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, scalar.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.InvalidInput, scalar.Find("😀\0", 1, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, scalar.LastFailure.Code);
        Assert.Equal(2, scalar.LastFailure.Offset);
        Assert.Equal(RegexSearchStatus.TimedOut, scalar.IsMatch("", options: new() { Timeout = TimeSpan.FromTicks(1) }));
    }
    [Fact]
    public void TypedFunctionsExactSurfaceAndApprovedEscapingCaptureTree()
    {
        Assert.True(XsdFunctions.IsValid("a:b", @"\i\c*"));
        Assert.False(XsdFunctions.IsValid("ba", "a"));
        Assert.False(XPathFunctions.Matches(null, "a*", "ssimxq"));
        Assert.True(XPathFunctions.Matches(null, "a*", "ssimx"));
        Assert.Equal("z2", XPathFunctions.Replace("z", "(z)", "$12"));
        Assert.Equal(new[] { "", "a", "" }, XPathFunctions.Tokenize(",a,", ","));
        Assert.Equal(new[] { "a", "b" }, XPathFunctions.Tokenize("  a\tb  "));
        var r = XPathFunctions.AnalyzeString("ab", "((a)|b)+");
        Assert.Equal(new[] { 2, 1 }, r.Nodes.Where(n => n.Kind == XPathAnalyzeNodeKind.Group).Select(n => n.GroupNumber));
        Assert.All(r.Nodes.Where(n => n.Kind == XPathAnalyzeNodeKind.Group), n => Assert.Equal(0, n.ParentIndex));
        Assert.Equal("ab", string.Concat(r.Nodes.Where(n => n.Kind == XPathAnalyzeNodeKind.Text).Select(n => r.Input.Substring(n.Range.Index, n.Range.Length))));
        Assert.Throws<ArgumentNullException>(() => XsdFunctions.IsValid(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => XPathFunctions.Matches("a", null!));
        Assert.Throws<RegexParseException>(() => XPathFunctions.Matches("a", "a", compile: new(RegexInputKind.Utf8)));
        Assert.Throws<RegexParseException>(() => XsdFunctions.IsValid("a", "a", new(RegexInputKind.Utf8)));
        var small = Assert.Throws<RegexExecutionException>(() => XsdFunctions.IsValid("\0", "a", match: new()
        { Limits = new(maxWorkspaceBytes: 1), CancellationToken = new(true) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, small.Failure.Code);
        Assert.Equal(RegexWorkspace.MetadataBytes + RegexPattern.Parse("a", new XsdProfile()).BooleanCapacity.RequiredBytes,
            small.Failure.RequiredCapacity);
        Assert.Equal(XPathErrorCode.FORX0001, Assert.Throws<XPathRegexException>(() => XPathFunctions.Replace("\0", "(", "$", "z")).Diagnostic.ErrorCode);
        Assert.Equal(XPathErrorCode.FORX0003, Assert.Throws<XPathRegexException>(() => XPathFunctions.Replace("", "a*", "$")).Diagnostic.ErrorCode);
        Assert.Throws<XPathHostException>(() => XPathFunctions.Replace("aaa", "a", "b", host: new(maxResults: 1)));
    }
}
