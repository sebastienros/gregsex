using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(GregsEx.Benchmarks.Core.CoreTimingConfig))]
public class RegexBenchmarks
{
    private readonly Regex _regex = new("^[a-z]+$", RegexOptions.CultureInvariant);
    private RegexWorkspace _workspace = null!;

    [Params("greg", "greg123")]
    public string Input { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var pattern = RegexPattern.Parse("^[a-z]+$", new RegularCoreProfile());
        _workspace = pattern.CreateWorkspace(new(pattern.BooleanCapacity));
        var expected = Input == "greg";
        if (DotNetRegex() != expected || GregsExPrepared() != expected)
        {
            throw new InvalidOperationException();
        }

        for (var i = 0; i < 16_384; i++)
        { _ = DotNetRegex(); _ = GregsExPrepared(); }
    }

    // Equal Boolean queries only on these two ASCII inputs without terminal LF.
    // BCL interpreted/CultureInvariant; no flavor-wide equivalence is claimed.
    [Benchmark(Baseline = true)]
    public bool DotNetRegex() => _regex.IsMatch(Input);
    [Benchmark]
    public bool GregsExPrepared() => _workspace.IsMatch(Input) == RegexSearchStatus.Match;
    [GlobalCleanup]
    public void Cleanup() => _workspace.Dispose();
}
