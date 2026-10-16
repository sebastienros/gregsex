using GregsEx.Contracts;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
using Xunit;

namespace GregsEx.Tests.Integration.EcmaScript;

public sealed class PublicBoundaryTests
{
    [Theory]
    [InlineData(RegexExecutionMode.Interpreted)]
    [InlineData(RegexExecutionMode.Auto)]
    public void RealFactoriesAndGenericBackreferencesShareImmutablePlan(RegexExecutionMode mode)
    {
        var profile = new EcmaScriptProfile(EcmaScriptFlags.HasIndices | EcmaScriptFlags.Unicode);
        var pattern = RegexPattern.Parse("(?<letter>a)\\k<letter>", profile, new(executionMode: mode));
        Assert.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
        Assert.Same(profile, pattern.Profile);
        Assert.True(pattern.BooleanCapacity.CaptureSlots > 0);
        using var workspace = pattern.CreateWorkspace(new(pattern.BooleanCapacity));
        Assert.Equal(RegexSearchStatus.Match, workspace.IsMatch("xaa"));
        Assert.Equal(RegexSearchStatus.Match, workspace.Find("xaa", 0, out var range));
        Assert.Equal(new(1, 2), range);
        Assert.True(EcmaScriptMatcher.TryParse("(?<letter>a)\\k<letter>", profile, out var typed, out var diagnostic, new(executionMode: mode), frameCapacity: 16));
        using (typed)
        {
            Assert.Equal(default, diagnostic);
            var captures = new RegexCapture[2];
            Assert.Equal(RegexSearchStatus.Match, typed!.Exec("xaa", captures, out var typedRange));
            Assert.Equal(range, typedRange);
            Assert.Equal(new(true, 1, 1), captures[1]);
        }
    }
    [Theory]
    [InlineData(RegexInputKind.Utf8)]
    [InlineData(RegexInputKind.Bytes)]
    public void RepresentationRejectionPrecedesGrammar(RegexInputKind kind)
    {
        var profile = new EcmaScriptProfile();
        Assert.False(RegexPattern.TryParse("(", profile, out var pattern, out var d, new(kind)));
        Assert.Null(pattern);
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, d.Code);
        Assert.False(EcmaScriptMatcher.TryParse("(", profile, out _, out var e, new(kind)));
        Assert.Equal(d, e.Diagnostic);
        Assert.Equal("ES-020", e.FeatureId);
        Assert.False(RegexPattern.TryParse([40, 255], profile, out _, out d, new(kind)));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, d.Code);
        Assert.Equal(RegexOffsetUnit.Bytes, d.LocationUnit);
        using var w = RegexPattern.Parse("a", profile).CreateWorkspace();
        Assert.Throws<ArgumentException>(() => w.IsMatch("a"u8));
        Assert.Throws<ArgumentException>(() => EcmaScriptMatcher.Bind(RegexPattern.Parse("a", new RegularCoreProfile())));
    }
    [Fact]
    public void GenericLowerBoundPreservesOriginalContextAndBuiltinPolicyRemainsDistinct()
    {
        var p = RegexPattern.Parse("(?<=a)b", new EcmaScriptProfile(EcmaScriptFlags.Unicode));
        using var w = p.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.Match, w.Find("ab", 1, out var range));
        Assert.Equal(new(1, 1), range);
        p = RegexPattern.Parse(".", new EcmaScriptProfile(EcmaScriptFlags.Unicode | EcmaScriptFlags.Sticky));
        using var lower = p.CreateWorkspace();
        using var builtin = EcmaScriptMatcher.Bind(p);
        Assert.Equal(RegexSearchStatus.InvalidInput, lower.Find("😀", 1, out range));
        Assert.Equal(default, range);
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, lower.LastFailure.Code);
        builtin.LastIndex = 1;
        var groups = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.Match, builtin.Exec("😀", groups, out range));
        Assert.Equal(new(0, 2), range);
        Assert.Equal(2, builtin.LastIndex);
        // C018 is the frozen project/oracle policy; source bookkeeping qualification remains in reports.
        Assert.Equal(RegexSearchStatus.Match, lower.Find("😀", 0, out _));
    }
    [Fact]
    public void LogicalOutputsAdmissionGrowthCancellationAndReuseRemainTransactional()
    {
        var p = RegexPattern.Parse("(a)(b)?", new EcmaScriptProfile());
        using var w = p.CreateWorkspace(new());
        var sentinel = new RegexCapture(true, 99, 99);
        RegexCapture[] groups = [sentinel, sentinel, sentinel, sentinel];
        using var c = new CancellationTokenSource();
        c.Cancel();
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, w.FindCaptures("a", 0, groups.AsSpan(0, 1), out var range, new() { CancellationToken = c.Token }));
        Assert.Equal(default, range);
        Assert.All(groups, g => Assert.Equal(sentinel, g));
        Assert.Equal(3, w.LastFailure.RequiredCapacity);
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures("a", 0, groups, out range));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        Assert.All(groups[..3], g => Assert.Equal(default, g));
        Assert.Equal(sentinel, groups[3]);
        w.Reserve(p.CaptureCapacity);
        Assert.Equal(RegexSearchStatus.Cancelled, w.FindCaptures("", 0, groups, out _, new() { CancellationToken = c.Token }));
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("a", 0, groups, out range));
        Assert.Equal(new(true, 0, 1), groups[1]);
        Assert.Equal(default, groups[2]);
        Assert.Equal(sentinel, groups[3]);
        Assert.Equal(RegexSearchStatus.NoMatch, w.FindCaptures("x", 0, groups, out range));
        Assert.All(groups[..3], g => Assert.Equal(default, g));
        Assert.Equal(sentinel, groups[3]);
        var capacity = w.Capacity;
        Assert.Throws<RegexExecutionException>(() => w.Reserve(new(int.MaxValue, int.MaxValue, int.MaxValue)));
        Assert.Equal(capacity, w.Capacity);
        using var growing = p.CreateWorkspace(new(allowGrowth: true));
        Assert.Equal(RegexSearchStatus.Match, growing.IsMatch("a"));
        Assert.True(growing.Capacity.Contains(p.BooleanCapacity));
        Assert.Equal(RegexSearchStatus.LimitExceeded, growing.IsMatch("a", options: new() { Limits = new(maxWork: 1) }));
        Assert.Equal(RegexSearchStatus.Match, growing.IsMatch("a"));
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.IsMatch("a"));
    }
    [Fact]
    public void CompletePlanAndCompilationCeilingsIncludeComposition()
    {
        var profile = new EcmaScriptProfile();
        _ = RegexPattern.Parse("a", profile); // Explicit shared data initialization, not a warm-count experiment.
        var p = RegexPattern.Parse("(a)\\1", profile);
        Assert.True(RegexPattern.TryParse("(a)\\1", profile, out var exact, out _, new(limits: new(maxPlanBytes: p.RetainedBytes))));
        Assert.Equal(p.RetainedBytes, exact!.RetainedBytes);
        Assert.False(RegexPattern.TryParse("(a)\\1", profile, out _, out var d, new(limits: new(maxPlanBytes: p.RetainedBytes - 1))));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Code);
        var budget = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var unicode, out _));
        Assert.True(new EcmaCompiler().TryCompile("(a)\\1", profile, unicode!, ref budget, out var program, out _));
        var parserWork = budget.WorkConsumed;
        Assert.True(new EcmaExecutorFactory().TryCreate(program!, unicode!, ref budget, out _, out _));
        Assert.True(budget.WorkConsumed > parserWork);
        Assert.False(RegexPattern.TryParse("(a)\\1", profile, out _, out d, new(limits: new(maxCompileWork: parserWork))));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Code);
        Assert.False(RegexPattern.TryParse("(a)\\1", profile, out _, out d, new(limits: new(maxCompileMemoryBytes: budget.ReservedBytes))));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, d.Code);
    }
    [Fact]
    public async Task SharedPatternHasIndependentWorkspacesAndSessions()
    {
        var p = RegexPattern.Parse("(?<a>a+)\\k<a>", new EcmaScriptProfile(EcmaScriptFlags.Global));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var w = p.CreateWorkspace();
            using var m = EcmaScriptMatcher.Bind(p, frameCapacity: 32);
            for (var i = 0; i < 32; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, w.Find("xaaaa", 1, out var range));
                Assert.Equal(new(1, 4), range);
                m.LastIndex = 1;
                var captures = new RegexCapture[2];
                Assert.Equal(RegexSearchStatus.Match, m.Exec("xaaaa", captures, out range));
                Assert.Equal(new(1, 4), range);
            }
        })));
    }
    [Fact]
    public void SeededLiteralAlternativeAndMutationBoundariesHaveIndependentExpectations()
    {
        var random = new Random(20251011);
        foreach (var flags in new[] { EcmaScriptFlags.None, EcmaScriptFlags.Unicode, EcmaScriptFlags.UnicodeSets })
        {
            for (var i = 0; i < 64; i++)
            {
                var a = (char)('a' + random.Next(20));
                var b = (char)(a + 1);
                var n = random.Next(1, 10);
                var p = RegexPattern.Parse($"({a}+)|({b}+)", new EcmaScriptProfile(flags));
                using var w = p.CreateWorkspace();
                var groups = new RegexCapture[3];
                var input = "!" + new string(b, n) + "?";
                Assert.Equal(RegexSearchStatus.Match, w.FindCaptures(input, 0, groups, out var range));
                Assert.Equal(new(1, n), range);
                Assert.Equal(default, groups[1]);
                Assert.Equal(new(true, 1, n), groups[2]);
                Assert.Equal(RegexSearchStatus.NoMatch, w.Find("!?", 0, out _));
            }
        }

        foreach (var invalid in new[] { "(", "[", "(?=", "(?<a>", "\\p{NoSuchProperty}", "[a--]", "[a&&]" })
        {
            Assert.False(RegexPattern.TryParse(invalid, new EcmaScriptProfile(EcmaScriptFlags.UnicodeSets), out _, out _));
        }
    }
}
