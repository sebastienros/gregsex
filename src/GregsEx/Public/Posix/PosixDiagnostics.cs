#nullable enable
namespace GregsEx;

/// <summary>Symbolic POSIX error identities; values are managed, not a native ABI.</summary>
public enum PosixErrorCode
{
    /// <summary>No native error mapping, including distinct GregsEx resource failures.</summary>
    None,
    /// <summary>REG_BADPAT.</summary>
    BadPattern,
    /// <summary>REG_BADBR.</summary>
    BadInterval,
    /// <summary>REG_BADRPT.</summary>
    BadRepeat,
    /// <summary>REG_EBRACE.</summary>
    UnbalancedBrace,
    /// <summary>REG_EBRACK.</summary>
    UnbalancedBracket,
    /// <summary>REG_ECOLLATE.</summary>
    InvalidCollatingElement,
    /// <summary>REG_ECTYPE.</summary>
    InvalidCharacterClass,
    /// <summary>REG_EESCAPE.</summary>
    TrailingEscape,
    /// <summary>REG_EPAREN.</summary>
    UnbalancedParenthesis,
    /// <summary>REG_ERANGE.</summary>
    InvalidRange,
    /// <summary>REG_ESUBREG.</summary>
    InvalidBackreference,
    /// <summary>REG_ESPACE, native oracle observation only; managed safety limits stay distinct.</summary>
    NoMemory,
    /// <summary>REG_NOMATCH, execution outcome only.</summary>
    NoMatch
}

/// <summary>The source of a structured diagnostic location.</summary>
public enum PosixDiagnosticLocation
{
    /// <summary>Native pattern units.</summary>
    Pattern,
    /// <summary>UTF-16 locale alias request.</summary>
    Locale,
    /// <summary>Representation/options request, a zero-length location.</summary>
    Options
}

/// <summary>Why a strict profile rejected syntax; do not call every rejection POSIX-required.</summary>
public enum PosixSyntaxDisposition
{
    /// <summary>Success or non-syntax budget failure.</summary>
    None,
    /// <summary>Source explicitly requires an invalid expression to fail.</summary>
    Invalid,
    /// <summary>Undefined syntax rejected by the GregsEx strict policy.</summary>
    UndefinedRejected,
    /// <summary>Unspecified syntax rejected by the GregsEx strict policy.</summary>
    UnspecifiedRejected,
    /// <summary>A nonstandard extension rejected by the named profile.</summary>
    ExtensionRejected,
    /// <summary>GregsEx pattern-domain disposition, including embedded NUL.</summary>
    PatternDomain,
    /// <summary>Unsupported locale or input representation.</summary>
    ProfileExclusion
}

/// <summary>POSIX diagnostic plus classification and stable inventory ID.</summary>
/// <param name="Diagnostic">Common native pattern/request coordinates.</param>
/// <param name="ErrorCode">Symbolic interface identity, never a platform integer.</param>
/// <param name="Location">Pattern, locale or options.</param>
/// <param name="Disposition">Source versus profile rejection classification.</param>
/// <param name="CapabilityId">PX inventory entry, or null for common budget failures.</param>
public readonly record struct PosixRegexDiagnostic(RegexDiagnostic Diagnostic,
    PosixErrorCode ErrorCode = PosixErrorCode.None,
    PosixDiagnosticLocation Location = PosixDiagnosticLocation.Pattern,
    PosixSyntaxDisposition Disposition = PosixSyntaxDisposition.None,
    string? CapabilityId = null);

/// <summary>Native-style byte endpoints. (-1,-1) means unused or nonparticipating.</summary>
/// <param name="Start">Inclusive byte offset, or -1.</param>
/// <param name="End">Exclusive byte offset, or -1.</param>
public readonly record struct PosixMatch(int Start, int End);

/// <summary>Throwing typed POSIX parse failure retaining the classification and source location.</summary>
public sealed class PosixRegexException : ArgumentException
{
    /// <summary>The original classified diagnostic.</summary>
    public PosixRegexDiagnostic Diagnostic { get; }
    /// <summary>Create an exception from an actual failed compilation.</summary>
    public PosixRegexException(PosixRegexDiagnostic diagnostic)
        : base(diagnostic.Diagnostic.Message ?? diagnostic.Diagnostic.Code.ToString(), "pattern")
    {
        if (diagnostic.Diagnostic.Code == RegexDiagnosticCode.None)
        {
            throw new ArgumentException("A POSIX exception requires a failed diagnostic.", nameof(diagnostic));
        }

        Diagnostic = diagnostic;
    }
}
