using GregsEx.Contracts;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class CompositionResourceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(@"\p{RGI_Emoji}", 2604)]
    [InlineData(@"[\p{RGI_Emoji}&&\p{RGI_Emoji}]", 2604)]
    [InlineData(@"[\p{RGI_Emoji}--\p{RGI_Emoji}]", 0)]
    [InlineData(@"[\p{RGI_Emoji}\p{RGI_Emoji}]", 2604)]
    public void RealLargeStringSetsRemainWithinDefaultWorkIncludingAlgebra(string pattern, int count)
    {
        var c = new Composition(pattern, "v");
        Assert.Equal(count, c.Program.Strings.Length);
        output.WriteLine($"pattern={pattern};work={c.Budget.WorkConsumed};retained={c.Budget.ReservedBytes};peak={c.Budget.PeakBytes}");
        Assert.InRange(c.Budget.WorkConsumed, 1, 10_000_000);
        if (count == 0)
        {
            Assert.Equal(RegexSearchStatus.NoMatch, c.Search("👩‍💻"));
        }
        else
        {
            c.Expect("x👩‍💻", 1, 5);
        }
    }

    [Theory]
    [InlineData(@"\p{RGI_Emoji}", "v")]
    [InlineData(@"[\q{ab|abc|AB}&&\q{ABC|ab}]", "vi")]
    [InlineData(@"(?<α>a)(?<=a)\k<α>", "u")]
    public void ExactWorkBoundaryAndComponentRetentionUseOneBudget(string pattern, string flags)
    {
        var budget = new CompileBudget(new());
        var data = Composition.Data(ref budget);
        var initial = budget.ReservedBytes;
        Assert.True(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), data, ref budget, out var p, out var d), d.ToString());
        Assert.Equal(initial + p!.RetainedBytes, budget.ReservedBytes);
        Assert.True(new EcmaExecutorFactory().TryCreate(p, data, ref budget, out var executor, out var error), error.ToString());
        Assert.Equal(initial + p.RetainedBytes + 192 + 64L * p.Nodes.Length, budget.ReservedBytes);
        var complete = budget.WorkConsumed;
        foreach (var ceiling in new[] { complete - 1, complete })
        {
            var bounded = new CompileBudget(new(maxCompileWork: ceiling));
            var again = Composition.Data(ref bounded);
            var parsed = new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), again, ref bounded, out var program, out var diagnostic);
            var bound = parsed && new EcmaExecutorFactory().TryCreate(program!, again, ref bounded, out _, out error);
            Assert.Equal(ceiling == complete, bound);
            if (!bound)
            {
                Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, parsed ? error.Code : diagnostic.Diagnostic.Code);
            }

            Assert.InRange(bounded.WorkConsumed, 1, ceiling);
        }
        // The executor's case-table object is the same once-created production table;
        // no independently copied table is charged or retained by VM binding.
        Assert.Same(p, executor!.Program);
    }

    [Fact]
    public void DistinctParserLimitsPreserveUnicodeReservationAndNativeSourceDiagnostics()
    {
        var cases = new (string Pattern, string Flags, RegexCompileLimits Limits, RegexDiagnosticCode Code)[]
        {
            ("ab", "u", new(maxPatternUnits: 1), RegexDiagnosticCode.PatternLimitExceeded),
            (@"\p{RGI_Emoji}", "v", new(maxCompileWork: 1000), RegexDiagnosticCode.CompileWorkLimitExceeded),
            (@"\p{RGI_Emoji}", "v", new(maxCompileMemoryBytes: 5000), RegexDiagnosticCode.CompileMemoryLimitExceeded),
            (@"\p{RGI_Emoji}", "v", new(maxPlanBytes: 1000), RegexDiagnosticCode.PlanLimitExceeded),
            ("ab", "u", new(maxInstructions: 2), RegexDiagnosticCode.ProgramLimitExceeded),
            ("[az]", "u", new(maxClassRanges: 1), RegexDiagnosticCode.ClassLimitExceeded),
            ("[[[a]]]", "v", new(maxNestingDepth: 2), RegexDiagnosticCode.NestingLimitExceeded),
            ("(a)", "u", new(maxCaptureGroups: 1), RegexDiagnosticCode.CaptureLimitExceeded),
            (@"x\p{script=Greek}", "u", new(), RegexDiagnosticCode.InvalidSyntax)
        };
        // Prepare the once-shared table before intentionally small ceilings.
        var warm = new CompileBudget(new());
        _ = Composition.Data(ref warm);
        foreach (var (Pattern, Flags, Limits, Code) in cases)
        {
            var budget = new CompileBudget(Limits);
            var data = Composition.Data(ref budget);
            var initial = budget.ReservedBytes;
            Assert.False(new EcmaCompiler().TryCompile(Pattern, GrammarTests.Profile(Flags), data, ref budget, out var p, out var d));
            Assert.Null(p);
            Assert.Equal(Code, d.Diagnostic.Code);
            Assert.Equal(initial, budget.ReservedBytes);
            Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, d.Diagnostic.LocationUnit);
            Assert.NotNull(d.FeatureId);
            Assert.InRange(d.Diagnostic.Offset, 0, Pattern.Length);
            Assert.InRange(d.Diagnostic.Length, 0, Pattern.Length - d.Diagnostic.Offset);
            output.WriteLine($"code={d.Diagnostic.Code};feature={d.FeatureId};offset={d.Diagnostic.Offset};length={d.Diagnostic.Length};work={budget.WorkConsumed};retained={budget.ReservedBytes};peak={budget.PeakBytes}");
        }
    }
}
