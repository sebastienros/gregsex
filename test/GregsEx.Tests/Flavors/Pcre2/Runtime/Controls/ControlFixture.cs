using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

internal sealed class ControlFixture
{
    internal readonly Pcre2Program Program;
    internal readonly Pcre2ControlPlan Plan;
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly int[] States, Captures, Scratch;
    internal readonly Pcre2OffsetPair[] CallbackPairs;
    internal long Work;
    internal bool Released;
    internal ControlFixture(string pattern, Pcre2Profile? profile = null, Pcre2ExecutionCapacity? capacity = null)
        : this(CompileContext(pattern, profile ?? new()), capacity) { }
    internal ControlFixture(Pcre2Program program, Pcre2ExecutionCapacity? capacity = null)
        : this(Context(program), capacity) { }
    internal ControlFixture(Pcre2ControlPlan plan, Pcre2ExecutionCapacity? capacity = null)
    {
        Plan = plan;
        Program = plan.Program;
        var program = Program;
        Capacity = capacity ?? new(1024, 16384, 64, 512);
        var required = Capacity.Required(program.GroupCount);
        States = new int[required.StateSlots];
        Captures = new int[required.CaptureSlots];
        Scratch = new int[required.ScratchSlots];
        CallbackPairs = new Pcre2OffsetPair[program.GroupCount];
    }
    private static Pcre2ControlPlan CompileContext(string pattern, Pcre2Profile profile)
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2Compiler().TryCompile(pattern, profile, Pcre2Data.Instance, ref budget,
            out var program, out var diagnostic), diagnostic.ToString());
        Assert.True(Pcre2ControlPlan.TryCreate(program!, ref budget, out var plan, out diagnostic), diagnostic.ToString());
        return plan!;
    }
    private static Pcre2ControlPlan Context(Pcre2Program program)
    {
        var budget = new CompileBudget(new());
        Assert.True(Pcre2ControlPlan.TryCreate(program, ref budget, out var plan, out var diagnostic), diagnostic.ToString());
        return plan!;
    }
    internal Pcre2MatchResult Match(string input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default,
        Pcre2CharCallbacks? callbacks = null, Pcre2MatchLimits? limits = null, int start = 0,
        Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(limits ?? new()), input.Length, Program.PatternLimits, start);
        var state = Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        try
        {
            return Pcre2ControlSearch.Find(Plan, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables), options,
            policy, callbacks, CallbackPairs, ref machine, ref state, pairs);
        }
        finally { Work = state.Budget.WorkConsumed; Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0; }
    }
    internal Pcre2MatchResult Match(byte[] input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default,
        Pcre2ByteCallbacks? callbacks = null, Pcre2MatchLimits? limits = null, int start = 0,
        Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(limits ?? new()), input.Length, Program.PatternLimits, start);
        var state = Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        try
        {
            return Pcre2ControlSearch.Find(Plan, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables), options,
            policy, callbacks, CallbackPairs, ref machine, ref state, pairs);
        }
        finally { Work = state.Budget.WorkConsumed; Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0; }
    }
    internal Pcre2MatchResult Ascii(string input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default,
        Pcre2MatchLimits? limits = null) => Program.Profile.Width == Pcre2CodeUnitWidth.Eight
        ? Match([.. input.Select(c => checked((byte)c))], pairs, options, limits: limits)
        : Match(input, pairs, options, limits: limits);
    internal static Pcre2Profile Profile(int domain, Pcre2CompileFlags flags = default) =>
        new(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            flags | (domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0));
}
