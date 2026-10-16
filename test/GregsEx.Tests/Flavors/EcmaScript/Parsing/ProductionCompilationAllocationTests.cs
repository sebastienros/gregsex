using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing.Allocation;

// Semantic compilation ownership checks. Allocation costs are measured separately with BDN.
// Shared Unicode initialization and fixture/profile creation retain finite budgets.
public sealed class ProductionCompilationAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(@"\p{RGI_Emoji}")]
    [InlineData(@"[\p{RGI_Emoji}&&\p{RGI_Emoji}]")]
    public void ActualPropertyCompilationReportsColdOwnedCosts(string pattern)
    {
        var compiler = new EcmaCompiler();
        var profile = GrammarTests.Profile("v");
        var limits = new RegexCompileLimits(maxCompileWork: 250_000_000);
        var setup = new CompileBudget(limits);
        var data = Composition.Data(ref setup);
        for (var i = 0; i < 8; i++)
        {
            var warm = new CompileBudget(limits);
            Assert.True(compiler.TryCompile(pattern, profile, data, ref warm, out _, out var d), d.ToString());
        }
        const int iterations = 8;
        long work = 0, retained = 0, peak = 0;
        var succeeded = true;
        for (var i = 0; i < iterations; i++)
        {
            var budget = new CompileBudget(limits);
            succeeded &= compiler.TryCompile(pattern, profile, data, ref budget, out var p, out _);
            work += budget.WorkConsumed;
            retained += p?.RetainedBytes ?? 0;
            peak += budget.PeakBytes;
        }
        Assert.True(succeeded);
        Assert.True(peak > retained);
        output.WriteLine($"pattern={pattern};iterations={iterations};retainedPerCompile={retained / iterations};peakPerCompile={peak / iterations};workPerCompile={work / iterations};finiteWorkCeiling=250000000;sharedDataWarm=true;noMatcherAllocationOrSpeedClaim=true");
    }
}
