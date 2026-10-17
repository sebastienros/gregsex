#nullable enable
namespace GregsEx;

/// <summary>Immutable native callout enumeration site, independent of invocation/capture state.</summary>
public readonly record struct Pcre2CalloutSiteInfo(int Number, bool IsString, bool Automatic,
    int PatternPosition, int NextItemLength, int StringOffset, int StringLength)
{
    /// <summary>The pinned native enumeration block version.</summary>
    public int Version => 0;
}

/// <summary>Version2 callout metadata in native pattern/subject units.</summary>
public readonly record struct Pcre2CalloutInfo(int Number, bool IsString, bool HasMark, int SubjectLength, int CaptureTop, int CaptureLast,
    int StartMatch, int CurrentPosition, int PatternPosition, int NextItemLength,
    int StringOffset, int StringLength, Pcre2CalloutFlags Flags)
{
    /// <summary>The pinned native callout block version.</summary>
    public int Version => 2;
}

/// <summary>Borrowed native16 callback view, valid only during the callback.</summary>
public readonly ref struct Pcre2CharCalloutContext
{
    /// <summary>Original subject, not a sliced search suffix.</summary>
    public ReadOnlySpan<char> Subject { get; }
    /// <summary>Readonly current captures; group zero is unset during a callout.</summary>
    public ReadOnlySpan<Pcre2OffsetPair> Captures { get; }
    /// <summary>Current native-width mark, empty if none; Info has no synthetic text conversion.</summary>
    public ReadOnlySpan<char> Mark { get; }
    /// <summary>Normalized native string-callout value, empty for numeric callouts.</summary>
    public ReadOnlySpan<char> CalloutString { get; }
    /// <summary>Native callback numeric fields.</summary>
    public Pcre2CalloutInfo Info { get; }
    /// <summary>Bind borrowed callback views; caller cannot retain a ref struct.</summary>
    public Pcre2CharCalloutContext(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> captures,
        ReadOnlySpan<char> mark, ReadOnlySpan<char> calloutString, Pcre2CalloutInfo info)
    { Subject = subject; Captures = captures; Mark = mark; CalloutString = calloutString; Info = info; }
}

/// <summary>Borrowed native8 callback view, without decoding or replacing original bytes.</summary>
public readonly ref struct Pcre2ByteCalloutContext
{
    /// <summary>Original native8 subject.</summary>
    public ReadOnlySpan<byte> Subject { get; }
    /// <summary>Readonly current captures; group zero is unset.</summary>
    public ReadOnlySpan<Pcre2OffsetPair> Captures { get; }
    /// <summary>Exact current mark bytes.</summary>
    public ReadOnlySpan<byte> Mark { get; }
    /// <summary>Exact normalized callout string bytes.</summary>
    public ReadOnlySpan<byte> CalloutString { get; }
    /// <summary>Native callback numeric fields.</summary>
    public Pcre2CalloutInfo Info { get; }
    /// <summary>Bind borrowed callback views.</summary>
    public Pcre2ByteCalloutContext(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> captures,
        ReadOnlySpan<byte> mark, ReadOnlySpan<byte> calloutString, Pcre2CalloutInfo info)
    { Subject = subject; Captures = captures; Mark = mark; CalloutString = calloutString; Info = info; }
}

/// <summary>0 continues, positive fails this path, negative aborts with that native return code.</summary>
public delegate int Pcre2CharCallout(in Pcre2CharCalloutContext context, object? state);
/// <summary>Native8 matching callback; exceptions propagate and active workspace reuse is forbidden.</summary>
public delegate int Pcre2ByteCallout(in Pcre2ByteCalloutContext context, object? state);

/// <summary>Native substitution callout version0 metadata.</summary>
public readonly record struct Pcre2SubstituteCalloutInfo(int SubstitutionCount, int OutputStart, int OutputEnd)
{
    /// <summary>Native substitution block version.</summary>
    public int Version => 0;
}

/// <summary>Borrowed native16 substitution inspection after expansion, before acceptance.</summary>
public readonly ref struct Pcre2CharSubstituteContext
{
    /// <summary>Original input.</summary>
    public ReadOnlySpan<char> Input { get; }
    /// <summary>Initialized output prefix including the proposed replacement.</summary>
    public ReadOnlySpan<char> Output { get; }
    /// <summary>Current complete native capture pairs.</summary>
    public ReadOnlySpan<Pcre2OffsetPair> Captures { get; }
    /// <summary>Substitution count and output replacement pair.</summary>
    public Pcre2SubstituteCalloutInfo Info { get; }
    /// <summary>Bind operation-borrowed readonly views.</summary>
    public Pcre2CharSubstituteContext(ReadOnlySpan<char> input, ReadOnlySpan<char> output,
        ReadOnlySpan<Pcre2OffsetPair> captures, Pcre2SubstituteCalloutInfo info)
    { Input = input; Output = output; Captures = captures; Info = info; }
}

/// <summary>Borrowed native8 substitution inspection preserving exact bytes.</summary>
public readonly ref struct Pcre2ByteSubstituteContext
{
    /// <summary>Original input bytes.</summary>
    public ReadOnlySpan<byte> Input { get; }
    /// <summary>Initialized proposed output bytes.</summary>
    public ReadOnlySpan<byte> Output { get; }
    /// <summary>Complete current native capture pairs.</summary>
    public ReadOnlySpan<Pcre2OffsetPair> Captures { get; }
    /// <summary>Substitution numeric metadata.</summary>
    public Pcre2SubstituteCalloutInfo Info { get; }
    /// <summary>Bind operation-borrowed readonly views.</summary>
    public Pcre2ByteSubstituteContext(ReadOnlySpan<byte> input, ReadOnlySpan<byte> output,
        ReadOnlySpan<Pcre2OffsetPair> captures, Pcre2SubstituteCalloutInfo info)
    { Input = input; Output = output; Captures = captures; Info = info; }
}

/// <summary>0 accepts; positive rejects and continues globally; negative rejects/stops and copies remaining input.</summary>
public delegate int Pcre2CharSubstituteCallout(in Pcre2CharSubstituteContext context, object? state);
/// <summary>Native8 substitute callout with the same accept/skip/stop semantics.</summary>
public delegate int Pcre2ByteSubstituteCallout(in Pcre2ByteSubstituteContext context, object? state);
/// <summary>Return required native units, or -1 for REPLACECASE. Input/output may overlap.</summary>
public delegate int Pcre2CharCaseCallout(ReadOnlySpan<char> input, Span<char> output, Pcre2SubstituteCase mode, object? state);
/// <summary>Native8 case conversion; return required size or -1, support overlapping buffers.</summary>
public delegate int Pcre2ByteCaseCallout(ReadOnlySpan<byte> input, Span<byte> output, Pcre2SubstituteCase mode, object? state);

/// <summary>Immutable per-operation callback registration; never retained in an immutable pattern.</summary>
public sealed record Pcre2CharCallbacks(Pcre2CharCallout? Match = null,
    Pcre2CharSubstituteCallout? Substitute = null, Pcre2CharCaseCallout? Case = null, object? State = null);
/// <summary>Native8 callback registration with caller-owned user state.</summary>
public sealed record Pcre2ByteCallbacks(Pcre2ByteCallout? Match = null,
    Pcre2ByteSubstituteCallout? Substitute = null, Pcre2ByteCaseCallout? Case = null, object? State = null);
