using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Compiled;

public sealed record EcmaScenario(string Name, string Pattern, string Flags, string Engine)
{
    public override string ToString() => $"{Name}:{Engine}";
}

/// <summary>
/// ECMAScript flavor: compiled backend against the ECMAScript interpreter (with and without its accelerator), BCL
/// (RegexOptions.ECMAScript, interpreted and compiled). Setup cross-checks every engine against BCL-interpreted.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public class CompiledEcmaBenchmarks
{
    public static readonly (string Name, string Pattern, string Flags)[] Shapes =
    [
        ("alt-captures", @"(the|and|of) ([a-z]+)", ""),
        ("loop-then-alt", @"[a-z]+(ing|ed)", ""),
        ("kv-captures", @"([a-z]+)=([a-z0-9]+)", ""),
        ("log-level", @"(ERROR|WARN) ([a-z]+)", ""),
        ("alt-prefix-loop", @"(wi|ri)n[a-z]*", ""),
        ("word-ing", @"\b[a-z]+ing\b", ""),
        ("date", @"\d{4}-\d{2}-\d{2}", ""),
        ("ci-alt", @"error|warn", "i"),
        ("ci-captures", @"(the|and|of) ([a-z]+)", "i"),
    ];

    public static readonly string[] Engines =
    [
        "gregsex-ecma", "gregsex-ecma-auto", "gregsex-ecma-compiled", "bcl-interpreted", "bcl-compiled",
    ];

    [ParamsSource(nameof(Scenarios))]
    public EcmaScenario Scenario { get; set; } = null!;

    public static IEnumerable<EcmaScenario> Scenarios()
    {
        foreach (var (name, pattern, flags) in Shapes)
        {
            foreach (var engine in Engines)
            {
                yield return new EcmaScenario(name, pattern, flags, engine);
            }
        }
    }

    private Func<long> _run = null!;
    private IDisposable? _owner;

    [GlobalSetup]
    public void Setup()
    {
        var text = CompiledShapeBenchmarks.BuildText();
        var (run, owner) = Create(Scenario.Engine, Scenario.Pattern, Scenario.Flags, text);
        _run = run;
        _owner = owner;
        var expected = Create("bcl-interpreted", Scenario.Pattern, Scenario.Flags, text).Run();
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

    private static (Func<long> Run, IDisposable? Owner) Create(string engine, string pattern, string flags, string text)
    {
        var ignoreCase = flags.Contains('i');
        if (engine.StartsWith("bcl-", StringComparison.Ordinal))
        {
            var options = RegexOptions.ECMAScript | (ignoreCase ? RegexOptions.IgnoreCase : 0) | (engine == "bcl-compiled" ? RegexOptions.Compiled : 0);
            var re = new Regex(pattern, options, TimeSpan.FromSeconds(20));
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
        var mode = engine == "gregsex-ecma" ? RegexExecutionMode.Interpreted : RegexExecutionMode.Auto;
        var limits = new RegexMatchLimits(20_000_000_000L, 1L << 30);
        var profile = new EcmaScriptProfile(ignoreCase ? EcmaScriptFlags.IgnoreCase : EcmaScriptFlags.None);
        var parsed = RegexPattern.Parse(pattern, profile, new RegexCompileOptions(RegexInputKind.Utf16, mode, defaultMatchLimits: limits));
        if (engine == "gregsex-ecma-compiled")
        {
            parsed = parsed.Compile();
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
