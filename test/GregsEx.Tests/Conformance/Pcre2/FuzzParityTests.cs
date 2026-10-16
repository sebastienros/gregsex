using System.Collections.Frozen;
using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class FuzzParityTests
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };
    private static readonly (FrozenDictionary<string, JsonElement> Inputs, FrozenDictionary<string, JsonElement> Observations) Corpus = ReadCorpus();

    private static (FrozenDictionary<string, JsonElement>, FrozenDictionary<string, JsonElement>) ReadCorpus()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/parity/fuzz-inputs.json")));
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/parity/fuzz-observations.json.gz")), CompressionMode.Decompress);
        using var native = JsonDocument.Parse(gzip);
        return (
            inputs.RootElement.GetProperty("cases").Clone().EnumerateArray()
                .ToFrozenDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal),
            native.RootElement.GetProperty("cases").Clone().EnumerateArray()
                .ToFrozenDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal));
    }

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Corpus.Inputs.Keys.Select(id => new TheoryDataRow<string>(id));

    [Fact]
    public void CompletePairedInventory()
    {
        Assert.Equal(256, Corpus.Inputs.Count);
        Assert.Equal(Corpus.Inputs.Keys.Order(StringComparer.Ordinal), Corpus.Observations.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DeterministicBoundedGrammarInputsCompareIndependentNativeResults(string id)
    {
        var input = Corpus.Inputs[id];
        var events = Corpus.Observations[id].GetProperty("events").EnumerateArray().ToArray();
        List<object> differences = [];
        var comparisons = 0;
        void Execute()
        {
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual, input, nativeEvents = events });
                }
            }
            var profile = Profile(input);
            var budget = new CompileBudget(new());
            var pattern = Units(input, "patternUnits");
            var accepted = profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2ExecutorFactory.TryCompile(Bytes(pattern), profile, ref budget, out var executor, out var error)
                : Pcre2ExecutorFactory.TryCompile(Chars(pattern), profile, ref budget, out executor, out error);
            var compile = events.First(e => e.GetProperty("event").GetString() == "compile");
            Compare("compile.accepted", compile.GetProperty("accepted"), accepted);
            if (!accepted)
            { Compare("compile.error", compile.GetProperty("error"), (int)error.SourceError); Compare("compile.offset", compile.GetProperty("nativeOffset"), error.NativeOffset); return; }
            using var workspace = new Pcre2PreparedWorkspace(executor!, new(4096, 65536, 512, 2048));
            var subject = Units(input, "subjectUnits");
            var pairs = new Pcre2OffsetPair[input.GetProperty("pairCapacity").GetInt32()];
            var result = profile.Width == Pcre2CodeUnitWidth.Eight ? workspace.Match(Bytes(subject), input.GetProperty("startAt").GetInt32(), pairs)
                : workspace.Match(Chars(subject), input.GetProperty("startAt").GetInt32(), pairs);
            var expected = events.First(e => e.GetProperty("event").GetString() == "match");
            Compare("match.returnCode", expected.GetProperty("returnCode"), result.NativeReturnCode);
            if (expected.GetProperty("pairs").GetArrayLength() > 0)
            {
                Compare("match.pairs", expected.GetProperty("pairs"), IndependentHostParityTests.Captures(pairs, count: expected.GetProperty("pairs").GetArrayLength()));
            }

            if (expected.GetProperty("startChar").ValueKind != JsonValueKind.Null)
            {
                Compare("match.startChar", expected.GetProperty("startChar"), result.StartChar);
            }

            var mark = result.MarkIndex < 0 ? null : profile.Width == Pcre2CodeUnitWidth.Eight ? executor!.Metadata.GetMarkBytes(result.MarkIndex).ToArray().Select(x => (int)x).ToArray() : [.. executor!.Metadata.GetMarkChars(result.MarkIndex).ToArray().Select(x => (int)x)];
            if (expected.GetProperty("markDefined").GetBoolean())
            {
                Compare("match.mark", expected.GetProperty("markUnits"), mark);
            }
        }
        Execute();
        var report = OutputPath(Path.Combine("fuzz-comparison", id + ".json"));
        File.WriteAllText(report, JsonSerializer.Serialize(new { id, cases = 1, comparisons, mismatchCount = differences.Count, differences }, JsonOutputOptions));
        Assert.True(differences.Count == 0, $"{id}: {differences.Count} bounded deterministic grammar differences; see {report}.");
    }
}
