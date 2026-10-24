using BenchmarkDotNet.Attributes;
using GregsEx;

namespace GregsEx.Benchmarks.EcmaScript;

// TextMate C# grammar patterns converted from Oniguruma to ES2025 (as Shiki's oniguruma-to-es does), searched for every
// match per line the way a tokenizer drives lastIndex, against the same Oniguruma source pattern under GregsEx's
// Oniguruma flavor. Lines keep their trailing \n. The corpus is the file GREGSEX_TEXTMATE_CORPUS names (TextMateSharp's
// bigfile.cs for the published tables), else this repository's own C# sources.
[MemoryDiagnoser]
[ShortRunJob]
public class TextMateLineSearch
{
    private static readonly Dictionary<string, (string Es, string Onig)> Patterns = new()
    {
        ["element_access"] = (@"(?:(?:(\?)\p{space}*)?(\.)\p{space}*|(->)\p{space}*)?(?:(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)\p{space}*)?(?:(\?)\p{space}*)?(?=\[)",
            @"(?:(?:(\?)\s*)?(\.)\s*|(->)\s*)?(?:(@?[_[:alpha:]][_[:alnum:]]*)\s*)?(?:(\?)\s*)?(?=\[)"),
        ["lookahead_bracket"] = (@"(?=\[)", @"(?=\[)"),
        ["member_ident"] = (@"(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)(?=\p{space}*(?:(?:\?\p{space}*)?\.|->)\p{space}*@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)",
            @"(@?[_[:alpha:]][_[:alnum:]]*)(?=\s*(?:(?:\?\s*)?\.|->)\s*@?[_[:alpha:]][_[:alnum:]]*)"),
        ["identifier"] = (@"@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*", @"@?[_[:alpha:]][_[:alnum:]]*"),
        ["identifier_ascii"] = (@"@?[_a-zA-Z][_a-zA-Z0-9]*", @"@?[_a-zA-Z][_a-zA-Z0-9]*"),
        ["comment"] = (@"(^\p{space}+)?(//)[^\n]*(?=\n?$)", @"(^\s+)?(//).*$"),
        ["keywords"] = (@"\b(bool|s?byte|u?short|n?u?int|u?long|float|double|decimal|char|string|object|void|dynamic)\b",
            @"\b(bool|s?byte|u?short|n?u?int|u?long|float|double|decimal|char|string|object|void|dynamic)\b"),
    };

    private string[] _lines = null!;
    private RegexWorkspace _es = null!;
    private RegexCapture[] _esGroups = null!;
    private OnigurumaWorkspace _onig = null!;
    private RegexCapture[] _onigGroups = null!;

    [Params("element_access", "lookahead_bracket", "member_ident", "identifier", "identifier_ascii", "comment", "keywords")]
    public string Pattern { get; set; } = "";

    [GlobalSetup]
    public void Prepare()
    {
        _lines = LoadLines();
        var (es, onig) = Patterns[Pattern];
        EcmaScriptProfile.TryParseFlags("dgv", out var profile, out _);
        var pattern = RegexPattern.Parse(es, profile!);
        _es = pattern.CreateWorkspace(new RegexWorkspaceOptions(default, allowGrowth: true));
        _esGroups = new RegexCapture[pattern.GroupCount];
        var onigPattern = OnigurumaPattern.Parse(onig, RegexProfiles.OnigurumaTextMate);
        _onig = onigPattern.CreateWorkspace();
        _onigGroups = new RegexCapture[onigPattern.GroupCount];
        long a = EcmaScript(), b = Oniguruma();
        if (a != b)
        {
            throw new InvalidOperationException($"{Pattern}: ECMAScript found {a} matches, Oniguruma {b}.");
        }
    }

    [Benchmark]
    public long EcmaScript()
    {
        long n = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && _es.FindCaptures(line, p, _esGroups, out _) == RegexSearchStatus.Match)
            {
                n++;
                p = _esGroups[0].Index + Math.Max(1, _esGroups[0].Length);
            }
        }

        return n;
    }

    [Benchmark(Baseline = true)]
    public long Oniguruma()
    {
        long n = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && _onig.FindCaptures(line, p, _onigGroups, out _) == RegexSearchStatus.Match)
            {
                n++;
                p = _onigGroups[0].Index + Math.Max(1, _onigGroups[0].Length);
            }
        }

        return n;
    }

    private static string[] LoadLines()
    {
        var corpus = Environment.GetEnvironmentVariable("GREGSEX_TEXTMATE_CORPUS");
        IEnumerable<string> files;
        if (!string.IsNullOrEmpty(corpus))
        {
            files = [corpus];
        }
        else
        {
            var dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "GregsEx.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            files = Directory.EnumerateFiles(Path.Combine(dir ?? throw new InvalidOperationException("Set GREGSEX_TEXTMATE_CORPUS."), "src", "GregsEx"),
                "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
        }

        return files.SelectMany(File.ReadLines).Select(l => l + "\n").ToArray();
    }
}
