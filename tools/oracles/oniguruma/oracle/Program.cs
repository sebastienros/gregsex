// Native Oniguruma oracle for the Oniguruma flavor (see ../README.md). It calls the shim of ../native (built from
// github.com/kkos/oniguruma at a tag by build-oracle.sh): UTF-16LE, ONIG_SYNTAX_DEFAULT, ONIG_OPTION_CAPTURE_GROUP,
// patterns exactly as written.
//
//   corpus <corpus-dir>                writes grammar-cases.json.gz and LICENSE-tm-grammars.txt from the inputs
//   cases <input.json> <output.json>   records [{ "pattern", "subjects": [..], "starts": [..]? }]
//   differential                       compares GregsEx with native over every tm-grammars pattern and sample line
//
// Options: --oniguruma <tag> (the oracle under artifacts/oniguruma/<tag>), --inputs <dir> (fetch-inputs.sh output).
using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GregsEx;

var tag = new Option<string>("--oniguruma") { DefaultValueFactory = _ => "v6.9.10", Description = "Oniguruma tag whose oracle build-oracle.sh wrote under artifacts/oniguruma.", Recursive = true };
var inputs = new Option<string>("--inputs") { DefaultValueFactory = _ => "artifacts/oniguruma/inputs", Description = "fetch-inputs.sh output.", Recursive = true };
var cli = new RootCommand("Development-only native Oniguruma oracle. Run from repository root.") { tag, inputs };

var corpusDir = new Argument<string>("corpus-dir");
var corpus = new Command("corpus", "Writes grammar-cases.json.gz and LICENSE-tm-grammars.txt.") { corpusDir };
corpus.SetAction(r => Corpus(new Inputs(r.GetValue(inputs)!), r.GetValue(corpusDir)!));
cli.Add(corpus);

var input = new Argument<string>("input");
var output = new Argument<string>("output");
var cases = new Command("cases", "Records native outcomes of a cases file.") { input, output };
cases.SetAction(r =>
{
    Native.Load(r.GetValue(tag)!);
    WriteText(r.GetValue(output)!, Cases(JsonNode.Parse(ReadText(r.GetValue(input)!))!.AsArray()).ToJsonString());
    return 0;
});
cli.Add(cases);

var report = new Option<string>("--report") { DefaultValueFactory = _ => "artifacts/oniguruma/differential.json", Description = "Where to write counts and mismatches." };
var differential = new Command("differential", "Compares GregsEx with native Oniguruma over all tm-grammars patterns and sample lines.") { report };
differential.SetAction(r =>
{
    Native.Load(r.GetValue(tag)!);
    return Differential(new Inputs(r.GetValue(inputs)!), r.GetValue(tag)!, r.GetValue(report)!);
});
cli.Add(differential);

if (!File.Exists("GregsEx.slnx"))
{
    Console.Error.WriteLine("Run from repository root.");
    return 2;
}
return cli.Parse(args).Invoke();

// The seven languages of the retained grammar corpus, each with the grammars it embeds.
static int Corpus(Inputs inputs, string directory)
{
    const int linesPerLanguage = 6;
    var cases = new JsonArray();
    var used = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var language in new[] { "shellscript", "typescript", "javascript", "html", "json", "yaml", "csharp" })
    {
        var closure = inputs.Closure(language);
        used.UnionWith(closure);
        var subjects = new JsonArray(inputs.Lines(language).Take(linesPerLanguage).Select(l => (JsonNode)l).ToArray());
        foreach (var pattern in closure.SelectMany(inputs.Patterns).Distinct())
        {
            cases.Add(new JsonObject { ["pattern"] = pattern, ["subjects"] = subjects.DeepClone(), ["starts"] = new JsonArray(0, 7, 21) });
        }
    }
    WriteText(Path.Combine(directory, "grammar-cases.json.gz"), cases.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

    // tm-grammars' license, then the NOTICE sections of the grammars the corpus draws from.
    var notice = File.ReadAllText(Path.Combine(inputs.Grammars, "NOTICE"));
    var kept = Regex.Split(notice, @"\n(?==+\nFiles:)").Where(section => Regex.Match(section, "Files:(.*)") is { Success: true } files
        && used.Any(g => Regex.IsMatch(files.Groups[1].Value, $@"(?:^|[\s,]){Regex.Escape(g)}\.json(?:$|[\s,])")));
    File.WriteAllText(Path.Combine(directory, "LICENSE-tm-grammars.txt"), File.ReadAllText(Path.Combine(inputs.Grammars, "LICENSE"))
        + "\n\nNOTICE sections of the grammars these fixtures draw patterns from (" + string.Join(", ", used) + "):\n\n" + string.Join("\n", kept));
    Console.WriteLine($"{cases.Count} grammar cases from {string.Join(", ", used)}");
    return 0;
}

// Per case: null when the pattern does not compile, else per subject and start: null (no match), an error code, or
// the capture pairs (start, end; -1 when a group did not participate).
static JsonArray Cases(JsonArray cases)
{
    var result = new JsonArray();
    foreach (var item in cases)
    {
        using var regex = Native.Create(item!["pattern"]!.GetValue<string>(), out _);
        if (regex is null)
        {
            result.Add(null);
            continue;
        }

        var subjects = new JsonArray();
        foreach (var subject in item["subjects"]!.AsArray())
        {
            var text = Text(subject!);
            var starts = item["starts"] is JsonArray s ? s.Select(x => x!.GetValue<int>()).Where(x => x <= text.Length).ToArray()
                : Enumerable.Range(0, text.Length + 1).ToArray();
            var outcomes = new JsonArray();
            foreach (var start in starts)
            {
                var r = regex.Search(text, start, out var captures);
                outcomes.Add(new JsonArray(start, r == -1 ? null : r < 0 ? JsonValue.Create(r) : new JsonArray(captures.Select(c => (JsonNode)c).ToArray())));
            }
            subjects.Add(outcomes);
        }
        result.Add(subjects);
    }
    return result;
}

// Every distinct pattern of every grammar, searched from a dozen starts of every line of each sample whose language
// embeds the grammar, and from three starts of a mix of the first lines of all samples. Near the retry limit the
// engines' budgets differ (OG-005: MaxWork bounds a whole search, the retry limit each start, and they count different
// work): on a subject where native reports an error from some start, LimitExceeded from any start is expected, and so
// is a result where native reports the error. Both are counted, not reported as mismatches.
static int Differential(Inputs inputs, string tag, string reportPath)
{
    var clock = Stopwatch.StartNew();
    var work = new Dictionary<string, (HashSet<string> Lines, bool Mix)>(StringComparer.Ordinal);
    var languages = inputs.Samples().ToArray();
    foreach (var language in languages)
    {
        var lines = inputs.Lines(language).ToArray();
        foreach (var pattern in inputs.Closure(language).SelectMany(inputs.Patterns))
        {
            if (!work.TryGetValue(pattern, out var entry))
            {
                work[pattern] = entry = (new HashSet<string>(StringComparer.Ordinal), false);
            }
            entry.Lines.UnionWith(lines);
        }
    }
    foreach (var pattern in inputs.AllGrammars().SelectMany(inputs.Patterns))
    {
        work[pattern] = work.TryGetValue(pattern, out var entry) ? (entry.Lines, true) : (new HashSet<string>(StringComparer.Ordinal), true);
    }
    var mix = languages.SelectMany(l => inputs.Lines(l).Where(line => line.Trim().Length > 0).Take(4)).Distinct(StringComparer.Ordinal).ToArray();
    Console.WriteLine($"{work.Count} distinct patterns from {inputs.AllGrammars().Count()} grammars; {languages.Length} samples; mix of {mix.Length} lines");

    var options = new OnigurumaCompileOptions(new RegexCompileLimits(maxPatternUnits: 1 << 20, maxNestingDepth: 4096, maxCaptureGroups: 32767));
    long searches = 0, matches = 0, nativeErrors = 0, limitAllowed = 0, nativeLimitOnly = 0, rejectedByBoth = 0, done = 0;
    var mismatches = new ConcurrentBag<JsonObject>();
    Parallel.ForEach(work.OrderBy(w => w.Key, StringComparer.Ordinal), new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, item =>
    {
        var (source, (lines, inMix)) = (item.Key, item.Value);
        using var regex = Native.Create(source, out var error);
        var parsed = OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out var diagnostic, options);
        if (regex is null || !parsed)
        {
            if (regex is null && !parsed)
            {
                Interlocked.Increment(ref rejectedByBoth);
            }
            else
            {
                mismatches.Add(new JsonObject { ["pattern"] = source, ["native"] = regex is null ? error : "compiles", ["gregsex"] = parsed ? "compiles" : diagnostic.Message });
            }
            Interlocked.Increment(ref done);
            return;
        }

        var workspace = pattern!.CreateWorkspace();
        var groups = new RegexCapture[pattern.GroupCount];
        var subjects = lines.Select(l => (Line: l, Full: true)).Concat(inMix ? mix.Where(l => !lines.Contains(l)).Select(l => (Line: l, Full: false)) : []);
        long localSearches = 0, localMatches = 0, localErrors = 0, localAllowed = 0, localLimitOnly = 0;
        var outcomes = new List<(int Start, int Native, int[] Captures)>();
        foreach (var (line, full) in subjects)
        {
            var text = line + "\n";
            var starts = (full ? new[] { 0, 1, 2, 3, 5, 7, 11, 13, 21, 34, text.Length / 2, text.Length - 1 } : [0, 9, text.Length / 2]).Where(s => s <= text.Length).Distinct();
            outcomes.Clear();
            foreach (var start in starts)
            {
                var r = regex.Search(text, start, out var captures);
                outcomes.Add((start, r, captures));
            }

            var nearLimit = outcomes.Any(o => o.Native < -1);
            foreach (var (start, native, captures) in outcomes)
            {
                localSearches++;
                var status = workspace.FindCaptures(text, start, groups, out _);
                if (native < -1)
                {
                    localErrors++;
                    if (status != RegexSearchStatus.LimitExceeded)
                    {
                        localLimitOnly++;
                        continue;
                    }
                }

                if (nearLimit && status == RegexSearchStatus.LimitExceeded)
                {
                    localAllowed++;
                    continue;
                }

                var agrees = native switch
                {
                    -1 => status == RegexSearchStatus.NoMatch,
                    < -1 => status == RegexSearchStatus.LimitExceeded,
                    _ => status == RegexSearchStatus.Match && Same(captures, groups),
                };
                if (native >= 0)
                {
                    localMatches++;
                }

                if (!agrees)
                {
                    mismatches.Add(new JsonObject
                    {
                        ["pattern"] = source, ["subject"] = text, ["start"] = start,
                        ["native"] = native >= 0 ? new JsonArray(captures.Select(c => (JsonNode)c).ToArray()) : native,
                        ["gregsex"] = status == RegexSearchStatus.Match ? new JsonArray(Flatten(groups).Select(c => (JsonNode)c).ToArray()) : status.ToString(),
                    });
                }
            }
        }
        Interlocked.Add(ref searches, localSearches);
        Interlocked.Add(ref matches, localMatches);
        Interlocked.Add(ref nativeErrors, localErrors);
        Interlocked.Add(ref limitAllowed, localAllowed);
        Interlocked.Add(ref nativeLimitOnly, localLimitOnly);
        if (Interlocked.Increment(ref done) % 1000 == 0)
        {
            Console.WriteLine($"  {done}/{work.Count} patterns, {Interlocked.Read(ref searches):N0} searches, {mismatches.Count} mismatches, {clock.Elapsed:hh\\:mm\\:ss}");
        }
    });

    var sorted = mismatches.OrderBy(m => m["pattern"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(m => m.ToJsonString(), StringComparer.Ordinal).ToArray();
    var result = new JsonObject
    {
        ["oniguruma"] = tag, ["inputs"] = Path.GetFullPath(inputs.Root), ["patterns"] = work.Count, ["rejectedByBoth"] = rejectedByBoth,
        ["searches"] = searches, ["matches"] = matches, ["nativeErrors"] = nativeErrors, ["limitExceededNearRetryLimit"] = limitAllowed, ["resultWhereNativeHitRetryLimit"] = nativeLimitOnly,
        ["mismatches"] = sorted.Length, ["seconds"] = (int)clock.Elapsed.TotalSeconds,
        ["details"] = new JsonArray(sorted.Take(10000).Select(m => (JsonNode)m).ToArray()),
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    File.WriteAllText(reportPath, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{work.Count} patterns ({rejectedByBoth} rejected by both), {searches:N0} searches ({matches:N0} matches, {nativeErrors} native errors, "
        + $"{limitAllowed} LimitExceeded near the retry limit, {nativeLimitOnly} results where native hit it), {sorted.Length} mismatches in {clock.Elapsed:hh\\:mm\\:ss}; report: {reportPath}");
    return sorted.Length == 0 ? 0 : 1;
}

static bool Same(int[] native, RegexCapture[] groups) => native.AsSpan().SequenceEqual(Flatten(groups));

static int[] Flatten(RegexCapture[] groups)
    => groups.SelectMany(g => g.Participated ? new[] { g.Index, g.Index + g.Length } : [-1, -1]).ToArray();

// A subject is a JSON string, or { "units": [...] } for UTF-16 text JSON cannot carry (lone surrogates).
static string Text(JsonNode subject)
    => subject is JsonObject o ? new string(o["units"]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray()) : subject.GetValue<string>();

// .gz paths are gzip-compressed; a written gzip header carries no timestamp, so outputs are reproducible.
static string ReadText(string path)
{
    using var file = File.OpenRead(path);
    using var stream = path.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void WriteText(string path, string text)
{
    if (!path.EndsWith(".gz", StringComparison.Ordinal))
    {
        File.WriteAllText(path, text);
        return;
    }

    using (var file = File.Create(path))
    using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
    using (var writer = new StreamWriter(gzip))
    {
        writer.Write(text);
    }

    using var patch = File.OpenWrite(path);
    patch.Seek(4, SeekOrigin.Begin);
    patch.Write(new byte[4]);
}

// fetch-inputs.sh output: tm-grammars (grammars/*.json, index.js metadata) and the textmate-grammars-themes samples.
internal sealed partial class Inputs(string root)
{
    private readonly Dictionary<string, string[]> _embedded = ReadEmbedded(Path.Combine(root, "tm-grammars", "index.js"));
    private readonly ConcurrentDictionary<string, string[]> _patterns = new(StringComparer.Ordinal);

    public string Root => root;

    public string Grammars => Path.Combine(root, "tm-grammars");

    public IEnumerable<string> AllGrammars()
        => Directory.EnumerateFiles(Path.Combine(Grammars, "grammars"), "*.json").Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal);

    public IEnumerable<string> Samples()
        => Directory.EnumerateFiles(Path.Combine(root, "samples"), "*.sample").Select(p => Path.GetFileNameWithoutExtension(p)).Order(StringComparer.Ordinal);

    // The grammar and those it embeds, transitively, in discovery order.
    public List<string> Closure(string name)
    {
        var closure = new List<string>();
        var pending = new Stack<string>([name]);
        while (pending.TryPop(out var grammar))
        {
            if (closure.Contains(grammar) || !File.Exists(Path.Combine(Grammars, "grammars", grammar + ".json")))
            {
                continue;
            }

            closure.Add(grammar);
            foreach (var embedded in _embedded.GetValueOrDefault(grammar, []))
            {
                pending.Push(embedded);
            }
        }
        return closure;
    }

    // Distinct lines of a sample in order, without line terminators (TextMate hosts append the "\n").
    public IEnumerable<string> Lines(string language)
        => File.ReadAllText(Path.Combine(root, "samples", language + ".sample")).Split('\n').Select(l => l.TrimEnd('\r')).Distinct(StringComparer.Ordinal);

    // Every begin, end, while and match string, as TextMate hosts hand them to Oniguruma: with Onigwrap's
    // UnicodeCharEscape rewrite (\xHH... to \x{HH...}, \x{7fffffff} to \x{7ffff}).
    public string[] Patterns(string grammar) => _patterns.GetOrAdd(grammar, g =>
    {
        var found = new List<string>();
        Walk(JsonNode.Parse(File.ReadAllText(Path.Combine(Grammars, "grammars", g + ".json"))), found);
        return found.Select(p => HexEscape().Replace(p, m => "\\x{" + m.Value[2..] + "}").Replace("\\x{7fffffff}", "\\x{7ffff}")).Distinct(StringComparer.Ordinal).ToArray();
    });

    private static void Walk(JsonNode? node, List<string> found)
    {
        if (node is JsonObject o)
        {
            foreach (var (key, value) in o)
            {
                if (key is "match" or "begin" or "end" or "while" && value is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    found.Add(s);
                }
                else
                {
                    Walk(value, found);
                }
            }
        }
        else if (node is JsonArray a)
        {
            foreach (var value in a)
            {
                Walk(value, found);
            }
        }
    }

    private static Dictionary<string, string[]> ReadEmbedded(string path)
    {
        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in File.ReadAllText(path).Split("\n  {\n"))
        {
            if (Name().Match(entry) is { Success: true } name)
            {
                var embedded = Embedded().Match(entry);
                result.TryAdd(name.Groups[1].Value, embedded.Success ? Quoted().Matches(embedded.Groups[1].Value).Select(m => m.Groups[1].Value).ToArray() : []);
            }
        }
        return result;
    }

    [GeneratedRegex(@"\\x[A-Fa-f0-9]{2,8}")]
    private static partial Regex HexEscape();

    [GeneratedRegex(@"^    name: '([^']+)',$", RegexOptions.Multiline)]
    private static partial Regex Name();

    [GeneratedRegex(@"^    embedded: \[\n((?:      '[^']+',\n)*)    \],$", RegexOptions.Multiline)]
    private static partial Regex Embedded();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();
}

internal sealed unsafe class Native : IDisposable
{
    private const string Library = "oniguruma-oracle";
    private IntPtr _regex;

    private Native(IntPtr regex) => _regex = regex;

    [DllImport(Library)] private static extern IntPtr oracle_create(char* pattern, int length, int* error);
    [DllImport(Library)] private static extern void oracle_free(IntPtr regex);
    [DllImport(Library)] private static extern int oracle_search(IntPtr regex, char* subject, int length, int start, int* captures, int capacity);

    public static void Load(string tag)
    {
        var name = OperatingSystem.IsMacOS() ? "liboniguruma-oracle.dylib" : "liboniguruma-oracle.so";
        var path = Path.GetFullPath(Path.Combine("artifacts", "oniguruma", tag, name));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Build it first: tools/oracles/oniguruma/native/build-oracle.sh {tag} <commit-sha>", path);
        }

        var handle = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (library, _, _) => library == Library ? handle : IntPtr.Zero);
        using var warm = Create("a", out _); // onig_initialize runs once, before any parallel use
    }

    // Null when Oniguruma rejects the pattern, with its error code.
    public static Native? Create(string pattern, out int error)
    {
        fixed (char* p = pattern)
        {
            int e;
            var regex = oracle_create(p, pattern.Length, &e);
            error = e;
            return regex == IntPtr.Zero ? null : new Native(regex);
        }
    }

    // The group count with the capture pairs, -1 for no match, or a negative Oniguruma error code (such as -17, the
    // retry limit).
    public int Search(string text, int start, out int[] captures)
    {
        var buffer = new int[64];
        while (true)
        {
            int r;
            fixed (char* t = text)
            fixed (int* c = buffer)
            {
                r = oracle_search(_regex, t, text.Length, start, c, buffer.Length / 2);
            }

            if (r < 0)
            {
                captures = [];
                return r;
            }

            if (2 * r <= buffer.Length)
            {
                captures = buffer[..(2 * r)];
                return r;
            }
            buffer = new int[2 * r];
        }
    }

    public void Dispose()
    {
        if (_regex != IntPtr.Zero)
        {
            oracle_free(_regex);
            _regex = IntPtr.Zero;
        }
    }
}
