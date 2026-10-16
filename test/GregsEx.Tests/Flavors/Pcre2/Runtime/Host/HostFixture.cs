using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

// Test-only interface binding; production executor/lifecycle belongs to runtime.
internal sealed class HostFixture : IPcre2Executor
{
    public Pcre2Program Program { get; }
    internal readonly Pcre2ControlPlan Plan;
    internal readonly Pcre2ExecutionCapacity Capacity = new(1024, 16384, 64, 512);
    internal readonly int[] States, Captures, Scratch;
    internal readonly Pcre2OffsetPair[] CallbackPairs;
    internal bool Released;
    internal long Work;
    internal HostFixture(string pattern, Pcre2Profile? profile = null)
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2Compiler().TryCompile(pattern, profile ?? new(), Pcre2Data.Instance, ref budget, out var program, out var diagnostic), diagnostic.ToString());
        Program = program!;
        Assert.True(Pcre2ControlPlan.TryCreate(Program, ref budget, out var plan, out diagnostic), diagnostic.ToString());
        Plan = plan!;
        var required = Capacity.Required(Program.GroupCount);
        States = new int[required.StateSlots];
        Captures = new int[required.CaptureSlots];
        Scratch = new int[required.ScratchSlots];
        CallbackPairs = new Pcre2OffsetPair[Program.GroupCount];
    }
    public Pcre2StorageRequirements GetRequiredCapacity(Pcre2ExecutionCapacity capacity) => capacity.RequiredStorage(Program.GroupCount);
    public Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks,
        Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs)
    {
        var machine = new Pcre2VmState(capacity, options.Limits ?? new(), input.Length, Program.PatternLimits, startAt);
        try
        { return Pcre2ControlSearch.Find(Plan, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables), options, policy, callbacks, callbackPairs, ref machine, ref state, pairs); }
        finally { Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0; }
    }
    public Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks,
        Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy, ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs)
    {
        var machine = new Pcre2VmState(capacity, options.Limits ?? new(), input.Length, Program.PatternLimits, startAt);
        try
        { return Pcre2ControlSearch.Find(Plan, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables), options, policy, callbacks, callbackPairs, ref machine, ref state, pairs); }
        finally { Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0; }
    }
    internal RunState State(Pcre2MatchOptions options = default) => Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch),
        Pcre2OperationLimits.Resolve(options.Limits ?? new(), Program.PatternLimits), options);
    internal Pcre2SubstituteResult Substitute(string input, string replacement, Span<char> destination, Pcre2SubstituteFlags flags = default,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null, Pcre2HostLimits? limits = null, int start = 0,
        ReadOnlySpan<Pcre2OffsetPair> precomputed = default, Pcre2MatchResult result = default)
    {
        var state = State(options);
        try
        { return new Pcre2Host().Substitute(this, input, start, replacement, destination, flags, options, callbacks, limits ?? new(), precomputed, result, Capacity, ref state, CallbackPairs); }
        finally { Work = state.Budget.WorkConsumed; }
    }
    internal Pcre2SubstituteResult Substitute(byte[] input, byte[] replacement, Span<byte> destination, Pcre2SubstituteFlags flags = default,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null, Pcre2HostLimits? limits = null, int start = 0,
        ReadOnlySpan<Pcre2OffsetPair> precomputed = default, Pcre2MatchResult result = default)
    {
        var state = State(options);
        try
        { return new Pcre2Host().Substitute(this, input, start, replacement, destination, flags, options, callbacks, limits ?? new(), precomputed, result, Capacity, ref state, CallbackPairs); }
        finally { Work = state.Budget.WorkConsumed; }
    }
}
