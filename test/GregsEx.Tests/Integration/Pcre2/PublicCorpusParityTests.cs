using System.Text.Json;
using Xunit;

using GregsEx.Tests.Conformance.Pcre2;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;
namespace GregsEx.Tests.Integration.Pcre2;

public sealed class PublicCorpusParityTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [Fact]
    public void PublicFactoriesAndNativeMatchingPreserveTheFrozenCorpus()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/inputs.json")));
        using var observations = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/observations.json")));
        var native = observations.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        List<object> mismatches = [];
        List<object> accounting = [];
        var comparisons = 0;
        foreach (var c in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            var events = native[id].GetProperty("events").EnumerateArray().ToArray();
            int begin = comparisons, failures = mismatches.Count;
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    mismatches.Add(new { id, field, expected, actual, input = c, nativeEvents = events });
                }
            }
            try
            {
                var compile = events.FirstOrDefault(x => x.GetProperty("event").GetString() == "compile");
                if (compile.ValueKind == JsonValueKind.Undefined)
                { accounting.Add(new { id, disposition = "boundary-event; pending explicit boundary comparison" }); continue; }
                var profile = Profile(c);
                var pattern = Units(c, "patternUnits");
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight
                    ? Pcre2Functions.TryParse(Bytes(pattern), profile, out var executor, out var diagnostic)
                    : Pcre2Functions.TryParse(Chars(pattern), profile, out executor, out diagnostic);
                Compare("compile.accepted", compile.GetProperty("accepted").GetBoolean(), ok);
                if (!ok)
                {
                    Compare("compile.error", compile.GetProperty("error").GetInt32(), (int)diagnostic.SourceError);
                    Compare("compile.nativeOffset", compile.GetProperty("nativeOffset").GetInt32(), diagnostic.NativeOffset);
                    accounting.Add(new { id, comparisons = comparisons - begin, failures = mismatches.Count - failures, disposition = "compile-rejected" });
                    continue;
                }
                foreach (var info in events.Where(x => x.GetProperty("event").GetString() == "info"))
                {
                    foreach (var value in info.GetProperty("values").EnumerateArray())
                    {
                        var query = value.GetProperty("query").GetInt32();
                        var found = executor!.TryGetInfo((Pcre2PatternInfoKey)query, out var actual, out var error);
                        Compare("info.returnCode." + query, value.GetProperty("returnCode").GetInt32(), found ? 0 : (int)error.SourceError);
                        if (found)
                        {
                            Compare("info.value." + query, value.GetProperty("value").GetInt64(), actual);
                        }
                    }
                }

                if (c.GetProperty("operation").GetString() == "Match")
                {
                    var expected = events.First(x => x.GetProperty("event").GetString() == "match");
                    var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 1234567, 1234567), c.GetProperty("pairCapacity").GetInt32()).ToArray();
                    var initialAt = 0;
                    foreach (var initial in c.GetProperty("initialPairs").EnumerateArray())
                    {
                        pairs[initialAt++] = initial.GetProperty("participated").GetBoolean() ? new(true, initial.GetProperty("start").GetInt32(), initial.GetProperty("end").GetInt32()) : default;
                    }

                    var subject = Units(c, "subjectUnits");
                    var options = new Pcre2MatchOptions
                    {
                        Flags = (Pcre2MatchFlags)c.GetProperty("matchFlags").GetUInt32(),
                        OffsetLimit = c.GetProperty("offsetLimit").ValueKind == JsonValueKind.Null ? null : c.GetProperty("offsetLimit").GetInt32(),
                        Limits = c.GetProperty("workLimit").ValueKind == JsonValueKind.Null ? null : new(maxWork: c.GetProperty("workLimit").GetInt64())
                    };
                    var call = 0;
                    var returns = Units(c, "callbackReturns");
                    int Return() => call < returns.Length ? returns[call++] : 0;
                    var bytesCallback = new Pcre2ByteCallbacks(Match: (in _, _) => Return());
                    var charsCallback = new Pcre2CharCallbacks(Match: (in _, _) => Return());
                    var callbacks = c.GetProperty("callbackEnabled").GetBoolean();
                    using var workspace = executor!.CreateWorkspace(new(4096, 65536, 512, 2048));
                    var result = profile.Width == Pcre2CodeUnitWidth.Eight
                        ? workspace.Match(Bytes(subject), c.GetProperty("startAt").GetInt32(), pairs, options, callbacks ? bytesCallback : null)
                        : workspace.Match(Chars(subject), c.GetProperty("startAt").GetInt32(), pairs, options, callbacks ? charsCallback : null);
                    if (c.GetProperty("nativeDisposition").GetString()!.StartsWith("SourceUndefined", StringComparison.Ordinal))
                    {
                        accounting.Add(new { id, disposition = "source-undefined continuation; raw managed result retained", result });
                    }
                    else
                    {
                        Compare("match.returnCode", expected.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
                        if (result.Status is Pcre2MatchStatus.Match or Pcre2MatchStatus.Partial)
                        {
                            Compare("match.pairs", expected.GetProperty("pairs").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? null : x.EnumerateArray().Select(y => y.ValueKind == JsonValueKind.Null ? (int?)null : y.GetInt32()).ToArray()).ToArray(),
                                pairs.Take(expected.GetProperty("pairs").GetArrayLength()).Select(p => p.Participated ? new int?[] { p.Start, p.End } : [null, null]).ToArray());
                            if (expected.GetProperty("startChar").ValueKind != JsonValueKind.Null)
                            {
                                Compare("match.startChar", expected.GetProperty("startChar").GetInt32(), result.StartChar);
                            }
                        }
                        if (expected.TryGetProperty("errorOffset", out var offset) && offset.ValueKind != JsonValueKind.Null)
                        {
                            Compare("match.errorOffset", offset.GetInt32(), result.StartChar);
                        }

                        if (expected.GetProperty("callerVector").ValueKind != JsonValueKind.Null)
                        {
                            Compare("match.callerVector", expected.GetProperty("callerVector"), IndependentHostParityTests.Captures(pairs));
                        }

                        if (expected.GetProperty("markDefined").GetBoolean())
                        {
                            var mark = result.MarkIndex < 0 ? null : profile.Width == Pcre2CodeUnitWidth.Eight ? executor!.GetMarkBytes(result.MarkIndex).ToArray().Select(x => (int)x).ToArray() : [.. executor!.GetMarkChars(result.MarkIndex).ToArray().Select(x => (int)x)];
                            Compare("match.mark", expected.GetProperty("markUnits").ValueKind == JsonValueKind.Null ? null : Units(expected, "markUnits"), mark);
                        }
                    }
                }
                accounting.Add(new { id, comparisons = comparisons - begin, failures = mismatches.Count - failures, disposition = "initial comparison; callback/host/upstream obligations pending" });
            }
            catch (Exception exception) { mismatches.Add(new { id, field = "exception", exception = exception.ToString(), input = c, nativeEvents = events }); }
        }
        File.WriteAllText(OutputPath("public-independent.json"), JsonSerializer.Serialize(new { comparisons, mismatchCount = mismatches.Count, mismatches, accounting }, Json));
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} differences; see artifacts/conformance/pcre2/public-independent.json.");
    }
}
