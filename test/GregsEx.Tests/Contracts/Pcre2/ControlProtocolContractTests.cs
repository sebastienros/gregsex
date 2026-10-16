using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Contracts.Pcre2;

public sealed class ControlProtocolContractTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NamedControlFramesUseTheSingleVersionThreeSerializer(int value)
    {
        var kind = value == 0 ? Pcre2FrameKind.Branch : Pcre2FrameKind.Verb;
        Assert.Equal(3, Pcre2Frame.LayoutVersion);
        Assert.Equal(26, Pcre2Frame.Slots);
        var capacity = new Pcre2ExecutionCapacity(2, 4, 1, 1);
        var size = capacity.Required(1);
        var s = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var m = new Pcre2VmState(capacity, new(), 3);
        var storage = new Pcre2Storage(s.Buffers, capacity, 1);
        var frame = new Pcre2Frame(kind, 1, 2, 3, 1, 0, 0, 1, 2, 3, -5, 0, 5, 6,
            Pcre2CalloutFlags.Backtrack, -1, RecurseLastUsedPosition: 3, HasSavedWindow: true,
            SavedWindow: new(2, 1, 3, 1, 0, 0), CallActive: false, NoteolCleared: true);
        Assert.True(storage.PushFrame(frame, ref m, ref s));
        Assert.True(storage.ReadFrame(0, ref m, ref s, out var read));
        Assert.Equal(frame, read);
        Assert.True(storage.PopFrame(ref m, ref s, out var popped));
        Assert.Equal(frame, popped);
        Assert.Equal(0, m.CallDepth);
    }

    [Fact]
    public void ExplicitTopReturnPollsCancellationBeforeZeroWorkAndCannotSetFlags()
    {
        var p = new KernelPlan();
        p.Leaf(Pcre2NodeKind.Empty);
        var f = new KernelFixture(p.Build());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var m = new Pcre2VmState(f.Capacity, new(maxWork: 0), 0);
        var s = f.State(m.Limits, new() { CancellationToken = cancelled.Token });
        Assert.False(Pcre2Kernel.NotifyRmatchReturn(false, ref m, ref s));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, s.Failure.Code);
        Assert.Equal(0, m.SourceReturnCode);
        Assert.Equal(0, (int)m.PendingFlags);
        Assert.False(Pcre2Kernel.UnwindToContinuation(f.Program, -1, true, ref m, ref s));
        Assert.False(Pcre2Kernel.ReadActiveContinuation(f.Program, -1, ref m, ref s, out _, out _));
        Assert.Equal(Pcre2StepKind.ResourceFailure, Pcre2Kernel.FinishAccept(f.Program, 0, ref m, ref s).Kind);
        Assert.Equal(0, m.FrameCount);
    }
}
