using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

// Replays the native Oniguruma 6.9.10 observations of test/corpora/oniguruma (see its README) with the Oniguruma
// profile TextMate hosts use. A native error code (the retry limit) agrees with LimitExceeded.
public class CorpusTests
{
    private static readonly OnigurumaCompileOptions Options = OnigurumaCompileOptions.TextMate;

    [Fact]
    public void EdgeCasesMatchNativeOniguruma()
        => AssertCases(Read("edge-cases.json"), Read("edge-cases.expected.json"), minimumOutcomes: 1200);

    [Fact]
    public void GrammarPatternsMatchNativeOniguruma()
        => AssertCases(Read("grammar-cases.json.gz"), Read("grammar-cases.expected.json.gz"), minimumOutcomes: 28000);

    // The failure memo from a search's first backtrack, so it covers every corpus search it applies to.
    [Fact]
    public void EdgeCasesMatchNativeOnigurumaWithTheMemoAlwaysOn()
        => AssertCases(Read("edge-cases.json"), Read("edge-cases.expected.json"), minimumOutcomes: 1200, memoAfter: 0);

    [Fact]
    public void GrammarPatternsMatchNativeOnigurumaWithTheMemoAlwaysOn()
        => AssertCases(Read("grammar-cases.json.gz"), Read("grammar-cases.expected.json.gz"), minimumOutcomes: 28000, memoAfter: 0);

    [Fact]
    public void RetainedFilesMatchTheirRecordedHashes()
    {
        var readme = File.ReadAllText(Path.Combine(CorpusDirectory(), "README.md"));
        var entries = Regex.Matches(readme, @"- `([^`]+)`: `([0-9a-f]{64})`");
        Assert.Equal(5, entries.Count);
        foreach (Match entry in entries)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(CorpusDirectory(), entry.Groups[1].Value))));
            Assert.Equal(entry.Groups[2].Value, hash);
        }
    }

    private static void AssertCases(JsonNode input, JsonNode expected, int minimumOutcomes, long? memoAfter = null)
    {
        var cases = input.AsArray();
        var outcomes = expected.AsArray();
        Assert.Equal(cases.Count, outcomes.Count);
        var checkedOutcomes = 0;
        for (var i = 0; i < cases.Count; i++)
        {
            var source = cases[i]!["pattern"]!.GetValue<string>();
            var parsed = OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out var diagnostic, Options);
            if (outcomes[i] is null)
            {
                Assert.False(parsed, $"native Oniguruma rejects {source}");
                continue;
            }

            Assert.True(parsed, $"{source}: {diagnostic.Message}");
            var workspace = pattern!.CreateWorkspace();
            if (memoAfter is { } after)
            {
                workspace.Matcher.MemoAfter = after;
            }

            var groups = new RegexCapture[pattern.GroupCount];
            var subjects = cases[i]!["subjects"]!.AsArray();
            for (var s = 0; s < subjects.Count; s++)
            {
                var text = Subject(subjects[s]!);
                // MaxWork bounds a whole search where Oniguruma's retry limit bounds each start: on a subject where
                // native hits that limit from some start, later starts may exhaust the shared budget.
                var nearLimit = outcomes[i]![s]!.AsArray().Any(o => o![1] is JsonValue v && v.GetValue<int>() < 0);
                foreach (var outcome in outcomes[i]![s]!.AsArray())
                {
                    var start = outcome![0]!.GetValue<int>();
                    var native = outcome[1];
                    var status = workspace.FindCaptures(text, start, groups, out _);
                    var context = $"{source} on {JsonValue.Create(text)!.ToJsonString()} from {start}";
                    if (nearLimit && status == RegexSearchStatus.LimitExceeded)
                    {
                        continue;
                    }

                    // The failure memo finishes some searches where native Oniguruma reaches its retry limit.
                    if (native is JsonValue && pattern.Program.MemoSlots is not null && status != RegexSearchStatus.LimitExceeded)
                    {
                        continue;
                    }

                    if (native is null)
                    {
                        Assert.True(status == RegexSearchStatus.NoMatch, $"{context}: {status}");
                    }
                    else if (native is JsonValue error)
                    {
                        Assert.True(error.GetValue<int>() < 0 && status == RegexSearchStatus.LimitExceeded, $"{context}: {status}");
                    }
                    else
                    {
                        Assert.True(status == RegexSearchStatus.Match, $"{context}: {status}");
                        Assert.Equal(native.ToJsonString(), Captures(groups));
                    }
                    checkedOutcomes++;
                }
            }
        }
        Assert.True(checkedOutcomes >= minimumOutcomes, $"{checkedOutcomes} outcomes");
    }

    private static string Subject(JsonNode subject)
        => subject is JsonObject units ? new string(units["units"]!.AsArray().Select(u => (char)u!.GetValue<int>()).ToArray()) : subject.GetValue<string>();

    private static string Captures(ReadOnlySpan<RegexCapture> groups)
    {
        var values = new List<int>();
        foreach (var g in groups)
        {
            values.Add(g.Participated ? g.Index : -1);
            values.Add(g.Participated ? g.Index + g.Length : -1);
        }
        return "[" + string.Join(",", values) + "]";
    }

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
