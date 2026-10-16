using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2CalloutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealCalloutPreservesNativePayloadAndSourceFields(bool bytes)
    {
        var p = new ControlPlan(new(bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen));
        p.Mark(77, 0, 88);
        p.Callout(text: [65, 0, 66]);
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(16, 64, 8, 32);
        var required = capacity.Required(program.GroupCount);
        var buffers = new WorkspaceBuffers(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]);
        var machine = new Pcre2VmState(capacity, new(), 4, startAt: 1);
        var state = Pcre2KernelOperation.CreateRunState(buffers, machine.Limits);
        Assert.True(Pcre2KernelOperation.PollEntry(program, ref machine, ref state));
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        machine.Position = 3;
        machine.ReportStart = 2;
        machine.PendingFlags |= Pcre2CalloutFlags.Backtrack;
        var controls = new Pcre2ControlState { NomatchMarkIndex = 0 };
        var pairs = new Pcre2OffsetPair[1];
        var count = 0;
        Pcre2CalloutInfo info = default;
        Pcre2Step step;
        if (bytes)
        {
            var callbacks = new Pcre2ByteCallbacks((in c, _) =>
            {
                count++;
                info = c.Info;
                Assert.Equal(new byte[] { 77, 0, 88 }, c.Mark.ToArray());
                Assert.Equal(new byte[] { 65, 0, 66 }, c.CalloutString.ToArray());
                Assert.False(c.Captures[0].Participated);
                Assert.Equal(new byte[] { 0, 97, 98, 99 }, c.Subject.ToArray());
                return 0;
            });
            step = Pcre2Callouts.Invoke(program, [0, 97, 98, 99], callbacks, pairs, ref controls, ref machine, ref state);
        }
        else
        {
            var callbacks = new Pcre2CharCallbacks((in c, _) =>
            {
                count++;
                info = c.Info;
                Assert.Equal("M\0X", c.Mark.ToString());
                Assert.Equal("A\0B", c.CalloutString.ToString());
                Assert.False(c.Captures[0].Participated);
                Assert.Equal("\0abc", c.Subject.ToString());
                return 0;
            });
            step = Pcre2Callouts.Invoke(program, "\0abc", callbacks, pairs, ref controls, ref machine, ref state);
        }
        Assert.Equal(1, count);
        Assert.Equal(1, machine.CalloutCount);
        Assert.Equal(Pcre2StepKind.Continue, step.Kind);
        Assert.Equal(Pcre2CalloutFlags.None, machine.PendingFlags);
        Assert.Equal(new Pcre2CalloutInfo(0, true, true, 4, 1, 0, 1, 3, 1, 0, 0, 3,
            Pcre2CalloutFlags.Startmatch | Pcre2CalloutFlags.Backtrack), info);
    }

    [Theory]
    [InlineData(0, (int)Pcre2StepKind.Continue)]
    [InlineData(1, (int)Pcre2StepKind.Continue)]
    [InlineData(-1009, (int)Pcre2StepKind.SourceAbort)]
    public void CallbackReturnIsExecutedAndAbortIdentityIsUnaltered(int result, int expected)
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
        var invoked = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) => { invoked++; return result; });
        var step = Pcre2Callouts.Invoke(program, "", callbacks, new Pcre2OffsetPair[1], ref controls, ref machine, ref state);
        Assert.Equal(1, invoked);
        Assert.Equal((Pcre2StepKind)expected, step.Kind);
        Assert.Equal(result < 0 ? result : 0, machine.SourceReturnCode);
        Assert.Equal(result < 0, controls.CallbackAborted);
        if (result >= 0)
        {
            var continuation = Pcre2Kernel.Step<char, Pcre2CharTextReader>(program, "", default, default, ref machine, ref state);
            Assert.Equal(result == 0 ? Pcre2StepKind.Accept : Pcre2StepKind.Backtrack, continuation.Kind);
        }
    }

    [Fact]
    public void AbsentCallbackLeavesPendingFlagsAndDispatchCountUnchanged()
    {
        var p = new ControlPlan();
        p.Callout(255, automatic: true);
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var machine = new Pcre2VmState(capacity, new(), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        var controls = new Pcre2ControlState();
        var step = Pcre2Callouts.Invoke(program, "", null, [], ref controls, ref machine, ref state);
        Assert.Equal(Pcre2StepKind.Continue, step.Kind);
        Assert.Equal(0, machine.CalloutCount);
        Assert.Equal(Pcre2CalloutFlags.Startmatch, machine.PendingFlags);
    }
}
