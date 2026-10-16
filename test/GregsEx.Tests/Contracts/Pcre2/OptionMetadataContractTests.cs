using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Contracts.Pcre2;

public sealed class OptionMetadataContractTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void ImmutableFlagDoesNotInferSourceNodesOrEffectiveOptions(bool executable, bool flag, bool cFlag)
    {
        // Deliberately independent metadata: an empty node over lexical (?J)
        // and enabled DUPNAMES cannot determine this field at the plan boundary.
        Pcre2Node[] nodes = [new(Pcre2NodeKind.Empty, default, 0, 0, 0, default, default, default,
            new(0, 4, RegexOffsetUnit.Utf16CodeUnits))];
        int[] units = ['(', '?', 'J', ')'], map = [0, 1, 2, 3, 4];
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(flags: Pcre2CompileFlags.Dupnames),
            executable ? Pcre2PlanStage.Executable : Pcre2PlanStage.Syntax, 4, 4,
            RegexOffsetUnit.Utf16CodeUnits, 1, new(nodes, units: units, sourceMap: map),
            default, ref budget, out var program, out var error,
            hasDuplicateNameOptionChange: flag, hasBackslashC: cFlag));
        Assert.Equal(default, error);
        Assert.Equal(flag, program!.HasDuplicateNameOptionChange);
        Assert.Equal(cFlag, program.HasBackslashC);
        Array.Fill(units, 'x');
        Array.Fill(map, 0);
        nodes[0] = nodes[0] with { Kind = Pcre2NodeKind.Literal, Operand = 'x' };
        Assert.Equal(flag, program.HasDuplicateNameOptionChange);
        Assert.Equal(cFlag, program.HasBackslashC);
        Assert.Equal(Pcre2NodeKind.Empty, program.Nodes[0].Kind);
        Assert.Equal('J', program.Units[2]);
        Assert.Equal(program.RetainedBytes, budget.ReservedBytes);
        Assert.Null(typeof(Pcre2Program).GetProperty("HasDuplicateNameOptionChange",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetMethod);
        Assert.Null(typeof(Pcre2Program).GetProperty("HasBackslashC",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetMethod);
        if (!executable)
        {
            Assert.True(new Pcre2Lowerer().TryLower(program, Pcre2Data.Instance, ref budget,
                out var lowered, out error), error.ToString());
            Assert.Equal(flag, lowered!.HasDuplicateNameOptionChange);
            Assert.Equal(cFlag, lowered.HasBackslashC);
            Assert.Equal(program.RetainedBytes + lowered.RetainedBytes, budget.ReservedBytes);
        }
    }

    [Fact]
    public void TrailingDefaultAndFlagKeepSameBoundedAdmission()
    {
        var data = new Pcre2PlanData([new(Pcre2NodeKind.Empty, default, 0, 0, 0,
            default, default, default, new(0, 0, RegexOffsetUnit.Utf16CodeUnits))], sourceMap: [0]);
        var legacyBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 0, 0,
            RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref legacyBudget, out var legacy, out _));
        Assert.False(legacy!.HasDuplicateNameOptionChange);
        Assert.False(legacy.HasBackslashC);
        var flagBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 0, 0,
            RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref flagBudget, out var flagged, out _,
            hasExplicitCrOrLf: true, hasDuplicateNameOptionChange: true, hasBackslashC: true));
        Assert.True(flagged!.HasDuplicateNameOptionChange);
        Assert.True(flagged.HasExplicitCrOrLf);
        Assert.True(flagged.HasBackslashC);
        Assert.Equal(legacy.RetainedBytes, flagged.RetainedBytes);
        Assert.Equal(legacyBudget.WorkConsumed, flagBudget.WorkConsumed);
        Assert.Equal(legacyBudget.PeakBytes, flagBudget.PeakBytes);
        var planBudget = new CompileBudget(new(maxPlanBytes: flagged.RetainedBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 0, 0,
            RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref planBudget, out var rejected,
            out var error, hasDuplicateNameOptionChange: true, hasBackslashC: true));
        Assert.Null(rejected);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, error.Diagnostic.Code);
        Assert.Equal(0, planBudget.ReservedBytes);
        var peakBudget = new CompileBudget(new(maxCompileMemoryBytes: flagBudget.PeakBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 0, 0,
            RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref peakBudget, out _, out error,
            hasDuplicateNameOptionChange: true, hasBackslashC: true));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, error.Diagnostic.Code);
        Assert.Equal(0, peakBudget.ReservedBytes);
    }
}
