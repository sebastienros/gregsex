using System.Text;
using GregsEx.Contracts.Acceleration;
using GregsEx.SourceGeneration;
using Xunit;

namespace GregsEx.Tests.Integration.Acceleration;

[CollectionDefinition("AutoCompilationSwitch", DisableParallelization = true)]
public sealed class AutoCompilationSwitchCollection;

[Collection("AutoCompilationSwitch")]
public sealed partial class ExecutionAccelerationTests
{
    private const string GeneralPattern = "((a)|b)+c";

    [GeneratedPattern(GeneralPattern, RegularCoreFlags.None)]
    private static partial RegexPattern GeneratedCoreGeneral();

    [GeneratedPattern(GeneralPattern, EcmaScriptFlags.None)]
    private static partial RegexPattern GeneratedEcmaGeneral();

    private static AutoSwitchScope WithAutoCompilation(bool enabled) => new(enabled);

    private readonly struct AutoSwitchScope : IDisposable
    {
        public AutoSwitchScope(bool enabled) => AppContext.SetSwitch(CompilationContract.AutoCompilationSwitch, enabled);
        public void Dispose() => AppContext.SetSwitch(CompilationContract.AutoCompilationSwitch, false);
    }

    [Fact]
    public void GuardedAutoCompilationSelectsCompiledBackendOnlyWhenSwitchIsEnabled()
    {
        using (WithAutoCompilation(false))
        {
            Assert.False(CompilationContract.IsAutoCompilationPermitted);
            Assert.False(ExecutionModePolicy.SwitchesFor(RegexExecutionMode.Auto).HasFlag(AcceleratorSwitches.Compiled));

            var core = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var ecma = RegexPattern.Parse(GeneralPattern, new EcmaScriptProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var xsd = RegexPattern.Parse(GeneralPattern, new XsdProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var xpath = RegexPattern.Parse(GeneralPattern, new XPathProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var pcre = RegexPattern.Parse(GeneralPattern, new Pcre2Profile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));

            Assert.Equal(RegexExecutionBackend.Interpreted, core.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, ecma.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, xsd.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, xpath.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, pcre.ExecutionBackend);
        }

        using (WithAutoCompilation(true))
        {
            Assert.True(CompilationContract.IsAutoCompilationPermitted);
            Assert.True(ExecutionModePolicy.SwitchesFor(RegexExecutionMode.Auto).HasFlag(AcceleratorSwitches.Compiled));

            var core = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var ecma = RegexPattern.Parse(GeneralPattern, new EcmaScriptProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var xsd = RegexPattern.Parse(GeneralPattern, new XsdProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var xpath = RegexPattern.Parse(GeneralPattern, new XPathProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var pcre = RegexPattern.Parse(GeneralPattern, new Pcre2Profile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var posix = RegexPattern.Parse(GeneralPattern, new PosixProfile(PosixDialect.Extended), new RegexCompileOptions(RegexInputKind.Bytes, RegexExecutionMode.Auto));

            Assert.Equal(RegexExecutionBackend.Compiled, core.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Compiled, ecma.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Compiled, xsd.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Compiled, xpath.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Compiled, pcre.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, posix.ExecutionBackend);

            var interpretedCore = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
            Assert.Equal(RegexExecutionBackend.Interpreted, interpretedCore.ExecutionBackend);
        }
    }

    [Theory]
    [InlineData("the")]
    [InlineData("([a-z]+)=([a-z0-9]+)")]
    [InlineData("^[a-z ]+$")]
    public void AutoPreservesSemanticFastPathsWithoutCompilingWhenSwitchIsEnabled(string pattern)
    {
        using (WithAutoCompilation(true))
        {
            var auto = RegexPattern.Parse(pattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            // Semantic accelerators (Literal, KeyValue, AnchoredClassRun) report Interpreted; only compiled/generated kernels report Compiled.
            Assert.Equal(RegexExecutionBackend.Interpreted, auto.ExecutionBackend);

            // Direct plans in ECMAScript and PCRE2 also stay Interpreted when Auto compilation is enabled.
            var ecmaDirect = RegexPattern.Parse("the", new EcmaScriptProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var pcreDirect = RegexPattern.Parse("the", new Pcre2Profile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            Assert.Equal(RegexExecutionBackend.Interpreted, ecmaDirect.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, pcreDirect.ExecutionBackend);
        }
    }

    [Fact]
    public void AutoCompilationRespectsMaxPlanBytesAndFallsBackCleanly()
    {
        var interpreted = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
        using (WithAutoCompilation(true))
        {
            // Give exact plan budget for the semantic program so the additional compiled kernel reservation exceeds MaxPlanBytes and falls back to Interpreted.
            var tightOptions = new RegexCompileOptions(
                RegexInputKind.Utf16,
                RegexExecutionMode.Auto,
                limits: new RegexCompileLimits(maxPlanBytes: interpreted.RetainedBytes));
            var tightPattern = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), tightOptions);
            Assert.Equal(RegexExecutionBackend.Interpreted, tightPattern.ExecutionBackend);

            using var workspace = tightPattern.CreateWorkspace();
            Assert.Equal(RegexSearchStatus.Match, workspace.Find("xx ababc yy", 0, out var span));
            Assert.Equal(new RegexMatchRange(3, 5), span);
        }
    }

    [Fact]
    public void GeneratedPatternsWorkWhenAutoCompilationSwitchIsEnabled()
    {
        using (WithAutoCompilation(true))
        {
            var core = GeneratedCoreGeneral();
            var ecma = GeneratedEcmaGeneral();
            Assert.Equal(RegexExecutionBackend.Compiled, core.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Compiled, ecma.ExecutionBackend);

            using var coreSpace = core.CreateWorkspace();
            using var ecmaSpace = ecma.CreateWorkspace();
            var coreCaptures = new RegexCapture[core.GroupCount];
            var ecmaCaptures = new RegexCapture[ecma.GroupCount];
            Assert.Equal(RegexSearchStatus.Match, coreSpace.FindCaptures("prefix ababc!", 0, coreCaptures, out var coreSpan));
            Assert.Equal(RegexSearchStatus.Match, ecmaSpace.FindCaptures("prefix ababc!", 0, ecmaCaptures, out var ecmaSpan));
            Assert.Equal(new RegexMatchRange(7, 5), coreSpan);
            Assert.Equal(coreSpan, ecmaSpan);
            Assert.Equal(coreCaptures, ecmaCaptures);
        }
    }

    [Fact]
    public void EcmaScriptHostOperationsMatchUnderInterpretedAndAutoCompiled()
    {
        var input = "ok ababc; ok ac; done";
        var interpreted = RegexPattern.Parse(GeneralPattern, new EcmaScriptProfile(EcmaScriptFlags.Global), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
        RegexPattern autoCompiled;
        using (WithAutoCompilation(true))
        {
            autoCompiled = RegexPattern.Parse(GeneralPattern, new EcmaScriptProfile(EcmaScriptFlags.Global), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
        }
        Assert.Equal(RegexExecutionBackend.Compiled, autoCompiled.ExecutionBackend);

        using var oracleMatcher = EcmaScriptMatcher.Bind(interpreted);
        using var autoMatcher = EcmaScriptMatcher.Bind(autoCompiled);
        var oracleGroups = new RegexCapture[interpreted.GroupCount];
        var autoGroups = new RegexCapture[autoCompiled.GroupCount];
        for (var i = 0; i < 3; i++)
        {
            var expectedStatus = oracleMatcher.Exec(input, oracleGroups, out var expectedRange);
            var actualStatus = autoMatcher.Exec(input, autoGroups, out var actualRange);
            Assert.Equal(expectedStatus, actualStatus);
            Assert.Equal(expectedRange, actualRange);
            Assert.Equal(oracleMatcher.LastIndex, autoMatcher.LastIndex);
            Assert.Equal(oracleGroups, autoGroups);
        }
    }

    public static IEnumerable<TheoryDataRow<string, string>> CrossFlavorDifferentialData()
    {
        var patterns = new[]
        {
            "the",
            "(the|and|of) ([a-z]+)",
            "([a-z]+)=([a-z0-9]+)",
            "^([0-9]{4})-([0-9]{2})-([0-9]{2})$",
            "(ERROR|WARN) ([a-z]+)",
            "a(b|c)*d",
            "[a-z]+(ing|ed)",
            "(ab){2,4}c",
        };
        foreach (var p in patterns)
        {
            yield return new TheoryDataRow<string, string>(p, "RegularCore");
            yield return new TheoryDataRow<string, string>(p, "EcmaScript");
            yield return new TheoryDataRow<string, string>(p, "Xsd");
            yield return new TheoryDataRow<string, string>(p, "XPath");
            yield return new TheoryDataRow<string, string>(p, "Posix");
            yield return new TheoryDataRow<string, string>(p, "Pcre2");
        }
    }

    [Theory]
    [MemberData(nameof(CrossFlavorDifferentialData))]
    public void CrossFlavorAutoMatchesInterpretedOracle(string pattern, string flavor)
    {
        var inputs = new[]
        {
            "",
            "the cat and dog of mice",
            "host=db01 port=5432",
            "2026-10-15",
            "xx ERROR failed WARN slow running walked abababc acbcd",
        };

        RegexProfile profile = flavor switch
        {
            "RegularCore" => new RegularCoreProfile(),
            "EcmaScript" => new EcmaScriptProfile(),
            "Xsd" => new XsdProfile(),
            "XPath" => new XPathProfile(),
            "Posix" => new PosixProfile(PosixDialect.Extended),
            "Pcre2" => new Pcre2Profile(),
            _ => throw new ArgumentOutOfRangeException(nameof(flavor)),
        };

        var kind = flavor == "Posix" ? RegexInputKind.Bytes : RegexInputKind.Utf16;
        var oracle = RegexPattern.Parse(pattern, profile, new RegexCompileOptions(kind, RegexExecutionMode.Interpreted));
        RegexPattern autoSemantic;
        using (WithAutoCompilation(false))
        {
            autoSemantic = RegexPattern.Parse(pattern, profile, new RegexCompileOptions(kind, RegexExecutionMode.Auto));
        }
        RegexPattern autoCompiled;
        using (WithAutoCompilation(true))
        {
            autoCompiled = RegexPattern.Parse(pattern, profile, new RegexCompileOptions(kind, RegexExecutionMode.Auto));
        }

        using var oracleSpace = oracle.CreateWorkspace();
        using var semanticSpace = autoSemantic.CreateWorkspace();
        using var compiledSpace = autoCompiled.CreateWorkspace();
        var oracleCaptures = new RegexCapture[oracle.GroupCount];
        var semanticCaptures = new RegexCapture[autoSemantic.GroupCount];
        var compiledCaptures = new RegexCapture[autoCompiled.GroupCount];

        foreach (var input in inputs)
        {
            if (flavor == "Xsd")
            {
                var expectedXsd = oracleSpace.IsMatch(input, 0);
                Assert.Equal(expectedXsd, semanticSpace.IsMatch(input, 0));
                Assert.Equal(expectedXsd, compiledSpace.IsMatch(input, 0));
                continue;
            }

            if (kind == RegexInputKind.Bytes)
            {
                var bytes = Encoding.UTF8.GetBytes(input);
                foreach (var start in new[] { 0, bytes.Length / 2, bytes.Length })
                {
                    Assert.Equal(oracleSpace.IsMatch(bytes, start), semanticSpace.IsMatch(bytes, start));
                    Assert.Equal(oracleSpace.IsMatch(bytes, start), compiledSpace.IsMatch(bytes, start));

                    var expectedStatus = oracleSpace.FindCaptures(bytes, start, oracleCaptures, out var expectedSpan);
                    var semanticStatus = semanticSpace.FindCaptures(bytes, start, semanticCaptures, out var semanticSpan);
                    var compiledStatus = compiledSpace.FindCaptures(bytes, start, compiledCaptures, out var compiledSpan);

                    Assert.Equal(expectedStatus, semanticStatus);
                    Assert.Equal(expectedStatus, compiledStatus);
                    Assert.Equal(expectedSpan, semanticSpan);
                    Assert.Equal(expectedSpan, compiledSpan);
                    Assert.Equal(oracleCaptures, semanticCaptures);
                    Assert.Equal(oracleCaptures, compiledCaptures);
                }
                continue;
            }

            foreach (var start in new[] { 0, input.Length / 2, input.Length })
            {
                var expectedMatch = oracleSpace.IsMatch(input, start);
                Assert.Equal(expectedMatch, semanticSpace.IsMatch(input, start));
                Assert.Equal(expectedMatch, compiledSpace.IsMatch(input, start));

                var expectedStatus = oracleSpace.FindCaptures(input, start, oracleCaptures, out var expectedSpan);
                var semanticStatus = semanticSpace.FindCaptures(input, start, semanticCaptures, out var semanticSpan);
                var compiledStatus = compiledSpace.FindCaptures(input, start, compiledCaptures, out var compiledSpan);

                Assert.Equal(expectedStatus, semanticStatus);
                Assert.Equal(expectedStatus, compiledStatus);
                Assert.Equal(expectedSpan, semanticSpan);
                Assert.Equal(expectedSpan, compiledSpan);
                Assert.Equal(oracleCaptures, semanticCaptures);
                Assert.Equal(oracleCaptures, compiledCaptures);
            }
        }
    }

    [Fact]
    public void LimitsAndCancellationHoldAcrossAutoTiers()
    {
        using (WithAutoCompilation(true))
        {
            var patterns = new[]
            {
                "the",                          // Literal
                "^([0-9]{4})-([0-9]{2})$",      // OnePass
                "(a|b)*c",                      // DFA / Compiled
                GeneralPattern,                 // Compiled
            };
            var text = string.Concat(Enumerable.Repeat("xxxx ", 400)) + "aaabbc ok";
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            foreach (var p in patterns)
            {
                var pattern = RegexPattern.Parse(p, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
                using var space = pattern.CreateWorkspace();
                Assert.Equal(RegexSearchStatus.Cancelled, space.Find(text, 0, out _, new RegexMatchOptions { CancellationToken = cts.Token }));

                var tinyWork = new RegexMatchOptions { Limits = new RegexMatchLimits(maxWork: 1) };
                var status = space.Find(text, 0, out _, tinyWork);
                Assert.True(status is RegexSearchStatus.LimitExceeded or RegexSearchStatus.NoMatch);
            }
        }
    }

    [Fact]
    public async Task SharedAutoPatternsServeConcurrentWorkspacesWithoutCrossTalk()
    {
        using (WithAutoCompilation(true))
        {
            var pattern = RegexPattern.Parse(GeneralPattern, new EcmaScriptProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            Assert.Equal(RegexExecutionBackend.Compiled, pattern.ExecutionBackend);
            var inputs = new[]
            {
                ("xx ababc alpha", new RegexMatchRange(3, 5)),
                ("zzzzzz ac beta!", new RegexMatchRange(7, 2)),
                ("no issues here", default),
            };

            var tasks = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                using var space = pattern.CreateWorkspace();
                var captures = new RegexCapture[pattern.GroupCount];
                for (var i = 0; i < 300; i++)
                {
                    var (text, expectedSpan) = inputs[(worker + i) % inputs.Length];
                    var status = space.FindCaptures(text, 0, captures, out var span);
                    if (expectedSpan.Length == 0)
                    {
                        Assert.Equal(RegexSearchStatus.NoMatch, status);
                    }
                    else
                    {
                        Assert.Equal(RegexSearchStatus.Match, status);
                        Assert.Equal(expectedSpan, span);
                    }
                }
            })).ToArray();

            await Task.WhenAll(tasks);
        }
    }

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

    [Fact]
    public void SteadyStateMatchingUnderAutoAllocatesZeroForUtf16AndBytes()
    {
        using (WithAutoCompilation(true))
        {
            var utf16Pattern = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            var bytesPattern = RegexPattern.Parse(GeneralPattern, new RegularCoreProfile(RegularCoreMode.RawBytes), new RegexCompileOptions(RegexInputKind.Bytes, RegexExecutionMode.Auto));
            Assert.Equal(RegexExecutionBackend.Compiled, utf16Pattern.ExecutionBackend);
            // RawBytes patterns run on the byte-oriented semantic/interpreted tiers and still allocate 0 bytes in steady state.
            Assert.Equal(RegexExecutionBackend.Interpreted, bytesPattern.ExecutionBackend);

            using var utf16Space = utf16Pattern.CreateWorkspace();
            using var bytesSpace = bytesPattern.CreateWorkspace();
            var utf16Captures = new RegexCapture[utf16Pattern.GroupCount];
            var bytesCaptures = new RegexCapture[bytesPattern.GroupCount];
            var text = "prefix ababc suffix";
            var utf8 = Encoding.UTF8.GetBytes(text);

            Assert.Equal(0, SteadyStateBytes(() => utf16Space.FindCaptures(text, 0, utf16Captures, out _)));
            Assert.Equal(0, SteadyStateBytes(() => utf16Space.Find(text, 0, out _)));
            Assert.Equal(0, SteadyStateBytes(() => bytesSpace.FindCaptures(utf8, 0, bytesCaptures, out _)));
            Assert.Equal(0, SteadyStateBytes(() => bytesSpace.Find(utf8, 0, out _)));
        }
    }
}
