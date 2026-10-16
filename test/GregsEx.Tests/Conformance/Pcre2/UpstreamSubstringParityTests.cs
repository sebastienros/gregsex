using System.IO.Compression;
using System.Text;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;
namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class UpstreamSubstringParityTests
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };
    [Fact]
    public void NativeUpstreamSubstringQueriesUseIndependentDefinedPairs()
    {
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/parity/complete-substring-cases.json.gz")), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        var plans = document.RootElement.GetProperty("plans");
        Dictionary<string, Pcre2Executor> cache = [];
        List<object> differences = [], ledger = [];
        int count = 0, comparisons = 0;
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            var query = c.GetProperty("helper");
            var key = c.GetProperty("planKey").GetString()!;
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual, query });
                }
            }
            if (c.GetProperty("input").GetProperty("dfa").GetBoolean())
            { ledger.Add(new { id, query, disposition = "PH-012 DFA match-data helper semantics excluded; standard-interpreter counterparts retained" }); continue; }
            if (!cache.TryGetValue(key, out var executor))
            {
                var plan = plans.GetProperty(key);
                var profile = Profile(plan.GetProperty("profile"));
                var pattern = Units(plan.GetProperty("input"), "units");
                var budget = new CompileBudget(new(maxCaptureGroups: 65536));
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2ExecutorFactory.TryCompile(Bytes(pattern), profile, ref budget, out executor, out _) : Pcre2ExecutorFactory.TryCompile(Chars(pattern), profile, ref budget, out executor, out _);
                Assert.True(ok, id);
                cache[key] = executor!;
            }
            var nativePairs = c.GetProperty("result").GetProperty("pairs");
            var pairs = nativePairs.EnumerateArray().Select(p => p[0].ValueKind == JsonValueKind.Null ? default : new Pcre2OffsetPair(true, p[0].GetInt32(), p[1].GetInt32())).ToArray();
            var subject = Units(c.GetProperty("input"), "units");
            var number = query.GetProperty("number").GetInt32();
            if (query.GetProperty("nameUnits").ValueKind != JsonValueKind.Null)
            { var nameUnits = Units(query, "nameUnits"); var name = executor!.Program.Profile.Width == Pcre2CodeUnitWidth.Eight && executor.Program.Profile.Utf ? Encoding.UTF8.GetString(Bytes(nameUnits)) : new(Chars(nameUnits)); number = Pcre2Substrings.Number(executor.Program, pairs, name); }
            var nativeRc = c.GetProperty("result").GetProperty("returnCode").GetInt32();
            var matchResult = new Pcre2MatchResult(nativeRc >= 0 ? Pcre2MatchStatus.Match : nativeRc == -2 ? Pcre2MatchStatus.Partial : Pcre2MatchStatus.Error, nativeRc, 0);
            var operation = query.GetProperty("operation").GetString()!;
            var length = Pcre2Substrings.Length(pairs, number, executor!.Program.GroupCount, matchResult);
            int rc;
            int[]? output = null;
            if (operation == "list")
            { rc = 0; output = length < 0 ? [] : subject.AsSpan(pairs[number].Start, length).ToArray(); }
            else if (operation == "copy")
            {
                var capacity = query.GetProperty("capacity").GetInt32();
                Pcre2SubstringResult result;
                if (executor.Program.Profile.Width == Pcre2CodeUnitWidth.Eight)
                {
                    var buffer = new byte[capacity];
                    result = Pcre2Substrings.Copy<byte>(Bytes(subject), pairs, number, buffer, executor.Program.GroupCount, matchResult);
                    if (result.NativeReturnCode == 0)
                    {
                        output = [.. buffer.AsSpan(0, result.OutputUnits).ToArray().Select(x => (int)x)];
                    }
                }
                else
                {
                    var buffer = new char[capacity];
                    result = Pcre2Substrings.Copy<char>(Chars(subject), pairs, number, buffer, executor.Program.GroupCount, matchResult);
                    if (result.NativeReturnCode == 0)
                    {
                        output = [.. buffer.AsSpan(0, result.OutputUnits).ToArray().Select(x => (int)x)];
                    }
                }
                rc = result.NativeReturnCode;
            }
            else
            {
                rc = length < 0 ? length : 0;
                if (length >= 0)
                {
                    output = executor.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ? [.. Pcre2Substrings.Materialize<byte>(Bytes(subject), pairs, number, new(), executor.Program.GroupCount).Select(x => (int)x)] : [.. Pcre2Substrings.MaterializeChars(Chars(subject), pairs, number, new(), executor.Program.GroupCount).Select(x => (int)x)];
                }
            }
            count++;
            Compare("returnCode", query.GetProperty("returnCode"), rc);
            if (rc == 0)
            { Compare("length", query.GetProperty("length"), output!.Length); Compare("output", query.GetProperty("outputUnits"), output); }
        }
        File.WriteAllText(OutputPath("complete-substring-comparison.json"), JsonSerializer.Serialize(new { count, comparisons, differences, ledger }, JsonOutputOptions));
        Assert.True(differences.Count == 0, $"{differences.Count} substring differences");
    }
}
