using GregsEx.Execution.Compiled;
using Xunit;

namespace GregsEx.Tests.Execution.Generated;

// Allocation checks are only meaningful once the JIT has tiered up the framework helpers, which parallel test load delays.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GeneratedPatternCollection { public const string Name = "GeneratedPatterns"; }

[Collection(GeneratedPatternCollection.Name)]
public class GeneratedPatternTests
{
    private const string Ing = GeneratedPatterns.Ing;

    private static RegexPattern Oracle(string pattern, RegexProfile profile) =>
        RegexPattern.Parse(pattern, profile, new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
    [
        new(nameof(GeneratedPatterns.CoreIng)),
        new(nameof(GeneratedPatterns.EcmaIng)),
        new(nameof(GeneratedPatterns.EcmaIngIgnoreCase)),
        new(nameof(GeneratedPatterns.XsdIng)),
        new(nameof(GeneratedPatterns.XPathIng)),
        new(nameof(GeneratedPatterns.XPathIngLines)),
        new(nameof(GeneratedPatterns.Pcre2Ing)),
        new(nameof(GeneratedPatterns.Pcre2IngLines)),
        new(nameof(GeneratedPatterns.Nested.Range))
    ];

    private static (Func<RegexPattern> Create, string Pattern, RegexProfile Profile) GetCase(string name) => name switch
    {
        nameof(GeneratedPatterns.CoreIng) => (GeneratedPatterns.CoreIng, Ing, new RegularCoreProfile(RegularCoreMode.UnicodeScalars, RegularCoreFlags.None)),
        nameof(GeneratedPatterns.EcmaIng) => (GeneratedPatterns.EcmaIng, Ing, new EcmaScriptProfile(EcmaScriptFlags.None)),
        nameof(GeneratedPatterns.EcmaIngIgnoreCase) => (GeneratedPatterns.EcmaIngIgnoreCase, Ing, new EcmaScriptProfile(EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Global)),
        nameof(GeneratedPatterns.XsdIng) => (GeneratedPatterns.XsdIng, Ing, new XsdProfile()),
        nameof(GeneratedPatterns.XPathIng) => (GeneratedPatterns.XPathIng, Ing, new XPathProfile(XPathFlags.None)),
        nameof(GeneratedPatterns.XPathIngLines) => (GeneratedPatterns.XPathIngLines, "^[a-z]+(ing|ed)$", new XPathProfile(XPathFlags.Multiline)),
        nameof(GeneratedPatterns.Pcre2Ing) => (GeneratedPatterns.Pcre2Ing, Ing, new Pcre2Profile()),
        nameof(GeneratedPatterns.Pcre2IngLines) => (GeneratedPatterns.Pcre2IngLines, "^[a-z]+(ing|ed)$", new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, Pcre2CompileFlags.Multiline)),
        nameof(GeneratedPatterns.Nested.Range) => (GeneratedPatterns.Nested.Range, "([a-z]+) ([0-9]+)(ing|ed)", new RegularCoreProfile(RegularCoreMode.UnicodeScalars, RegularCoreFlags.None)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown generated pattern.")
    };

    private static readonly string[] Texts =
    [
        "", "x", "winning", "walked", "Winning WALKED", "xx 12 winning walked ", "ab\nwinning\nwalked\nzz", "ing", "12-345 and 6-7",
        "walkedwalkedwalked!", string.Concat(Enumerable.Repeat("xx 12 winning walked ", 50)),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void GeneratedKernelsReportCompiledAndAgreeWithTheInterpreter(string name)
    {
        var (create, pattern, profile) = GetCase(name);
        var generated = create();
        var oracle = Oracle(pattern, profile);
        Assert.Equal(RegexExecutionBackend.Compiled, generated.ExecutionBackend);
        Assert.Same(generated, create());
        Assert.Same(generated, generated.Compile());
        Assert.Equal(oracle.GroupCount, generated.GroupCount);

        var expectedSpace = oracle.CreateWorkspace();
        var actualSpace = generated.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[oracle.GroupCount];
        foreach (var text in Texts)
        {
            // XSD has no search position, only whole-value membership at zero.
            for (var start = 0; start <= (profile is XsdProfile ? 0 : Math.Min(text.Length, 12)); start++)
            {
                Assert.Equal(expectedSpace.Find(text, start, out var expectedRange), actualSpace.Find(text, start, out var actualRange));
                Assert.Equal(expectedRange, actualRange);
                Assert.Equal(expectedSpace.IsMatch(text, start), actualSpace.IsMatch(text, start));
                Assert.Equal(expectedSpace.FindCaptures(text, start, expectedGroups, out var expectedWhole),
                    actualSpace.FindCaptures(text, start, actualGroups, out var actualWhole));
                Assert.Equal(expectedWhole, actualWhole);
                Assert.Equal(expectedGroups, actualGroups);
            }
        }
    }

    [Fact]
    public void AHandWrittenAcceleratedPatternStaysOffTheGeneratedPath()
    {
        var literal = GeneratedPatterns.CoreLiteral();
        Assert.NotEqual(RegexExecutionBackend.Compiled, literal.ExecutionBackend);
        Assert.Equal(RegexSearchStatus.Match, literal.CreateWorkspace().Find("hay needle hay", 0, out var range));
        Assert.Equal(new RegexMatchRange(4, 6), range);
    }

    [Fact]
    public void AGeneratedAmbiguousPatternAgreesWithTheInterpreter()
    {
        var text = new string('a', 3000) + "b";
        var profile = new RegularCoreProfile(RegularCoreMode.UnicodeScalars, RegularCoreFlags.None);
        var expected = RegexPattern.Parse("(a|aa)*c", profile, new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted))
            .CreateWorkspace().Find(text, 0, out var expectedRange);
        var actual = GeneratedPatterns.CoreAmbiguous().CreateWorkspace().Find(text, 0, out var actualRange);
        if (expected != RegexSearchStatus.Match && expected != RegexSearchStatus.NoMatch)
        {
            return;
        }

        Assert.Equal(expected, actual);
        Assert.Equal(expectedRange, actualRange);
    }

    [Fact]
    public void AStaleFingerprintFallsBackToTheInterpreter()
    {
        var pattern = RegexPattern.Parse(Ing, new RegularCoreProfile(RegularCoreMode.UnicodeScalars, RegularCoreFlags.None),
            new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
        static int kernel(ReadOnlySpan<char> text, int start, Span<int> caps, Span<int> stack, long max, ref long work, ref KernelGuard guard) =>
            throw new InvalidOperationException("a stale kernel must never run");
        var fallback = pattern.WithGeneratedKernels(0x1234UL, kernel, kernel);
        Assert.NotEqual(RegexExecutionBackend.Compiled, fallback.ExecutionBackend);
        Assert.Equal(RegexSearchStatus.Match, fallback.CreateWorkspace().Find("winning", 0, out _));
    }

    [Fact]
    public void GeneratedPosixIsRejected()
    {
        var posix = RegexPattern.Parse("a+", new PosixProfile(PosixDialect.Extended), new RegexCompileOptions(RegexInputKind.Bytes, RegexExecutionMode.Auto));
        static int kernel(ReadOnlySpan<char> text, int start, Span<int> caps, Span<int> stack, long max, ref long work, ref KernelGuard guard) => 0;
        Assert.Throws<NotSupportedException>(() => posix.WithGeneratedKernels(0, kernel, kernel));
    }

    private static long SteadyStateBytes(Func<RegexSearchStatus> call)
    {
        var best = long.MaxValue;
        for (var round = 0; round < 100 && best != 0; round++)
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

    public static IEnumerable<TheoryDataRow<string>> Safety() =>
    [
        new(nameof(GeneratedPatterns.CoreIng)),
        new(nameof(GeneratedPatterns.EcmaIng)),
        new(nameof(GeneratedPatterns.XPathIng)),
        new(nameof(GeneratedPatterns.Pcre2Ing))
    ];

    private static string Text(int repeats) => string.Concat(Enumerable.Repeat("xx 12 winning walked ", repeats));

    [Theory]
    [MemberData(nameof(Safety))]
    public void GeneratedMatchingAllocatesNothing(string name)
    {
        var create = GetCase(name).Create;
        var space = create().CreateWorkspace();
        var text = Text(200);
        var groups = new RegexCapture[create().GroupCount];
        Assert.Equal(0, SteadyStateBytes(() => space.FindCaptures(text, 0, groups, out _)));
        Assert.Equal(0, SteadyStateBytes(() => space.Find(text, 5, out _)));
    }

    [Theory]
    [MemberData(nameof(Safety))]
    public void GeneratedKernelsHonourCancellationAndTimeouts(string name)
    {
        var create = GetCase(name).Create;
        var space = create().CreateWorkspace();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, space.Find(Text(2000), 0, out _, new RegexMatchOptions { CancellationToken = cancelled.Token }));
        var big = Text(200_000);
        Assert.Equal(RegexSearchStatus.TimedOut, space.Find(big, 0, out _, new RegexMatchOptions { Timeout = TimeSpan.FromTicks(1) }));
        Assert.Equal(RegexSearchStatus.Match, space.Find(big, 0, out _, new RegexMatchOptions { Timeout = TimeSpan.FromMinutes(1) }));
    }

    [Theory]
    [MemberData(nameof(Safety))]
    public void GeneratedKernelsNeverFailALimitTheInterpreterSurvives(string name)
    {
        var create = GetCase(name).Create;
        var generated = create();
        var oracle = RegexPattern.Parse(Ing, ProfileOf(generated), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted)).CreateWorkspace();
        var space = generated.CreateWorkspace();
        var text = Text(40);
        foreach (var work in new long[] { 1, 5, 17, 100, 1000, 20000, 10_000_000 })
        {
            var options = new RegexMatchOptions { Limits = new RegexMatchLimits(work, 1 << 20) };
            var expected = oracle.Find(text, 0, out var expectedRange, options);
            var actual = space.Find(text, 0, out var actualRange, options);
            if (expected != RegexSearchStatus.Match && expected != RegexSearchStatus.NoMatch)
            {
                continue;
            }

            Assert.Equal(expected, actual);
            Assert.Equal(expectedRange, actualRange);
        }
    }

    private static RegexProfile ProfileOf(RegexPattern pattern) => pattern.Profile;

    [Theory]
    [MemberData(nameof(Safety))]
    public async Task OneGeneratedPatternServesConcurrentWorkspaces(string name)
    {
        var create = GetCase(name).Create;
        var generated = create();
        var text = Text(50);
        generated.CreateWorkspace().Find(text, 0, out var expected);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            var space = generated.CreateWorkspace();
            for (var i = 0; i < 300; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, space.Find(text, 0, out var range));
                Assert.Equal(expected, range);
            }
        })));
    }
}
