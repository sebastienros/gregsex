using System.Text;
using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

public class Pcre2NativeHostTests
{
    private static string Corpus => Path.Combine(
        GregsEx.Tests.Conformance.Pcre2.IndependentParityTests.Root, "test/corpora/pcre2/host");
    public static IEnumerable<TheoryDataRow<string, int>> Cases()
    {
        var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus, "native-inputs.json"))).RootElement;
        foreach (var item in inputs.EnumerateArray())
        {
            foreach (var width in new[] { 8, 16 })
            {
                yield return new TheoryDataRow<string, int>(item.GetProperty("id").GetString()!, width);
            }
        }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void FiniteIndependentNativeSubstitutionObservations(string id, int width)
    {
        var input = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus, "native-inputs.json"))).RootElement.EnumerateArray().First(c => c.GetProperty("id").GetString() == id);
        var events = File.ReadLines(Path.Combine(Corpus, "native-probes.jsonl")).Select(s => JsonDocument.Parse(s).RootElement.Clone()).Where(c => c.GetProperty("id").GetString() == id && c.GetProperty("width").GetInt32() == width).ToArray();
        var expected = events.Last();
        string pattern = input.GetProperty("pattern").GetString()!, subject = input.GetProperty("subject").GetString()!, replacement = input.GetProperty("replacement").GetString()!;
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, (Pcre2CompileFlags)input.GetProperty("compile").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extra").GetUInt32());
        var fixture = new HostFixture(pattern, profile);
        var flags = (Pcre2SubstituteFlags)input.GetProperty("flags").GetUInt32();
        int capacity = input.GetProperty("capacity").GetInt32(), pairCount = input.GetProperty("pairs").GetInt32(), start = input.GetProperty("start").GetInt32();
        int subret = input.GetProperty("sub").GetInt32(), caseret = input.GetProperty("case").GetInt32(), subcalls = 0;
        var casetrace = new List<(int Mode, int Capacity, int[] Input)>();
        var precomputed = pairCount > 0 ? new Pcre2OffsetPair[pairCount] : [];
        Pcre2MatchResult precomputedResult = default;
        Pcre2SubstituteResult result;
        int[] output;
        if (width == 16)
        {
            var callbacks = new Pcre2CharCallbacks(Substitute: subret == 999 ? null : (in Pcre2CharSubstituteContext c, object? _) => { subcalls++; return subret; },
                Case: caseret == 0 ? null : (ReadOnlySpan<char> text, Span<char> target, Pcre2SubstituteCase mode, object? _) =>
                {
                    casetrace.Add(((int)mode, target.Length, text.ToArray().Select(c => (int)c).ToArray()));
                    if (caseret == 2)
                    {
                        return -1;
                    }

                    if (caseret == 3)
                    { if (!target.IsEmpty) { target[0] = '!'; } return 1; }
                    if (caseret == 4)
                    { target[..Math.Min(target.Length, text.Length * 2)].Fill('X'); return text.Length * 2; }
                    text[..Math.Min(text.Length, target.Length)].CopyTo(target);
                    return text.Length;
                });
            if (pairCount > 0)
            { var state = fixture.State(); precomputedResult = fixture.Match(subject, start, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, precomputed); flags |= Pcre2SubstituteFlags.Matched; }
            var target = Enumerable.Repeat('U', capacity).ToArray();
            result = fixture.Substitute(subject, replacement, target, flags, callbacks: callbacks, start: start, precomputed: precomputed, result: precomputedResult);
            output = result.NativeReturnCode >= 0 ? [.. target[..result.OutputUnits].Select(c => (int)c)] : [];
        }
        else
        {
            var encoding = profile.Utf ? Encoding.UTF8 : Encoding.Latin1;
            byte[] nativeSubject = encoding.GetBytes(subject), nativeReplacement = encoding.GetBytes(replacement);
            var callbacks = new Pcre2ByteCallbacks(Substitute: subret == 999 ? null : (in Pcre2ByteSubstituteContext c, object? _) => { subcalls++; return subret; },
                Case: caseret == 0 ? null : (ReadOnlySpan<byte> text, Span<byte> target, Pcre2SubstituteCase mode, object? _) =>
                {
                    casetrace.Add(((int)mode, target.Length, text.ToArray().Select(c => (int)c).ToArray()));
                    if (caseret == 2)
                    {
                        return -1;
                    }

                    if (caseret == 3)
                    { if (!target.IsEmpty) { target[0] = (byte)'!'; } return 1; }
                    if (caseret == 4)
                    { target[..Math.Min(target.Length, text.Length * 2)].Fill((byte)'X'); return text.Length * 2; }
                    text[..Math.Min(text.Length, target.Length)].CopyTo(target);
                    return text.Length;
                });
            if (pairCount > 0)
            { var state = fixture.State(); precomputedResult = fixture.Match(nativeSubject, start, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, precomputed); flags |= Pcre2SubstituteFlags.Matched; }
            var target = Enumerable.Repeat((byte)'U', capacity).ToArray();
            result = fixture.Substitute(nativeSubject, nativeReplacement, target, flags, callbacks: callbacks, start: start, precomputed: precomputed, result: precomputedResult);
            output = result.NativeReturnCode >= 0 ? [.. target[..result.OutputUnits].Select(c => (int)c)] : [];
        }
        Assert.Equal(expected.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
        var reported = expected.GetProperty("reportedLength").GetInt64();
        Assert.Equal(reported, result.NativeReturnCode == (int)Pcre2ErrorCode.Nomemory && (flags & Pcre2SubstituteFlags.OverflowLength) != 0 ? result.RequiredCapacity : result.OutputUnits);
        Assert.Equal(expected.GetProperty("output").EnumerateArray().Select(c => c.GetInt32()).ToArray(), output);
        Assert.Equal(expected.GetProperty("subCalls").GetInt32(), subcalls);
        var nativeCases = events.Where(e => e.GetProperty("event").GetString() == "case").ToArray();
        Assert.Equal(nativeCases.Length, casetrace.Count);
        for (var i = 0; i < nativeCases.Length; i++)
        {
            Assert.Equal(nativeCases[i].GetProperty("mode").GetInt32(), casetrace[i].Mode);
            Assert.Equal(nativeCases[i].GetProperty("capacity").GetInt32(), casetrace[i].Capacity);
            Assert.Equal(nativeCases[i].GetProperty("input").EnumerateArray().Select(c => c.GetInt32()).ToArray(), casetrace[i].Input);
        }
    }
    [Fact]
    public void NativeMetadataQueriesUseVerifiedSourceProvenance()
    {
        var descriptors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus, "metadata-inputs.json"))).RootElement;
        var records = File.ReadLines(Path.Combine(Corpus, "native-metadata.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        foreach (var descriptor in descriptors.EnumerateArray())
        {
            foreach (var width in new[] { 8, 16 })
            {
                var id = descriptor.GetProperty("id").GetString()!;
                var fixture = new HostFixture(descriptor.GetProperty("pattern").GetString()!, new((Pcre2CodeUnitWidth)width, (Pcre2CompileFlags)descriptor.GetProperty("flags").GetUInt32()));
                var budget = new GregsEx.Contracts.CompileBudget(new());
                Assert.True(GregsEx.Flavors.Pcre2.Runtime.Host.Pcre2HostMetadata.TryCreate(fixture.Program, ref budget, out var metadata, out _));
                foreach (var record in records.Where(r => r.GetProperty("id").GetString() == id && r.GetProperty("width").GetInt32() == width))
                {
                    var key = (Pcre2PatternInfoKey)record.GetProperty("key").GetInt32();
                    var ok = metadata!.TryGetInfo(key, out var value, out var diagnostic);
                    var rc = record.GetProperty("returnCode").GetInt32();
                    Assert.True(ok == (rc == 0), $"{id}/{width}/{key}: native rc{rc}, managed {diagnostic}");
                    if (ok)
                    {
                        Assert.True(value == record.GetProperty("value").GetInt64(), $"{id}/{width}/{key}: expected {record.GetProperty("value")}, got {value}");
                    }
                    else
                    {
                        Assert.Equal(rc, (int)diagnostic.SourceError);
                    }
                }
            }
        }
    }

}
