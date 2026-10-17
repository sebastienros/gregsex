using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class ResourceTests
{
    [Fact]
    public void EveryCompileCeilingHasADistinctFailureAndReleasesTemporaries()
    {
        GrammarTests.Reject("ab", "u", RegexDiagnosticCode.PatternLimitExceeded, new(maxPatternUnits: 1));
        GrammarTests.Reject("a", "u", RegexDiagnosticCode.CompileMemoryLimitExceeded, new(maxCompileMemoryBytes: 2047));
        GrammarTests.Reject("a", "u", RegexDiagnosticCode.CompileWorkLimitExceeded, new(maxCompileWork: 1));
        GrammarTests.Reject("(a)", "u", RegexDiagnosticCode.CaptureLimitExceeded, new(maxCaptureGroups: 1));
        GrammarTests.Reject("(a)", "u", RegexDiagnosticCode.NestingLimitExceeded, new(maxNestingDepth: 1));
        GrammarTests.Reject("ab", "u", RegexDiagnosticCode.ProgramLimitExceeded, new(maxInstructions: 2));
        GrammarTests.Reject("[az]", "u", RegexDiagnosticCode.ClassLimitExceeded, new(maxClassRanges: 1));
        GrammarTests.Reject("a", "u", RegexDiagnosticCode.PlanLimitExceeded, new(maxPlanBytes: 1));
        GrammarTests.Reject("[[[a]]]", "v", RegexDiagnosticCode.NestingLimitExceeded, new(maxNestingDepth: 2));
        GrammarTests.Reject("[a][z]", "u", RegexDiagnosticCode.ClassLimitExceeded, new(maxClassRanges: 1));
        GrammarTests.Reject(new string('a', 10000), "u", RegexDiagnosticCode.CompileWorkLimitExceeded, new(maxCompileWork: 1000));
        GrammarTests.Reject(@"[\q{" + new string('a', 4096) + "}]", "v", RegexDiagnosticCode.CompileMemoryLimitExceeded, new(maxCompileMemoryBytes: 10000));
    }
    [Theory]
    [InlineData("a{2147483648}")]
    [InlineData("a{0,2147483648}")]
    [InlineData("a{999999999999999999999999}")]
    [InlineData("a{999999999999999999999999,}")]
    [InlineData("a{000002147483648,000009999999999999999999999}")]
    public void ValidCountsBeyondInt32AreAdmittedAndClamped(string pattern)
    {
        // ECMAScript admits any DecimalDigits. A minimum above int.MaxValue clamps to it (no UTF-16 subject can
        // satisfy either with a non-empty atom) and a maximum above it is unbounded; nothing wraps.
        foreach (var flags in new[] { "", "u", "v" })
        {
            var program = GrammarTests.Compile(pattern, flags);
            var repeat = program.Nodes.ToArray().Single(n => n.Kind == EcmaNodeKind.Repeat);
            Assert.True(repeat.Min is 0 or int.MaxValue);
            Assert.True(repeat.Max is -1 or int.MaxValue);
            Assert.True(repeat.Max == -1 || repeat.Max >= repeat.Min);
        }
    }
    [Fact]
    public void LargeCountsRemainCompactWithoutExpansionAndNumericWrap()
    {
        foreach (var pattern in new[] { "a{2147483647}", "a{1,2147483647}", "a{2147483647,}" })
        {
            var p = GrammarTests.Compile(pattern, "u");
            Assert.Equal(2, p.Nodes.Length);
            Assert.True(p.Nodes[p.Root].Max == int.MaxValue || p.Nodes[p.Root].Max == -1);
        }
        GrammarTests.Reject("a{999999999999999999999999,999999999999999999999998}", "u");
    }
    [Fact]
    public void ClassRangeCeilingAppliesAfterNormalizationAndAlgebra()
    {
        foreach (var flags in new[] { "", "u", "v" })
        {
            var p = GrammarTests.Compile("[acb]", flags, new(maxClassRanges: 1));
            Assert.Equal(new GregsEx.Contracts.EcmaScript.EcmaInterval('a', 'c'), Assert.Single(p.Intervals.ToArray()));
        }
        Assert.Empty(GrammarTests.Compile("[[aceg]--[aceg]]", "v", new(maxClassRanges: 1)).Intervals.ToArray());
    }
    [Fact]
    public void DeepSyntaxUsesFiniteExplicitStacksAndFailedCallsPreserveExistingReservation()
    {
        GrammarTests.Reject(new string('(', 5000) + "a" + new string(')', 5000), "u", RegexDiagnosticCode.NestingLimitExceeded);
        GrammarTests.Reject(new string('[', 5000) + "a" + new string(']', 5000), "v", RegexDiagnosticCode.NestingLimitExceeded);
        var budget = new CompileBudget(new());
        Assert.True(budget.TryReserveBytes(123, new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out _));
        Assert.False(new EcmaCompiler().TryCompile("(?<x>a)(?<x>b)", GrammarTests.Profile("u"), ContractUnicode.Instance, ref budget, out _, out _));
        Assert.Equal(123, budget.ReservedBytes);
        Assert.True(new EcmaCompiler().TryCompile("a", GrammarTests.Profile("u"), ContractUnicode.Instance, ref budget, out var p, out _));
        Assert.Equal(123 + p!.RetainedBytes, budget.ReservedBytes);
        Assert.True(budget.PeakBytes > budget.ReservedBytes);
    }
    [Fact]
    public void WorkBoundsCoverSetAlgebraNamesStringsAndFinalPlanValidation()
    {
        foreach (var pattern in new[] { @"[[a-z]--[aeiou]]", @"[\q{abc|def}&&\q{abc|xyz}]", @"\k<x>(?<x>a)|(?<x>b)", @"\P{Ll}", "abc" })
        {
            var initial = new CompileBudget(new());
            Assert.True(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile("vi"), ContractUnicode.Instance, ref initial, out _, out _));
            var complete = initial.WorkConsumed;
            Assert.True(complete > 1);
            foreach (var ceiling in new[] { 1L, complete / 2, complete - 1 })
            {
                var budget = new CompileBudget(new(maxCompileWork: ceiling));
                Assert.False(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile("vi"), ContractUnicode.Instance, ref budget, out _, out var diagnostic));
                Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, diagnostic.Diagnostic.Code);
                Assert.Equal(0, budget.ReservedBytes);
                Assert.InRange(budget.WorkConsumed, 0, ceiling);
            }
        }
    }
}
