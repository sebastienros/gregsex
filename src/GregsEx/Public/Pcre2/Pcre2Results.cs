#nullable enable
namespace GregsEx;

/// <summary>Independent native endpoints: a successful reset-start pair may be reversed.</summary>
public readonly record struct Pcre2OffsetPair
{
    /// <summary>Unset differs from an empty participating capture.</summary>
    public bool Participated { get; }
    /// <summary>Native-unit start; zero for unset.</summary>
    public int Start { get; }
    /// <summary>Native-unit end; zero for unset, may precede Start for group zero.</summary>
    public int End { get; }
    /// <summary>Create a valid native pair; reversed pairs are intentionally admitted.</summary>
    public Pcre2OffsetPair(bool participated, int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(end);
        if (!participated && (start != 0 || end != 0))
        {
            throw new ArgumentException("Unset pairs use zero endpoints.");
        }

        Participated = participated;
        Start = start;
        End = end;
    }
    /// <summary>Project a participating ordered pair only, without changing a native result.</summary>
    public bool TryGetRange(out RegexMatchRange range)
    {
        range = Participated && Start <= End ? new(Start, End - Start) : default;
        return Participated && Start <= End;
    }
}

/// <summary>Typed results preserve partial/source errors separately from shared search statuses.</summary>
public enum Pcre2MatchStatus
{
    /// <summary>Complete success; native return0 is still success with a short vector.</summary>
    Match,
    /// <summary>Native no-match.</summary>
    NoMatch,
    /// <summary>Native partial match; only group-zero pair is defined.</summary>
    Partial,
    /// <summary>Source-defined input/option/callback error.</summary>
    Error,
    /// <summary>Explicit managed or mapped resource exhaustion.</summary>
    LimitExceeded,
    /// <summary>Cancellation.</summary>
    Cancelled,
    /// <summary>Deadline expiration.</summary>
    TimedOut
}

/// <summary>Diagnostic class independent of native human-readable messages.</summary>
public enum Pcre2DiagnosticKind
{
    /// <summary>No diagnostic.</summary>
    None,
    /// <summary>Invalid syntax.</summary>
    Syntax,
    /// <summary>Source-invalid option combination.</summary>
    Options,
    /// <summary>Valid feature excluded by the named native host boundary or requested projection.</summary>
    Capability,
    /// <summary>Invalid pattern encoding/domain.</summary>
    PatternEncoding,
    /// <summary>Compilation resource exhaustion.</summary>
    CompileLimit,
    /// <summary>Invalid internal plan, not a user syntax error.</summary>
    InvalidProgram
}

/// <summary>Native source identity plus structured GregsEx location/capability diagnosis.</summary>
public readonly record struct Pcre2Diagnostic(RegexDiagnostic Diagnostic, Pcre2DiagnosticKind Kind,
    Pcre2ErrorCode SourceError = Pcre2ErrorCode.None, string? CapabilityId = null, int NativeOffset = -1);

/// <summary>Typed invocation metadata; caller-owned pairs and output are separate.</summary>
public readonly record struct Pcre2MatchResult(Pcre2MatchStatus Status, int NativeReturnCode,
    int StartChar, int MarkIndex = -1, RegexExecutionFailure Failure = default);

/// <summary>Substitution result with bounded caller-buffer sizing independent of match status.</summary>
public readonly record struct Pcre2SubstituteResult(int NativeReturnCode, int OutputUnits,
    long RequiredCapacity, RegexExecutionFailure Failure = default);

/// <summary>Caller-buffer substring sizing/error result, including required native terminator capacity.</summary>
public readonly record struct Pcre2SubstringResult(int NativeReturnCode, int OutputUnits, long RequiredCapacity);
/// <summary>One immutable native name-table semantic entry; duplicate names retain every group number.</summary>
public readonly record struct Pcre2NamedGroup(string Name, int Number);

/// <summary>Explicit generic projection policy, requiring whole-subject validity.</summary>
public enum Pcre2ProjectionPolicy
{
    /// <summary>Ordinary full matches only; preserve established generic buffer/status/validation semantics.</summary>
    WholeSubjectValidated
}

/// <summary>Pattern facts that prohibit a generic projection; typed PCRE results remain valid.</summary>
[Flags]
public enum Pcre2ProjectionRestrictions
{
    /// <summary>Eligible in the explicitly stated whole-valid subject domain.</summary>
    None = 0,
    /// <summary>Lookaround reset-start can report reversed or pre-start group-zero endpoints.</summary>
    LookaroundReset = 1,
    /// <summary>Code-unit matching can split encoded UTF scalars.</summary>
    UtfCodeUnit = 2,
    /// <summary>Invalid-UTF subject matching is intentionally admitted.</summary>
    InvalidUtf = 4,
    /// <summary>Native unchecked validation request.</summary>
    NoUtfCheck = 8
}
