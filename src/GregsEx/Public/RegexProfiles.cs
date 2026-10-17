#nullable enable
namespace GregsEx;

/// <summary>Shared immutable profiles with pinned versions and fixed configurations.</summary>
public static class RegexProfiles
{
    /// <summary>RegularCore v1 strict Unicode scalar profile with no flags.</summary>
    public static RegularCoreProfile RegularCoreUnicodeScalars { get; } = new();

    /// <summary>RegularCore v1 UTF-16 code-unit profile with no flags.</summary>
    public static RegularCoreProfile RegularCoreUtf16CodeUnits { get; } = new(RegularCoreMode.Utf16CodeUnits);

    /// <summary>RegularCore v1 raw-byte profile with no flags.</summary>
    public static RegularCoreProfile RegularCoreRawBytes { get; } = new(RegularCoreMode.RawBytes);

    /// <summary>ECMAScript 2025 legacy profile with no flags.</summary>
    public static EcmaScriptProfile EcmaScript { get; } = new();

    /// <summary>ECMAScript 2025 profile with only the u flag.</summary>
    public static EcmaScriptProfile EcmaScriptUnicode { get; } = new(EcmaScriptFlags.Unicode);

    /// <summary>ECMAScript 2025 profile with only the v flag.</summary>
    public static EcmaScriptProfile EcmaScriptUnicodeSets { get; } = new(EcmaScriptFlags.UnicodeSets);

    /// <summary>XSD 1.1 profile with Unicode 16.0.0 and XML 1.0 Fifth Edition rules.</summary>
    public static XsdProfile Xsd { get; } = new();

    /// <summary>F&amp;O 3.1 profile with no flags, Unicode 16.0.0 and XML 1.0 Fifth Edition rules.</summary>
    public static XPathProfile XPath { get; } = new();

    /// <summary>Issue 7/2018 C-byte BRE profile with no compilation flags.</summary>
    public static PosixProfile PosixBasic { get; } = new();

    /// <summary>Issue 7/2018 C-byte ERE profile with no compilation flags.</summary>
    public static PosixProfile PosixExtended { get; } = new(PosixDialect.Extended);

    /// <summary>PCRE2 10.46 raw 8-bit profile with default options and no flags.</summary>
    public static Pcre2Profile Pcre2Raw8 { get; } = new(Pcre2CodeUnitWidth.Eight);

    /// <summary>PCRE2 10.46 UTF-8 profile with default options and only UTF enabled, not UCP.</summary>
    public static Pcre2Profile Pcre2Utf8 { get; } = new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf);

    /// <summary>PCRE2 10.46 raw 16-bit profile with default options and no flags.</summary>
    public static Pcre2Profile Pcre2Raw16 { get; } = new();

    /// <summary>PCRE2 10.46 UTF-16 profile with default options and only UTF enabled, not UCP.</summary>
    public static Pcre2Profile Pcre2Utf16 { get; } = new(flags: Pcre2CompileFlags.Utf);
}
