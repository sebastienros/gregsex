using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Tables;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using Xunit;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using System.Runtime.CompilerServices;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Syntax;

public sealed class ParityInfoTests
{
    [Fact]
    public void OptionalProvenancePreservesLegacyViewsAndBoundedImmutableCopies()
    {
        Assert.True(Unsafe.SizeOf<Pcre2PatternLimits>() <= 64);
        Assert.True(Unsafe.SizeOf<Pcre2SearchHints>() <= 48);
        var data = new Pcre2PlanData([new(Pcre2NodeKind.Empty, default, 0, 0, 0, default, default, default, new(0, 0, RegexOffsetUnit.Utf16CodeUnits))], sourceMap: [0]);
        var oldBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, new(1, 2, 3072), ref oldBudget, out var old, out _));
        var budget = new CompileBudget(new());
        var limits = new Pcre2PatternLimits(4, int.MaxValue, 6144, 4, uint.MaxValue, 6);
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, limits, ref budget, out var plan, out _, searchHints: new(0, 0, true, MaximumBackreference: 0)));
        Assert.Equal(old!.RetainedBytes, plan!.RetainedBytes);
        Assert.Equal(oldBudget.PeakBytes, budget.PeakBytes);
        Assert.True(Pcre2HostMetadata.TryCreate(old, ref oldBudget, out var oldInfo, out _));
        Assert.True(oldInfo!.TryGetInfo(Pcre2PatternInfoKey.Matchlimit, out var value, out _));
        Assert.Equal(1, value);
        Assert.True(oldInfo.TryGetInfo(Pcre2PatternInfoKey.Depthlimit, out value, out _));
        Assert.Equal(2, value);
        Assert.True(oldInfo.TryGetInfo(Pcre2PatternInfoKey.Heaplimit, out value, out _));
        Assert.Equal(3, value);
        Assert.True(Pcre2HostMetadata.TryCreate(plan, ref budget, out var info, out _));
        Assert.False(info!.TryGetInfo(Pcre2PatternInfoKey.Depthlimit, out _, out var unset));
        Assert.Equal(Pcre2ErrorCode.Unset, unset.SourceError);
        Assert.True(info.TryGetInfo(Pcre2PatternInfoKey.Matchlimit, out value, out _));
        Assert.Equal(4, value);
        Assert.True(info.TryGetInfo(Pcre2PatternInfoKey.Heaplimit, out value, out _));
        Assert.Equal(6, value);
        foreach (var invalid in new[] { -1, 1, 65536 })
        {
            var reject = new CompileBudget(new());
            Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref reject, out _, out _, searchHints: new(0, 0, true, MaximumBackreference: invalid)));
            Assert.Equal(0, reject.ReservedBytes);
        }
        var denied = new CompileBudget(new(maxPlanBytes: plan.RetainedBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 0, 0, RegexOffsetUnit.Utf16CodeUnits, 1, data, limits, ref denied, out _, out var failure));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, failure.Diagnostic.Code);
        Assert.Equal(0, denied.ReservedBytes);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ExactUnsignedLastDirectivesPreserveFiniteEffectiveLimits(int domain)
    {
        var profile = new Pcre2Profile(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0);
        const string text = "(*LIMIT_MATCH=1)(*LIMIT_MATCH=1000)(*LIMIT_DEPTH=1)(*LIMIT_RECURSION=4294967289)(*LIMIT_HEAP=1)(*LIMIT_HEAP=1000)abc";
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(text.AsSpan(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d), d.ToString());
        var limits = syntax!.PatternLimits;
        Assert.Equal(1000u, limits.NativeMatchLimit);
        Assert.Equal(4294967289u, limits.NativeDepthLimit);
        Assert.Equal(1000u, limits.NativeHeapLimit);
        Assert.Equal(1000, limits.MatchWork);
        Assert.Equal(int.MaxValue, limits.Depth);
        Assert.Equal(1024000, limits.HeapBytes);
        var operation = Pcre2OperationLimits.Resolve(new(maxWork: 500, maxDepth: 7, maxWorkspaceBytes: 65536), limits);
        Assert.Equal(500, operation.MaxWork);
        Assert.Equal(7, operation.MaxDepth);
        Assert.Equal(65536, operation.MaxWorkspaceBytes);
        Assert.Equal(syntax.RetainedBytes, budget.ReservedBytes);
        Assert.Equal(new Pcre2PatternLimits(3, 4, 5), new(3, 4, 5, null, null, null));
    }
    [Theory]
    [InlineData("(*LIMIT_DEPTH=4294967290)a", 160)]
    [InlineData("(*LIMIT_DEPTH=4294967295)a", 160)]
    public void SourceDecimalOverflowRemainsRejected(string text, int error)
    {
        var budget = new CompileBudget(new());
        Assert.False(new Pcre2SyntaxParser().TryParse(text.AsSpan(), new(), Pcre2Data.Instance, ref budget, out _, out var d));
        Assert.Equal(error, (int)d.SourceError);
        Assert.Equal(0, budget.ReservedBytes);
    }
}
