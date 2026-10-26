#nullable enable
namespace GregsEx;

/// <summary>Oniguruma compile options (ONIG_OPTION_*).</summary>
[Flags]
public enum OnigurumaOptions
{
    /// <summary>ONIG_OPTION_NONE.</summary>
    None = 0,
    /// <summary>ONIG_OPTION_IGNORECASE: simple Unicode case folding.</summary>
    IgnoreCase = 1,
    /// <summary>ONIG_OPTION_MULTILINE: dot also matches newline, as (?m).</summary>
    Multiline = 2,
    /// <summary>ONIG_OPTION_EXTEND: whitespace and # comments are ignored, as (?x).</summary>
    Extended = 4,
    /// <summary>ONIG_OPTION_CAPTURE_GROUP: unnamed groups capture even when named groups exist (TextMate's setting).</summary>
    CaptureGroup = 8,
    /// <summary>ONIG_OPTION_DONT_CAPTURE_GROUP: only named groups capture.</summary>
    DontCaptureGroup = 16,
}

/// <summary>
/// Oniguruma 6.9.10 with ONIG_SYNTAX_ONIGURUMA (Ruby syntax) over UTF-16 subjects, using Oniguruma's own
/// Unicode 16.0 property and case-fold data.
/// </summary>
public sealed class OnigurumaProfile : RegexProfile
{
    /// <summary>Versioned syntax and semantics identity.</summary>
    public const string ContractId = "Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1";

    /// <summary>The pinned Oniguruma source commit (tag v6.9.10).</summary>
    public const string SourceRevision = "4ef89209a239c1aea328cf13c05a2807e5c146d1";

    /// <summary>Oniguruma's generated Unicode tables of the pinned revision.</summary>
    public const string UnicodeDataId = "Oniguruma-6.9.10-Unicode16.0.0-4ef89209";

    /// <inheritdoc />
    public override string Id => ContractId;

    /// <inheritdoc />
    public override string DataId => UnicodeDataId;

    /// <summary>The compile options.</summary>
    public OnigurumaOptions Options { get; }

    /// <summary>Creates a profile with explicit compile options.</summary>
    public OnigurumaProfile(OnigurumaOptions options = OnigurumaOptions.None)
    {
        const OnigurumaOptions all = OnigurumaOptions.IgnoreCase | OnigurumaOptions.Multiline | OnigurumaOptions.Extended
            | OnigurumaOptions.CaptureGroup | OnigurumaOptions.DontCaptureGroup;
        if ((options & ~all) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if ((options & OnigurumaOptions.CaptureGroup) != 0 && (options & OnigurumaOptions.DontCaptureGroup) != 0)
        {
            throw new ArgumentException("CaptureGroup and DontCaptureGroup are exclusive.", nameof(options));
        }

        Options = options;
    }
}

/// <summary>Oniguruma compile ceilings and default match ceilings.</summary>
public sealed record OnigurumaCompileOptions
{
    /// <summary>Pattern, nesting, instruction and group ceilings.</summary>
    public RegexCompileLimits Limits { get; }

    /// <summary>Work (backtracks per match attempt, Oniguruma's retry limit) and workspace ceilings.</summary>
    public RegexMatchLimits DefaultMatchLimits { get; }

    /// <summary>Creates options; omitted values use finite defaults.</summary>
    public OnigurumaCompileOptions(RegexCompileLimits? limits = null, RegexMatchLimits? defaultMatchLimits = null)
    {
        Limits = limits ?? new RegexCompileLimits();
        DefaultMatchLimits = defaultMatchLimits ?? new RegexMatchLimits();
    }

    /// <summary>
    /// The compile limits TextMate grammars need (patterns of hundreds of thousands of units, Oniguruma's own parse
    /// depth, tens of thousands of groups), with default match limits: 4 Mi pattern units, nesting 4096, 16 Mi
    /// instructions, 1 Mi class ranges, 32767 groups, 4 Gi compile work units, 1 GiB plan and compile memory. Grammar
    /// hosts, generators emitting their kernels and the patterns binding them name the same limits through it.
    /// </summary>
    public static OnigurumaCompileOptions TextMate { get; } = new(new RegexCompileLimits(maxPatternUnits: 1 << 22,
        maxNestingDepth: 4096, maxInstructions: 1 << 24, maxClassRanges: 1 << 20, maxCaptureGroups: 32767,
        maxCompileWork: 1L << 32, maxPlanBytes: 1L << 30, maxCompileMemoryBytes: 1L << 30));
}
