using GregsEx.Compilation.Core;
using GregsEx.Contracts;
using Xunit;

namespace GregsEx.Tests.Compilation.Core;

public sealed class CoreResourceTests
{
    private static ProfileDescriptor Profile()
    {
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(), RegexInputKind.Utf16, out var p, out _));
        return p!;
    }
    private static RegexDiagnostic Reject(string pattern, RegexCompileLimits limits)
    {
        var b = new CompileBudget(limits);
        Assert.False(new CoreCompiler().TryCompile(pattern, Profile(), ref b, out var p, out var d));
        Assert.Null(p);
        Assert.Equal(0, b.ReservedBytes);
        Assert.True(b.WorkConsumed <= limits.MaxCompileWork);
        Assert.True(b.PeakBytes <= limits.MaxCompileMemoryBytes);
        Assert.NotEqual(RegexDiagnosticCode.InvalidProgram, d.Code);
        return d;
    }

    [Theory]
    [InlineData("ab", 1, RegexDiagnosticCode.PatternLimitExceeded)]
    [InlineData("a", 3, RegexDiagnosticCode.ProgramLimitExceeded)]
    [InlineData("a{2147483648}", 1_000_000, RegexDiagnosticCode.ProgramLimitExceeded)]
    [InlineData("a{999999999999999999999999}", 1_000_000, RegexDiagnosticCode.ProgramLimitExceeded)]
    [InlineData("a{2147483647}", 1_000_000, RegexDiagnosticCode.ProgramLimitExceeded)]
    [InlineData("(a{999999}){999999}", 1_000_000, RegexDiagnosticCode.ProgramLimitExceeded)]
    public void PatternAndCheckedExpansionAreFinite(string pattern, int limit, RegexDiagnosticCode expected)
    {
        var limits = expected == RegexDiagnosticCode.PatternLimitExceeded ? new RegexCompileLimits(maxPatternUnits: limit) : new(maxInstructions: limit);
        Assert.Equal(expected, Reject(pattern, limits).Code);
    }
    [Fact]
    public void NestedGroupsClassesAndCaptureLimits()
    {
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, Reject("((a))", new(maxNestingDepth: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, Reject("([a])", new(maxNestingDepth: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.CaptureLimitExceeded, Reject("(a)", new(maxCaptureGroups: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, Reject("[ac]", new(maxClassRanges: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, Reject("[a][c]", new(maxClassRanges: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, Reject(@"\w", new(maxClassRanges: 3)).Code);
    }
    [Fact]
    public void WorkPeakMemoryAndRetainedMemoryAreDistinct()
    {
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, Reject("a", new(maxCompileWork: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, Reject("a", new(maxCompileMemoryBytes: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, Reject("a", new(maxPlanBytes: 1)).Code);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, Reject("[a]", new(maxPlanBytes: 64)).Code);
        // Sweep early/middle/late deterministic work-budget failures.
        for (var work = 1; work < 400; work++)
        {
            var b = new CompileBudget(new(maxCompileWork: work));
            var success = new CoreCompiler().TryCompile("((a|[z-a]){1,3})", Profile(), ref b, out _, out var d);
            Assert.False(success);
            Assert.Contains(d.Code, new[] { RegexDiagnosticCode.CompileWorkLimitExceeded, RegexDiagnosticCode.InvalidSyntax });
            Assert.Equal(0, b.ReservedBytes);
            Assert.True(b.WorkConsumed <= work);
        }
    }
    [Fact]
    public void ReplacementGrowthChargesOldAndNewCapacitiesAndReleasesTemporaries()
    {
        var profile = Profile();
        var b = new CompileBudget(new());
        Assert.True(new CoreCompiler().TryCompile("abcde", profile, ref b, out var p, out _));
        var peak = b.PeakBytes;
        var retained = b.ReservedBytes;
        Assert.True(peak > retained);
        Assert.Equal(256L + 32L * p!.Instructions.Length, retained);
        var low = Reject("abcde", new(maxCompileMemoryBytes: retained));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, low.Code);
        // A failed compile must retain reservations belonging to its caller.
        var prior = new CompileBudget(new(maxCompileWork: 1));
        Assert.True(prior.TryReserveBytes(32, new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out _));
        Assert.False(new CoreCompiler().TryCompile("abc", profile, ref prior, out _, out _));
        Assert.Equal(32, prior.ReservedBytes);
    }
    [Fact]
    public void DeepSyntaxAndLongConcatenationUseNoInputControlledCallStack()
    {
        var profile = Profile();
        var b = new CompileBudget(new(maxNestingDepth: 6000));
        var pattern = string.Concat(Enumerable.Repeat("(?:", 5000)) + "a" + new string(')', 5000);
        Assert.True(new CoreCompiler().TryCompile(pattern, profile, ref b, out var p, out var d), d.ToString());
        Assert.Equal(4, p!.Instructions.Length);
        b = new(new());
        Assert.True(new CoreCompiler().TryCompile(new string('a', 12000), profile, ref b, out p, out d), d.ToString());
        Assert.Equal(12003, p!.Instructions.Length);
    }
    [Fact]
    public void DeterministicAdversarialMutationNeverEscapesExpectedDiagnostics()
    {
        var random = new Random(0x4D3143);
        const string alphabet = "abc0129()[]{}*+?|^$\\:-&,xu";
        var compiler = new CoreCompiler();
        var profile = Profile();
        for (var sample = 0; sample < 3000; sample++)
        {
            var chars = new char[random.Next(0, 80)];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[random.Next(alphabet.Length)];
            }

            var budget = new CompileBudget(new(maxCompileWork: 4000, maxInstructions: 500, maxCompileMemoryBytes: 65536));
            var okay = compiler.TryCompile(chars, profile, ref budget, out var p, out var d);
            Assert.Equal(okay, p is not null);
            Assert.Equal(okay, d.Code == RegexDiagnosticCode.None);
            Assert.NotEqual(RegexDiagnosticCode.InvalidProgram, d.Code);
            Assert.True(budget.WorkConsumed <= 4000);
            Assert.True(budget.PeakBytes <= 65536);
            if (!okay)
            {
                Assert.Equal(0, budget.ReservedBytes);
            }

            Assert.InRange(d.Offset, 0, chars.Length);
            Assert.InRange(d.Length, 0, chars.Length - d.Offset);
        }
    }

    [Fact]
    public void EveryLoweringAndPublicationBudgetBoundaryRollsBackSafely()
    {
        const string pattern = "((a|[bc]){2,4})";
        var compiler = new CoreCompiler();
        var profile = Profile();
        var measured = new CompileBudget(new());
        Assert.True(compiler.TryCompile(pattern, profile, ref measured, out _, out _));
        for (long work = 1; work < measured.WorkConsumed; work++)
        {
            var budget = new CompileBudget(new(maxCompileWork: work));
            Assert.False(compiler.TryCompile(pattern, profile, ref budget, out _, out var d));
            Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Code);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.True(budget.WorkConsumed <= work);
        }
        for (long bytes = 1; bytes < measured.PeakBytes; bytes += 31)
        {
            var budget = new CompileBudget(new(maxCompileMemoryBytes: bytes));
            Assert.False(compiler.TryCompile(pattern, profile, ref budget, out _, out var d));
            Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, d.Code);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.True(budget.PeakBytes <= bytes);
        }
        var exact = new CompileBudget(new(maxCompileWork: measured.WorkConsumed, maxCompileMemoryBytes: measured.PeakBytes));
        Assert.True(compiler.TryCompile(pattern, profile, ref exact, out _, out _));
    }

    [Fact]
    public void ColdCompileOwnershipIsFinite()
    {
        var compiler = new CoreCompiler();
        var profile = Profile();
        var warm = new CompileBudget(new());
        Assert.True(compiler.TryCompile("(a|[bc]){2,4}", profile, ref warm, out _, out _));
        var b = new CompileBudget(new());
        var okay = compiler.TryCompile("(a|[bc]){2,4}", profile, ref b, out var p, out _);
        Assert.True(okay);
        Assert.NotNull(p);
        Assert.True(p.RetainedBytes > 0);
        Assert.True(b.PeakBytes <= b.Limits.MaxCompileMemoryBytes);
    }
}
