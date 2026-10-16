using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// The compiled ECMAScript, XPath and PCRE2 paths carry the same safety mitigations as the RegularCore path: deadline and
// cancellation polled inside the kernel, work limits that never fail where the interpreter survives, no allocation on match,
// and bounded process-wide kernel memory.
public class CompiledFlavorSafetyTests
{
    private const string Pattern = "[a-z]+(ing|ed)";

    public static IEnumerable<TheoryDataRow<string>> Flavors() => [
        new TheoryDataRow<string>("ecma"),
        new TheoryDataRow<string>("xpath"),
        new TheoryDataRow<string>("pcre2")
    ];

    private static RegexPattern Interpreted(string flavor, RegexMatchLimits? limits = null)
    {
        RegexProfile profile = flavor switch
        {
            "ecma" => new EcmaScriptProfile(EcmaScriptFlags.None),
            "xpath" => new XPathProfile(),
            _ => new Pcre2Profile(),
        };
        return RegexPattern.Parse(Pattern, profile, new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));
    }

    private static string Text(int repeats) => string.Concat(Enumerable.Repeat("xx 12 winning walked ", repeats));

    [Theory]
    [MemberData(nameof(Flavors))]
    public void CompiledReportsItsBackendAndLeavesTheSourceInterpreted(string flavor)
    {
        var interpreted = Interpreted(flavor);
        var compiled = interpreted.Compile();
        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Interpreted, interpreted.ExecutionBackend);
        Assert.Same(compiled, compiled.Compile());
    }

    // Tier-0 code can allocate before the JIT promotes a hot method, so a steady-state allocation is the minimum over rounds.
    private static long SteadyStateBytes(Func<RegexSearchStatus> call)
    {
        var best = long.MaxValue;
        for (var round = 0; round < 30 && best != 0; round++)
        {
            for (var i = 0; i < 400; i++)
            {
                call();
            }

            Thread.Sleep(100);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++)
            {
                if (call() != RegexSearchStatus.Match)
                {
                    Assert.Fail("no match");
                }
            }

            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void MatchingAllocatesNothing(string flavor)
    {
        var compiled = Interpreted(flavor).Compile();
        var space = compiled.CreateWorkspace();
        var text = Text(200);
        var groups = new RegexCapture[compiled.GroupCount];
        Assert.Equal(0, SteadyStateBytes(() => space.FindCaptures(text, 0, groups, out _)));
        Assert.Equal(0, SteadyStateBytes(() => space.Find(text, 5, out _)));
        Assert.Equal(0, SteadyStateBytes(() => space.IsMatch(text)));
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void CompiledNeverFailsALimitTheInterpreterSurvivesAndAgreesOtherwise(string flavor)
    {
        var text = Text(40);
        var oracle = Interpreted(flavor).CreateWorkspace();
        var compiledSpace = Interpreted(flavor).Compile().CreateWorkspace();
        foreach (var work in new long[] { 1, 2, 5, 17, 100, 400, 1000, 5000, 20000, 100000, 10_000_000 })
        {
            var options = new RegexMatchOptions { Limits = new RegexMatchLimits(work, 1 << 20) };
            var expected = oracle.Find(text, 0, out var expectedRange, options);
            var actual = compiledSpace.Find(text, 0, out var actualRange, options);
            if (expected != RegexSearchStatus.Match && expected != RegexSearchStatus.NoMatch)
            {
                continue;
            }

            Assert.Equal(expected, actual);
            Assert.Equal(expectedRange, actualRange);
        }
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void ACancelledTokenAbortsACompiledRun(string flavor)
    {
        var space = Interpreted(flavor).Compile().CreateWorkspace();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, space.Find(Text(2000), 0, out _, new RegexMatchOptions { CancellationToken = cancelled.Token }));
        var groups = new RegexCapture[Interpreted(flavor).GroupCount];
        Assert.Equal(RegexSearchStatus.Cancelled, space.FindCaptures(Text(2000), 0, groups, out _, new RegexMatchOptions { CancellationToken = cancelled.Token }));
        Assert.Equal(RegexSearchStatus.Match, space.Find(Text(2000), 0, out _));
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void TinyTimeoutAbortsACompiledScanWithTimedOut(string flavor)
    {
        var text = Text(200_000);
        var space = Interpreted(flavor).Compile().CreateWorkspace();
        Assert.Equal(RegexSearchStatus.TimedOut, space.Find(text, 0, out _, new RegexMatchOptions { Timeout = TimeSpan.FromTicks(1) }));
        Assert.Equal(RegexSearchStatus.Match, space.Find(text, 0, out _, new RegexMatchOptions { Timeout = TimeSpan.FromMinutes(1) }));
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public async Task OneCompiledPatternServesConcurrentWorkspaces(string flavor)
    {
        var compiled = Interpreted(flavor).Compile();
        var text = Text(50);
        Interpreted(flavor).CreateWorkspace().Find(text, 0, out var expected);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var space = compiled.CreateWorkspace();
            for (var i = 0; i < 300; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, space.Find(text, 0, out var range));
                Assert.Equal(expected, range);
            }
        })));
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void CompiledPatternsReserveFromTheProcessWideKernelBudget(string flavor)
    {
        // Other tests' compiled patterns release their reservations from finalizers at any time, so retry the observation.
        var reserved = false;
        RegexPattern compiled = null!;
        for (var attempt = 0; attempt < 50 && !reserved; attempt++)
        {
            var before = GregsEx.Execution.Compiled.CompiledKernelBudget.LiveBytes;
            compiled = Interpreted(flavor).Compile();
            reserved = GregsEx.Execution.Compiled.CompiledKernelBudget.LiveBytes > before;
        }
        Assert.True(reserved);
        GC.KeepAlive(compiled);
    }

    [Theory]
    [MemberData(nameof(Flavors))]
    public void AmbiguousPatternsAgreeWithTheInterpreterUnderALargeBudget(string flavor)
    {
        var text = new string('a', 3000) + "b";
        var limits = new RegexMatchLimits(1_000_000_000, 1 << 26);
        RegexProfile profile = flavor switch
        {
            "ecma" => new EcmaScriptProfile(EcmaScriptFlags.None),
            "xpath" => new XPathProfile(),
            _ => new Pcre2Profile(),
        };
        RegexPattern Make() => RegexPattern.Parse("(a|aa)*c", profile,
            new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));
        var expected = Make().CreateWorkspace().Find(text, 0, out var expectedRange);
        RegexPattern compiled;
        try
        { compiled = Make().Compile(); }
        catch (NotSupportedException) { return; }
        var actual = compiled.CreateWorkspace().Find(text, 0, out var actualRange);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedRange, actualRange);
    }
}
