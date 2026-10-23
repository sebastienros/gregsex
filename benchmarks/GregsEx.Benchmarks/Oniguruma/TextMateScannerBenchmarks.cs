using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Oniguruma;

// Replays the scanner calls TextMateSharp makes tokenizing 40 docs-heavy fences per language
// (test/corpora/oniguruma/trace.json.gz: tm-grammars 1.32.22 patterns, many regexes searched from one position and
// the earliest match kept) through native Oniguruma 6.9.10 via Onigwrap 1.0.11 (what TextMateSharp 2.0.4 uses) and
// through GregsEx's OnigurumaScanner. One operation is the whole trace of a language: divide by the call and line
// counts printed in the setup. StringBacked lines let both keep each regex's last result per string; ArrayBacked
// lines (Douze passes lines without their newline, which TextMateSharp copies into an array) disable that cache.
[MemoryDiagnoser]
[ShortRunJob]
public class TextMateScannerBenchmarks
{
    private (int Scanner, int Line, int Offset)[] _calls = [];
    private string[] _lines = [];
    private ReadOnlyMemory<char>[] _arrays = [];
    private Onigwrap.OnigScanner[] _native = [];
    private OnigurumaScanner[] _managed = [];

    [Params("bash", "ts", "js", "html", "json", "yaml", "csharp")]
    public string Language { get; set; } = "";

    [Params(false, true)]
    public bool StringBacked { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var node = Read()[Language]!;
        var patterns = node["patterns"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();
        var scanners = node["scanners"]!.AsArray().Select(s => s!.AsArray().Select(i => patterns[i!.GetValue<int>()]).ToArray()).ToArray();
        _lines = node["lines"]!.AsArray().Select(l => l!.GetValue<string>()).ToArray();
        _arrays = _lines.Select(l => (ReadOnlyMemory<char>)l.ToCharArray()).ToArray();
        var flat = node["calls"]!.AsArray().Select(c => c!.GetValue<int>()).ToArray();
        _calls = Enumerable.Range(0, flat.Length / 3).Select(i => (flat[3 * i], flat[3 * i + 1], flat[3 * i + 2])).ToArray();
        _native = scanners.Select(s => new Onigwrap.OnigScanner(s)).ToArray();
        _managed = scanners.Select(s => new OnigurumaScanner(s, RegexProfiles.OnigurumaTextMate)).ToArray();
        Console.WriteLine($"// {Language}: {_calls.Length} calls over {_lines.Length} lines, {scanners.Length} scanners, {patterns.Length} patterns");
        if (Onigwrap() != GregsEx())
        {
            throw new InvalidOperationException("the engines disagree");
        }
    }

    [Benchmark(Baseline = true)]
    public int Onigwrap()
    {
        var checksum = 0;
        foreach (var (scanner, line, offset) in _calls)
        {
            var result = StringBacked ? _native[scanner].FindNextMatchSync(_lines[line], offset) : _native[scanner].FindNextMatchSync(_arrays[line], offset);
            if (result is not null)
            {
                var captures = result.GetCaptureIndices();
                checksum += result.GetIndex() + captures[0].Start + captures.Length;
            }
        }
        return checksum;
    }

    [Benchmark]
    public int GregsEx()
    {
        var checksum = 0;
        foreach (var (scanner, line, offset) in _calls)
        {
            var s = _managed[scanner];
            var found = StringBacked ? s.FindNextMatch(_lines[line], offset, out var index) : s.FindNextMatch(_arrays[line], offset, out index);
            if (found)
            {
                var captures = s.Captures;
                checksum += index + captures[0].Index + captures.Length;
            }
        }
        return checksum;
    }

    private static JsonObject Read([CallerFilePath] string source = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../../test/corpora/oniguruma/trace.json.gz"));
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonNode.Parse(gzip)!.AsObject();
    }
}
