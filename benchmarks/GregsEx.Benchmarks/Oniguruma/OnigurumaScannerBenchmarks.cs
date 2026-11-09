using BenchmarkDotNet.Attributes;
using GregsEx;
using GregsEx.Benchmarks.Compiled;

namespace GregsEx.Benchmarks.Oniguruma;

/// <summary>
/// <see cref="OnigurumaScanner"/> as a tokenizer drives it, from match end to match end: every pattern of one grammar
/// of the Oniguruma corpus as one scanner, interpreted, over the grammar's sample lines as strings (whose results the
/// scanner keeps per string), as arrays (which it searches afresh each call), and joined into one long line.
/// Setup checks each walk against searching every pattern to the end of the line from every position it visits.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class OnigurumaScannerBenchmarks
{
    // The corpus holds grammar patterns beyond the default compile limits.
    private static readonly OnigurumaCompileOptions Options = OnigurumaCompileOptions.TextMate;

    private static readonly Dictionary<string, string> FirstLines = new()
    {
        ["shellscript"] = "#!/bin/bash",
        ["typescript"] = "// Using 'typeof' to infer types",
        ["csharp"] = "using KCTest.Infrastructure.Database;",
        ["html"] = "<!DOCTYPE html>",
    };

    [Params("shellscript", "typescript", "csharp", "html")]
    public string Grammar { get; set; } = "shellscript";

    private OnigurumaScanner _scanner = null!;
    private string[] _lines = null!;
    private char[][] _arrays = null!;
    // Two instances of the long line, searched in turn: a scanner keeps its results for the string it searched last.
    private string[] _long = null!;
    private int _turn;

    [GlobalSetup]
    public void Setup()
    {
        var (sources, lines) = CompiledOnigurumaBenchmarks.LoadCorpus(FirstLines[Grammar]);
        var patterns = new List<OnigurumaPattern>();
        foreach (var source in sources)
        {
            // The corpus also records patterns native Oniguruma rejects.
            if (OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out _, Options))
            {
                patterns.Add(pattern!);
            }
        }

        _scanner = new OnigurumaScanner(patterns);
        _lines = [.. lines.Select(l => l + "\n")];
        _arrays = [.. _lines.Select(l => l.ToCharArray())];
        var joined = string.Concat(Enumerable.Repeat(string.Join(" ", lines), 16)) + "\n";
        _long = [joined, new string(joined.AsSpan())];

        var workspaces = patterns.Select(p => p.CreateWorkspace()).ToArray();
        long expected = 0, expectedLong = 0;
        foreach (var line in _lines)
        {
            expected += Reference(workspaces, line);
        }

        expectedLong = Reference(workspaces, joined);
        Check(expected, Strings(), nameof(Strings));
        Check(expected, Arrays(), nameof(Arrays));
        Check(expectedLong, LongLine(), nameof(LongLine));
        Check(expectedLong, LongLine(), nameof(LongLine));
    }

    private void Check(long expected, long actual, string walk)
    {
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Grammar} {walk}: checksum {actual} disagrees with searching every pattern, {expected}.");
        }
    }

    [Benchmark]
    public long Strings()
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            sum += Walk(line.AsMemory());
        }

        return sum;
    }

    [Benchmark]
    public long Arrays()
    {
        long sum = 0;
        foreach (var line in _arrays)
        {
            sum += Walk(line);
        }

        return sum;
    }

    [Benchmark]
    public long LongLine() => Walk(_long[_turn ^= 1].AsMemory());

    // A tokenizer's loop: the first pattern to match from the position, then on from the match's end.
    private long Walk(ReadOnlyMemory<char> line)
    {
        long sum = 0;
        var p = 0;
        while (p <= line.Length && _scanner.FindNextMatch(line, p, out var index))
        {
            var whole = _scanner.Captures[0];
            sum += 1 + index + whole.Index + whole.Length;
            p = whole.Index + Math.Max(1, whole.Length);
        }

        return sum;
    }

    // The same walk, selecting among every pattern's own search to the end of the line.
    private static long Reference(OnigurumaWorkspace[] workspaces, string line)
    {
        long sum = 0;
        var p = 0;
        while (p <= line.Length)
        {
            var index = -1;
            var best = new RegexMatchRange(int.MaxValue, 0);
            for (var i = 0; i < workspaces.Length; i++)
            {
                if (workspaces[i].Find(line, p, out var match) == RegexSearchStatus.Match && match.Index < best.Index)
                {
                    index = i;
                    best = match;
                }
            }

            if (index < 0)
            {
                break;
            }

            sum += 1 + index + best.Index + best.Length;
            p = best.Index + Math.Max(1, best.Length);
        }

        return sum;
    }
}
