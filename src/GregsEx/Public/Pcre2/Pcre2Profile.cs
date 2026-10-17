#nullable enable
using System.Security.Cryptography;
using GregsEx.Contracts.Pcre2;

namespace GregsEx;

/// <summary>The native code-unit width; this contract admits exactly 8 and 16.</summary>
public enum Pcre2CodeUnitWidth
{
    /// <summary>8-bit native patterns and subjects.</summary>
    Eight = 8,
    /// <summary>16-bit native patterns and subjects.</summary>
    Sixteen = 16
}

/// <summary>Source-observable optimizer selection, independent of a GregsEx execution backend.</summary>
public readonly record struct Pcre2OptimizationPolicy(bool AutoPossess, bool DotStarAnchor, bool StartOptimize)
{
    /// <summary>Native full optimization default; observable for verbs and callouts.</summary>
    public static Pcre2OptimizationPolicy Full => new(true, true, true);
    /// <summary>Native PCRE2_OPTIMIZATION_NONE.</summary>
    public static Pcre2OptimizationPolicy None => default;
}

/// <summary>Immutable copied low-byte tables in the pinned native layout; never ambient locale state.</summary>
public sealed class Pcre2CharacterTables
{
    private readonly byte[] _bytes;
    /// <summary>1088 bytes: lower256, flip256, ten32-byte class maps, type256.</summary>
    public const int PayloadLength = 1088;
    /// <summary>The frozen native C-locale default descriptor.</summary>
    public static Pcre2CharacterTables Default { get; } = new(Pcre2DefaultTables.Bytes);
    /// <summary>SHA256 of the exact owned payload; part of profile/cache identity.</summary>
    public string Digest { get; }
    /// <summary>Read-only borrowed view valid for this immutable descriptor's lifetime.</summary>
    public ReadOnlySpan<byte> Bytes => _bytes;
    /// <summary>Copy the exact native table payload. Arbitrary byte-valued tables are admitted.</summary>
    public Pcre2CharacterTables(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != PayloadLength)
        {
            throw new ArgumentException("PCRE2 10.46 tables require1088 bytes.", nameof(bytes));
        }

        _bytes = bytes.ToArray();
        Digest = Convert.ToHexStringLower(SHA256.HashData(_bytes));
    }
}

/// <summary>Ratified supporting descriptor. Its existence is not an implemented matcher claim.</summary>
public sealed class Pcre2Profile : RegexProfile
{
    /// <summary>The human-approved complete defined standard-matcher package identity.</summary>
    public const string ContractId = "Pcre2-10.46-ManagedStandard8_16-v1";
    /// <summary>Immutable upstream source commit, independent of installed oracle versions.</summary>
    public const string SourceRevision = "b2bd4254b379b9d7dc9a3dda060a7e27009ccdff";
    /// <summary>The actual PCRE2 Unicode-data/algorithm identity.</summary>
    public const string UnicodeDataId = "PCRE2-10.46-Unicode16.0.0-b2bd4254";
    /// <summary>The native finite repeat/capture bound, before stricter GregsEx ceilings.</summary>
    public const int NativeCountMaximum = 65535;
    /// <summary>Named scope identity; include all descriptor properties in cache keys.</summary>
    public override string Id => ContractId;
    /// <summary>PCRE-specific Unicode identity; low-byte table digest is separate.</summary>
    public override string DataId => UnicodeDataId;
    /// <summary>Native storage width.</summary>
    public Pcre2CodeUnitWidth Width { get; }
    /// <summary>Requested flags; parser normalizes pattern directives independently.</summary>
    public Pcre2CompileFlags Flags { get; }
    /// <summary>Requested compile-extra word.</summary>
    public Pcre2ExtraFlags ExtraFlags { get; }
    /// <summary>Requested newline convention; pattern directives may override it.</summary>
    public Pcre2Newline Newline { get; }
    /// <summary>Requested backslash-R convention.</summary>
    public Pcre2Bsr Bsr { get; }
    /// <summary>Immutable low-byte table provider.</summary>
    public Pcre2CharacterTables CharacterTables { get; }
    /// <summary>Requested optimizer states before legacy flags/directive precedence.</summary>
    public Pcre2OptimizationPolicy Optimization { get; }
    /// <summary>Requested bounded variable lookbehind maximum, default255; fixed limits are separate.</summary>
    public int MaxVariableLookbehind { get; }
    /// <summary>MATCH_INVALID_UTF implies UTF independently of UCP.</summary>
    public bool Utf => (Flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.MatchInvalidUtf)) != 0;
    /// <summary>Unicode classes do not imply UTF decoding.</summary>
    public bool Ucp => (Flags & Pcre2CompileFlags.Ucp) != 0;
    /// <summary>Storage kind for this descriptor before source directives normalize UTF.</summary>
    public RegexInputKind InputKind => Width == Pcre2CodeUnitWidth.Sixteen ? RegexInputKind.Utf16 :
        Utf ? RegexInputKind.Utf8 : RegexInputKind.Bytes;
    /// <summary>Legacy disabling flags override requested optimization enabling.</summary>
    public Pcre2OptimizationPolicy EffectiveOptimization => new(
        Optimization.AutoPossess && !Flags.HasFlag(Pcre2CompileFlags.NoAutoPossess),
        Optimization.DotStarAnchor && !Flags.HasFlag(Pcre2CompileFlags.NoDotstarAnchor),
        Optimization.StartOptimize && !Flags.HasFlag(Pcre2CompileFlags.NoStartOptimize));

    /// <summary>Construct validated identities. Source-invalid option combinations are parser diagnostics.</summary>
    public Pcre2Profile(Pcre2CodeUnitWidth width = Pcre2CodeUnitWidth.Sixteen,
        Pcre2CompileFlags flags = Pcre2CompileFlags.None, Pcre2ExtraFlags extraFlags = Pcre2ExtraFlags.None,
        Pcre2Newline newline = Pcre2Newline.Lf, Pcre2Bsr bsr = Pcre2Bsr.Unicode,
        Pcre2CharacterTables? characterTables = null, Pcre2OptimizationPolicy? optimization = null,
        int maxVariableLookbehind = 255)
    {
        if (!Enum.IsDefined(width))
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (((uint)flags & ~0xEFFFFFFFu) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        if (((uint)extraFlags & ~0x0001FFFFu) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extraFlags));
        }

        if (!Enum.IsDefined(newline))
        {
            throw new ArgumentOutOfRangeException(nameof(newline));
        }

        if (!Enum.IsDefined(bsr))
        {
            throw new ArgumentOutOfRangeException(nameof(bsr));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxVariableLookbehind);
        Width = width;
        Flags = flags;
        ExtraFlags = extraFlags;
        Newline = newline;
        Bsr = bsr;
        CharacterTables = characterTables ?? Pcre2CharacterTables.Default;
        Optimization = optimization ?? Pcre2OptimizationPolicy.Full;
        MaxVariableLookbehind = maxVariableLookbehind;
    }
    /// <summary>Storage admission; typed byte/char native APIs additionally validate the actual overload width.</summary>
    public bool Supports(RegexInputKind kind) => kind == InputKind;
}

/// <summary>Finite logical ceilings; zero is an explicit resource denial, never unlimited.</summary>
public sealed record Pcre2MatchLimits
{
    /// <summary>Cumulative managed work, including validation/retries/host operations.</summary>
    public long MaxWork { get; }
    /// <summary>Sum of active VM frames and subject windows; every subroutine reserves a frame.</summary>
    public int MaxDepth { get; }
    /// <summary>Logical prepared workspace bytes, not native allocation equivalence.</summary>
    public long MaxWorkspaceBytes { get; }
    /// <summary>Capture undo events.</summary>
    public int MaxCaptureEvents { get; }
    /// <summary>Callback invocations across the entire operation.</summary>
    public int MaxCallouts { get; }
    /// <summary>Construct nonnegative finite ceilings; ordinary defaults are bounded.</summary>
    public Pcre2MatchLimits(long maxWork = 10_000_000, int maxDepth = 4096,
        long maxWorkspaceBytes = 67_108_864, int maxCaptureEvents = 65536, int maxCallouts = 1_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWork);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfNegative(maxWorkspaceBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCaptureEvents);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCallouts);
        MaxWork = maxWork;
        MaxDepth = maxDepth;
        MaxWorkspaceBytes = maxWorkspaceBytes;
        MaxCaptureEvents = maxCaptureEvents;
        MaxCallouts = maxCallouts;
    }
    internal bool IsWithin(Pcre2MatchLimits ceiling) => MaxWork <= ceiling.MaxWork && MaxDepth <= ceiling.MaxDepth &&
        MaxWorkspaceBytes <= ceiling.MaxWorkspaceBytes && MaxCaptureEvents <= ceiling.MaxCaptureEvents && MaxCallouts <= ceiling.MaxCallouts;
}

/// <summary>Typed native-width compilation policy, without a conflicting generic input-kind request.</summary>
public sealed record Pcre2CompileOptions
{
    /// <summary>Shared positive compilation ceilings.</summary>
    public RegexCompileLimits Limits { get; }
    /// <summary>Retained operation safety ceilings.</summary>
    public Pcre2MatchLimits DefaultMatchLimits { get; }
    /// <summary>Execution preference; interpreted and Auto modes remain AOT-safe.</summary>
    public RegexExecutionMode ExecutionMode { get; }
    /// <summary>Create immutable bounded options.</summary>
    public Pcre2CompileOptions(RegexCompileLimits? limits = null, Pcre2MatchLimits? defaultMatchLimits = null,
        RegexExecutionMode executionMode = RegexExecutionMode.Interpreted)
    {
        if (!Enum.IsDefined(executionMode))
        {
            throw new ArgumentOutOfRangeException(nameof(executionMode));
        }

        Limits = limits ?? new();
        DefaultMatchLimits = defaultMatchLimits ?? new();
        ExecutionMode = executionMode;
    }
}

/// <summary>Per-operation native controls plus cumulative cancellation/time/limit policy.</summary>
public readonly record struct Pcre2MatchOptions
{
    /// <summary>Native flags; partial-hard takes precedence when both partial flags are present.</summary>
    public Pcre2MatchFlags Flags { get; init; }
    /// <summary>Optional candidate-start offset ceiling; requires USE_OFFSET_LIMIT in the compiled profile.</summary>
    public int? OffsetLimit { get; init; }
    /// <summary>Whole-operation cancellation.</summary>
    public CancellationToken CancellationToken { get; init; }
    /// <summary>Positive optional duration, without resetting across retries.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Optional smaller finite logical ceilings.</summary>
    public Pcre2MatchLimits? Limits { get; init; }
    internal Pcre2MatchLimits Resolve(Pcre2MatchLimits ceiling)
    {
        if (((uint)Flags & ~0xE004603Fu) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Flags));
        }

        if (OffsetLimit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(OffsetLimit));
        }

        if (Timeout is { } time && time <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }

        if (Limits is { } limits && !limits.IsWithin(ceiling))
        {
            throw new ArgumentOutOfRangeException(nameof(Limits));
        }

        return Limits ?? ceiling;
    }
}

/// <summary>Finite host materialization/iteration/output ceilings.</summary>
public sealed record Pcre2HostLimits
{
    /// <summary>Maximum emitted matches/substitutions.</summary>
    public int MaxResults { get; }
    /// <summary>Maximum output native code units, including reserved terminator when required.</summary>
    public int MaxOutputUnits { get; }
    /// <summary>Peak simultaneously live host buffer/result memory.</summary>
    public long MaxMemoryBytes { get; }
    /// <summary>Create positive finite host bounds.</summary>
    public Pcre2HostLimits(int maxResults = 65536, int maxOutputUnits = 16_777_216, long maxMemoryBytes = 67_108_864)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputUnits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemoryBytes);
        MaxResults = maxResults;
        MaxOutputUnits = maxOutputUnits;
        MaxMemoryBytes = maxMemoryBytes;
    }
}

/// <summary>Finite prepared typed-workspace policy; no growth is the ordinary default.</summary>
public sealed record Pcre2WorkspaceOptions
{
    /// <summary>Prepared26-slot backtracking frames.</summary>
    public int Frames { get; }
    /// <summary>Prepared4-slot capture undo events.</summary>
    public int CaptureEvents { get; }
    /// <summary>Prepared6-slot subject windows.</summary>
    public int Windows { get; }
    /// <summary>Prepared Unicode/script scratch Int32 slots.</summary>
    public int UnicodeScratch { get; }
    /// <summary>Explicit allocating growth preference; all growth obeys retained safety ceilings.</summary>
    public bool AllowGrowth { get; }
    /// <summary>Create positive prepared capacities independently from smaller logical operation limits.</summary>
    public Pcre2WorkspaceOptions(int frames = 4096, int captureEvents = 65536, int windows = 4096,
        int unicodeScratch = 512, bool allowGrowth = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureEvents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unicodeScratch);
        Frames = frames;
        CaptureEvents = captureEvents;
        Windows = windows;
        UnicodeScratch = unicodeScratch;
        AllowGrowth = allowGrowth;
    }
}

/// <summary>Throwing parse convenience retains the complete typed source diagnosis.</summary>
public sealed class Pcre2ParseException : ArgumentException
{
    /// <summary>Actual failing parse diagnostic.</summary>
    public Pcre2Diagnostic Diagnostic { get; }
    /// <summary>Create a parse exception only from a failing diagnostic.</summary>
    public Pcre2ParseException(Pcre2Diagnostic diagnostic) : base(diagnostic.Diagnostic.Message ?? diagnostic.Kind.ToString(), "pattern")
    {
        if (diagnostic.Kind == Pcre2DiagnosticKind.None)
        {
            throw new ArgumentException("A failing diagnostic is required.", nameof(diagnostic));
        }

        Diagnostic = diagnostic;
    }
}
