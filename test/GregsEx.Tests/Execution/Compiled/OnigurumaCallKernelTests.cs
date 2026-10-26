using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using GregsEx.Execution.Compiled;
using GregsEx.Tests.Execution.Generated;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// Subexpression calls (\g<name>) in the Oniguruma kernels, on both tiers (Compile() and the generator's emitted source):
// the interpreter is the oracle. The C# grammar of VS Code is the motivating case: its declaration patterns call
// type_name, type_args and tuple groups recursively, and TextMate hosts search them from every token boundary.
public class OnigurumaCallKernelTests
{
    private static readonly OnigurumaCompileOptions Options = OnigurumaCompileOptions.TextMate;
    private const int TextMate = 8; // OnigurumaOptions.CaptureGroup

    private static readonly Lazy<JsonNode> Fixture = new(() => JsonNode.Parse(File.ReadAllText(Path.Combine(CorpusDirectory(), "csharp-subexp-calls.json")))!);

    private static IEnumerable<(string Rule, string Pattern)> GrammarPatterns()
        => Fixture.Value["patterns"]!.AsArray().Select(p => (p!["rule"]!.GetValue<string>(), p["pattern"]!.GetValue<string>()));

    private static string[] Subjects() => Fixture.Value["subjects"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray();

    public static IEnumerable<TheoryDataRow<string>> Rules() => GrammarPatterns().Select(p => new TheoryDataRow<string>(p.Rule));

    private static string Result(OnigurumaWorkspace workspace, string input, int start, RegexCapture[] groups)
    {
        var status = workspace.FindCaptures(input, start, groups, out var range);
        if (status != RegexSearchStatus.Match)
        {
            return status.ToString();
        }

        var text = range.Index + ":" + range.Length;
        for (var g = 1; g < groups.Length; g++)
        {
            text += "|" + (groups[g].Participated ? groups[g].Index + ":" + groups[g].Length : "-");
        }

        return text;
    }

    // Every search a tokenizer could make: each subject from every offset, captures and the plain outcome.
    private static int Compare(OnigurumaPattern oracle, OnigurumaPattern kernels, IEnumerable<string> subjects, string name)
    {
        Assert.Equal(RegexExecutionBackend.Interpreted, oracle.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Compiled, kernels.ExecutionBackend);
        var expectedSpace = oracle.CreateWorkspace();
        var actualSpace = kernels.CreateWorkspace();
        var expectedGroups = new RegexCapture[oracle.GroupCount];
        var actualGroups = new RegexCapture[kernels.GroupCount];
        var compared = 0;
        foreach (var subject in subjects)
        {
            for (var start = 0; start <= subject.Length; start++)
            {
                var expected = Result(expectedSpace, subject, start, expectedGroups);
                var actual = Result(actualSpace, subject, start, actualGroups);
                Assert.True(expected == actual, $"{name} on {JsonValue.Create(subject)!.ToJsonString()} from {start}: interpreter {expected}, kernel {actual}");
                Assert.Equal(expectedSpace.IsMatch(subject, start), actualSpace.IsMatch(subject, start));
                compared++;
            }
        }

        return compared;
    }

    [Fact]
    public void EveryCallPatternOfTheCSharpGrammarGetsKernels()
    {
        foreach (var (rule, pattern) in GrammarPatterns())
        {
            Assert.Contains("\\g<", pattern);
            var result = KernelSourceTests.Generator.TryEmit(pattern, TextMate, compileOptions: KernelSourceTests.Generator.TextMateLimits());
            Assert.True(result.Status == "Succeeded", $"{rule}: {result.Status} {result.DeclineReason}");
            Assert.NotNull(result.CaptureClass);
        }
    }

    [Theory]
    [MemberData(nameof(Rules))]
    public void CompiledKernelsAgreeWithTheInterpreterOnTheCSharpGrammar(string rule)
    {
        var source = GrammarPatterns().First(p => p.Rule == rule).Pattern;
        var oracle = OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate, Options);
        var compiled = oracle.Compile();
        Assert.True(Compare(oracle, compiled, Subjects(), rule) > 1000);
    }

    [Fact]
    public void GeneratedKernelsAgreeWithTheInterpreterOnTheCSharpGrammar()
    {
        // One compilation hosts every pattern's classes, as a generated grammar does.
        var patterns = GrammarPatterns().ToList();
        var results = patterns.Select((p, i) => KernelSourceTests.Generator.TryEmit(p.Pattern, TextMate, "P" + i, "C" + i, KernelSourceTests.Generator.TextMateLimits())).ToList();
        Assert.All(results, r => Assert.Equal("Succeeded", r.Status));
        var kernels = KernelSourceTests.Compile(results, patterns.Select((_, i) => "P" + i).ToList(), patterns.Select((_, i) => "C" + i).ToList());
        var subjects = Subjects();
        for (var i = 0; i < patterns.Count; i++)
        {
            var oracle = OnigurumaPattern.Parse(patterns[i].Pattern, RegexProfiles.OnigurumaTextMate, Options);
            var generated = oracle.WithGeneratedKernels(results[i].Fingerprint, kernels[i].Plain, kernels[i].Capture);
            Compare(oracle, generated, subjects, patterns[i].Rule);
        }
    }

    [Fact]
    public void TheScannerAgreesWithTheInterpreterOnTheCSharpGrammar()
    {
        var patterns = GrammarPatterns().Select(p => OnigurumaPattern.Parse(p.Pattern, RegexProfiles.OnigurumaTextMate, Options)).ToList();
        var oracle = new OnigurumaScanner(patterns);
        var scanner = new OnigurumaScanner(patterns.Select(p => p.Compile()).ToList());
        foreach (var subject in Subjects())
        {
            for (var at = 0; at <= subject.Length; at++)
            {
                var expected = oracle.FindNextMatch(subject, at, out var expectedIndex);
                Assert.Equal(expected, scanner.FindNextMatch(subject, at, out var actualIndex));
                Assert.Equal(expectedIndex, actualIndex);
                Assert.Equal(oracle.Captures.ToArray(), scanner.Captures.ToArray());
            }
        }
    }

    // Focused shapes: nested and recursive calls, captures inside called groups, calls before their definition, the
    // whole-pattern call, calls under look-arounds and atomic groups, unrolled and empty repeats of a called group, the
    // nest limit, and searches that run out of work (which the kernel hands back to the interpreter).
    public static IEnumerable<TheoryDataRow<string, string>> Shapes()
    {
        yield return new(@"(?<t>\((?:[^()]|\g<t>)*\))", "x((a)(b))y ((()) (a(b)c)");
        yield return new(@"(?<t>\((?:[^()]|\g<t>)*\))", new string('(', 60));
        yield return new(@"(?<t>\((?:[^()]|\g<t>)*\))", new string('(', 30) + new string(')', 30));
        yield return new(@"(?<t>a\g<t>?b)", "aaabbb aabbb ab");
        yield return new(@"(?<x>a)\g<x>(b)", "aab");
        yield return new(@"\g<x>(?<x>ab)", "abab");
        yield return new(@"(?<t>a|b\g<t>)", "bbba");
        yield return new(@"(?<p>(?<q>a)|b\g<p>)", "bba");
        yield return new(@"(?<p>(?<q>a)|b\g<p>)c", "bbac");
        yield return new(@"(?<n>\d+)\.\g<n>\d", "12.345");
        yield return new(@"a\g<0>?b", "aaabbb");
        yield return new(@"(a)\g<0>?b", "aaabbb");
        yield return new(@"(?<t>x(?=\g<t>)|y)", "xxy");
        yield return new(@"(?<t>x(?!\g<t>)|y)", "xxy");
        yield return new(@"(?<t>\[(?:[^\[\]]|\g<t>)*\])(?<=\])", "[[x]]");
        yield return new(@"(?<=\g<t>)(?<t>ab)", "abab");
        yield return new(@"(?<t>a(?>\g<t>?)b)", "aabb");
        yield return new(@"(?<t>a\g<t>?+b)", "aabb");
        yield return new(@"(?<t>[a-z]+)(?:\s*,\s*\g<t>)*", "ab , cd,ef");
        yield return new(@"(?<t>a){2}\g<t>", "aaa");
        yield return new(@"(?<t>a){0}\g<t>", "a");
        yield return new(@"(?<t>a(?<u>b)|c\g<t>)\g<u>", "cabb");
        yield return new(@"(?<t>x\g<t>*y)", "xxyxyy");
        yield return new(@"(?<t>(?:a|b)\g<t>?c)", "abcc");
        yield return new(@"(?i)(?<t>A\g<t>?b)", "aAbB");
        yield return new(@"(?<t>\[\g<t>?\])", new string('[', 25) + new string(']', 25));
        yield return new(@"(?<t>\[\g<t>?\])", new string('[', 20) + new string(']', 20));
        yield return new(@"(?<t>a(?:\g<t>|a)*b)", new string('a', 12) + "b");
        yield return new(@"(?<t>a(?:\g<t>|a)*b)", new string('a', 14));
        yield return new(@"(?<i>[_[:alpha:]][_[:alnum:]]*)\s*<(?<args>\s*\g<i>(?:\s*,\s*\g<i>)*\s*)>", "List< int , string > Dictionary<a,>");
    }

    // The shapes' emitted kernels, compiled together once.
    private static readonly Lazy<Dictionary<string, (ulong Fingerprint, CoreKernel Plain, CoreKernel Capture)>> GeneratedShapes = new(() =>
    {
        var patterns = Shapes().Select(s => s.Data.Item1).Distinct().ToList();
        var results = patterns.Select((p, i) => KernelSourceTests.Generator.TryEmit(p, TextMate, "P" + i, "C" + i, KernelSourceTests.Generator.TextMateLimits())).ToList();
        for (var i = 0; i < patterns.Count; i++)
        {
            Assert.True(results[i].Status == "Succeeded", $"{patterns[i]}: {results[i].Status} {results[i].DeclineReason}");
        }

        var kernels = KernelSourceTests.Compile(results, patterns.Select((_, i) => "P" + i).ToList(), patterns.Select((_, i) => "C" + i).ToList());
        return patterns.Select((p, i) => (p, (results[i].Fingerprint, kernels[i].Plain, kernels[i].Capture))).ToDictionary(e => e.p, e => e.Item2);
    });

    [Theory]
    [MemberData(nameof(Shapes))]
    public void ShapesAgreeWithTheInterpreterOnBothTiers(string pattern, string subject)
    {
        var oracle = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate, Options);
        Compare(oracle, oracle.Compile(), [subject], pattern);
        var (fingerprint, plain, capture) = GeneratedShapes.Value[pattern];
        Compare(oracle, oracle.WithGeneratedKernels(fingerprint, plain, capture), [subject], pattern);
    }

    [Fact]
    public void ASearchThatRunsOutOfWorkIsTheInterpreters()
    {
        // The kernel's step budget ends the attempt and the interpreter decides, so the limit status is its own.
        var oracle = OnigurumaPattern.Parse(@"(?<t>a(?:\g<t>|a)*b)", RegexProfiles.OnigurumaTextMate, Options);
        var compiled = oracle.Compile();
        var subject = new string('a', 40);
        Assert.Equal(RegexSearchStatus.LimitExceeded, oracle.CreateWorkspace().Find(subject, 0, out _));
        Assert.Equal(RegexSearchStatus.LimitExceeded, compiled.CreateWorkspace().Find(subject, 0, out _));
    }

    [Fact]
    public void ACallWhoseLoopBodyCanBeEmptyStaysOnTheInterpreter()
    {
        Assert.Throws<NotSupportedException>(() => OnigurumaPattern.Parse(@"(?<n>a?)\g<n>*", RegexProfiles.OnigurumaTextMate).Compile());
    }

    private static string CorpusDirectory([CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../corpora/oniguruma"));
}
