using System.Reflection;
using Xunit;

namespace GregsEx.Tests.Contracts;

public class ProfilePresetTests
{
    [Fact]
    public void PresetsAreCentralizedGetOnlyAndConcretelyTyped()
    {
        var properties = typeof(RegexProfiles).GetProperties(BindingFlags.Public | BindingFlags.Static);
        Assert.Equal(16, properties.Length);
        foreach (var property in properties)
        {
            Assert.Null(property.SetMethod);
            Assert.True(property.PropertyType.IsSealed);
            Assert.True(property.PropertyType.IsSubclassOf(typeof(RegexProfile)));
        }

        Type[] profileTypes = [typeof(RegexProfile), typeof(RegularCoreProfile), typeof(EcmaScriptProfile),
            typeof(XsdProfile), typeof(XPathProfile), typeof(PosixProfile), typeof(Pcre2Profile), typeof(OnigurumaProfile)];
        foreach (var type in profileTypes)
        {
            Assert.Empty(type.GetProperties(BindingFlags.Public | BindingFlags.Static));
        }
    }

    [Fact]
    public void OnigurumaPresetsDifferOnlyInGroupCapture()
    {
        Assert.Same(RegexProfiles.Oniguruma, RegexProfiles.Oniguruma);
        Assert.Equal(OnigurumaOptions.None, RegexProfiles.Oniguruma.Options);
        Assert.Equal(OnigurumaOptions.CaptureGroup, RegexProfiles.OnigurumaTextMate.Options);
        Assert.Equal(RegexProfiles.Oniguruma.Id, RegexProfiles.OnigurumaTextMate.Id);
    }

    [Fact]
    public void RegularCorePresetsHaveExplicitModesAndNoFlags()
    {
        VerifyPreset(() => RegexProfiles.RegularCoreUnicodeScalars, new RegularCoreProfile(), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.RegularCoreUnicodeScalars, new RegularCoreProfile(), RegexInputKind.Utf8);
        VerifyPreset(() => RegexProfiles.RegularCoreUtf16CodeUnits,
            new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.RegularCoreRawBytes,
            new RegularCoreProfile(RegularCoreMode.RawBytes), RegexInputKind.Bytes);
    }

    [Fact]
    public void EcmaScriptPresetsKeepLegacyUnicodeAndUnicodeSetsSeparate()
    {
        VerifyPreset(() => RegexProfiles.EcmaScript, new EcmaScriptProfile(), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.EcmaScriptUnicode,
            new EcmaScriptProfile(EcmaScriptFlags.Unicode), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.EcmaScriptUnicodeSets,
            new EcmaScriptProfile(EcmaScriptFlags.UnicodeSets), RegexInputKind.Utf16);

        Assert.True(RegexPattern.Parse("^.$", RegexProfiles.EcmaScript).IsMatch("\uD800"));
        Assert.False(RegexPattern.Parse("^.$", RegexProfiles.EcmaScript).IsMatch("\U0001F600"));
        Assert.True(RegexPattern.Parse("^.$", RegexProfiles.EcmaScriptUnicode).IsMatch("\U0001F600"));
        Assert.True(RegexPattern.Parse("^.$", RegexProfiles.EcmaScriptUnicodeSets).IsMatch("\U0001F600"));
    }

    [Fact]
    public void XmlPresetsUseDefaultVersionedPolicies()
    {
        VerifyPreset(() => RegexProfiles.Xsd, new XsdProfile(), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.Xsd, new XsdProfile(), RegexInputKind.Utf8);
        VerifyPreset(() => RegexProfiles.XPath, new XPathProfile(), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.XPath, new XPathProfile(), RegexInputKind.Utf8);
    }

    [Fact]
    public void PosixPresetsUseDistinctDialectsAndCLocale()
    {
        VerifyPreset(() => RegexProfiles.PosixBasic, new PosixProfile(), RegexInputKind.Bytes);
        VerifyPreset(() => RegexProfiles.PosixExtended,
            new PosixProfile(PosixDialect.Extended), RegexInputKind.Bytes);
        Assert.Same(PosixLocale.C, RegexProfiles.PosixBasic.Locale);
        Assert.Same(PosixLocale.C, RegexProfiles.PosixExtended.Locale);
    }

    [Fact]
    public void Pcre2PresetsKeepWidthUtfAndUcpIndependent()
    {
        VerifyPreset(() => RegexProfiles.Pcre2Raw8,
            new Pcre2Profile(Pcre2CodeUnitWidth.Eight), RegexInputKind.Bytes);
        VerifyPreset(() => RegexProfiles.Pcre2Utf8,
            new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf), RegexInputKind.Utf8);
        VerifyPreset(() => RegexProfiles.Pcre2Raw16, new Pcre2Profile(), RegexInputKind.Utf16);
        VerifyPreset(() => RegexProfiles.Pcre2Utf16,
            new Pcre2Profile(flags: Pcre2CompileFlags.Utf), RegexInputKind.Utf16);
    }

    [Fact]
    public void CustomConstructionDoesNotChangePresets()
    {
        var core = new RegularCoreProfile(flags: RegularCoreFlags.DotAll);
        var ecma = new EcmaScriptProfile(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase);
        var xpath = new XPathProfile(XPathFlags.IgnoreCase);
        var posix = new PosixProfile(PosixDialect.Extended, PosixCompileFlags.Newline);
        var pcre2 = new Pcre2Profile(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp);

        Assert.Equal(RegularCoreFlags.DotAll, core.Flags);
        Assert.Equal(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase, ecma.Flags);
        Assert.Equal(XPathFlags.IgnoreCase, xpath.Flags);
        Assert.Equal(PosixCompileFlags.Newline, posix.Flags);
        Assert.True(pcre2.Ucp);
        Assert.Equal(RegularCoreFlags.None, RegexProfiles.RegularCoreUnicodeScalars.Flags);
        Assert.Equal(EcmaScriptFlags.Unicode, RegexProfiles.EcmaScriptUnicode.Flags);
        Assert.Equal(XPathFlags.None, RegexProfiles.XPath.Flags);
        Assert.Equal(PosixCompileFlags.None, RegexProfiles.PosixExtended.Flags);
        Assert.False(RegexProfiles.Pcre2Utf16.Ucp);
    }

    private static void VerifyPreset(Func<RegexProfile> getPreset, RegexProfile expected, RegexInputKind inputKind)
    {
        var preset = getPreset();
        Assert.Same(preset, getPreset());
        Assert.NotSame(expected, preset);
        VerifyProperties(expected, preset);

        var pattern = RegexPattern.Parse("a", preset, new(inputKind));
        var constructedPattern = RegexPattern.Parse("a", expected, new(inputKind));
        VerifyProperties(constructedPattern.Profile, pattern.Profile);
        Assert.Equal(inputKind, pattern.InputKind);
        Assert.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
        var otherPattern = RegexPattern.Parse("b", preset, new(inputKind));
        VerifyProperties(constructedPattern.Profile, otherPattern.Profile);
        if (inputKind == RegexInputKind.Utf16)
        {
            Assert.True(pattern.IsMatch("a"));
            Assert.False(pattern.IsMatch("b"));
            Assert.True(otherPattern.IsMatch("b"));
        }
        else
        {
            Assert.True(pattern.IsMatch("a"u8));
            Assert.False(pattern.IsMatch("b"u8));
            Assert.True(otherPattern.IsMatch("b"u8));
        }
        VerifyProperties(expected, preset);
    }

    private static void VerifyProperties(RegexProfile expected, RegexProfile actual)
    {
        Assert.Equal(expected.GetType(), actual.GetType());
        foreach (var property in actual.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Null(property.SetMethod);
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
        }
    }
}
