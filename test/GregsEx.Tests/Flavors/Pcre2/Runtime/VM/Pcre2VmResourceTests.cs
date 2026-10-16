using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Runtime.VM;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.VM;

public sealed class Pcre2VmResourceTests
{
    private static VmFixture Mixed(Pcre2ExecutionCapacity? capacity = null, Pcre2PatternLimits limits = default)
    {
        var p = new VmPlan(Pcre2VmCompositionTests.Profile(3));
        var routine = p.Define(p.Capture(p.Seq(p.Assertion(p.Leaf(Pcre2NodeKind.Grapheme)),
            p.Script(p.Repeat(p.Capture(p.Leaf(Pcre2NodeKind.Grapheme), 2), 2, 2))), 1));
        p.Seq(routine, p.Call(1));
        return new(p.Build(limits), capacity);
    }

    [Fact]
    public void OneBudgetCoversValidationModulesCandidatesAndPublication()
    {
        var f = Mixed();
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("xxaa", pairs, start: 2).Status);
        var needed = f.Work;
        Assert.True(needed > 0);
        Pcre2OffsetPair[] sentinel = [new(true, 9, 10), new(true, 9, 10), new(true, 9, 10)];
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("xxaa", sentinel, limits: new(maxWork: needed - 1), start: 2).Status);
        Assert.All(sentinel, value => Assert.Equal(new(true, 9, 10), value));
        Assert.True(f.Work <= needed - 1);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("xxaa", pairs, limits: new(maxWork: needed), start: 2).Status);
        var states = f.States;
        var scratch = f.Scratch;
        var captures = f.Captures;
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match(new string('α', 80) + "ab", sentinel, limits: new(maxWork: 200)).Status);
        Assert.Same(states, f.States);
        Assert.Same(scratch, f.Scratch);
        Assert.Same(captures, f.Captures);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PhysicalAndLogicalCeilingsFailThenWorkspaceIsReusable(int ceiling)
    {
        var f = Mixed();
        var pairs = new Pcre2OffsetPair[3];
        var limits = ceiling switch
        {
            0 => new Pcre2MatchLimits(maxWork: 0),
            1 => new Pcre2MatchLimits(maxDepth: 0),
            2 => new Pcre2MatchLimits(maxWorkspaceBytes: f.Capacity.RequiredStorage(3).RequiredBytes - 1),
            3 => new Pcre2MatchLimits(maxCaptureEvents: 1),
            _ => new Pcre2MatchLimits(maxDepth: 1)
        };
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("ab", pairs, limits: limits).Status);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs).Status);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, Mixed(new(2, 256, 2, 64)).Match("ab", pairs).Status);
    }

    [Fact]
    public void PatternLimitsReduceOuterBudgetBeforeTextAndNeverResetInsideCalls()
    {
        var f = Mixed(limits: new(MatchWork: 40, Depth: null, HeapBytes: null));
        var pairs = new Pcre2OffsetPair[3];
        var result = f.Match("ab", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.True(f.Work <= 40);
        var zero = Mixed(limits: new(MatchWork: 0, Depth: null, HeapBytes: null));
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, zero.Match("\ud800", pairs).NativeReturnCode);
        Assert.Equal(0, zero.Work);
    }

    [Fact]
    public void CancelAndDeadlinePrecedeNativeZeroAndInvalidEncoding()
    {
        var f = Mixed();
        Pcre2OffsetPair[] pairs = [new(true, 2, 4)];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(Pcre2MatchStatus.Cancelled, f.Match("\ud800", pairs,
            new() { CancellationToken = cts.Token }, new(maxWork: 0, maxDepth: 0, maxWorkspaceBytes: 0)).Status);
        Assert.Equal(Pcre2MatchStatus.TimedOut, f.Match("\ud800", pairs,
            new() { Timeout = TimeSpan.FromTicks(1) }, new(maxWork: 0)).Status);
        Assert.Equal(new(true, 2, 4), pairs[0]);
        Assert.True(f.Released);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs).Status);
    }

    [Fact]
    public void HostRetryWithSameRunStateCannotAcquireFreshWork()
    {
        var p = new VmPlan();
        p.Script(p.Lit('a'));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("a", pairs).Status);
        var firstWork = f.Work;
        var machine = new Pcre2VmState(f.Capacity, new(maxWork: firstWork + 1), 1);
        var state = Pcre2KernelOperation.CreateRunState(new(f.States, f.Captures, f.Scratch), machine.Limits);
        var data = Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables);
        Assert.Equal(Pcre2MatchStatus.Match, Pcre2VmSearch.FindWithoutControls(f.Program, "a", data,
            default, Pcre2ValidationPolicy.NativeWindow, false, ref machine, ref state, pairs).Status);
        Assert.Equal(firstWork, state.Budget.WorkConsumed);
        machine = new(f.Capacity, new(maxWork: firstWork + 1), 1);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, Pcre2VmSearch.FindWithoutControls(f.Program, "a", data,
            default, Pcre2ValidationPolicy.NativeWindow, false, ref machine, ref state, pairs).Status);
        Assert.True(state.Budget.WorkConsumed <= firstWork + 1);
    }

    [Fact]
    public void V2SizedBuffersAreDeniedBeforeAnyTextRead()
    {
        var f = Mixed();
        var required = f.Capacity.Required(3);
        var machine = new Pcre2VmState(f.Capacity, new(), 2);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots - f.Capacity.Frames],
            f.Captures, f.Scratch), machine.Limits);
        var result = Pcre2VmSearch.FindWithoutControls(f.Program, "\ud800x", Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables),
            default, Pcre2ValidationPolicy.NativeWindow, false, ref machine, ref state, new Pcre2OffsetPair[3]);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, result.Failure.Code);
        Assert.Equal(0, state.Budget.WorkConsumed);
    }

    [Fact]
    public void SharedImmutablePlanHasIndependentWorkspacesAndNoBorrowedSubjectRetention()
    {
        var p = new VmPlan(Pcre2VmCompositionTests.Profile(1));
        p.Script(p.Repeat(p.Leaf(Pcre2NodeKind.Grapheme), 2, 2));
        var f = new VmFixture(p.Build());
        byte[] input = [97, 98];
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs, Pcre2VmCompositionTests.Anchored).Status);
        input[0] = 0xff;
        Assert.Equal(Pcre2MatchStatus.Error, f.Match(input, pairs).Status);
        input[0] = 97;
        Parallel.For(0, 8, _ => Assert.Equal(Pcre2MatchStatus.Match,
            new VmFixture(f.Program).Match(input, new Pcre2OffsetPair[1], Pcre2VmCompositionTests.Anchored).Status));
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(input, pairs).Status);
        Assert.True(f.Released);
    }

    [Fact]
    public void ActiveCancellationStopsNestedExecutionAndPreventsPublication()
    {
        var p = new VmPlan();
        p.Script(p.Repeat(p.Lit('a'), 2, 2));
        var f = new VmFixture(p.Build());
        using var cts = new CancellationTokenSource();
        var options = new Pcre2MatchOptions { CancellationToken = cts.Token };
        var machine = new Pcre2VmState(f.Capacity, new(), 2);
        var state = Pcre2KernelOperation.CreateRunState(new(f.States, f.Captures, f.Scratch), machine.Limits, options);
        Assert.True(Pcre2KernelOperation.PollEntry(f.Program, ref machine, ref state));
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        var data = Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Vm.Step<char, Pcre2CharTextReader>(f.Program, "aa", data, options, ref machine, ref state).Kind);
        Assert.True(machine.FrameCount > 0);
        cts.Cancel();
        Assert.Equal(Pcre2StepKind.ResourceFailure, Pcre2Vm.RunCandidate<char, Pcre2CharTextReader>(f.Program, "aa", data, options, ref machine, ref state).Kind);
        Pcre2OffsetPair[] pairs = [new(true, 8, 9)];
        Assert.Equal(Pcre2MatchStatus.Cancelled, Pcre2Kernel.Publish(f.Program, ref machine, ref state, pairs).Status);
        Assert.Equal(new(true, 8, 9), pairs[0]);
        Pcre2KernelOperation.ReleaseTransient(ref machine);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("aa", pairs).Status);
    }
}
