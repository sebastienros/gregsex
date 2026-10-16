using BenchmarkDotNet.Attributes;
using XmlProbe;

namespace GregsEx.Benchmarks.Xml;

[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(XmlTimingConfig))]
public class XmlSharedBenchmarks
{
    private Fixture _fixture = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = new Fixture();
        _fixture.VerifyAndWarm();
    }

    [Benchmark]
    public int Utf16Matrix() => _fixture.CharMatrix();

    [Benchmark]
    public int Utf8Matrix() => _fixture.ByteMatrix();
}
