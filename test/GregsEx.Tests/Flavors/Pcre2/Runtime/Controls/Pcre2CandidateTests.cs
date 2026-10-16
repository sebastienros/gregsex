using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2CandidateTests
{
    private static (bool Admitted, int Position, RegexExecutionFailure Failure) Admit(Pcre2Program program,
        string input, bool anchored = false, Pcre2MatchFlags flags = default, int start = 0, bool firstline = false)
    {
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(program.GroupCount);
        var machine = new Pcre2VmState(capacity, new(), input.Length, startAt: start);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        var policy = new Pcre2CandidatePolicy();
        var admitted = policy.Admit<char, Pcre2CharTextReader, Pcre2CharUnits>(program, input,
            Pcre2Data.Instance.Bind(program.Profile.CharacterTables), flags, anchored, firstline, ref machine, ref state);
        return (admitted, machine.CandidateStart, state.Failure);
    }

    [Fact]
    public void StartOptimizationIsObservableAndLegacyOffWins()
    {
        var p = new ControlPlan();
        p.Lit('a');
        var program = p.Build(new(1, 0, false, FirstCodeUnit: 'a'));
        Assert.Equal((true, 3, default), Admit(program, "xxxa"));
        Assert.False(Admit(program, "xxxa", anchored: true).Admitted);
        var off = new ControlPlan(new(flags: Pcre2CompileFlags.NoStartOptimize));
        off.Lit('a');
        Assert.Equal((true, 0, default), Admit(off.Build(new(100, 0, false, FirstCodeUnit: 'a', RequiredCodeUnit: 'b')), "xxxa"));
        var none = new ControlPlan(new(optimization: Pcre2OptimizationPolicy.None));
        none.Lit('a');
        Assert.True(Admit(none.Build(new(99, 0, false)), "").Admitted);
    }

    [Theory]
    [InlineData((uint)Pcre2MatchFlags.PartialSoft)]
    [InlineData((uint)Pcre2MatchFlags.PartialHard)]
    public void PartialRetainsFirstFilterButDisablesMinimumAndRequired(uint flags)
    {
        var p = new ControlPlan();
        p.Lit('a');
        var program = p.Build(new(4, 0, false, FirstCodeUnit: 'a', RequiredCodeUnit: 'z'));
        Assert.False(Admit(program, "xxa").Admitted);
        Assert.Equal((true, 2, default), Admit(program, "xxa", flags: (Pcre2MatchFlags)flags));
        Assert.Equal((true, 3, default), Admit(program, "xxx", flags: (Pcre2MatchFlags)flags));
        Assert.False(Admit(program, "xxx", anchored: true, flags: (Pcre2MatchFlags)flags).Admitted);
    }

    [Fact]
    public void RequiredUnitStartsAfterFirstUnitAndUsesImportedFlip()
    {
        var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
        tables[256 + 'z'] = (byte)'X';
        var p = new ControlPlan(new(characterTables: new(tables)));
        p.Lit('a');
        var program = p.Build(new(1, 0, false, FirstCodeUnit: 'a', RequiredCodeUnit: 'z', RequiredCodeUnitCaseless: true));
        Assert.True(Admit(program, "aX").Admitted);
        Assert.False(Admit(program, "az", start: 1).Admitted);
        var q = new ControlPlan();
        q.Lit('a');
        Assert.False(Admit(q.Build(new(1, 0, false, FirstCodeUnit: 'a', RequiredCodeUnit: 'a')), "a").Admitted);
    }

    [Fact]
    public void Bitmap255IncludesAllWiderNative16Units()
    {
        var p = new ControlPlan();
        p.Leaf(Pcre2NodeKind.Dot);
        var bitmap = new byte[32];
        bitmap[31] = 0x80;
        Assert.Equal((true, 2, default), Admit(p.Build(new(1, 0, false), bitmap), "ab\u0100"));
    }

    [Fact]
    public void FirstlineMayAttemptNewlineAndConsumptionKeepsOriginalEnd()
    {
        var p = new ControlPlan(new(flags: Pcre2CompileFlags.Firstline));
        p.Lit('\n');
        Assert.Equal((true, 2, default), Admit(p.Build(new(1, 0, false, FirstCodeUnit: '\n')), "ab\nc", firstline: true));
        var q = new ControlPlan(p.Profile);
        q.Lit('c');
        Assert.Equal((true, 2, default), Admit(q.Build(new(1, 0, false, FirstCodeUnit: 'c')), "ab\nc", firstline: true));
    }

    [Fact]
    public void Native16CaselessFirstHintUsesPinnedUnicodeOthercase()
    {
        var p = new ControlPlan(new(flags: Pcre2CompileFlags.Utf));
        p.Lit(0x3b1);
        Assert.Equal((true, 1, default), Admit(p.Build(new(1, 0, false,
            FirstCodeUnit: 0x3b1, FirstCodeUnitCaseless: true)), "x\u0391"));
    }
}
