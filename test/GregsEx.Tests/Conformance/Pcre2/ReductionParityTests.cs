using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class ReductionParityTests
{
    [Fact]
    public void FiniteSourceReductionsKeepFullAndNoOptimizationOutcomesIndependent()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/parity/reduction-inputs.json")));
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/parity/reduction-observations.json.gz")), CompressionMode.Decompress);
        using var native = JsonDocument.Parse(gzip);
        var observations = native.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        List<object> differences = [], traces = [];
        int count = 0, comparisons = 0;
        foreach (var input in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = input.GetProperty("id").GetString()!;
            count++;
            var events = observations[id].GetProperty("events").EnumerateArray().ToArray();
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual, input });
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
            { Compare("compile.error", compile.GetProperty("error"), (int)error.SourceError); continue; }
            foreach (var info in events.First(e => e.GetProperty("event").GetString() == "info").GetProperty("values").EnumerateArray())
            {
                var query = info.GetProperty("query").GetInt32();
                var found = executor!.Metadata.TryGetInfo((Pcre2PatternInfoKey)query, out var value, out var diagnostic);
                Compare("info.returnCode." + query, info.GetProperty("returnCode"), found ? 0 : (int)diagnostic.SourceError);
                if (found)
                {
                    Compare("info.value." + query, info.GetProperty("value"), value);
                }
            }
            List<Dictionary<string, object?>> calls = [];
            var callAt = 0;
            var returns = Units(input, "callbackReturns");
            int Return() => callAt < returns.Length ? returns[callAt++] : 0;
            var bytes = new Pcre2ByteCallbacks(Match: (in x, _) => { var r = Return(); calls.Add(IndependentHostParityTests.Callout(x.Info, [.. x.Subject.ToArray().Select(x => (int)x)], [.. x.CalloutString.ToArray().Select(x => (int)x)], [.. x.Mark.ToArray().Select(x => (int)x)], x.Captures, r)); return r; });
            var chars = new Pcre2CharCallbacks(Match: (in x, _) => { var r = Return(); calls.Add(IndependentHostParityTests.Callout(x.Info, [.. x.Subject.ToArray().Select(x => (int)x)], [.. x.CalloutString.ToArray().Select(x => (int)x)], [.. x.Mark.ToArray().Select(x => (int)x)], x.Captures, r)); return r; });
            using var workspace = new Pcre2PreparedWorkspace(executor!, new(4096, 65536, 512, 2048));
            var subject = Units(input, "subjectUnits");
            var pairs = new Pcre2OffsetPair[input.GetProperty("pairCapacity").GetInt32()];
            var result = profile.Width == Pcre2CodeUnitWidth.Eight ? workspace.Match(Bytes(subject), 0, pairs, callbacks: bytes) : workspace.Match(Chars(subject), 0, pairs, callbacks: chars);
            var expected = events.First(e => e.GetProperty("event").GetString() == "match");
            Compare("match.returnCode", expected.GetProperty("returnCode"), result.NativeReturnCode);
            if (expected.GetProperty("pairs").GetArrayLength() > 0)
            {
                Compare("match.pairs", expected.GetProperty("pairs"), IndependentHostParityTests.Captures(pairs, count: expected.GetProperty("pairs").GetArrayLength()));
            }

            var nativeCalls = events.Where(e => e.GetProperty("event").GetString() == "callout").ToArray();
            Compare("callout.count", nativeCalls.Length, calls.Count);
            for (var i = 0; i < Math.Min(nativeCalls.Length, calls.Count); i++)
            {
                foreach (var field in calls[i])
                {
                    Compare("callout." + i + "." + field.Key, nativeCalls[i].GetProperty(field.Key), field.Value);
                }
            }

            if (calls.Count > 0)
            {
                traces.Add(new { id, input, nativeCalls, managedCalls = calls, managedResult = result });
            }
        }
        using (var gzipOutput = new GZipStream(File.Create(OutputPath("reduction-comparison.json.gz")), CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzipOutput, new { count, comparisons, mismatchCount = differences.Count, differences, traces });
        }

        Assert.Equal(80, count);
        Assert.True(differences.Count == 0, $"{differences.Count} finite source reduction differences; original source cases stay unchanged.");
    }
}
