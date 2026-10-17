using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;

namespace GregsEx.Benchmarks.Compiled;

/// <summary>A pattern shape for the compiled-backend comparison. Groups > 1 measures the capture-publishing path.</summary>
public sealed record ShapeScenario(string Name, string Pattern, string Engine)
{
    public override string ToString() => $"{Name}:{Engine}";
}

/// <summary>
/// End-to-end (through RegexWorkspace) comparison of the compiled RegularCore backend against the interpreted and Auto-accelerated
/// GregsEx paths, System.Text.RegularExpressions (interpreted/compiled). The shapes are the ones that have no hand-written
/// accelerated path, i.e. where the compiled backend can apply. Setup cross-checks every engine against BCL-interpreted.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public class CompiledShapeBenchmarks
{
    public static readonly (string Name, string Pattern)[] Shapes =
    [
        ("alt-captures", @"(the|and|of) ([a-z]+)"),
        ("loop-then-alt", @"[a-z]+(ing|ed)"),
        ("log-level", @"(ERROR|WARN) ([a-z]+)"),
        ("alt-prefix-loop", @"(wi|ri)n[a-z]*"),
        ("wildcard-span", @"t[a-z]*e"),
        ("alt-star-tail", @"(a|b)*c"),
        ("kv-captures", @"([a-z]+)=([a-z0-9]+)"),
    ];

    public static readonly string[] Engines =
    [
        "gregsex-core", "gregsex-core-auto", "gregsex-core-compiled", "gregsex-core-codegen", "bcl-interpreted", "bcl-compiled",
    ];

    [ParamsSource(nameof(Scenarios))]
    public ShapeScenario Scenario { get; set; } = null!;

    public static IEnumerable<ShapeScenario> Scenarios()
    {
        foreach (var (name, pattern) in Shapes)
        {
            foreach (var engine in Engines)
            {
                if (engine != "gregsex-core-compiled" || GregsEx.Benchmarks.Acceleration.Corpus.CoreEngine.TryCompile(pattern, out _))
                {
                    yield return new ShapeScenario(name, pattern, engine);
                }
            }
        }
    }

    internal static string BuildText()
    {
        var words = new[] { "the", "of", "and", "to", "in", "is", "that", "for", "running", "thing", "morning", "going", "making", "river", "stone", "garden", "window", "ERROR", "WARN" };
        var rnd = new Random(7);
        var sb = new StringBuilder();
        while (sb.Length < 65536)
        {
            sb.Append(words[rnd.Next(words.Length)]);
            sb.Append(rnd.Next(12) == 0 ? "=x1 " : rnd.Next(30) == 0 ? " 2024-05-17 ERROR " : " ");
        }
        return sb.ToString(0, 65536);
    }

    private Func<long> _run = null!;
    private IDisposable? _owner;

    [GlobalSetup]
    public void Setup()
    {
        var text = BuildText();
        var (run, owner) = Create(Scenario.Engine, Scenario.Pattern, text);
        _run = run;
        _owner = owner;
        var expected = Create("bcl-interpreted", Scenario.Pattern, text).Run();
        var actual = run();
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with BCL {expected}.");
        }
    }

    [Benchmark]
    public long Run() => _run();

    [GlobalCleanup]
    public void Cleanup() => _owner?.Dispose();

    private static (Func<long> Run, IDisposable? Owner) Create(string engine, string pattern, string text)
    {
        if (engine.StartsWith("bcl-", StringComparison.Ordinal))
        {
            var re = new Regex(pattern, engine == "bcl-compiled" ? RegexOptions.Compiled : RegexOptions.None, TimeSpan.FromSeconds(20));
            return (() =>
            {
                long sum = 0;
                foreach (var m in re.EnumerateMatches(text))
                {
                    sum += 1 + m.Index + m.Length;
                }

                return sum;
            }, null);
        }
        var mode = engine is "gregsex-core" or "gregsex-core-codegen" ? RegexExecutionMode.Interpreted : RegexExecutionMode.Auto;
        var limits = new RegexMatchLimits(20_000_000_000L, 1L << 30);
        var parsed = RegexPattern.Parse(pattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, mode, defaultMatchLimits: limits));
        if (engine == "gregsex-core-compiled")
        {
            parsed = parsed.Compile();
        }

        if (engine == "gregsex-core-codegen")
        {
            parsed = GeneratedShapes.Map[pattern]();
        }

        var ws = parsed.CreateWorkspace(new(parsed.CaptureCapacity, allowGrowth: true));
        return (() =>
        {
            long sum = 0;
            for (var at = 0; at <= text.Length;)
            {
                var status = ws.Find(text, at, out var r);
                if (status != RegexSearchStatus.Match)
                {
                    break;
                }

                sum += 1 + r.Index + r.Length;
                at = r.Length == 0 ? r.Index + 1 : r.Index + r.Length;
            }
            return sum;
        }, ws);
    }
}

internal sealed class ShapeConfig : ManualConfig
{
    public ShapeConfig() => SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(48);
}
