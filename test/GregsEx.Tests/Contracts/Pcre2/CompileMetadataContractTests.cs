using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Contracts.Pcre2;

public sealed class CompileMetadataContractTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ImmutableExplicitFlagIsCopiedWithoutInferringNodeMembership(bool executable, bool flag)
    {
        var stage = executable ? Pcre2PlanStage.Executable : Pcre2PlanStage.Syntax;
        Pcre2Node[] nodes = [new(Pcre2NodeKind.Literal, default, 10, 0, 0, default, default, default, new(0, 1, RegexOffsetUnit.Utf16CodeUnits))];
        int[] units = [10], map = [0, 1];
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), stage, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, units: units, sourceMap: map), default, ref budget, out var program, out var error, hasExplicitCrOrLf: flag));
        Assert.Equal(default, error);
        Assert.Equal(flag, program!.HasExplicitCrOrLf);
        nodes[0] = nodes[0] with { Operand = 'x' };
        units[0] = 'x';
        map[1] = 0;
        Assert.Equal(flag, program.HasExplicitCrOrLf);
        Assert.Equal(10, program.Nodes[0].Operand);
        Assert.Equal(program.RetainedBytes, budget.ReservedBytes);
        Assert.Null(typeof(Pcre2Program).GetProperty("HasExplicitCrOrLf", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetMethod);
    }
    [Fact]
    public void LegacyDefaultAndTrueFlagKeepSameBoundedStorageAdmission()
    {
        var data = new Pcre2PlanData([new(Pcre2NodeKind.Empty, default, 0, 0, 0, default, default, default, new(0, 0, RegexOffsetUnit.Utf16CodeUnits))], sourceMap: [0]);
        var defaultBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref defaultBudget, out var legacy, out _));
        Assert.False(legacy!.HasExplicitCrOrLf);
        var flagBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref flagBudget, out var flagged, out _, hasExplicitCrOrLf: true));
        Assert.True(flagged!.HasExplicitCrOrLf);
        Assert.Equal(legacy.RetainedBytes, flagged.RetainedBytes);
        Assert.Equal(defaultBudget.PeakBytes, flagBudget.PeakBytes);
        var planBudget = new CompileBudget(new(maxPlanBytes: flagged.RetainedBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref planBudget, out var rejected, out var error, hasExplicitCrOrLf: true));
        Assert.Null(rejected);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, error.Diagnostic.Code);
        Assert.Equal(0, planBudget.ReservedBytes);
        var peakBudget = new CompileBudget(new(maxCompileMemoryBytes: flagBudget.PeakBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref peakBudget, out _, out var peak, hasExplicitCrOrLf: true));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, peak.Diagnostic.Code);
        Assert.Equal(0, peakBudget.ReservedBytes);
    }
}
