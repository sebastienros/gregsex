using System.IO.Compression;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Compiled;

public sealed record OnigurumaScenario(string Name, string Engine)
{
    public override string ToString() => $"{Name}:{Engine}";
}

/// <summary>
/// Oniguruma flavor through the three execution tiers (interpreter, <c>Compile()</c>, <c>[GeneratedPattern]</c>): TextMate C#
/// grammar patterns searched for every match per line the way a tokenizer drives lastIndex over this repository's C# sources,
/// and a whole-grammar <see cref="OnigurumaScanner"/> (every pattern of the shellscript grammar from the Oniguruma corpus) driven
/// over the corpus's sample lines. Setup cross-checks every engine's checksum against the interpreter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public partial class CompiledOnigurumaBenchmarks
{
    public static readonly (string Name, string Pattern)[] Shapes =
    [
        ("tm-element-access", @"(?:(?:(\?)\s*)?(\.)\s*|(->)\s*)?(?:(@?[_[:alpha:]][_[:alnum:]]*)\s*)?(?:(\?)\s*)?(?=\[)"),
        ("tm-lookahead-bracket", @"(?=\[)"),
        ("tm-member-ident", @"(@?[_[:alpha:]][_[:alnum:]]*)(?=\s*(?:(?:\?\s*)?\.|->)\s*@?[_[:alpha:]][_[:alnum:]]*)"),
        ("tm-comment", @"(^\s+)?(//).*$"),
        ("tm-keywords", @"\b(bool|s?byte|u?short|n?u?int|u?long|float|double|decimal|char|string|object|void|dynamic)\b"),
        ("tm-identifier", @"@?[_[:alpha:]][_[:alnum:]]*"),
        ("keyword-lookbehind", @"(?<=\s|^)(?:var|int|string|void)(?=\s)"),
        ("ident-not-prefixed", @"\b(?!_)[a-z]+(?=\()"),
        ("sticky-token", @"\G\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])"),
        ("shell-alias", @"[\t ]*+(alias)[\t ]*+((?:((?<!\w)-\w+)\b[\t ]*+)*)[\t ]*+((?<!\w)[-0-9A-Z_a-z]+(?!\w))(?:(\[)((?:(?:\$?(?<!\w)[-0-9A-Z_a-z]+(?!\w)|@)|\*)|(-?\d+))(]))?(?:(?:(=)|(\+=))|(-=))"),
    ];

    public static readonly string[] Engines = ["interpreted", "compiled", "codegen"];

    // The corpus holds grammar patterns beyond the default compile limits (TextMateSharp's adapter raises them the same way).
    private static readonly OnigurumaCompileOptions CorpusOptions = new(new RegexCompileLimits(maxPatternUnits: 1 << 20,
        maxNestingDepth: 4096, maxCaptureGroups: 32767));

    [ParamsSource(nameof(Scenarios))]
    public OnigurumaScenario Scenario { get; set; } = null!;

    public static IEnumerable<OnigurumaScenario> Scenarios()
    {
        foreach (var (name, _) in Shapes)
        {
            foreach (var engine in Engines)
            {
                yield return new OnigurumaScenario(name, engine);
            }
        }

        foreach (var engine in new[] { "interpreted", "compiled" })
        {
            yield return new OnigurumaScenario("scanner-shellscript", engine);
        }
    }

    private string[] _lines = null!;
    private OnigurumaWorkspace? _workspace;
    private OnigurumaScanner? _scanner;
    private RegexCapture[] _groups = null!;
    private bool _sticky;

    [GlobalSetup]
    public void Setup()
    {
        if (Scenario.Name == "scanner-shellscript")
        {
            SetupScanner();
            return;
        }

        _lines = EcmaScript.TextMateLineSearch.LoadLines();
        var pattern = Create(Scenario.Engine, Scenario.Name);
        _sticky = Scenario.Name == "sticky-token";
        _workspace = pattern.CreateWorkspace();
        _groups = new RegexCapture[pattern.GroupCount];
        var expectedBackend = Scenario.Engine == "interpreted" ? RegexExecutionBackend.Interpreted : RegexExecutionBackend.Compiled;
        if (pattern.ExecutionBackend != expectedBackend)
        {
            throw new InvalidOperationException($"{Scenario}: backend is {pattern.ExecutionBackend}.");
        }

        var oracle = Create("interpreted", Scenario.Name);
        long expected = Run(oracle.CreateWorkspace(), new RegexCapture[oracle.GroupCount]), actual = Run();
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with the interpreter's {expected}.");
        }
    }

    private void SetupScanner()
    {
        var (patterns, lines) = LoadCorpus("#!/bin/bash");
        _lines = lines;
        // The corpus also records patterns native Oniguruma rejects; the scanner takes the valid ones.
        patterns = patterns.Where(p => OnigurumaPattern.TryParse(p, RegexProfiles.OnigurumaTextMate, out _, out _, CorpusOptions)).ToList();
        var parsed = patterns.Select(p => OnigurumaPattern.Parse(p, RegexProfiles.OnigurumaTextMate, CorpusOptions)).ToList();
        if (Scenario.Engine == "compiled")
        {
            var compiled = 0;
            for (var i = 0; i < parsed.Count; i++)
            {
                try
                {
                    parsed[i] = parsed[i].Compile();
                    compiled++;
                }
                catch (NotSupportedException)
                {
                }
            }

            Console.WriteLine($"scanner-shellscript: {compiled}/{parsed.Count} patterns compiled.");
        }

        _scanner = new OnigurumaScanner(parsed);
        var oracle = new OnigurumaScanner(patterns.Select(p => OnigurumaPattern.Parse(p, RegexProfiles.OnigurumaTextMate, CorpusOptions)).ToList());
        long expected = Scan(oracle), actual = Run();
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with the interpreter's {expected}.");
        }
    }

    [Benchmark]
    public long Run() => _scanner is { } scanner ? Scan(scanner) : Run(_workspace!, _groups);

    private long Run(OnigurumaWorkspace workspace, RegexCapture[] groups)
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && workspace.FindCaptures(line, p, groups, out var range) == RegexSearchStatus.Match)
            {
                sum += 1 + range.Index + range.Length;
                p = range.Index + Math.Max(1, range.Length);
                if (_sticky && range.Length == 0)
                {
                    break;
                }
            }
        }

        return sum;
    }

    // A tokenizer's loop: the first pattern to match from the position, then on from the match's end.
    private long Scan(OnigurumaScanner scanner)
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && scanner.FindNextMatch(line, p, out var index))
            {
                var whole = scanner.Captures[0];
                sum += 1 + index + whole.Index + whole.Length;
                p = whole.Index + Math.Max(1, whole.Length);
            }
        }

        return sum;
    }

    private static OnigurumaPattern Create(string engine, string name)
    {
        if (engine == "codegen")
        {
            return GeneratedOnigurumaShapes.Map[name]();
        }

        var pattern = OnigurumaPattern.Parse(Shapes.First(s => s.Name == name).Pattern, RegexProfiles.OnigurumaTextMate);
        return engine == "compiled" ? pattern.Compile() : pattern;
    }

    // The distinct patterns and sample lines of one grammar of the Oniguruma corpus, identified by its first sample line.
    private static (List<string> Patterns, string[] Lines) LoadCorpus(string firstLine)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "GregsEx.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        using var file = File.OpenRead(Path.Combine(dir ?? throw new InvalidOperationException("Run from the repository."), "test", "corpora", "oniguruma", "grammar-cases.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        var cases = JsonNode.Parse(gz)!.AsArray();
        var patterns = new List<string>();
        var lines = new List<string>();
        foreach (var c in cases)
        {
            var subjects = c!["subjects"]!.AsArray();
            if (subjects.Count == 0 || subjects[0] is not JsonValue first || first.GetValue<string>() != firstLine)
            {
                continue;
            }

            patterns.Add(c["pattern"]!.GetValue<string>());
            if (lines.Count == 0)
            {
                lines.AddRange(subjects.Select(s => s is JsonValue v ? v.GetValue<string>() : ""));
            }
        }

        return (patterns, lines.ToArray());
    }
}

// The shapes compiled by the GregsEx source generator at build time (the "codegen" engine).
internal static partial class GeneratedOnigurumaShapes
{
    [GeneratedPattern(@"(?:(?:(\?)\s*)?(\.)\s*|(->)\s*)?(?:(@?[_[:alpha:]][_[:alnum:]]*)\s*)?(?:(\?)\s*)?(?=\[)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ElementAccess();
    [GeneratedPattern(@"(?=\[)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern LookaheadBracket();
    [GeneratedPattern(@"(@?[_[:alpha:]][_[:alnum:]]*)(?=\s*(?:(?:\?\s*)?\.|->)\s*@?[_[:alpha:]][_[:alnum:]]*)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern MemberIdent();
    [GeneratedPattern(@"(^\s+)?(//).*$", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern Comment();
    [GeneratedPattern(@"\b(bool|s?byte|u?short|n?u?int|u?long|float|double|decimal|char|string|object|void|dynamic)\b", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern Keywords();
    [GeneratedPattern(@"@?[_[:alpha:]][_[:alnum:]]*", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern Identifier();
    [GeneratedPattern(@"(?<=\s|^)(?:var|int|string|void)(?=\s)", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern KeywordLookbehind();
    [GeneratedPattern(@"\b(?!_)[a-z]+(?=\()", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern IdentNotPrefixed();
    [GeneratedPattern(@"\G\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern StickyToken();
    [GeneratedPattern(@"[\t ]*+(alias)[\t ]*+((?:((?<!\w)-\w+)\b[\t ]*+)*)[\t ]*+((?<!\w)[-0-9A-Z_a-z]+(?!\w))(?:(\[)((?:(?:\$?(?<!\w)[-0-9A-Z_a-z]+(?!\w)|@)|\*)|(-?\d+))(]))?(?:(?:(=)|(\+=))|(-=))", OnigurumaOptions.CaptureGroup)]
    private static partial OnigurumaPattern ShellAlias();

    internal static readonly Dictionary<string, Func<OnigurumaPattern>> Map = new()
    {
        ["tm-element-access"] = ElementAccess,
        ["tm-lookahead-bracket"] = LookaheadBracket,
        ["tm-member-ident"] = MemberIdent,
        ["tm-comment"] = Comment,
        ["tm-keywords"] = Keywords,
        ["tm-identifier"] = Identifier,
        ["keyword-lookbehind"] = KeywordLookbehind,
        ["ident-not-prefixed"] = IdentNotPrefixed,
        ["sticky-token"] = StickyToken,
        ["shell-alias"] = ShellAlias,
    };
}
