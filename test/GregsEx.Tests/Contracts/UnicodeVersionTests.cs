using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Unicode;
using GregsEx.Flavors.XmlShared;
using Xunit;

namespace GregsEx.Tests.Contracts;

public class UnicodeVersionTests
{
    private const string SideticLetter = "\U00010940";
    private const string NewEmoji = "\U0001FAEA";

    [Fact]
    public void DeploymentProbeExercisesGeneratedAndOrdinaryProfiles() => UnicodeVersionSmoke.Run();

    [Fact]
    public void DefaultsAndPresetsRemainUnicode16()
    {
        Assert.Equal(UnicodeVersion.Unicode16_0, new EcmaScriptProfile().UnicodeVersion);
        Assert.Equal(UnicodeVersion.Unicode16_0, new XsdProfile().UnicodeVersion);
        Assert.Equal(UnicodeVersion.Unicode16_0, new XPathProfile().UnicodeVersion);
        Assert.Equal(UnicodeVersion.Unicode16_0, RegexProfiles.EcmaScript.UnicodeVersion);
        Assert.Equal(UnicodeVersion.Unicode16_0, RegexProfiles.Xsd.UnicodeVersion);
        Assert.Equal(UnicodeVersion.Unicode16_0, RegexProfiles.XPath.UnicodeVersion);
        Assert.Equal("Unicode-16.0.0+Emoji-16.0", RegexProfiles.EcmaScript.DataId);
        Assert.Equal("Unicode-16.0.0+XML-1.0-5e", RegexProfiles.Xsd.DataId);
        Assert.Equal("PCRE2-10.46-Unicode16.0.0-b2bd4254", RegexProfiles.Pcre2Utf16.DataId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1800)]
    [InlineData(-1)]
    public void UndefinedVersionsAreArgumentErrors(int value)
    {
        var version = (UnicodeVersion)value;
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptProfile(unicodeVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XsdProfile(version));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPathProfile(unicodeVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => EcmaScriptProfile.TryParseFlags("!", out _, out _, version));
        Assert.Throws<ArgumentOutOfRangeException>(() => XPathProfile.TryParseFlags("!", out _, out _, version));
        Assert.Throws<ArgumentOutOfRangeException>(() => XsdFunctions.IsValid("a", "a", unicodeVersion: version));
        Assert.Throws<ArgumentOutOfRangeException>(() => XPathFunctions.Matches("a", "a", unicodeVersion: version));
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0, false)]
    [InlineData(UnicodeVersion.Unicode17_0, true)]
    public void EcmaPropertiesEmojiIdentifiersAndNativeOffsetsUseTheSelectedVersion(UnicodeVersion version, bool added)
    {
        Assert.True(EcmaScriptProfile.TryParseFlags("v", out var profile, out _, version));
        Assert.Equal(version, profile!.UnicodeVersion);
        Assert.Equal(added ? "Unicode-17.0.0+Emoji-17.0" : "Unicode-16.0.0+Emoji-16.0", profile.DataId);
        var letters = RegexPattern.Parse(@"(\p{L})", profile);
        Assert.Same(profile, letters.Profile);
        using var workspace = letters.CreateWorkspace();
        Assert.Equal(added ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
            workspace.Find("1" + SideticLetter, 0, out var range));
        Assert.Equal(added ? new RegexMatchRange(1, 2) : default, range);
        Assert.Equal(!added, RegexPattern.Parse(@"\p{Cn}", profile).IsMatch(SideticLetter));
        Assert.Equal(added, RegexPattern.Parse(@"\p{RGI_Emoji}", profile).IsMatch(NewEmoji));
        Assert.Equal(added, RegexPattern.TryParse(@"(?<" + SideticLetter + ">a)", profile, out _, out _));
        Assert.Equal(added, RegexPattern.TryParse(@"\p{sc=Sidetic}", profile, out var script, out _));
        if (added)
        {
            Assert.True(script!.IsMatch(SideticLetter));
            Assert.False(script.IsMatch("a"));
            Assert.True(RegexPattern.Parse(@"\p{scx=Sidt}", profile).IsMatch(SideticLetter));
        }
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0, false)]
    [InlineData(UnicodeVersion.Unicode17_0, true)]
    public void XmlCategoriesUseTheSelectedVersionInBothRepresentations(UnicodeVersion version, bool added)
    {
        RegexProfile[] profiles = [new XsdProfile(version), new XPathProfile(unicodeVersion: version)];
        foreach (var profile in profiles)
        {
            Assert.Equal(added ? "Unicode-17.0.0+XML-1.0-5e" : "Unicode-16.0.0+XML-1.0-5e", profile.DataId);
            Assert.Equal(added, RegexPattern.Parse(@"\p{L}", profile).IsMatch(SideticLetter));
            Assert.Equal(!added, RegexPattern.Parse(@"\p{Cn}", profile).IsMatch(SideticLetter));
            var bytes = System.Text.Encoding.UTF8.GetBytes(SideticLetter);
            using var workspace = RegexPattern.Parse(@"(\p{L})", profile, new(RegexInputKind.Utf8)).CreateWorkspace();
            Assert.Equal(added ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
                workspace.Find(bytes, 0, out var range));
            Assert.Equal(added ? new RegexMatchRange(0, 4) : default, range);
        }
    }

    [Fact]
    public void XmlBlocksKeepVersionedUnknownBlockRules()
    {
        var oldXsd = RegexPattern.Parse(@"\p{IsSidetic}", new XsdProfile());
        var newXsd = RegexPattern.Parse(@"\p{IsSidetic}", new XsdProfile(UnicodeVersion.Unicode17_0));
        Assert.True(oldXsd.IsMatch("a"));
        Assert.False(newXsd.IsMatch("a"));
        Assert.True(newXsd.IsMatch(SideticLetter));
        Assert.False(RegexPattern.TryParse(@"\p{IsSidetic}", new XPathProfile(), out _, out _));
        Assert.True(XPathFunctions.Matches(SideticLetter, @"\p{IsSidetic}", unicodeVersion: UnicodeVersion.Unicode17_0));
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0, false)]
    [InlineData(UnicodeVersion.Unicode17_0, true)]
    public void CaseMappingsAreVersionedAcrossInterpretedAutoAndExplicitCompilation(UnicodeVersion version, bool added)
    {
        RegexProfile[] profiles =
        [
            new EcmaScriptProfile(EcmaScriptFlags.IgnoreCase, version),
            new EcmaScriptProfile(EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, version),
            new XPathProfile(XPathFlags.IgnoreCase, version)
        ];
        foreach (var profile in profiles)
        {
            const string source = "(\uA7CE|a)+x";
            var interpreted = RegexPattern.Parse(source, profile);
            Assert.Equal(added, interpreted.IsMatch("\uA7CFx"));
            Assert.Equal(added, RegexPattern.Parse(source, profile, new(executionMode: RegexExecutionMode.Auto)).IsMatch("\uA7CFx"));
            var compiled = interpreted.Compile();
            Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
            Assert.Same(profile, compiled.Profile);
            Assert.Equal(added, compiled.IsMatch("\uA7CFx"));
            Assert.True(compiled.IsMatch("ax"));
        }
    }

    [Fact]
    public void XmlHostFunctionsHonorExplicitVersionsWithoutChangingDefaults()
    {
        const UnicodeVersion version = UnicodeVersion.Unicode17_0;
        Assert.False(XsdFunctions.IsValid(SideticLetter, @"\p{L}"));
        Assert.True(XsdFunctions.IsValid(SideticLetter, @"\p{L}", unicodeVersion: version));
        Assert.False(XPathFunctions.Matches(SideticLetter, @"\p{L}"));
        Assert.True(XPathFunctions.Matches(SideticLetter, @"\p{L}", unicodeVersion: version));
        Assert.Equal(SideticLetter, XPathFunctions.Replace(SideticLetter, @"\p{L}", "x"));
        Assert.Equal("x", XPathFunctions.Replace(SideticLetter, @"\p{L}", "x", unicodeVersion: version));
        Assert.Equal(new[] { "1", "2" }, XPathFunctions.Tokenize("1" + SideticLetter + "2", @"\p{L}", unicodeVersion: version));
        var oldResult = XPathFunctions.AnalyzeString(SideticLetter, @"(\p{L})");
        Assert.Equal(XPathAnalyzeNodeKind.NonMatch, Assert.Single(oldResult.Nodes).Kind);
        var result = XPathFunctions.AnalyzeString(SideticLetter, @"(\p{L})", unicodeVersion: version);
        Assert.Equal(XPathAnalyzeNodeKind.Match, result.Nodes[0].Kind);
        Assert.Equal(new RegexMatchRange(0, 2), result.Nodes[0].Range);
        Assert.Contains(result.Nodes, node => node.Kind == XPathAnalyzeNodeKind.Group);
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0)]
    [InlineData(UnicodeVersion.Unicode17_0)]
    public void SelectedDatasetsRemainBudgetedAndIndependent(UnicodeVersion version)
    {
        var tiny = new CompileBudget(new(maxPlanBytes: 1));
        Assert.False(EcmaUnicodeData.TryCreate(version, ref tiny, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, diagnostic.Code);
        var memory = new CompileBudget(new(maxCompileMemoryBytes: 1));
        Assert.False(EcmaUnicodeData.TryCreate(version, ref memory, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, diagnostic.Code);
        var budget = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(version, ref budget, out var data, out _));
        var next = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(version, ref next, out var same, out _));
        Assert.Same(data, same);
        Assert.Equal(EcmaUnicodeData.ReferenceBytes, next.ReservedBytes);
        var xml = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(version, ref xml, out var xmlData, out _));
        Assert.Equal(XmlUnicodeData.GetRetainedBytes(version), xml.ReservedBytes);
        Assert.Equal(new XsdProfile(version).DataId, xmlData!.DataId);
    }

    [Fact]
    public void DifferentVersionsCanBeUsedConcurrently()
    {
        Parallel.For(0, 32, i =>
        {
            var added = (i & 1) != 0;
            var version = added ? UnicodeVersion.Unicode17_0 : UnicodeVersion.Unicode16_0;
            var ecma = RegexPattern.Parse(@"\p{L}+", new EcmaScriptProfile(EcmaScriptFlags.Unicode, version));
            var xpath = RegexPattern.Parse(@"\p{L}+", new XPathProfile(unicodeVersion: version));
            Assert.Equal(added, ecma.IsMatch(SideticLetter));
            Assert.Equal(added, xpath.IsMatch(SideticLetter));
        });
    }
}
