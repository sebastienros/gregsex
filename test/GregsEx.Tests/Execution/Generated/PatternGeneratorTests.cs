using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GregsEx.Tests.Execution.Generated;

public class PatternGeneratorTests
{
    private static ISourceGenerator NewGenerator()
    {
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "generator", "GregsEx.Generator.dll"));
        var generator = (IIncrementalGenerator)Activator.CreateInstance(assembly.GetType("GregsEx.Generator.PatternGenerator", throwOnError: true)!)!;
        return generator.AsSourceGenerator();
    }
    private static GeneratorDriverRunResult Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        var compilation = CSharpCompilation.Create("Generated", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return CSharpGeneratorDriver.Create(NewGenerator()).RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
    }

    private static string Wrap(string member) => $$"""
        using GregsEx;
        using GregsEx.SourceGeneration;
        public static partial class Patterns
        {
            {{member}}
        }
        """;

    private static string[] Ids(GeneratorDriverRunResult result) => [.. result.Diagnostics.Select(d => d.Id)];

    [Fact]
    public void AnEligiblePatternEmitsKernelsAndNoDiagnostic()
    {
        var result = Run(Wrap("""[GeneratedPattern("[a-z]+(ing|ed)")] public static partial RegexPattern P();"""));
        Assert.Empty(result.Diagnostics);
        var text = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        Assert.Contains("WithGeneratedKernels", text);
        Assert.Contains("__GregsEx_P_Plain", text);
    }

    [Fact]
    public void AnInvalidPatternIsReportedAtBuildTime() =>
        Assert.Contains("GREGEX001", Ids(Run(Wrap("""[GeneratedPattern("(unclosed")] public static partial RegexPattern P();"""))));

    [Fact]
    public void AMethodThatIsNotStaticPartialRegexPatternIsRejected()
    {
        Assert.Contains("GREGEX002", Ids(Run(Wrap("""[GeneratedPattern("a+")] public static partial int P();"""))));
        Assert.Contains("GREGEX002", Ids(Run(Wrap("""[GeneratedPattern("a+")] public static partial RegexPattern P(int x);"""))));
    }

    [Fact]
    public void APatternServedByAHandWrittenPathStaysInterpretedWithAnInfoDiagnostic()
    {
        var result = Run(Wrap("""[GeneratedPattern("needle")] public static partial RegexPattern P();"""));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("GREGEX003", diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Info, diagnostic.Severity);
        Assert.DoesNotContain("WithGeneratedKernels", string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString())));
    }

    [Fact]
    public void GeneratedSourceIsIncrementallyCached()
    {
        var source = Wrap("""[GeneratedPattern("[a-z]+(ing|ed)")] public static partial RegexPattern P();""");
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase)).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        var compilation = CSharpCompilation.Create("Generated", [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([NewGenerator()], driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation, TestContext.Current.CancellationToken);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated {}", cancellationToken: TestContext.Current.CancellationToken)), TestContext.Current.CancellationToken);
        var step = driver.GetRunResult().Results[0].TrackedSteps["Specs"].SelectMany(s => s.Outputs);
        Assert.NotEmpty(step);
        Assert.All(step, o => Assert.True(o.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
    }

    [Theory]
    [InlineData("EcmaScriptFlags.IgnoreCase")]
    [InlineData("XPathFlags.IgnoreCase")]
    [InlineData("GeneratedPatternFlavor.Xsd")]
    public void Unicode17SelectionIsRetainedInGeneratedProfiles(string flavor)
    {
        var result = Run(Wrap($$"""[GeneratedPattern("((a)|b)+c", {{flavor}}, UnicodeVersion = UnicodeVersion.Unicode17_0)] public static partial RegexPattern P();"""));
        Assert.Empty(result.Diagnostics);
        var text = string.Join("\n", result.GeneratedTrees.Select(t => t.GetText().ToString()));
        Assert.Contains("WithGeneratedKernels", text);
        Assert.Contains("UnicodeVersion.Unicode17_0", text);
    }

    [Theory]
    [InlineData("EcmaScriptFlags.None", "(UnicodeVersion)0")]
    [InlineData("XPathFlags.None", "(UnicodeVersion)1800")]
    [InlineData("GeneratedPatternFlavor.Xsd", "(UnicodeVersion)(-1)")]
    [InlineData("RegularCoreFlags.None", "UnicodeVersion.Unicode17_0")]
    [InlineData("Pcre2CompileFlags.None", "UnicodeVersion.Unicode17_0")]
    public void UnsupportedUnicodeSelectionsHaveAnExplicitDiagnostic(string flavor, string version)
    {
        var result = Run(Wrap($$"""[GeneratedPattern("a+", {{flavor}}, UnicodeVersion = {{version}})] public static partial RegexPattern P();"""));
        Assert.Contains("GREGEX005", Ids(result));
    }

    [Fact]
    public void AttributeTypesDoNotConflictAcrossFriendAssemblies()
    {
        // Every project referencing GregsEx gets the attribute types; a friend assembly (InternalsVisibleTo) sees
        // those of the library it references too, which must not raise CS0436 (TextMateSharp's tests and grammars).
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        var cancellation = TestContext.Current.CancellationToken;

        var library = CSharpCompilation.Create("Library",
            [CSharpSyntaxTree.ParseText("""[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("App")]""", cancellationToken: cancellation)],
            references, options);
        CSharpGeneratorDriver.Create(NewGenerator()).RunGeneratorsAndUpdateCompilation(library, out var libraryOutput, out _, cancellation);
        using var image = new MemoryStream();
        var emitted = libraryOutput.Emit(image, cancellationToken: cancellation);
        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));

        var app = CSharpCompilation.Create("App",
            [CSharpSyntaxTree.ParseText(Wrap("""[GeneratedPattern("[a-z]+")] public static partial RegexPattern P();"""), cancellationToken: cancellation)],
            [.. references, MetadataReference.CreateFromImage(image.ToArray())], options);
        CSharpGeneratorDriver.Create(NewGenerator()).RunGeneratorsAndUpdateCompilation(app, out var appOutput, out _, cancellation);
        var diagnostics = appOutput.GetDiagnostics(cancellation).Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void KernelsCompileWithoutImplicitUsings()
    {
        // Kernels call span extension methods (AsSpan, SequenceEqual, IndexOfAnyInRange); TextMateSharp's grammar
        // project has no ImplicitUsings, so the generated file has to import System itself.
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList();
        var cancellation = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create("App",
            [CSharpSyntaxTree.ParseText(Wrap("""
                [GeneratedPattern("prefix[a-z]+tail")] public static partial RegexPattern Literal();
                [GeneratedPattern("(?:alpha|beta)[^\\n]*")] public static partial RegexPattern Branches();
                """), cancellationToken: cancellation)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        CSharpGeneratorDriver.Create(NewGenerator()).RunGeneratorsAndUpdateCompilation(compilation, out var output, out _, cancellation);
        Assert.Contains(output.SyntaxTrees, tree => tree.ToString().Contains(".AsSpan()", StringComparison.Ordinal));
        Assert.Empty(output.GetDiagnostics(cancellation).Where(d => d.Severity == DiagnosticSeverity.Error));
    }
}
