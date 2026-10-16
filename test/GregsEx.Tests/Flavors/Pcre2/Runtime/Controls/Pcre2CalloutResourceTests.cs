using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2CalloutResourceTests
{
    [Fact]
    public void FreshWorkspaceFieldDoesNotInventUnexecutedMarkAtFirstCallout()
    {
        // (?C1)a(*MARK:M): immutable mark payload exists, but it has not run.
        var p = new ControlPlan();
        p.Mark('M');
        p.Callout(1);
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var machine = new Pcre2VmState(capacity, new(), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        Pcre2ControlState controls = default;
        var callbacks = new Pcre2CharCallbacks((in c, _) =>
        {
            Assert.False(c.Info.HasMark);
            Assert.True(c.Mark.IsEmpty);
            return 0;
        });
        Assert.Equal(Pcre2StepKind.Continue,
            Pcre2Callouts.Invoke(program, "", callbacks, new Pcre2OffsetPair[1], ref controls, ref machine, ref state).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DispatchBoundAndPostCallbackCancellationAreSticky(bool cancel)
    {
        var p = new ControlPlan();
        p.Callout();
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var limits = new Pcre2MatchLimits(maxCallouts: cancel ? 1 : 0);
        using var cancellation = new CancellationTokenSource();
        var options = new Pcre2MatchOptions { CancellationToken = cancellation.Token };
        var machine = new Pcre2VmState(capacity, limits, 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits, options);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        var controls = new Pcre2ControlState();
        var count = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) => { count++; cancellation.Cancel(); return 0; });
        var result = Pcre2Callouts.Invoke(program, "", callbacks, new Pcre2OffsetPair[1], ref controls, ref machine, ref state);
        Assert.Equal(Pcre2StepKind.ResourceFailure, result.Kind);
        Assert.Equal(cancel ? 1 : 0, count);
        Assert.Equal(cancel ? RegexExecutionFailureCode.Cancelled : RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        Assert.Equal(Pcre2StepKind.ResourceFailure,
            Pcre2Callouts.Invoke(program, "", callbacks, new Pcre2OffsetPair[1], ref controls, ref machine, ref state).Kind);
        Assert.Equal(cancel ? 1 : 0, count);
    }

    [Fact]
    public void CallbackUsesPreparedFullCaptureVectorAndKeepsPreviousOpenCapture()
    {
        var p = new ControlPlan();
        p.Seq(p.Capture(p.Lit('a'), 1), p.Callout());
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(2);
        var machine = new Pcre2VmState(capacity, new(), 4);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        var storage = new Pcre2Storage(state.Buffers, capacity, 2);
        Assert.True(storage.WriteCapture(1, 0, 1, ref machine, ref state));
        machine.CaptureTop = 2;
        machine.CaptureLast = 1;
        machine.Node = 2;
        var controls = new Pcre2ControlState();
        var callbacks = new Pcre2CharCallbacks((in c, _) =>
        {
            Assert.Equal(2, c.Captures.Length);
            Assert.False(c.Captures[0].Participated);
            Assert.Equal(new(true, 0, 1), c.Captures[1]);
            Assert.Equal(2, c.Info.CaptureTop);
            Assert.Equal(1, c.Info.CaptureLast);
            return 0;
        });
        Assert.Equal(Pcre2StepKind.Continue,
            Pcre2Callouts.Invoke(program, "abcd", callbacks, new Pcre2OffsetPair[2], ref controls, ref machine, ref state).Kind);
        Assert.Equal((0, 1), storage.Registers(1));
    }

    [Fact]
    public void UserExceptionPropagatesWithoutConvertingToNoMatch()
    {
        var p = new ControlPlan();
        p.Callout();
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var machine = new Pcre2VmState(capacity, new(), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        var controls = new Pcre2ControlState();
        var callbacks = new Pcre2CharCallbacks((in c, _) => throw new ApplicationException("user-callout"));
        try
        {
            Pcre2Callouts.Invoke(program, "", callbacks, new Pcre2OffsetPair[1], ref controls, ref machine, ref state);
            Assert.Fail("User callback must throw.");
        }
        catch (ApplicationException error) { Assert.Equal("user-callout", error.Message); }
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
        Assert.Equal(0, machine.SourceReturnCode);
    }
}
