namespace GregsEx;

/// <summary>The pinned POSIX edition. This descriptor does not certify an engine.</summary>
public enum PosixEdition
{
    /// <summary>Issue 7, 2018 edition, XBD chapter 9 and regex interfaces.</summary>
    Issue7_2018
}

/// <summary>Separate POSIX basic and extended grammars.</summary>
public enum PosixDialect
{
    /// <summary>BRE, including escaped groups/intervals and references 1–9.</summary>
    Basic,
    /// <summary>ERE, including alternation; standard ERE has no backreferences.</summary>
    Extended
}

/// <summary>Issue 7 regex-interface compilation options.</summary>
[Flags]
public enum PosixCompileFlags
{
    /// <summary>Case-sensitive matching; LF is ordinary.</summary>
    None = 0,
    /// <summary>REG_ICASE, frozen provider case counterparts.</summary>
    IgnoreCase = 1,
    /// <summary>REG_NEWLINE, LF dot/negated-list exclusion and internal line anchors.</summary>
    Newline = 2,
    /// <summary>REG_NOSUB, typed RegExec ignores its output span.</summary>
    NoSub = 4
}

/// <summary>Per-invocation Issue 7 regex-interface execution options.</summary>
[Flags]
public enum PosixExecutionFlags
{
    /// <summary>Original input endpoints are eligible anchor positions.</summary>
    None = 0,
    /// <summary>REG_NOTBOL suppresses only the original input beginning.</summary>
    NotBol = 1,
    /// <summary>REG_NOTEOL suppresses only the original input end.</summary>
    NotEol = 2
}

/// <summary>A closed immutable provider identity; no process-global locale changes.</summary>
public sealed class PosixLocale
{
    private PosixLocale() { }
    /// <summary>The only delivered provider descriptor; C and POSIX resolve to it.</summary>
    public static PosixLocale C { get; } = new();
    /// <summary>Canonical locale alias.</summary>
    public string Name => "C";
    /// <summary>Managed ASCII classification and ordinal 8-bit collation identity.</summary>
    public string ProviderId => "GregsEx.Posix.C.Byte-v1";
    /// <summary>SHA256 of the frozen canonical 256-row classification/case/collation serialization.</summary>
    public string DataDigest => "9ecb7646317a4fe07c42c908c4a27598e152225194fa59ff436af9c74fcab7ae";
    /// <summary>Resolve exactly C or POSIX; other locales return a structured unsupported feature.</summary>
    public static bool TryGet(ReadOnlySpan<char> name, out PosixLocale? locale,
        out PosixRegexDiagnostic diagnostic)
    {
        if (name.SequenceEqual("C") || name.SequenceEqual("POSIX"))
        { locale = C; diagnostic = default; return true; }
        locale = null;
        diagnostic = new(new(RegexDiagnosticCode.UnsupportedFeature, 0, name.Length,
            RegexOffsetUnit.Utf16CodeUnits), PosixErrorCode.None,
            PosixDiagnosticLocation.Locale, PosixSyntaxDisposition.ProfileExclusion, "PX-003");
        return false;
    }
}

/// <summary>Named Issue 7 C-only byte profile. Supporting descriptor, not a compatibility certification.</summary>
public sealed class PosixProfile : RegexProfile
{
    /// <summary>Frozen edition.</summary>
    public PosixEdition Edition => PosixEdition.Issue7_2018;
    /// <summary>Chosen basic or extended grammar.</summary>
    public PosixDialect Dialect { get; }
    /// <summary>Validated compilation flags.</summary>
    public PosixCompileFlags Flags { get; }
    /// <summary>Immutable managed provider; only C/POSIX is admitted.</summary>
    public PosixLocale Locale { get; }
    /// <summary>Explicitly C-only identity, never unrestricted POSIX parity.</summary>
    public override string Id => Dialect == PosixDialect.Basic ? "PosixBre2018-CByte" : "PosixEre2018-CByte";
    /// <summary>Table identity; cache keys must also include the digest, dialect and flags.</summary>
    public override string DataId => Locale.ProviderId;
    /// <summary>Largest finite interval count admitted by this profile.</summary>
    public const int RepeatMaximum = 255;
    /// <summary>Create a validated immutable descriptor.</summary>
    public PosixProfile(PosixDialect dialect = PosixDialect.Basic,
        PosixCompileFlags flags = PosixCompileFlags.None, PosixLocale? locale = null)
    {
        if (!Enum.IsDefined(dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(dialect));
        }

        if ((flags & ~(PosixCompileFlags)7) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        Dialect = dialect;
        Flags = flags;
        Locale = locale ?? PosixLocale.C;
    }
    /// <summary>Only uninterpreted byte subjects are admitted; no UTF-8/UTF-16 decoding.</summary>
    public bool Supports(RegexInputKind inputKind) => inputKind == RegexInputKind.Bytes;
}
