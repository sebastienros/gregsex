#nullable enable
namespace GregsEx;

/// <summary>Original UTF-16 source carrying a flavor diagnostic.</summary>
public enum XmlRegexDiagnosticLocation
{
    /// <summary>Original pattern, before x processing.</summary>
    Pattern,
    /// <summary>Original flags.</summary>
    Flags,
    /// <summary>Original replacement.</summary>
    Replacement
}

/// <summary>Standard F&amp;O errors; resource and input failures keep shared failure identities.</summary>
public enum XPathErrorCode
{
    /// <summary>No standard error.</summary>
    None,
    /// <summary>Invalid flags.</summary>
    FORX0001,
    /// <summary>Invalid pattern.</summary>
    FORX0002,
    /// <summary>Replace, tokenize or analyze-string pattern matches the empty string.</summary>
    FORX0003,
    /// <summary>Invalid replacement.</summary>
    FORX0004
}

/// <summary>Flavor detail without replacing bounded shared diagnostics.</summary>
/// <param name="Diagnostic">Source location and shared category.</param>
/// <param name="Location">Original source domain.</param>
/// <param name="ErrorCode">FORX code, or None for XSD/resource/admission errors.</param>
/// <param name="FeatureId">Stable XS/XP inventory key, or null on success.</param>
public readonly record struct XmlRegexDiagnostic(RegexDiagnostic Diagnostic,
    XmlRegexDiagnosticLocation Location, XPathErrorCode ErrorCode, string? FeatureId)
{
    /// <summary>Namespace identity of every FORX error.</summary>
    public const string ErrorNamespace = "http://www.w3.org/2005/xqt-errors";
}

/// <summary>A standard function error with structured source location.</summary>
public sealed class XPathRegexException : Exception
{
    /// <summary>Structured diagnostic with standard error code.</summary>
    public XmlRegexDiagnostic Diagnostic { get; }
    /// <summary>Creates a standard error; resource failures use RegexExecutionException.</summary>
    public XPathRegexException(XmlRegexDiagnostic diagnostic) : base(diagnostic.ErrorCode.ToString())
    {
        if (!Enum.IsDefined(diagnostic.ErrorCode) || diagnostic.ErrorCode == XPathErrorCode.None)
        {
            throw new ArgumentException("A standard error code is required.", nameof(diagnostic));
        }

        Diagnostic = diagnostic;
    }
}

/// <summary>Finite ceilings for owned strings and analyze-string nodes.</summary>
public sealed record XPathHostLimits
{
    /// <summary>Maximum tokens/segments/nodes, including groups and text nodes.</summary>
    public int MaxResults { get; }
    /// <summary>Maximum total materialized UTF-16 units.</summary>
    public int MaxOutputUnits { get; }
    /// <summary>Maximum conservatively accounted live bytes, including growth overlap.</summary>
    public long MaxMemoryBytes { get; }
    /// <summary>Creates finite positive limits; work/cancellation/deadline span the entire host call.</summary>
    public XPathHostLimits(int maxResults = 1_000_000, int maxOutputUnits = 16_777_216,
        long maxMemoryBytes = 67_108_864)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputUnits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemoryBytes);
        MaxResults = maxResults;
        MaxOutputUnits = maxOutputUnits;
        MaxMemoryBytes = maxMemoryBytes;
    }
}

/// <summary>Distinct host materialization ceilings, separate from standard FORX errors.</summary>
public enum XPathHostFailureCode
{
    /// <summary>No failure.</summary>
    None,
    /// <summary>Token or analyze-string node count limit.</summary>
    ResultLimitExceeded,
    /// <summary>Total owned output length limit.</summary>
    OutputLimitExceeded,
    /// <summary>Peak live host memory limit.</summary>
    MemoryLimitExceeded
}

/// <summary>Host output exhaustion without a partially published result.</summary>
/// <param name="Code">Failed ceiling.</param>
/// <param name="RequiredCapacity">Attempted result count, UTF-16 length or bytes.</param>
/// <param name="WorkConsumed">Cumulative work at the failure.</param>
public readonly record struct XPathHostFailure(XPathHostFailureCode Code, long RequiredCapacity, long WorkConsumed);

/// <summary>A bounded owned-output failure, distinct from FORX and search failures.</summary>
public sealed class XPathHostException : Exception
{
    /// <summary>Structured failure.</summary>
    public XPathHostFailure Failure { get; }
    /// <summary>Creates a non-success host failure.</summary>
    public XPathHostException(XPathHostFailure failure) : base(failure.Code.ToString())
    {
        if (!Enum.IsDefined(failure.Code) || failure.Code == XPathHostFailureCode.None ||
            failure.RequiredCapacity < 0 || failure.WorkConsumed < 0)
        {
            throw new ArgumentException("A valid failing host result is required.", nameof(failure));
        }

        Failure = failure;
    }
}
