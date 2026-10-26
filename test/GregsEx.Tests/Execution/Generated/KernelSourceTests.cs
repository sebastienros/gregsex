using System.Reflection;
using GregsEx.Contracts.Acceleration;
using GregsEx.Execution.Compiled;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GregsEx.Tests.Execution.Generated;

// GregsEx.SourceGeneration.KernelSource is the public kernel-emission surface of GregsEx.Generator.dll for third-party
// source generators (a TextMate grammar generator cannot use [GeneratedPattern]: Roslyn generators never see each
// other's output). Such a generator loads the consumer's GregsEx.Generator.dll at build time and calls TryEmit through
// reflection, so these tests go through the same shim: the public signature is the contract.
public class KernelSourceTests
{
    private const int TextMate = 8; // OnigurumaOptions.CaptureGroup

    private sealed class Shim
    {
        private readonly Assembly _assembly;
        private readonly MethodInfo _tryEmit;
        private readonly Type _options;

        internal Shim()
        {
            _assembly = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "generator", "GregsEx.Generator.dll"));
            var type = _assembly.GetType("GregsEx.SourceGeneration.KernelSource", throwOnError: true)!;
            _tryEmit = type.GetMethod("TryEmit", BindingFlags.Public | BindingFlags.Static)!;
            _options = _assembly.GetType("GregsEx.OnigurumaOptions", throwOnError: true)!;
        }

        // RegexCompileLimits(maxPatternUnits, maxNestingDepth, maxInstructions, maxClassRanges, maxCaptureGroups, ...): the
        // raised ceilings TextMate grammars need, built in the generator assembly's own copy of the public types.
        internal object RaisedLimits()
        {
            var limits = Activator.CreateInstance(_assembly.GetType("GregsEx.RegexCompileLimits", throwOnError: true)!,
                1 << 22, 4096, 1 << 24, 65_536, 32_767, 10_000_000L, 67_108_864L, 67_108_864L)!;
            return Activator.CreateInstance(_assembly.GetType("GregsEx.OnigurumaCompileOptions", throwOnError: true)!, limits, null)!;
        }

        internal Result TryEmit(string? pattern, int options, string plain = "Plain", string capture = "Capture", object? compileOptions = null)
        {
            object result;
            try
            { result = _tryEmit.Invoke(null, [pattern, Enum.ToObject(_options, options), plain, capture, compileOptions])!; }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { throw e.InnerException; }
            var type = result.GetType();
            object Get(string name) => type.GetProperty(name)!.GetValue(result)!;
            var diagnostic = Get("Diagnostic");
            return new Result(Get("Status").ToString()!, (ulong)Get("Fingerprint"), (int)Get("GroupCount"),
                (string?)type.GetProperty("PlainClass")!.GetValue(result), (string?)type.GetProperty("CaptureClass")!.GetValue(result),
                (string?)type.GetProperty("DeclineReason")!.GetValue(result),
                Convert.ToInt32(diagnostic.GetType().GetProperty("Code")!.GetValue(diagnostic), System.Globalization.CultureInfo.InvariantCulture),
                (int)diagnostic.GetType().GetProperty("Offset")!.GetValue(diagnostic)!,
                (string?)diagnostic.GetType().GetProperty("Message")!.GetValue(diagnostic));
        }
    }

    private sealed record Result(string Status, ulong Fingerprint, int GroupCount, string? PlainClass, string? CaptureClass,
        string? DeclineReason, int DiagnosticCode, int DiagnosticOffset, string? DiagnosticMessage);

    private static readonly Shim Generator = new();

    private static ulong RuntimeFingerprint(string pattern, OnigurumaProfile profile, RegexCompileLimits? limits = null)
    {
        Assert.True(OnigKernelPlan.TryPlan(pattern, profile, limits ?? new RegexCompileLimits(), LoweringLimits.Default, out var plan, out _, out _));
        Assert.NotNull(plan);
        return plan!.Fingerprint();
    }

    // Compiles the emitted classes as a consumer would host them and returns their Run methods as kernels.
    private static (CoreKernel Plain, CoreKernel Capture) Compile(Result result)
    {
        var source = "using System;\n#nullable enable\n#pragma warning disable\nstatic class Host\n{\n" + result.PlainClass + "\n" + (result.CaptureClass ?? string.Empty) + "\n}\n";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase)).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        var compilation = CSharpCompilation.Create("Kernels", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var host = Assembly.Load(image.ToArray()).GetType("Host", throwOnError: true)!;
        CoreKernel Run(string name) => (CoreKernel)Delegate.CreateDelegate(typeof(CoreKernel),
            host.GetNestedType(name, BindingFlags.NonPublic)!.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static)!);
        var plain = Run("Plain");
        return (plain, result.CaptureClass is null ? plain : Run("Capture"));
    }

    [Fact]
    public void AnEligiblePatternYieldsBothClassesAndTheRuntimeFingerprint()
    {
        var result = Generator.TryEmit("[a-z]+(ing|ed)", TextMate);
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(2, result.GroupCount);
        Assert.Equal(RuntimeFingerprint("[a-z]+(ing|ed)", RegexProfiles.OnigurumaTextMate), result.Fingerprint);
        Assert.Contains("private static class Plain", result.PlainClass);
        Assert.Contains("internal static int Run(", result.PlainClass);
        Assert.Contains("private static class Capture", result.CaptureClass);
        Assert.Null(result.DeclineReason);
        Assert.Equal(0, result.DiagnosticCode);
    }

    [Fact]
    public void APatternWithoutGroupsHasNoCaptureClass()
    {
        var result = Generator.TryEmit("[a-z]+(?:ing|ed)", TextMate, "P", "C");
        Assert.Equal("Succeeded", result.Status);
        Assert.Equal(1, result.GroupCount);
        Assert.Contains("private static class P", result.PlainClass);
        Assert.Null(result.CaptureClass);
    }

    [Fact]
    public void EmittedKernelsBindAndMatchLikeTheInterpreter()
    {
        const string pattern = @"\b([a-z]+)(ing|ed)\b";
        var result = Generator.TryEmit(pattern, TextMate);
        Assert.Equal("Succeeded", result.Status);
        var (plain, capture) = Compile(result);

        var interpreted = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate);
        var generated = interpreted.WithGeneratedKernels(result.Fingerprint, plain, capture);
        Assert.Equal(RegexExecutionBackend.Interpreted, interpreted.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Compiled, generated.ExecutionBackend);

        foreach (var text in new[] { "they were winning and jumped", "nothing", "ing ed", "x" })
        {
            var expectedGroups = new RegexCapture[interpreted.GroupCount];
            var actualGroups = new RegexCapture[generated.GroupCount];
            var expected = interpreted.CreateWorkspace().FindCaptures(text, 0, expectedGroups, out var expectedRange);
            var actual = generated.CreateWorkspace().FindCaptures(text, 0, actualGroups, out var actualRange);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedRange, actualRange);
            Assert.Equal(expectedGroups, actualGroups);
        }
    }

    [Fact]
    public void AKernelOfAnotherRevisionIsIgnoredAtBinding()
    {
        var result = Generator.TryEmit("[a-z]+(ing|ed)", TextMate);
        var (plain, capture) = Compile(result);
        var bound = OnigurumaPattern.Parse("[a-z]+(ing|ed)", RegexProfiles.OnigurumaTextMate).WithGeneratedKernels(result.Fingerprint ^ 1, plain, capture);
        Assert.Equal(RegexExecutionBackend.Interpreted, bound.ExecutionBackend);
    }

    [Fact]
    public void APatternTheKernelDoesNotModelIsDeclinedWithTheReason()
    {
        var result = Generator.TryEmit(@"(a)\1", TextMate);
        Assert.Equal("Declined", result.Status);
        Assert.False(string.IsNullOrEmpty(result.DeclineReason));
        Assert.Null(result.PlainClass);
        Assert.Null(result.CaptureClass);
        Assert.Equal(0UL, result.Fingerprint);
        Assert.False(OnigKernelPlan.TryPlan(@"(a)\1", RegexProfiles.OnigurumaTextMate, new RegexCompileLimits(), LoweringLimits.Default, out var plan, out var reason, out _) && plan is not null);
        Assert.Equal(reason, result.DeclineReason);
    }

    [Fact]
    public void AnInvalidPatternCarriesTheParseDiagnostic()
    {
        var result = Generator.TryEmit("(unclosed", TextMate);
        Assert.Equal("Invalid", result.Status);
        Assert.Null(result.PlainClass);
        Assert.Null(result.DeclineReason);
        Assert.False(OnigurumaPattern.TryParse("(unclosed", RegexProfiles.OnigurumaTextMate, out _, out var expected));
        Assert.Equal((int)expected.Code, result.DiagnosticCode);
        Assert.Equal(expected.Offset, result.DiagnosticOffset);
        Assert.Equal(expected.Message, result.DiagnosticMessage);
    }

    [Fact]
    public void RaisedCompileLimitsAdmitWhatTheDefaultsReject()
    {
        // A pattern longer than the default 65,536 units (an extended-mode comment), within the raised ceilings TextMate
        // grammars use. The options are CaptureGroup | Extended.
        const int options = 8 | 4;
        var profile = new OnigurumaProfile(OnigurumaOptions.CaptureGroup | OnigurumaOptions.Extended);
        var pattern = "# " + new string('x', 70_000) + "\n[a-z]+(ing|ed)";
        var rejected = Generator.TryEmit(pattern, options);
        Assert.Equal("Invalid", rejected.Status);
        Assert.Equal((int)RegexDiagnosticCode.PatternLimitExceeded, rejected.DiagnosticCode);

        var admitted = Generator.TryEmit(pattern, options, compileOptions: Generator.RaisedLimits());
        Assert.Equal("Succeeded", admitted.DeclineReason ?? admitted.Status);
        var limits = new RegexCompileLimits(1 << 22, 4096, 1 << 24, 65_536, 32_767);
        Assert.Equal(RuntimeFingerprint(pattern, profile, limits), admitted.Fingerprint);
        var (plain, capture) = Compile(admitted);
        var bound = OnigurumaPattern.Parse(pattern, profile, new OnigurumaCompileOptions(limits))
            .WithGeneratedKernels(admitted.Fingerprint, plain, capture);
        Assert.Equal(RegexExecutionBackend.Compiled, bound.ExecutionBackend);
        Assert.Equal(RegexSearchStatus.Match, bound.CreateWorkspace().Find("they were winning", 0, out var range));
        Assert.Equal(new RegexMatchRange(10, 7), range);
        // Parsed with the default limits, the pattern is rejected before any kernel can bind.
        Assert.False(OnigurumaPattern.TryParse(pattern, profile, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.PatternLimitExceeded, diagnostic.Code);
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        Assert.Throws<ArgumentNullException>(() => Generator.TryEmit(null, TextMate));
        Assert.Throws<ArgumentException>(() => Generator.TryEmit("a", TextMate, plain: ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => Generator.TryEmit("a", 1024));
        Assert.Throws<ArgumentException>(() => Generator.TryEmit("a", 8 | 16));
    }
}
