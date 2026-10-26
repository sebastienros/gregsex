using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Compiled;

public sealed record OnigurumaCallScenario(string Name, string Engine)
{
    public override string ToString() => $"{Name}:{Engine}";
}

/// <summary>
/// The C# grammar's subexpression-call patterns (every VS Code C# pattern holding <c>\g&lt;name&gt;</c>: declarations, casts,
/// <c>new</c>, parameters, with recursive type_name/type_args/tuple groups), each driven over the fixture's C# lines the way a
/// tokenizer drives lastIndex (every match from successive positions), through the interpreter, <c>Compile()</c> and
/// <c>[GeneratedPattern]</c>; and the whole set as one <see cref="OnigurumaScanner"/>. Setup cross-checks every engine's
/// checksum against the interpreter. <c>GREGSEX_CALL_CORPUS</c> names a source file whose first 20,000 lines replace the
/// fixture's lines (TextMateSharp's <c>bigfile.cs</c>, say).
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public class CompiledOnigurumaCallBenchmarks
{
    public static readonly string[] Engines = ["interpreted", "compiled", "codegen"];

    private static readonly Lazy<(List<(string Rule, string Pattern)> Patterns, string[] Lines)> Fixture = new(Load);

    [ParamsSource(nameof(Scenarios))]
    public OnigurumaCallScenario Scenario { get; set; } = null!;

    public static IEnumerable<OnigurumaCallScenario> Scenarios()
    {
        foreach (var engine in Engines)
        {
            yield return new OnigurumaCallScenario("scanner-all", engine);
        }

        foreach (var (rule, _) in Fixture.Value.Patterns)
        {
            foreach (var engine in Engines)
            {
                yield return new OnigurumaCallScenario(Short(rule), engine);
            }
        }
    }

    private string[] _lines = null!;
    private OnigurumaWorkspace? _workspace;
    private OnigurumaScanner? _scanner;
    private RegexCapture[] _groups = null!;

    [GlobalSetup]
    public void Setup()
    {
        _lines = Fixture.Value.Lines;
        if (Scenario.Name == "scanner-all")
        {
            var patterns = Fixture.Value.Patterns.Select(p => Create(Scenario.Engine, p.Rule)).ToList();
            _scanner = new OnigurumaScanner(patterns);
            var oracle = new OnigurumaScanner(Fixture.Value.Patterns.Select(p => Create("interpreted", p.Rule)).ToList());
            long expected = Scan(oracle), actual = Run();
            if (expected != actual)
            {
                throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with the interpreter's {expected}.");
            }

            return;
        }

        var rule = Fixture.Value.Patterns.First(p => Short(p.Rule) == Scenario.Name).Rule;
        var pattern = Create(Scenario.Engine, rule);
        var expectedBackend = Scenario.Engine == "interpreted" ? RegexExecutionBackend.Interpreted : RegexExecutionBackend.Compiled;
        if (pattern.ExecutionBackend != expectedBackend)
        {
            throw new InvalidOperationException($"{Scenario}: backend is {pattern.ExecutionBackend}.");
        }

        _workspace = pattern.CreateWorkspace();
        _groups = new RegexCapture[pattern.GroupCount];
        var reference = Create("interpreted", rule);
        long expectedSum = Run(reference.CreateWorkspace(), new RegexCapture[reference.GroupCount]), actualSum = Run();
        if (expectedSum != actualSum)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actualSum} disagrees with the interpreter's {expectedSum}.");
        }
    }

    [Benchmark]
    public long Run() => _scanner is { } scanner ? Scan(scanner) : Run(_workspace!, _groups);

    private long Run(OnigurumaWorkspace workspace, RegexCapture[] groups)
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && workspace.FindCaptures(line, p, groups, out var range) == RegexSearchStatus.Match)
            {
                sum += 1 + range.Index + range.Length;
                p = range.Index + Math.Max(1, range.Length);
            }
        }

        return sum;
    }

    // A tokenizer's loop: the first pattern to match from the position, then on from the match's end.
    private long Scan(OnigurumaScanner scanner)
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && scanner.FindNextMatch(line, p, out var index))
            {
                var whole = scanner.Captures[0];
                sum += 1 + index + whole.Index + whole.Length;
                p = whole.Index + Math.Max(1, whole.Length);
            }
        }

        return sum;
    }

    private static string Short(string rule) => rule.Replace("repository.", "", StringComparison.Ordinal).Replace(".patterns", "", StringComparison.Ordinal);

    private static OnigurumaPattern Create(string engine, string rule)
    {
        if (engine == "codegen")
        {
            return GeneratedOnigurumaCallShapes.Map[rule]();
        }

        var pattern = OnigurumaPattern.Parse(Fixture.Value.Patterns.First(p => p.Rule == rule).Pattern, RegexProfiles.OnigurumaTextMate);
        return engine == "compiled" ? pattern.Compile() : pattern;
    }

    private static (List<(string, string)>, string[]) Load()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "GregsEx.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(dir ?? throw new InvalidOperationException("Run from the repository."),
            "test", "corpora", "oniguruma", "csharp-subexp-calls.json")))!;
        var patterns = fixture["patterns"]!.AsArray().Select(p => (p!["rule"]!.GetValue<string>(), p["pattern"]!.GetValue<string>())).ToList();
        var corpus = Environment.GetEnvironmentVariable("GREGSEX_CALL_CORPUS");
        var lines = string.IsNullOrEmpty(corpus)
            ? fixture["subjects"]!.AsArray().Select(s => s!.GetValue<string>()).ToArray()
            : File.ReadLines(corpus).Take(20_000).ToArray();
        return (patterns, lines);
    }
}
