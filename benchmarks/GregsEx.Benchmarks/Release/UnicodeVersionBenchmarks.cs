using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Release;

[MemoryDiagnoser]
[ShortRunJob]
public class UnicodeVersionBenchmarks
{
    [Params(UnicodeVersion.Unicode16_0, UnicodeVersion.Unicode17_0)]
    public UnicodeVersion Version { get; set; }

    private const string Input = "prefix ABCDE12345 suffix";
    private RegexWorkspace _ecma = null!;
    private RegexWorkspace _xpath = null!;
    private RegexWorkspace _xsd = null!;
    private readonly RegexCapture[] _captures = new RegexCapture[3];

    [GlobalSetup]
    public void Setup()
    {
        _ecma = RegexPattern.Parse("([a-z]+)([0-9]+)", new EcmaScriptProfile(EcmaScriptFlags.IgnoreCase, Version)).CreateWorkspace();
        _xpath = RegexPattern.Parse("([a-z]+)([0-9]+)", new XPathProfile(XPathFlags.IgnoreCase, Version)).CreateWorkspace();
        _xsd = RegexPattern.Parse("[a-zA-Z]+[0-9]+", new XsdProfile(Version)).CreateWorkspace();
        for (var i = 0; i < 64; i++)
        {
            if (_ecma.FindCaptures(Input, 0, _captures, out var ecmaRange) != RegexSearchStatus.Match ||
                ecmaRange != new RegexMatchRange(7, 10) ||
                _xpath.FindCaptures(Input, 0, _captures, out var xpathRange) != RegexSearchStatus.Match ||
                xpathRange != ecmaRange || _xsd.IsMatch("ABCDE12345") != RegexSearchStatus.Match)
            {
                throw new InvalidOperationException("The Unicode-version benchmark's semantic checks failed.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ecma.Dispose();
        _xpath.Dispose();
        _xsd.Dispose();
    }

    [Benchmark]
    public RegexSearchStatus EcmaCaptures() => _ecma.FindCaptures(Input, 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus XPathCaptures() => _xpath.FindCaptures(Input, 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus XsdMembership() => _xsd.IsMatch("ABCDE12345");
}
