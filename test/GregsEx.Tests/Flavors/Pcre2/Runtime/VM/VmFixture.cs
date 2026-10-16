using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Runtime.VM;
using GregsEx.Flavors.Pcre2.Tables;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.VM;

internal sealed class VmFixture
{
    internal readonly Pcre2Program Program;
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly int[] States, Captures, Scratch;
    internal long Work;
    internal bool Released;
    internal VmFixture(Pcre2Program program, Pcre2ExecutionCapacity? capacity = null)
    {
        Program = program;
        Capacity = capacity ?? new(1024, 16384, 64, 512);
        var required = Capacity.Required(program.GroupCount);
        States = new int[required.StateSlots];
        Captures = new int[required.CaptureSlots];
        Scratch = new int[required.ScratchSlots];
    }
    internal Pcre2MatchResult Match(string input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default,
        Pcre2MatchLimits? limits = null, int start = 0, bool hasExplicitCrOrLf = true, Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(limits ?? new()), input.Length, Program.PatternLimits, start);
        var state = Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        var result = Pcre2VmSearch.FindWithoutControls(Program, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables),
            options, policy, hasExplicitCrOrLf, ref machine, ref state, pairs);
        Work = state.Budget.WorkConsumed;
        Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0;
        return result;
    }
    internal Pcre2MatchResult Match(byte[] input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default,
        Pcre2MatchLimits? limits = null, int start = 0, bool hasExplicitCrOrLf = true, Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(limits ?? new()), input.Length, Program.PatternLimits, start);
        var state = Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        var result = Pcre2VmSearch.FindWithoutControls(Program, input, Pcre2Data.Instance.Bind(Program.Profile.CharacterTables),
            options, policy, hasExplicitCrOrLf, ref machine, ref state, pairs);
        Work = state.Budget.WorkConsumed;
        Released = machine.FrameCount == 0 && machine.CallDepth == 0 && machine.WindowDepth == 0;
        return result;
    }
}
