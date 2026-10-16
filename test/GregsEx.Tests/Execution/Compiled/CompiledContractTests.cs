using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

public class CompiledContractTests
{
    private const string Pattern = "(ERROR|WARN) ([a-z]+)";

    private static RegexPattern Interpreted(string pattern = Pattern, RegexMatchLimits? limits = null, RegularCoreMode mode = RegularCoreMode.Utf16CodeUnits)
        => RegexPattern.Parse(pattern, new RegularCoreProfile(mode),
            new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));

    private static string Text(int repeats) => string.Concat(Enumerable.Repeat("xx ERROR failed WARN slow ", repeats));

    [Fact]
    public void CompileIsAnnotatedWithTheFrozenContractMessages()
    {
        var method = typeof(RegexPattern).GetMethod("Compile", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal(CompilationContract.RequiresDynamicCodeMessage, method.GetCustomAttribute<RequiresDynamicCodeAttribute>()!.Message);
        Assert.Equal(CompilationContract.RequiresUnreferencedCodeMessage, method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()!.Message);
        Assert.Equal(typeof(RegexPattern), method.ReturnType);
    }

    [Fact]
    public void CompileReportsBackendAndLeavesTheSourcePatternInterpreted()
    {
        var interpreted = Interpreted();
        Assert.Equal(RegexExecutionBackend.Interpreted, interpreted.ExecutionBackend);
        var compiled = interpreted.Compile();
        Assert.NotSame(interpreted, compiled);
        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Interpreted, interpreted.ExecutionBackend);
        Assert.Same(compiled, compiled.Compile());
        Assert.Equal(interpreted.GroupCount, compiled.GroupCount);
        Assert.Equal(interpreted.MatchLimits, compiled.MatchLimits);
    }

    [Theory]
    [InlineData("the")]
    [InlineData("([a-z]+)=([a-z0-9]+)")]
    [InlineData("^[a-z ]+$")]
    public void PatternsServedByAcceleratedFastPathsAreNotCompiled(string pattern)
        => Assert.Throws<NotSupportedException>(() => Interpreted(pattern).Compile());

    [Theory]
    [InlineData("(a*)*b")]
    [InlineData("(a|b*)*c")]
    public void ProgramsWithEpsilonCyclesAreNotCompiled(string pattern)
        => Assert.Throws<NotSupportedException>(() => Interpreted(pattern).Compile());

    [Fact]
    public void PosixIsNotCompiledBecauseItIsLeftmostLongest()
    {
        var posix = RegexPattern.Parse("a|ab", new PosixProfile(PosixDialect.Extended), new RegexCompileOptions(RegexInputKind.Bytes, RegexExecutionMode.Interpreted));
        using var posixSpace = posix.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.Match, posixSpace.Find("ab"u8, 0, out var posixRange));
        // POSIX picks the longest alternative; a leftmost-first kernel (what a RegularCore "a|ab" yields) would pick "a".
        Assert.Equal(2, posixRange.Length);
        using var core = Interpreted("a|ab").CreateWorkspace();
        Assert.Equal(RegexSearchStatus.Match, core.Find("ab", 0, out var coreRange));
        Assert.Equal(1, coreRange.Length);

        var ex = Assert.Throws<NotSupportedException>(() => posix.Compile());
        Assert.Contains("leftmost-longest", ex.Message);
    }

    [Fact]
    public void OtherFlavorsAreNotCompiled()
    {
        var ecma = RegexPattern.Parse(Pattern, new EcmaScriptProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
        Assert.Throws<NotSupportedException>(() => ecma.Compile());
    }

    [Fact]
    public void MatchingAllocatesNothing()
    {
        var compiled = Interpreted().Compile();
        var space = compiled.CreateWorkspace();
        var text = Text(200);
        var groups = new RegexCapture[compiled.GroupCount];
        for (var i = 0; i < 3; i++)
        {
            space.FindCaptures(text, 0, groups, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20; i++)
        {
            if (space.FindCaptures(text, 0, groups, out _) != RegexSearchStatus.Match)
            {
                Assert.Fail("no match");
            }

            if (space.Find(text, 5, out _) != RegexSearchStatus.Match)
            {
                Assert.Fail("no match");
            }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void CompiledNeverFailsALimitTheInterpreterSurvivesAndAgreesOtherwise()
    {
        var text = Text(40);
        var oracle = Interpreted().CreateWorkspace();
        var compiledSpace = Interpreted().Compile().CreateWorkspace();
        foreach (var work in new long[] { 1, 2, 5, 17, 100, 400, 1000, 5000, 20000, 100000, 10_000_000 })
        {
            var options = new RegexMatchOptions { Limits = new RegexMatchLimits(work, 1 << 20) };
            var expected = oracle.Find(text, 0, out var expectedRange, options);
            var actual = compiledSpace.Find(text, 0, out var actualRange, options);
            if (expected == RegexSearchStatus.LimitExceeded)
            {
                continue; // Compiled may succeed where Pike's accounting overflows; never the reverse.
            }

            Assert.Equal(expected, actual);
            Assert.Equal(expectedRange, actualRange);
        }
    }

    [Fact]
    public void ConstrainedRunsHonourCancellationAndDeadlineLikeTheInterpreter()
    {
        var text = Text(2000);
        var compiledSpace = Interpreted().Compile().CreateWorkspace();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, compiledSpace.Find(text, 0, out _, new RegexMatchOptions { CancellationToken = cancelled.Token }));
        var live = compiledSpace.Find(text, 0, out var range, new RegexMatchOptions { Timeout = TimeSpan.FromMinutes(1) });
        var oracle = Interpreted().CreateWorkspace().Find(text, 0, out var expected, new RegexMatchOptions { Timeout = TimeSpan.FromMinutes(1) });
        Assert.Equal(oracle, live);
        Assert.Equal(expected, range);
    }

    [Fact]
    public void KernelsPollTheDeadlineAndCancellationThemselves()
    {
        var kernelCancelled = new CancellationTokenSource();
        kernelCancelled.Cancel();
        var guard = new GregsEx.Execution.Compiled.KernelGuard(new GregsEx.Contracts.RunBudget(long.MaxValue, kernelCancelled.Token, null));
        Assert.Equal(GregsEx.Execution.Compiled.CoreKernelResult.Cancelled, guard.Poll(0));
        var expired = new GregsEx.Execution.Compiled.KernelGuard(new GregsEx.Contracts.RunBudget(long.MaxValue, default, TimeSpan.Zero));
        Assert.Equal(GregsEx.Execution.Compiled.CoreKernelResult.TimedOut, expired.Poll(0));
        var none = GregsEx.Execution.Compiled.KernelGuard.None;
        Assert.Equal(long.MaxValue, none.NextPoll);
    }

    [Fact]
    public void TinyTimeoutAbortsACompiledScanWithTimedOut()
    {
        var text = Text(200_000);
        var compiled = Interpreted().Compile().CreateWorkspace();
        var options = new RegexMatchOptions { Timeout = TimeSpan.FromTicks(1) };
        Assert.Equal(RegexSearchStatus.TimedOut, compiled.Find(text, 0, out _, options));
        Assert.Equal(RegexSearchStatus.Match, compiled.Find(text, 0, out _, new RegexMatchOptions { Timeout = TimeSpan.FromMinutes(1) }));
    }

    [Fact]
    public void AmbiguousPatternsBailToTheInterpreterWithTheSameAnswer()
    {
        var text = new string('a', 5000) + "b";
        var limits = new RegexMatchLimits(1_000_000_000, 1 << 26);
        var oracle = Interpreted("(a|aa)*c", limits).CreateWorkspace();
        var compiledSpace = Interpreted("(a|aa)*c", limits).Compile().CreateWorkspace();
        var expected = oracle.Find(text, 0, out var expectedRange);
        var actual = compiledSpace.Find(text, 0, out var actualRange);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedRange, actualRange);
    }

    [Fact]
    public async Task OneCompiledPatternServesConcurrentWorkspaces()
    {
        var compiled = Interpreted().Compile();
        var text = Text(50);
        var oracle = Interpreted().CreateWorkspace();
        oracle.Find(text, 0, out var expected);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var space = compiled.CreateWorkspace();
            for (var i = 0; i < 500; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, space.Find(text, 0, out var range));
                Assert.Equal(expected, range);
            }
        })).ToArray();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public void PublicSurfaceExposesNoCompilerTypes()
    {
        var exported = typeof(RegexPattern).Assembly.GetExportedTypes();
        var generatedCodeAbi = new[] { typeof(GregsEx.Execution.Compiled.CoreKernel), typeof(GregsEx.Execution.Compiled.KernelGuard) };
        Assert.DoesNotContain(exported, t => !generatedCodeAbi.Contains(t)
            && (t.FullName!.Contains("FastExpressionCompiler") || t.Namespace?.EndsWith("Execution.Compiled", StringComparison.Ordinal) == true));
        // The only public compiled-backend types are the ABI of source-generated patterns, and they stay hidden from IntelliSense.
        Assert.All(generatedCodeAbi, t => Assert.Contains(t.GetCustomAttributes(typeof(System.ComponentModel.EditorBrowsableAttribute), false),
            a => ((System.ComponentModel.EditorBrowsableAttribute)a).State == System.ComponentModel.EditorBrowsableState.Never));
    }

    [Fact]
    public void CompiledPatternsReserveFromTheProcessWideKernelBudget()
    {
        // Other tests' compiled patterns release their reservations from finalizers at any time, so retry the observation.
        var reserved = false;
        RegexPattern compiled = null!;
        for (var attempt = 0; attempt < 50 && !reserved; attempt++)
        {
            var before = GregsEx.Execution.Compiled.CompiledKernelBudget.LiveBytes;
            compiled = Interpreted().Compile();
            reserved = GregsEx.Execution.Compiled.CompiledKernelBudget.LiveBytes > before;
        }
        Assert.True(reserved);
        GC.KeepAlive(compiled);
        Assert.False(GregsEx.Execution.Compiled.CompiledKernelBudget.TryReserve(GregsEx.Execution.Compiled.CompiledKernelBudget.CeilingBytes + 1, out _));
    }
}
