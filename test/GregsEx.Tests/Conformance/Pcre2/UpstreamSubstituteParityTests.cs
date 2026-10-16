using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class UpstreamSubstituteParityTests
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };
    [Fact]
    public void IndependentlyDecodedUpstreamSubstitutionInvocations()
    {
        using var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/parity/complete-substitute-cases.json.gz")), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(gzip);
        List<object> differences = [], ledger = [];
        int comparisons = 0, invocations = 0;
        foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = c.GetProperty("id").GetString()!;
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual });
                }
            }
            try
            {
                var source = c.GetProperty("plan");
                var profile = Profile(source.GetProperty("profile"));
                var input = c.GetProperty("input");
                var native = c.GetProperty("events").EnumerateArray().ToArray();
                var expected = native[^1];
                if (input.GetProperty("nullSubject").GetBoolean() || input.GetProperty("nullReplacement").GetBoolean())
                { ledger.Add(new { id, disposition = "PH-012 null native pointer API boundary; managed spans have no null pointer distinction" }); continue; }
                var budget = new CompileBudget(new(maxCaptureGroups: 65536));
                var pattern = Units(source.GetProperty("input"), "units");
                var compiled = profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2ExecutorFactory.TryCompile(Bytes(pattern), profile, ref budget, out var executor, out var error) : Pcre2ExecutorFactory.TryCompile(Chars(pattern), profile, ref budget, out executor, out error);
                if (!compiled)
                { ledger.Add(new { id, disposition = "managed compile bound", error }); Assert.Equal(Pcre2DiagnosticKind.CompileLimit, error.Kind); continue; }
                var calls = native.Where(x => x.GetProperty("kind").GetString() == "callout").ToArray();
                var subs = native.Where(x => x.GetProperty("kind").GetString() == "substituteCallout").ToArray();
                var caseInputs = native.Where(x => x.GetProperty("kind").GetString() == "caseCalloutInput").ToArray();
                var caseOutputs = native.Where(x => x.GetProperty("kind").GetString() == "caseCalloutOutput").ToArray();
                var controls = c.GetProperty("controls").EnumerateArray().Select(x => x.GetString()).ToArray();
                int ca = 0, sa = 0, ka = 0;
                int Call(Pcre2CalloutInfo info, int[] subject, int[] text, int[] mark, ReadOnlySpan<Pcre2OffsetPair> pairs)
                {
                    if (ca >= calls.Length)
                    { differences.Add(new { id, field = "extra match callback" }); return 0; }
                    var e = calls[ca++];
                    var rc = e.GetProperty("returnCode").GetInt32();
                    foreach (var field in IndependentHostParityTests.Callout(info, subject, text, mark, pairs, rc))
                    {
                        Compare("callout." + (ca - 1) + "." + field.Key, e.GetProperty(field.Key), field.Value);
                    }

                    return rc;
                }
                int Sub(Pcre2SubstituteCalloutInfo info, int[] output, ReadOnlySpan<Pcre2OffsetPair> pairs)
                {
                    if (sa >= subs.Length)
                    { differences.Add(new { id, field = "extra substitute callback" }); return 0; }
                    var e = subs[sa++];
                    Compare("sub.version", e.GetProperty("version"), info.Version);
                    Compare("sub.count", e.GetProperty("count"), info.SubstitutionCount);
                    Compare("sub.start", e.GetProperty("outputStart"), info.OutputStart);
                    Compare("sub.end", e.GetProperty("outputEnd"), info.OutputEnd);
                    Compare("sub.output", e.GetProperty("proposedOutput"), output);
                    Compare("sub.pairs", e.GetProperty("captures"), IndependentHostParityTests.Captures(pairs, count: e.GetProperty("captures").GetArrayLength()));
                    return e.GetProperty("returnCode").GetInt32();
                }
                int Case(int[] units, Span<int> output, Pcre2SubstituteCase mode)
                {
                    if (ka >= caseInputs.Length)
                    { differences.Add(new { id, field = "extra case callback" }); return -1; }
                    var e = caseInputs[ka];
                    var written = caseOutputs[ka++];
                    Compare("case.input", e.GetProperty("inputUnits"), units);
                    Compare("case.mode", e.GetProperty("mode"), (int)mode);
                    var required = written.GetProperty("requiredUnits").ValueKind == JsonValueKind.Null ? -1 : written.GetProperty("requiredUnits").GetInt32();
                    if (written.GetProperty("outputUnits").ValueKind != JsonValueKind.Null)
                    { var values = Units(written, "outputUnits"); values.AsSpan(0, Math.Min(values.Length, output.Length)).CopyTo(output); }
                    return required;
                }
                int[] U(ReadOnlySpan<byte> a) => [.. a.ToArray().Select(x => (int)x)];
                int[] V(ReadOnlySpan<char> a) => [.. a.ToArray().Select(x => (int)x)];
                var bc = new Pcre2ByteCallbacks(calls.Length == 0 ? null : (in Pcre2ByteCalloutContext x, object? _) => Call(x.Info, U(x.Subject), U(x.CalloutString), U(x.Mark), x.Captures), subs.Length == 0 && !controls.Contains("substitute_callout") ? null : (in Pcre2ByteSubstituteContext x, object? _) => Sub(x.Info, U(x.Output), x.Captures), caseInputs.Length == 0 && !controls.Contains("substitute_case_callout") ? null : (ReadOnlySpan<byte> x, Span<byte> y, Pcre2SubstituteCase m, object? _) => { var output = new int[y.Length]; var rc = Case(U(x), output, m); for (var i = 0; i < Math.Min(Math.Max(rc, 0), y.Length); i++) { y[i] = (byte)output[i]; } return rc; });
                var cc = new Pcre2CharCallbacks(calls.Length == 0 ? null : (in Pcre2CharCalloutContext x, object? _) => Call(x.Info, V(x.Subject), V(x.CalloutString), V(x.Mark), x.Captures), subs.Length == 0 && !controls.Contains("substitute_callout") ? null : (in Pcre2CharSubstituteContext x, object? _) => Sub(x.Info, V(x.Output), x.Captures), caseInputs.Length == 0 && !controls.Contains("substitute_case_callout") ? null : (ReadOnlySpan<char> x, Span<char> y, Pcre2SubstituteCase m, object? _) => { var output = new int[y.Length]; var rc = Case(V(x), output, m); for (var i = 0; i < Math.Min(Math.Max(rc, 0), y.Length); i++) { y[i] = (char)output[i]; } return rc; });
                var flags = input.GetProperty("options").GetUInt32();
                var substituteFlags = (Pcre2SubstituteFlags)(flags & 0x00039F00u);
                var options = new Pcre2MatchOptions { Flags = (Pcre2MatchFlags)(flags & ~0x00039F00u), OffsetLimit = c.GetProperty("context").GetProperty("offsetLimit").ValueKind == JsonValueKind.Null ? null : c.GetProperty("context").GetProperty("offsetLimit").GetInt32() };
                var pairs = new Pcre2OffsetPair[input.GetProperty("pairCapacity").GetInt32()];
                Pcre2MatchResult precomputed = default;
                if ((substituteFlags & Pcre2SubstituteFlags.Matched) != 0)
                {
                    var i = 0;
                    foreach (var p in input.GetProperty("precomputedPairs").EnumerateArray())
                    { if (i >= executor!.Program.GroupCount) { break; } if (p[0].ValueKind != JsonValueKind.Null && p[0].GetInt64() <= int.MaxValue) { pairs[i] = new(true, p[0].GetInt32(), p[1].GetInt32()); } i++; }
                    var rc = input.GetProperty("precomputedReturnCode").GetInt32();
                    precomputed = new(rc >= 0 ? Pcre2MatchStatus.Match : rc == -1 ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Error, rc, 0, -1);
                }
                int[] subject = Units(input, "units"), replacement = Units(input, "replacementUnits");
                var capacity = input.GetProperty("outputCapacity").GetInt32();
                using var workspace = new Pcre2PreparedWorkspace(executor!, new(4096, 65536, 4096, 2048));
                Pcre2SubstituteResult result;
                int[]? outputUnits;
                if (profile.Width == Pcre2CodeUnitWidth.Eight)
                { var output = new byte[capacity]; result = workspace.Substitute(Bytes(subject), input.GetProperty("startAt").GetInt32(), Bytes(replacement), output, substituteFlags, options, bc, precomputed: pairs, precomputedResult: precomputed, pairCapacity: pairs.Length); outputUnits = result.NativeReturnCode < 0 ? null : U(output.AsSpan(0, result.OutputUnits)); }
                else
                { var output = new char[capacity]; result = workspace.Substitute(Chars(subject), input.GetProperty("startAt").GetInt32(), Chars(replacement), output, substituteFlags, options, cc, precomputed: pairs, precomputedResult: precomputed, pairCapacity: pairs.Length); outputUnits = result.NativeReturnCode < 0 ? null : V(output.AsSpan(0, result.OutputUnits)); }
                invocations++;
                Compare("substitute.returnCode", expected.GetProperty("returnCode"), result.NativeReturnCode);
                Compare("substitute.length", expected.GetProperty("outputLength"), result.OutputUnits >= 0 ? result.OutputUnits : result.RequiredCapacity == 0 ? null : result.RequiredCapacity);
                Compare("substitute.output", expected.GetProperty("outputUnits"), outputUnits);
                Compare("callout.count", calls.Length, ca);
                Compare("subcall.count", subs.Length, sa);
                Compare("case.count", caseInputs.Length, ka);
            }
            catch (Exception e) { differences.Add(new { id, field = "exception", error = e.ToString() }); }
        }
        File.WriteAllText(OutputPath("complete-substitute-comparison.json"), JsonSerializer.Serialize(new { invocations, comparisons, differences, ledger }, JsonOutputOptions));
        Assert.True(differences.Count == 0, $"{differences.Count} upstream substitution differences; see complete-substitute-comparison.json");
    }
}
