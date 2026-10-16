using System.Text.RegularExpressions;
using GregsEx.Execution.Compiled;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace GregsEx.Tests.Execution.Generated;

// The generator is a netstandard2.0 assembly that links the engine's parser and planner sources instead of referencing
// GregsEx.dll. Its decisions must be identical to the runtime's (net10) KernelPlanner for every pattern and flavor:
// same validity, same eligibility, same plan fingerprint.
public class GeneratorPlannerParityTests
{
    private sealed record Case(string Pattern, string Attribute, RegexProfile Profile);

    private static string Literal(string s) => SymbolDisplay.FormatLiteral(s, quote: true);

    private static IEnumerable<Case> Cases()
    {
        var seen = new HashSet<string>();
        static IEnumerable<Case> All()
        {
            foreach (var row in Compiled.CompiledDifferentialTests.Data().Where(row => row.Data.Item2 == RegularCoreMode.UnicodeScalars))
            {
                var (pattern, _, flags) = row.Data;
                yield return new(pattern, $"(global::GregsEx.RegularCoreFlags){(int)flags}",
                    new RegularCoreProfile(RegularCoreMode.UnicodeScalars, flags));
            }

            foreach (var row in Compiled.EcmaCompiledDifferentialTests.Data())
            {
                var (pattern, flags) = row.Data;
                yield return new(pattern, $"(global::GregsEx.EcmaScriptFlags){(int)flags}",
                    new EcmaScriptProfile(flags));
            }

            foreach (var row in Compiled.Pcre2CompiledDifferentialTests.Data())
            {
                var (pattern, flags) = row.Data;
                yield return new(pattern, $"(global::GregsEx.Pcre2CompileFlags){(uint)flags}u",
                    new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, flags));
            }

            foreach (var row in Compiled.XmlCompiledDifferentialTests.XPathData())
            {
                var (pattern, flags) = row.Data;
                yield return new(pattern, $"(global::GregsEx.XPathFlags){(int)flags}",
                    new XPathProfile(flags));
            }

            foreach (var row in Compiled.XmlCompiledDifferentialTests.XsdData())
            {
                yield return new(row.Data, "global::GregsEx.SourceGeneration.GeneratedPatternFlavor.Xsd", new XsdProfile());
            }

            foreach (var bad in new[] { "(unclosed", "[z-a]", "a{3,2}", "\\", "(?<n>a)(?<n>b)", "*a" })
            {
                yield return new(bad, "(global::GregsEx.RegularCoreFlags)0", new RegularCoreProfile(RegularCoreMode.UnicodeScalars, RegularCoreFlags.None));
                yield return new(bad, "(global::GregsEx.EcmaScriptFlags)0", new EcmaScriptProfile(EcmaScriptFlags.None));
                yield return new(bad, "(global::GregsEx.Pcre2CompileFlags)0", new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, Pcre2CompileFlags.None));
                yield return new(bad, "(global::GregsEx.XPathFlags)0", new XPathProfile(XPathFlags.None));
                yield return new(bad, "global::GregsEx.SourceGeneration.GeneratedPatternFlavor.Xsd", new XsdProfile());
            }
        }
        foreach (var c in All())
        {
            if (seen.Add(c.Attribute + "|" + c.Pattern))
            {
                yield return c;
            }
        }
    }

    [Fact]
    public void TheNetstandardGeneratorDecidesLikeTheRuntimePlanner()
    {
        var cases = Cases().ToArray();
        var source = new System.Text.StringBuilder("using GregsEx;\nusing GregsEx.SourceGeneration;\npublic static partial class Patterns\n{\n");
        for (var i = 0; i < cases.Length; i++)
        {
            source.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"[GeneratedPattern({Literal(cases[i].Pattern)}, {cases[i].Attribute})] public static partial RegexPattern P{i}();");
        }

        source.AppendLine("}");

        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(path => !path.Contains("xunit", StringComparison.OrdinalIgnoreCase)).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        var compilation = CSharpCompilation.Create("Parity",
            [CSharpSyntaxTree.ParseText(source.ToString(), cancellationToken: TestContext.Current.CancellationToken)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var assembly = System.Reflection.Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "generator", "GregsEx.Generator.dll"));
        var generator = (IIncrementalGenerator)Activator.CreateInstance(assembly.GetType("GregsEx.Generator.PatternGenerator", throwOnError: true)!)!;
        var result = CSharpGeneratorDriver.Create(generator.AsSourceGenerator())
            .RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();

        var invalid = result.Diagnostics.Where(d => d.Id == "GREGEX001").Select(d => MethodOf(d)).ToHashSet();
        var interpreted = result.Diagnostics.Where(d => d.Id == "GREGEX003").Select(d => MethodOf(d)).ToHashSet();
        var fingerprints = new Dictionary<string, string>();
        foreach (var tree in result.GeneratedTrees)
        {
            var text = tree.GetText(TestContext.Current.CancellationToken).ToString();
            var match = Regex.Match(text, @"__GregsEx_(P\d+)_Create\(\) =>\s*global::GregsEx\.RegexPattern\.Parse.*?WithGeneratedKernels\(0x([0-9A-F]{16})UL", RegexOptions.Singleline);
            if (match.Success)
            {
                fingerprints[match.Groups[1].Value] = match.Groups[2].Value;
            }
        }

        var mismatches = new List<string>();
        var compiled = 0;
        for (var i = 0; i < cases.Length; i++)
        {
            var name = $"P{i}";
            var parsed = RegexPattern.TryParse(cases[i].Pattern, cases[i].Profile, out var pattern, out _,
                new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            string expected;
            if (!parsed)
            {
                expected = "invalid";
            }
            else if (!KernelPlanner.TryPlan(pattern!, out var plan, out _))
            {
                expected = "interpreted";
            }
            else
            {
                expected = plan!.Fingerprint().ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
            }

            var actual = invalid.Contains(name) ? "invalid" : interpreted.Contains(name) ? "interpreted"
                : fingerprints.TryGetValue(name, out var fp) ? fp : "missing";
            if (expected == "interpreted" || expected == "invalid" || actual == "interpreted" || actual == "invalid")
            { }
            else
            {
                compiled++;
            }

            if (expected != actual)
            {
                mismatches.Add($"{cases[i].Pattern} [{cases[i].Attribute}]: runtime={expected} generator={actual}");
            }
        }
        Assert.True(compiled > 100, $"Too few compiled cases to be a meaningful parity check: {compiled}.");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {cases.Length} differ:\n" + string.Join("\n", mismatches.Take(20)));
    }

    private static string MethodOf(Diagnostic d)
    {
        var message = d.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        var start = message.IndexOf('\'') + 1;
        return message[start..message.IndexOf('\'', start)];
    }
}
