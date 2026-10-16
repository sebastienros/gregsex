using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2MarkTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void SkipNamesUseSourceTerminatorWhileNativePayloadLengthRemainsIntact(bool bytes, bool samePrefix)
    {
        // Source strcmp102–111 stops at NUL. Getter/callout length semantics do
        // not change this SKIP:argument matching operation (match.c6656).
        var p = new ControlPlan(new(bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen));
        int a = p.Mark(65, 0, 66), b = p.Mark(samePrefix ? 65 : 67, 0, 68);
        p.Leaf(Pcre2NodeKind.Empty);
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var machine = new Pcre2VmState(capacity, new(), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.Equal(samePrefix, Pcre2Marks.SameSkipName(program, a, b, ref machine, ref state));
        Assert.Equal(3, program.Marks[a].Count);
        Assert.Equal(3, program.Marks[b].Count);
        if (bytes)
        {
            Assert.Equal(new byte[] { 65, 0, 66 }, program.NativeBytes(program.Marks[a]).ToArray());
        }
        else
        {
            Assert.Equal("A\0B", program.NativeChars(program.Marks[a]).ToString());
        }
    }

    [Fact]
    public void NameComparisonIsBoundedAndDoesNotTreatDenialAsEqual()
    {
        var p = new ControlPlan();
        int a = p.Mark(65, 65, 65), b = p.Mark(65, 65, 65);
        p.Leaf(Pcre2NodeKind.Empty);
        var program = p.Build();
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(1);
        var machine = new Pcre2VmState(capacity, new(maxWork: 3), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.False(Pcre2Marks.SameSkipName(program, a, b, ref machine, ref state));
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, machine.SourceReturnCode);
    }
}
