#nullable enable
namespace GregsEx;

/// <summary>A named capture, resolved from all numbered aliases in source order.</summary>
public readonly record struct EcmaScriptNamedCapture(string Name, RegexCapture Capture);

/// <summary>Immutable owned native ranges; no workspace or subject storage is borrowed.</summary>
public sealed class EcmaScriptExecResult
{
    private readonly RegexCapture[] _captures;
    private readonly EcmaScriptNamedCapture[] _names;
    /// <summary>The selected whole-match range.</summary>
    public RegexMatchRange Range { get; }
    /// <summary>Final numbered captures, including group zero.</summary>
    public ReadOnlySpan<RegexCapture> Captures => _captures;
    /// <summary>Final named captures, including absent names.</summary>
    public ReadOnlySpan<EcmaScriptNamedCapture> Names => _names;
    /// <summary>Whether d requests the indices result.</summary>
    public bool HasIndices { get; }
    /// <summary>Indices when d is present; absence differs from an unmatched capture.</summary>
    public ReadOnlySpan<RegexCapture> Indices => HasIndices ? _captures : [];
    /// <summary>Named indices when d is present.</summary>
    public ReadOnlySpan<EcmaScriptNamedCapture> NamedIndices => HasIndices ? _names : [];
    internal EcmaScriptExecResult(RegexMatchRange range, RegexCapture[] captures,
        EcmaScriptNamedCapture[] names, bool indices)
    { Range = range; _captures = captures; _names = names; HasIndices = indices; }
}

/// <summary>Distinguishes a single Exec-shaped result from global whole-match strings.</summary>
public sealed class EcmaScriptMatchResult
{
    /// <summary>Whether the global operation was selected.</summary>
    public bool IsGlobal { get; }
    /// <summary>Non-global match, otherwise null.</summary>
    public EcmaScriptExecResult? Single { get; }
    /// <summary>Immutable global whole-match strings, otherwise empty.</summary>
    public IReadOnlyList<string> GlobalMatches { get; }
    internal EcmaScriptMatchResult(EcmaScriptExecResult single)
    { Single = single; GlobalMatches = Array.Empty<string>(); }
    internal EcmaScriptMatchResult(string[] matches)
    { IsGlobal = true; GlobalMatches = Array.AsReadOnly(matches); }
}
