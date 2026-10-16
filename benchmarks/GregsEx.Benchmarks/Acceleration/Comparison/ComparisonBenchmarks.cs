using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Reports;
using GregsEx.Benchmarks.Acceleration.Corpus;

namespace GregsEx.Benchmarks.Acceleration.Comparison;

/// <summary>One curated scenario: a pinned corpus case (docs/benchmarks/release-manifest.json), one operation and one engine.</summary>
public sealed record Scenario(string Case, string Operation, string Engine)
{
    public override string ToString() => $"{Case}:{Operation}:{Engine}";
}

/// <summary>Curated subset of the release manifest compared across GregsEx, System.Text.RegularExpressions and Jint.</summary>
public static class CuratedCases
{
    /// <summary>Operation measured per case; categories cover literal, alternation, anchored, captures, Unicode/classes, backtracking-prone and large-input.</summary>
    public static readonly (string Case, string Operation)[] Cases =
    [
        ("lit-rare-64k", "find"),
        ("lit-miss-64k", "is_match"),
        ("lit-dense-64k", "count"),
        ("alt-names-64k", "count"),
        ("anc-end-64k", "is_match"),
        ("anc-full-4k", "is_match"),
        ("cap-kv-64k", "captures"),
        ("cap-date-log-64k", "captures"),
        ("cls-ing-64k", "count"),
        ("uni-letters-64k", "count"),
        ("bt-overlap-alt", "is_match"),
        ("bt-dotstar-4k", "is_match"),
        ("lrg-lit-1m", "find"),
        ("lrg-alt-1m", "count"),
    ];

    public static readonly string[] Engines =
    [
        "gregsex-core", "gregsex-core-auto", "gregsex-core-compiled", "gregsex-ecma", "gregsex-pcre2", "gregsex-posix",
        "bcl-interpreted", "bcl-compiled", "bcl-sourcegen", "bcl-ecma-interpreted", "bcl-ecma-compiled",
#if JINT_QUICKJS
        "jint-quickjs",
#endif
    ];

    private static readonly Lazy<Dictionary<string, CorpusCase>> Manifest = new(() =>
        CorpusGenerator.LoadCases(FindManifest()).ToDictionary(c => c.Id));

    public static CorpusCase Get(string id) => Manifest.Value[id];

    public static IEnumerable<Scenario> Scenarios()
    {
        foreach (var (id, operation) in Cases)
        {
            var c = Get(id);
            foreach (var engine in Engines)
            {
                if (CaseEngines.IsApplicable(engine, c))
                {
                    yield return new Scenario(id, operation, engine);
                }
            }
        }
    }

    private static string FindManifest()
    {
        if (Environment.GetEnvironmentVariable("GREGSEX_MANIFEST") is { Length: > 0 } explicitPath)
        {
            return explicitPath;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "benchmarks", "release-manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("docs/benchmarks/release-manifest.json not found above the benchmark binaries; set GREGSEX_MANIFEST to its path.");
    }
}

/// <summary>
/// Cross-engine comparison. Filter with e.g. <c>--filter "*ComparisonBenchmarks*lit-*"</c>. Setup cross-checks each engine's
/// checksum against System.Text.RegularExpressions (interpreted) and fails the scenario on disagreement.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ComparisonConfig))]
public class ComparisonBenchmarks
{
    [ParamsSource(nameof(Scenarios))]
    public Scenario Scenario { get; set; } = null!;

    public static IEnumerable<Scenario> Scenarios() => CuratedCases.Scenarios();

    private CaseEngine _engine = null!;
    private string _operation = "";

    [GlobalSetup]
    public void Setup()
    {
        var c = CuratedCases.Get(Scenario.Case);
        var text = c.Generate();
        _operation = Scenario.Operation;
        _engine = CaseEngines.Create(Scenario.Engine, c, text);
        var actual = _engine.Run(_operation);
        if (Scenario.Engine != "bcl-interpreted" && TryReference(c, text, out var expected) && expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with System.Text.RegularExpressions {expected}.");
        }
    }

    private bool TryReference(CorpusCase c, string text, out int expected)
    {
        try
        {
            using var reference = CaseEngines.Create("bcl-interpreted", c, text);
            expected = reference.Run(_operation);
            return true;
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            expected = 0;
            return false;
        }
    }

    [Benchmark]
    public int Run() => _engine.Run(_operation);

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();
}

// Scenario ids (case:operation:engine) must not be truncated in the summary table.
internal sealed class ComparisonConfig : ManualConfig
{
    public ComparisonConfig() => SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(64);
}
