using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Core;

[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(GregsEx.Benchmarks.Core.CoreTimingConfig))]
public class CoreLifecycleBenchmarks
{
    private const string Source = "(a+)(b)?";
    private readonly RegularCoreProfile _profile = new();
    private RegexPattern _pattern = null!;
    private readonly RegexWorkspaceOptions _empty = new(default);

    [GlobalSetup]
    public void Setup()
    {
        _pattern = RegexPattern.Parse(Source, _profile);
        using var prepared = _pattern.CreateWorkspace();
        Console.WriteLine($"CORE-LIFECYCLE plan={_pattern.RetainedBytes} reserved={prepared.ReservedBytes} capacity={prepared.Capacity}");
        if (prepared.IsMatch("aa") != RegexSearchStatus.Match)
        {
            throw new InvalidOperationException();
        }
    }
    // Parsing a new owned plan, with static/JIT setup already warmed by BDN.
    [Benchmark]
    public long Parse() => RegexPattern.Parse(Source, _profile).RetainedBytes;
    [Benchmark]
    public long CreateWorkspace()
    {
        using var workspace = _pattern.CreateWorkspace();
        return workspace.ReservedBytes;
    }
    // Explicit composite: empty wrapper creation + Reserve growth + disposal.
    [Benchmark]
    public long CreateEmptyAndReserve()
    {
        using var workspace = _pattern.CreateWorkspace(_empty);
        workspace.Reserve(_pattern.CaptureCapacity);
        return workspace.ReservedBytes;
    }
    [Benchmark]
    public bool ConvenienceIsMatch() => _pattern.IsMatch("aa");
}
