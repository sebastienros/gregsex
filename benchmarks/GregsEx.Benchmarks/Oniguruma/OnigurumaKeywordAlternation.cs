using BenchmarkDotNet.Attributes;
using GregsEx;

namespace GregsEx.Benchmarks.Oniguruma;

// Searching a line for a keyword alternation, the shape of the large keyword rules in TextMate grammars (SQL, CSS,
// PowerShell): sorted words, so a word late in the list sits behind many branches that cannot start where it does.
// The words are generated from syllables, the line mixes keywords with identifiers and punctuation, and the benchmark
// searches it from every match end as a tokenizer does.
[MemoryDiagnoser]
[ShortRunJob]
public class OnigurumaKeywordAlternation
{
    private static readonly string[] Syllables = ["sel", "ect", "ins", "ert", "up", "da", "te", "fi", "le", "st", "at", "us", "off", "set", "ra", "no"];

    private OnigurumaWorkspace _workspace = null!;
    private string _line = null!;

    [Params(50, 400)]
    public int Words { get; set; }

    [Params(false, true)]
    public bool IgnoreCase { get; set; }

    [GlobalSetup]
    public void Prepare()
    {
        var words = new SortedSet<string>(StringComparer.Ordinal);
        for (var i = 0; words.Count < Words; i++)
        {
            words.Add(Syllables[i % 16] + Syllables[(i / 16) % 16] + Syllables[(i / 256) % 16]);
        }

        var pattern = (IgnoreCase ? @"(?i)" : "") + @"\b(" + string.Join("|", words) + @")\b";
        _workspace = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate, OnigurumaCompileOptions.TextMate).CreateWorkspace();
        var list = words.ToArray();
        var parts = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            parts.Add(list[(i * 7919) % list.Length]);
            parts.Add("ident_" + i + " = (" + i + ");");
        }

        _line = string.Join(" ", parts);
    }

    [Benchmark]
    public int SearchLine()
    {
        var count = 0;
        var pos = 0;
        while (pos <= _line.Length && _workspace.Find(_line, pos, out var match) == RegexSearchStatus.Match)
        {
            count++;
            pos = match.Index + Math.Max(match.Length, 1);
        }

        return count;
    }
}
