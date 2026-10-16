using GregsEx.Flavors.Pcre2.Runtime.Composition;

namespace GregsEx;

/// <summary>Immutable PCRE2 10.46 managed standard8/16 plan, with native typed outcomes.</summary>
public sealed class Pcre2Pattern
{
    internal const long WrapperBytes = 128, ProjectionBytes = 512;
    internal Pcre2Executor Executor { get; }
    internal Pcre2CompileOptions Options { get; }
    /// <summary>Effective immutable source-normalized profile and data identity.</summary>
    public Pcre2Profile Profile => Executor.Program.Profile;
    /// <summary>Number of capture groups including group zero.</summary>
    public int GroupCount => Executor.Program.GroupCount;
    /// <summary>Capabilities requiring native typed operations.</summary>
    public Pcre2ProjectionRestrictions ProjectionRestrictions => Executor.Program.ProjectionRestrictions;
    /// <summary>Conservative retained plan/executor/public metadata bytes; shared data is separate.</summary>
    public long RetainedBytes => Executor.RetainedBytes + WrapperBytes;
    /// <summary>Actual backend; ordinary and Auto operations remain interpreted.</summary>
    public RegexExecutionBackend ExecutionBackend => RegexExecutionBackend.Interpreted;
    internal Pcre2Pattern(Pcre2Executor executor, Pcre2CompileOptions options) { Executor = executor; Options = options; }
    /// <summary>Create an exclusive bounded native workspace.</summary>
    public Pcre2Workspace CreateWorkspace(Pcre2WorkspaceOptions? options = null) => new(this, options ?? new());
    /// <summary>Explicitly admit whole-subject/full-capture generic semantics before any execution.</summary>
    public bool TryProject(Pcre2ProjectionPolicy policy, out RegexPattern? pattern, out Pcre2Diagnostic diagnostic)
    {
        pattern = null;
        if (!Executor.Program.TryAdmitProjection(policy, out diagnostic))
        {
            return false;
        }

        var retained = checked(Executor.RetainedBytes + ProjectionBytes);
        if (retained > Options.Limits.MaxPlanBytes || retained + WrapperBytes > Options.Limits.MaxCompileMemoryBytes)
        { diagnostic = new(new(RegexDiagnosticCode.PlanLimitExceeded, 0, Executor.Program.SourceLength, Executor.Program.SourceUnit), Pcre2DiagnosticKind.CompileLimit); return false; }
        var limits = new RegexMatchLimits(Math.Max(1, Options.DefaultMatchLimits.MaxWork), Math.Max(1, Options.DefaultMatchLimits.MaxWorkspaceBytes));
        var options = new RegexCompileOptions(Profile.InputKind, Options.ExecutionMode, Options.Limits, limits);
        pattern = new(Executor, Options.DefaultMatchLimits, options, retained);
        diagnostic = default;
        return true;
    }
    /// <summary>All numbers for an exact capture name; empty when unknown.</summary>
    public ReadOnlySpan<int> GetGroupNumbers(ReadOnlySpan<char> name) => Executor.Metadata.GetGroupNumbers(name);
    /// <summary>Immutable sorted semantic name entries, including duplicates.</summary>
    public ReadOnlySpan<Pcre2NamedGroup> GetNamedGroups() => Executor.Metadata.GetNamedGroups();
    /// <summary>Unique number, NOSUBSTRING or NOUNIQUESUBSTRING.</summary>
    public int GroupNumberFromName(ReadOnlySpan<char> name) => Executor.Metadata.GroupNumberFromName(name);
    /// <summary>Defined scalar pattern information; physical/JIT/layout keys return a capability diagnostic.</summary>
    public bool TryGetInfo(Pcre2PatternInfoKey key, out long value, out Pcre2Diagnostic diagnostic) => Executor.Metadata.TryGetInfo(key, out value, out diagnostic);
    /// <summary>Immutable native8 mark units.</summary>
    public ReadOnlySpan<byte> GetMarkBytes(int markIndex) => Executor.Metadata.GetMarkBytes(markIndex);
    /// <summary>Immutable native16 mark units.</summary>
    public ReadOnlySpan<char> GetMarkChars(int markIndex) => Executor.Metadata.GetMarkChars(markIndex);
    /// <summary>Immutable native callout-site enumeration.</summary>
    public ReadOnlySpan<Pcre2CalloutSiteInfo> GetCalloutSites() => Executor.Metadata.GetCalloutSites();
    /// <summary>Immutable native8 callout-string units.</summary>
    public ReadOnlySpan<byte> GetCalloutStringBytes(int siteIndex) => Executor.Metadata.GetCalloutStringBytes(siteIndex);
    /// <summary>Immutable native16 callout-string units.</summary>
    public ReadOnlySpan<char> GetCalloutStringChars(int siteIndex) => Executor.Metadata.GetCalloutStringChars(siteIndex);
}
