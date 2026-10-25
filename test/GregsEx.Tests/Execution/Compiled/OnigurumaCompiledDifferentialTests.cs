using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// The Oniguruma interpreter is the oracle for the compiled Oniguruma backend. The TextMate grammar corpus (whose native
// observations CorpusTests replays) supplies realistic patterns and subjects; the tables below pin the constructs the
// lowering models on their own.
public class OnigurumaCompiledDifferentialTests
{
    private static readonly OnigurumaCompileOptions Options = new(new RegexCompileLimits(maxPatternUnits: 1 << 20,
        maxNestingDepth: 4096, maxCaptureGroups: 32767));

    private static OnigurumaPattern? TryCompile(OnigurumaPattern interpreted)
    {
        try
        { return interpreted.Compile(); }
        catch (NotSupportedException) { return null; }
    }

    private static string Result(OnigurumaWorkspace workspace, string input, int start, RegexCapture[] groups)
    {
        var status = workspace.FindCaptures(input, start, groups, out var range);
        if (status != RegexSearchStatus.Match)
        {
            return status.ToString();
        }

        var text = range.Index + ":" + range.Length;
        for (var g = 1; g < groups.Length; g++)
        {
            text += "|" + (groups[g].Participated ? groups[g].Index + ":" + groups[g].Length : "-");
        }

        return text;
    }

    public static IEnumerable<TheoryDataRow<string>> Corpora() => [new("grammar-cases.json.gz"), new("edge-cases.json")];

    [Theory]
    [MemberData(nameof(Corpora))]
    public void CompiledMatchesInterpretedOverTheCorpus(string corpus)
    {
        var cases = Read(corpus).AsArray();
        int compiled = 0, total = 0, compared = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var c in cases)
        {
            var source = c!["pattern"]!.GetValue<string>();
            if (!OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var oracle, out _, Options))
            {
                continue;
            }

            total++;
            OnigurumaPattern pattern;
            try
            { pattern = oracle!.Compile(); }
            catch (NotSupportedException e)
            {
                reasons[e.Message] = reasons.GetValueOrDefault(e.Message) + 1;
                continue;
            }

            compiled++;
            Assert.Equal(RegexExecutionBackend.Compiled, pattern.ExecutionBackend);
            Assert.Equal(RegexExecutionBackend.Interpreted, oracle.ExecutionBackend);
            var expectedSpace = oracle.CreateWorkspace();
            var actualSpace = pattern.CreateWorkspace();
            var expectedGroups = new RegexCapture[oracle.GroupCount];
            var actualGroups = new RegexCapture[pattern.GroupCount];
            var starts = c["starts"]?.AsArray().Select(s => s!.GetValue<int>()).ToArray() ?? [0];
            foreach (var subjectNode in c["subjects"]!.AsArray())
            {
                var subject = Subject(subjectNode!);
                foreach (var start in starts.Append(subject.Length / 2).Append(subject.Length).Distinct())
                {
                    if (start > subject.Length)
                    {
                        continue;
                    }

                    var expected = Result(expectedSpace, subject, start, expectedGroups);
                    var actual = Result(actualSpace, subject, start, actualGroups);
                    Assert.True(expected == actual, $"{source} on {JsonValue.Create(subject)!.ToJsonString()} from {start}: interpreter {expected}, compiled {actual}");
                    Assert.Equal(expectedSpace.IsMatch(subject, start), actualSpace.IsMatch(subject, start));
                    compared++;
                }
            }
        }

        var summary = $"{compiled}/{total} compiled, {compared} searches compared; declined: " +
            string.Join("; ", reasons.OrderByDescending(r => r.Value).Select(r => r.Value + " " + r.Key));
        Assert.True(compiled * 2 >= total, summary);
    }

    [Fact]
    public void ScannerUsesTheKernels()
    {
        var cases = Read("grammar-cases.json.gz").AsArray().Take(200).ToArray();
        var patterns = new List<OnigurumaPattern>();
        var subjects = new List<string>();
        foreach (var c in cases)
        {
            if (OnigurumaPattern.TryParse(c!["pattern"]!.GetValue<string>(), RegexProfiles.OnigurumaTextMate, out var p, out _, Options))
            {
                patterns.Add(p!);
            }

            subjects.AddRange(c["subjects"]!.AsArray().Select(s => Subject(s!)));
        }

        var compiled = patterns.Select(p => TryCompile(p) ?? p).ToList();
        Assert.Contains(compiled, p => p.ExecutionBackend == RegexExecutionBackend.Compiled);
        var oracle = new OnigurumaScanner(patterns);
        var scanner = new OnigurumaScanner(compiled);
        foreach (var subject in subjects.Distinct())
        {
            for (var at = 0; at <= subject.Length; at++)
            {
                var expected = oracle.FindNextMatch(subject, at, out var expectedIndex);
                var actual = scanner.FindNextMatch(subject, at, out var actualIndex);
                Assert.Equal(expected, actual);
                Assert.Equal(expectedIndex, actualIndex);
                Assert.Equal(oracle.Captures.ToArray(), scanner.Captures.ToArray());
            }
        }
    }

    // Constructs the lowering models, with known Oniguruma results (TextMate options), checked on both tiers.
    public static IEnumerable<TheoryDataRow<string, string, int, string>> KnownCases()
    {
        // Look-behind: nearest start first, alternatives of different lengths divided in order, bodies ending at the position.
        yield return new("(?<=(ab)|(b))c", "abc", 0, "2:1|0:2|-");
        yield return new("(?<=(a)|(ba))c", "bac", 0, "2:1|1:1|-");
        yield return new("(?<=(b)|(ab))c", "abc", 0, "2:1|1:1|-");
        yield return new("(?<=a+)b", "aaab", 0, "3:1");
        yield return new("(?<=(a+))b", "aaab", 0, "3:1|2:1");
        yield return new("(?<=(a*)x)b", "aaxb", 0, "3:1|2:0");
        yield return new("(?<=^|\\s)[a-z]+", " the cat", 0, "1:3");
        yield return new("(?<!\\w)-\\w+\\b", "a -b -c", 0, "2:2");
        yield return new("(?<=a)b", "b", 0, "NoMatch");
        yield return new("(?<=(?>a+))b", "aab", 0, "2:1");
        yield return new("(?<=a(?<=a)b)c", "abc", 0, "2:1");
        yield return new("(?<=[a-z]{2,3})x", "ax abx", 0, "5:1");
        // Atomic groups and possessive repeats.
        yield return new("(?>a|ab)c", "abc", 0, "NoMatch");
        yield return new("(?>ab|a)c", "abc", 0, "0:3");
        yield return new("a++a", "aaa", 0, "NoMatch");
        yield return new("a{2,3}+a", "aaaa", 0, "0:4");
        yield return new("(?:ab)++c", "ababc", 0, "0:5");
        yield return new("(?:a|ab)++c", "ababc", 0, "NoMatch");
        yield return new("[\\t ]*+(alias)", "  alias", 0, "0:7|2:5");
        yield return new("(a|(b))+", "ba", 0, "0:2|1:1|0:1");
        // Anchors.
        yield return new("^a", "b\na", 0, "2:1");
        yield return new("^$", "a\n", 0, "NoMatch");
        yield return new("a$", "a\nb", 0, "0:1");
        yield return new("a\\Z", "a\n", 0, "0:1");
        yield return new("a\\z", "a\n", 0, "NoMatch");
        yield return new("\\Ga", "ba", 1, "1:1");
        yield return new("\\Ga|b", "cab", 0, "2:1");
        yield return new("\\bé\\b", "a é b", 0, "2:1");
        yield return new("\\b\\w+\\b", "x ²y z", 0, "0:1");
        yield return new("(?W)\\b\\w+\\b", "é ab", 0, "2:2");
        // Case folding resolved by the parser.
        yield return new("(?i)straße", "STRASSE", 0, "0:7");
        yield return new("(?i)ss", "ß", 0, "0:1");
        yield return new("(?i)[k]", "K", 0, "0:1");
        yield return new("(?i)abc", "ABC", 0, "0:3");
        // Dot, sets and repeats.
        yield return new("a.c", "a\nc abc", 0, "4:3");
        yield return new("(?m)a.c", "a\nc", 0, "0:3");
        yield return new("[^a]+", "aab\nc", 0, "2:3");
        yield return new("\\d{2,}", "1 23 456", 0, "2:2");
        yield return new("a{3}", "aaaa", 0, "0:3");
        yield return new("(a?)+?b", "aab", 0, "0:3|1:1");
        yield return new("x*?y", "xxy", 0, "0:3");
        yield return new("(?=\\[)", "a[b]", 0, "1:0");
        yield return new("(?!a)[a-z]", "ab", 0, "1:1");
        yield return new("(?=(a))a\\1", "aa", 0, "0:2|0:1");
        yield return new("(?<=\\G)b", "ab", 1, "1:1");
        yield return new("(?<=\\G|x)b", "ab", 1, "1:1");
        yield return new("(?<!\\w)(?<=\\G)b", "ab", 1, "NoMatch");
        yield return new("(?<!\\w)(?<=\\G|x)([^a]+)", "a/b", 2, "2:1|2:1");
        yield return new("(?<!a)-", "a -b", 0, "2:1");
        yield return new("(?<!\\w)-", "a -b", 0, "2:1");
        yield return new("(?<!a)b|c", "ab c", 0, "3:1");
        yield return new("x(?<!a)b", "xb", 0, "0:2");
        yield return new("(?<!a)b", "b", 0, "0:1");
        yield return new("(?<!a)b", "cb", 0, "1:1");
        yield return new("(?<!a)bc", "cbc", 0, "1:2");
        yield return new("(?<!a)[bc]+", "cbc", 0, "0:3");
        yield return new("(?<!a)-[bc]+", "c-bc", 0, "1:3");
        yield return new("(?<!a)-[bc]+\\b", "c-bc", 0, "1:3");
        yield return new("(?<!a)-\\w+\\b", "c-bc", 0, "1:3");
        yield return new("-\\w+\\b", "c-bc", 0, "1:3");
    }

    [Theory]
    [MemberData(nameof(KnownCases))]
    public void KnownResultsHoldOnBothTiers(string pattern, string input, int start, string expected)
    {
        var oracle = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate);
        var compiled = TryCompile(oracle);
        foreach (var pat in new[] { oracle, compiled })
        {
            if (pat is null)
            {
                continue;
            }

            var groups = new RegexCapture[pat.GroupCount];
            var actual = Result(pat.CreateWorkspace(), input, start, groups);
            Assert.True(expected == actual, $"{pattern} ({pat.ExecutionBackend}): {actual}");
        }
    }

    [Fact]
    public void ModelledConstructsAreLowered()
    {
        foreach (var pattern in new[] { "(?<=(ab)|(b))c", "(?<=a+)b", "(?>a|ab)c", "a{2,3}+a", "(?:a|ab)++c", "\\Ga|b", "(?i)straße", "\\b\\w+\\b", "(?<!\\w)-\\w+\\b", "(?=\\[)" })
        {
            Assert.NotNull(TryCompile(OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate)));
        }
    }

    [Fact]
    public void UnmodelledConstructsAreRejected()
    {
        foreach (var pattern in new[] { "(?<n>a)\\k<n>", "(?<n>a)\\g<n>", "(a)(?(1)b|c)", "(?~a)", "a\\Kb", "\\y", "\\X", "(a*)*b", "😀" })
        {
            Assert.Null(TryCompile(OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate)));
        }
    }

    [Fact]
    public void SubjectsWithSurrogatesAndSearchesWithOptionsUseTheInterpreter()
    {
        var oracle = OnigurumaPattern.Parse("(?<=.)[^a]+", RegexProfiles.OnigurumaTextMate);
        var compiled = oracle.Compile();
        var groups = new RegexCapture[1];
        foreach (var input in new[] { "😀bc", "a\uD800", "x😀" })
        {
            Assert.Equal(Result(oracle.CreateWorkspace(), input, 0, groups), Result(compiled.CreateWorkspace(), input, 0, groups));
        }

        var limited = new RegexMatchOptions { Limits = new RegexMatchLimits(maxWork: 1) };
        var space = compiled.CreateWorkspace();
        Assert.Equal(oracle.CreateWorkspace().IsMatch("abc", 0, limited), space.IsMatch("abc", 0, limited));
        Assert.Equal(RegexSearchStatus.Match, space.IsMatch("abc", 0));
        Assert.True(space.LastWork > 0);
    }

    private static string Subject(JsonNode subject)
        => subject is JsonObject units ? new string(units["units"]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray()) : subject.GetValue<string>();

    private static JsonNode Read(string name)
    {
        var path = Path.Combine(CorpusDirectory(), name);
        using var file = File.OpenRead(path);
        using var stream = name.EndsWith(".gz", StringComparison.Ordinal) ? new GZipStream(file, CompressionMode.Decompress) : (Stream)file;
        return JsonNode.Parse(stream)!;
    }

    private static string CorpusDirectory([CallerFilePath] string path = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../corpora/oniguruma"));
}
