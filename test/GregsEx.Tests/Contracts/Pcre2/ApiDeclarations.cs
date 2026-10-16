using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Tests.Contracts.Pcre2.Declarations;

// Nonshipping abstract declarations only. Actual concrete public factories,
// patterns, workspaces and iterator sessions belong to integrated implementation.
internal abstract class Pcre2Functions
{
    public abstract Pcre2Pattern Parse(ReadOnlySpan<char> pattern, Pcre2Profile profile, Pcre2CompileOptions? options = null);
    public abstract Pcre2Pattern Parse(ReadOnlySpan<byte> pattern, Pcre2Profile profile, Pcre2CompileOptions? options = null);
    public abstract bool TryParse(ReadOnlySpan<char> pattern, Pcre2Profile profile,
        out Pcre2Pattern? result, out Pcre2Diagnostic diagnostic, Pcre2CompileOptions? options = null);
    public abstract bool TryParse(ReadOnlySpan<byte> pattern, Pcre2Profile profile,
        out Pcre2Pattern? result, out Pcre2Diagnostic diagnostic, Pcre2CompileOptions? options = null);
    public abstract Pcre2SubstringResult CopySubstring(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs,
        int number, Span<char> destination);
    public abstract Pcre2SubstringResult CopySubstring(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs,
        int number, Span<byte> destination);
    public abstract Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<char> subject,
        ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Span<char> destination);
    public abstract Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<byte> subject,
        ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Span<byte> destination);
    public abstract int SubstringLength(ReadOnlySpan<Pcre2OffsetPair> pairs, int number);
    public abstract string MaterializeSubstring(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2HostLimits? limits = null);
    public abstract byte[] MaterializeSubstring(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2HostLimits? limits = null);
}
internal abstract class Pcre2Pattern
{
    public abstract Pcre2Profile Profile { get; }
    public abstract int GroupCount { get; }
    public abstract Pcre2ProjectionRestrictions ProjectionRestrictions { get; }
    public abstract Pcre2Workspace CreateWorkspace(Pcre2WorkspaceOptions? options = null);
    public abstract bool TryProject(Pcre2ProjectionPolicy policy, out RegexPattern? pattern, out Pcre2Diagnostic diagnostic);
    public abstract ReadOnlySpan<int> GetGroupNumbers(ReadOnlySpan<char> name);
    public abstract ReadOnlySpan<Pcre2NamedGroup> GetNamedGroups();
    public abstract int GroupNumberFromName(ReadOnlySpan<char> name);
    // Semantic scalar queries only; native bitmap/layout/JIT/bytecode-size keys
    // return a structured PH004/PH012 capability exclusion, not bogus zero values.
    public abstract bool TryGetInfo(Pcre2PatternInfoKey key, out long value, out Pcre2Diagnostic diagnostic);
    public abstract ReadOnlySpan<byte> GetMarkBytes(int markIndex);
    public abstract ReadOnlySpan<char> GetMarkChars(int markIndex);
    public abstract ReadOnlySpan<Pcre2CalloutSiteInfo> GetCalloutSites();
    public abstract ReadOnlySpan<byte> GetCalloutStringBytes(int siteIndex);
    public abstract ReadOnlySpan<char> GetCalloutStringChars(int siteIndex);
}
internal abstract class Pcre2Workspace : IDisposable
{
    // Only populated by COPY_MATCHED_SUBJECT; owned until next operation/disposal.
    public abstract ReadOnlySpan<char> CopiedSubjectChars { get; }
    public abstract ReadOnlySpan<byte> CopiedSubjectBytes { get; }
    public abstract void Reserve(Pcre2WorkspaceOptions capacity);
    public abstract Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null);
    public abstract Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null);
    public abstract Pcre2SubstituteResult Substitute(ReadOnlySpan<char> input, int startAt, ReadOnlySpan<char> replacement,
        Span<char> destination, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null, Pcre2HostLimits? limits = null,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs = default, Pcre2MatchResult precomputedResult = default);
    public abstract Pcre2SubstituteResult Substitute(ReadOnlySpan<byte> input, int startAt, ReadOnlySpan<byte> replacement,
        Span<byte> destination, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null, Pcre2HostLimits? limits = null,
        ReadOnlySpan<Pcre2OffsetPair> precomputedPairs = default, Pcre2MatchResult precomputedResult = default);
    public abstract Pcre2Iteration CreateIteration(int startAt = 0, Pcre2MatchOptions options = default,
        Pcre2CharCallbacks? charCallbacks = null, Pcre2ByteCallbacks? byteCallbacks = null, Pcre2HostLimits? limits = null);
    public abstract void Dispose();
}
internal abstract class Pcre2Iteration : IDisposable
{
    // Session owns the exclusive workspace/cumulative budget, never a borrowed
    // span. Caller supplies the original subject each time, with revalidation.
    public abstract Pcre2MatchResult Next(ReadOnlySpan<char> originalInput, Span<Pcre2OffsetPair> pairs);
    public abstract Pcre2MatchResult Next(ReadOnlySpan<byte> originalInput, Span<Pcre2OffsetPair> pairs);
    public abstract void Dispose();
}
internal static class CompileSamples
{
    internal static Pcre2MatchResult Native16(Pcre2Functions api, ReadOnlySpan<char> input)
    {
        var profile = new Pcre2Profile(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp,
            extraFlags: Pcre2ExtraFlags.AllowLookaroundBsk);
        var pattern = api.Parse("(?=ab\\K)", profile);
        using var workspace = pattern.CreateWorkspace();
        Span<Pcre2OffsetPair> pairs = stackalloc Pcre2OffsetPair[1];
        var result = workspace.Match(input, 0, pairs, new() { Flags = Pcre2MatchFlags.PartialHard });
        if (result.Status == Pcre2MatchStatus.Match && pairs[0].TryGetRange(out var range))
        {
            _ = input.Slice(range.Index, range.Length);
        }

        if (result.MarkIndex >= 0)
        {
            _ = pattern.GetMarkChars(result.MarkIndex);
        }

        _ = pattern.GetGroupNumbers("p");
        _ = pattern.GetCalloutSites();
        _ = pattern.TryProject(Pcre2ProjectionPolicy.WholeSubjectValidated, out _, out _);
        return result;
    }
    internal static Pcre2SubstituteResult Native8(Pcre2Functions api, ReadOnlySpan<byte> input, Span<byte> output)
    {
        var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.AutoCallout,
            optimization: Pcre2OptimizationPolicy.None);
        var pattern = api.Parse("(a)(?C1)"u8, profile);
        using var workspace = pattern.CreateWorkspace(new(frames: 16, captureEvents: 32, windows: 8));
        workspace.Reserve(new(frames: 32));
        var callbacks = new Pcre2ByteCallbacks(Match: Inspect, Substitute: Accept, Case: Case);
        Span<Pcre2OffsetPair> pairs = stackalloc Pcre2OffsetPair[2];
        _ = workspace.Match(input, 0, pairs, callbacks: callbacks);
        using (var iterator = workspace.CreateIteration(byteCallbacks: callbacks))
        {
            _ = iterator.Next(input, pairs);
        }

        return workspace.Substitute(input, 0, "$1"u8, output, Pcre2SubstituteFlags.Global,
            callbacks: callbacks, limits: new(maxOutputUnits: 1024));
    }
    private static int Inspect(in Pcre2ByteCalloutContext context, object? state)
    { _ = context.Subject.Length; _ = context.Captures.Length; _ = context.Info.Version; return 0; }
    private static int Accept(in Pcre2ByteSubstituteContext context, object? state)
    { _ = context.Output.Length; _ = context.Info.SubstitutionCount; return 0; }
    private static int Case(ReadOnlySpan<byte> input, Span<byte> output, Pcre2SubstituteCase mode, object? state)
    { if (input.Length <= output.Length) { input.CopyTo(output); } return input.Length; }
    internal static bool Compile(IPcre2Compiler compiler, IPcre2Data data, ReadOnlySpan<byte> source,
        ref CompileBudget budget, out Pcre2Program? program, out Pcre2Diagnostic diagnostic)
        => compiler.TryCompile(source, new(Pcre2CodeUnitWidth.Eight), data, ref budget, out program, out diagnostic);
    internal static Pcre2Step Kernel<TKernel, TReader>(Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TKernel : struct, IPcre2VmKernel where TReader : struct, IPcre2TextReader<byte>
        => TKernel.Step<byte, TReader>(program, input, data, default, ref machine, ref state);
    internal static Pcre2Step Assertion<TAssertion, TReader>(Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TAssertion : struct, IPcre2AssertionOps where TReader : struct, IPcre2TextReader<char>
        => TAssertion.Step<char, TReader>(program, input, data, ref machine, ref state);
    internal static Pcre2Step Recursion<TRecursion>(Pcre2Program program, ref Pcre2VmState machine, ref RunState state)
        where TRecursion : struct, IPcre2RecursionOps => TRecursion.Step(program, default, ref machine, ref state);
    internal static Pcre2Step Unicode<TUnicode, TReader>(Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2DataView data, ref Pcre2VmState machine, ref RunState state)
        where TUnicode : struct, IPcre2UnicodeOps where TReader : struct, IPcre2TextReader<char>
        => TUnicode.Step<char, TReader>(program, input, data, ref machine, ref state);
    internal static Pcre2Step Controls<TControls>(Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs, ref Pcre2VmState machine, ref RunState state)
        where TControls : struct, IPcre2Controls => TControls.Notify(Pcre2ExecutionEvent.EnterNode,
            program, input, callbacks, callbackPairs, ref machine, ref state);
}
