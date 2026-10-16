namespace GregsEx;

/// <summary>XSD 1.1 Appendix G descriptor. It does not implement a schema validator.</summary>
public sealed class XsdProfile : RegexProfile
{
    /// <summary>Recommendation dated 5 April 2012.</summary>
    public override string Id => "Xsd11";
    /// <summary>Frozen Unicode categories/blocks and XML character/name rules.</summary>
    public override string DataId => UnicodeVersion == UnicodeVersion.Unicode16_0
        ? "Unicode-16.0.0+XML-1.0-5e" : "Unicode-17.0.0+XML-1.0-5e";
    /// <summary>The immutable Unicode dataset selection; XML character/name rules remain unchanged.</summary>
    public UnicodeVersion UnicodeVersion { get; }
    /// <summary>Creates a descriptor using Unicode 16.0.0.</summary>
    public XsdProfile() : this(UnicodeVersion.Unicode16_0) { }
    /// <summary>Creates a descriptor using an explicit, supported Unicode dataset.</summary>
    public XsdProfile(UnicodeVersion unicodeVersion)
    {
        UnicodeVersionPolicy.Validate(unicodeVersion);
        UnicodeVersion = unicodeVersion;
    }
    /// <summary>Strict scalar UTF-16 and UTF-8 subjects; raw bytes are unsupported.</summary>
    public bool Supports(RegexInputKind kind) => kind is RegexInputKind.Utf16 or RegexInputKind.Utf8;
}

/// <summary>F&amp;O 3.1 flags, shared by XPath and XQuery.</summary>
[Flags]
public enum XPathFlags
{
    /// <summary>Case-sensitive default syntax.</summary>
    None = 0,
    /// <summary>Dot includes CR and LF (s).</summary>
    DotAll = 1,
    /// <summary>LF multiline anchors (m).</summary>
    Multiline = 2,
    /// <summary>Unicode lower/upper case variants (i).</summary>
    IgnoreCase = 4,
    /// <summary>Remove XML whitespace outside character classes (x).</summary>
    IgnoreWhitespace = 8,
    /// <summary>Literal pattern and replacement; m/s/x are ineffective (q).</summary>
    Quote = 16
}

/// <summary>F&amp;O 3.1 Recommendation descriptor; one profile serves both query languages.</summary>
public sealed class XPathProfile : RegexProfile
{
    /// <summary>Recommendation dated 21 March 2017.</summary>
    public override string Id => "XPath31";
    /// <summary>Frozen full default case mappings, categories, blocks and XML policy.</summary>
    public override string DataId => UnicodeVersion == UnicodeVersion.Unicode16_0
        ? "Unicode-16.0.0+XML-1.0-5e" : "Unicode-17.0.0+XML-1.0-5e";
    /// <summary>The immutable Unicode dataset selection; XML character/name rules remain unchanged.</summary>
    public UnicodeVersion UnicodeVersion { get; }
    /// <summary>Normalized flag presence; repetitions in a flag string are permitted.</summary>
    public XPathFlags Flags { get; }
    /// <summary>Creates a descriptor with validated enum bits.</summary>
    public XPathProfile(XPathFlags flags) : this(flags, UnicodeVersion.Unicode16_0) { }

    /// <summary>Creates a descriptor with validated flags and an explicit, supported Unicode dataset.</summary>
    public XPathProfile(XPathFlags flags = XPathFlags.None,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        UnicodeVersionPolicy.Validate(unicodeVersion);
        if ((flags & ~(XPathFlags)31) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        Flags = flags;
        UnicodeVersion = unicodeVersion;
    }
    /// <summary>Strict scalar UTF-16 and UTF-8 borrowed subjects are admitted.</summary>
    public bool Supports(RegexInputKind kind) => kind is RegexInputKind.Utf16 or RegexInputKind.Utf8;
    /// <summary>Parses s/m/i/x/q flags, preserving the first invalid original source location.</summary>
    public static bool TryParseFlags(ReadOnlySpan<char> flags, out XPathProfile? profile,
        out XmlRegexDiagnostic diagnostic)
        => TryParseFlags(flags, out profile, out diagnostic, UnicodeVersion.Unicode16_0);

    /// <summary>Parses F&amp;O flags for the selected Unicode dataset; undefined versions are argument errors.</summary>
    public static bool TryParseFlags(ReadOnlySpan<char> flags, out XPathProfile? profile,
        out XmlRegexDiagnostic diagnostic, UnicodeVersion unicodeVersion)
    {
        UnicodeVersionPolicy.Validate(unicodeVersion);
        var value = XPathFlags.None;
        for (var i = 0; i < flags.Length; i++)
        {
            var flag = flags[i] switch
            {
                's' => XPathFlags.DotAll,
                'm' => XPathFlags.Multiline,
                'i' => XPathFlags.IgnoreCase,
                'x' => XPathFlags.IgnoreWhitespace,
                'q' => XPathFlags.Quote,
                _ => XPathFlags.None
            };
            if (flag == XPathFlags.None)
            {
                profile = null;
                diagnostic = new(new(RegexDiagnosticCode.InvalidSyntax, i, 1,
                    RegexOffsetUnit.Utf16CodeUnits), XmlRegexDiagnosticLocation.Flags,
                    XPathErrorCode.FORX0001, "XP-008");
                return false;
            }
            value |= flag;
        }
        profile = new(value, unicodeVersion);
        diagnostic = default;
        return true;
    }
}
