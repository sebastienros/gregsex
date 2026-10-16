using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;
using Xunit;

namespace GregsEx.Tests.Conformance.Xml.Xsd;

// These execute interchange/provenance joins, never masquerade as product matcher tests.
public sealed class IntakeTests
{
    private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../../.."));
    private static string Data => Path.Combine(Root(), "test/corpora/xml/xsd");
    private static XmlCorpus Read() => XmlCorpus.Read(File.ReadAllText(Path.Combine(Data, "corpus.json")));
    [Fact]
    public void NativeCorpusUsesFrozenInterchangeAndWholeValueGroupZero()
    {
        var corpus = Read();
        Assert.True(corpus.Cases.Length > 4600);
        Assert.All(corpus.Cases, c => Assert.Equal("Xsd11", c.ProfileId));
        foreach (var c in corpus.Cases)
        {
            foreach (var e in c.Permitted)
            {
                foreach (var match in e.Matches)
                {
                    Assert.Equal(new RegexMatchRange(0, c.InputUnits.Length), match.Range);
                    Assert.Single(match.Captures);
                }
            }
        }

        Assert.Contains(corpus.Cases, c => c.InputKind == "Utf8" && c.InputUnits.SequenceEqual([240, 159, 152, 128]));
        Assert.Contains(corpus.Cases, c => c.InputUnits.Contains(0xD800));
        Assert.Contains(corpus.Cases, c => c.PatternKind == "Bytes");
    }
    [Fact]
    public void EveryCapabilityHasPositiveNegativeAndBoundaryWitnesses()
    {
        var corpus = Read();
        var ids = corpus.Cases.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "coverage.json")));
        var coverage = doc.RootElement.GetProperty("capabilities");
        Assert.Equal(12, coverage.EnumerateObject().Count());
        foreach (var cap in coverage.EnumerateObject())
        {
            var roles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in cap.Value.EnumerateArray())
            {
                var id = w.GetProperty("id").GetString()!;
                Assert.Contains(id, ids);
                Assert.Contains(cap.Name, corpus.Cases.Single(c => c.Id == id).Capabilities);
                roles.Add(w.GetProperty("role").GetString()!);
            }
            Assert.Contains("positive", roles);
            Assert.Contains("negative", roles);
            Assert.Contains("boundary", roles);
        }
        Assert.Equal(35, doc.RootElement.GetProperty("productionWitnesses").EnumerateObject().Count());
        Assert.Equal(36, doc.RootElement.GetProperty("categoryNames").GetArrayLength());
        Assert.Equal(341, doc.RootElement.GetProperty("blockNames").GetArrayLength());
    }
    [Fact]
    public void ActualProcessorHasXsd11QualificationAndIndependentHostIsolation()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "processor-results.json")));
        var root = doc.RootElement;
        Assert.True(root.GetProperty("xsd11AssertionQualification").GetBoolean());
        Assert.Equal("16.0.0", root.GetProperty("processorUnicode").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Data, "corpus.json")))), root.GetProperty("corpusSha256").GetString());
        var host = root.GetProperty("hostAudit");
        Assert.Equal(new[] { true, false }, host.GetProperty("stringPreservesWhitespace").EnumerateArray().Select(x => x.GetBoolean()));
        Assert.Equal(new[] { true, true }, host.GetProperty("tokenCollapsesWhitespace").EnumerateArray().Select(x => x.GetBoolean()));
        Assert.Equal(new[] { true, false }, host.GetProperty("literalEntityText").EnumerateArray().Select(x => x.GetBoolean()));
    }
    [Fact]
    public void EveryMismatchIsReducedAndDisposedWithoutChangingGoldenResults()
    {
        var corpus = Read();
        using var outputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "processor-results.json")));
        using var dispositions = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "disagreements.json")));
        var cases = corpus.Cases.ToDictionary(c => c.Id);
        var ds = dispositions.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(d => d.GetProperty("id").GetString()!);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mismatches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in outputs.RootElement.GetProperty("results").EnumerateArray())
        {
            var id = row.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id));
            var c = cases[id];
            var e = c.Permitted[0];
            var outcome = row.GetProperty("outcome").GetString();
            if (outcome == "not-applicable")
            { Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("reason").GetString())); continue; }
            var same = outcome switch
            {
                "membership" => e.Status is "Match" or "NoMatch" && row.GetProperty("boolean").GetBoolean() == e.Boolean,
                "invalid-pattern" => e.Status == "InvalidSyntax",
                "valid-pattern" => e.Status == "ParseSuccess",
                _ => false
            };
            if (same)
            {
                continue;
            }

            mismatches.Add(id);
            Assert.True(ds.TryGetValue(id, out var disposition), id);
            Assert.False(string.IsNullOrWhiteSpace(disposition.GetProperty("resolution").GetString()));
            Assert.True(JsonElement.DeepEquals(row, disposition.GetProperty("oracleOutcome")));
        }
        Assert.Equal(cases.Count, ids.Count);
        Assert.Equal(ds.Keys.Order(), mismatches.Order());
    }
    [Fact]
    public void FrozenSeedCoordinatesStayExactAndNewWitnessesDoNotClaimOracleOffsets()
    {
        var corpus = Read();
        var original = XmlCorpus.Read(File.ReadAllText(Path.Combine(Root(), "test/GregsEx.Tests/Contracts/Xml/ContractVectors.json")));
        foreach (var seed in original.Cases.Where(c => c.ProfileId == "Xsd11"))
        {
            var retained = corpus.Cases.Single(c => c.Id == "SEED-" + seed.Id) with { Id = seed.Id };
            using var a = JsonDocument.Parse(JsonSerializer.Serialize(seed, XmlCorpus.JsonOptions));
            using var b = JsonDocument.Parse(JsonSerializer.Serialize(retained, XmlCorpus.JsonOptions));
            Assert.True(JsonElement.DeepEquals(a.RootElement, b.RootElement));
        }
        using var annotations = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "diagnostic-provenance.json")));
        foreach (var annotation in annotations.RootElement.GetProperty("cases").EnumerateArray())
        {
            Assert.False(annotation.GetProperty("oracleDerivedCoordinate").GetBoolean());
            var id = annotation.GetProperty("id").GetString()!;
            var fixture = corpus.Cases.Single(c => c.Id == id);
            var error = fixture.Permitted[0].Error!;
            Assert.Equal(error.OffsetUnit, annotation.GetProperty("offsetUnit").GetString());
            Assert.Equal(id.StartsWith("SEED-", StringComparison.Ordinal), annotation.GetProperty("coordinateAuthority").GetString() == "FrozenExactContract");
        }
        var badUtf8 = corpus.Cases.Single(c => c.Id == "XSD-domain-Utf8-0");
        Assert.Equal(new[] { 97, 0 }, badUtf8.InputUnits);
        Assert.Equal(1, badUtf8.Permitted[0].Error!.Offset);
        Assert.Equal("Bytes", badUtf8.Permitted[0].Error!.OffsetUnit);
        var supplementary = corpus.Cases.Single(c => c.Id == "XSD-native-success-utf16");
        Assert.Equal(new[] { 0xD83D, 0xDE00 }, supplementary.InputUnits);
        Assert.Equal(new RegexMatchRange(0, 2), supplementary.Permitted[0].Matches[0].Range);
    }
    [Fact]
    public void PinnedIndependentArtifactsAndLicensedUnicodeInputsMatchDigests()
    {
        using var pin = JsonDocument.Parse(File.ReadAllText(Path.Combine(Data, "processor-pin.json")));
        foreach (var wheel in pin.RootElement.GetProperty("wheels").EnumerateArray())
        {
            var path = Path.Combine(Root(), "tools/oracles/xml/xsd/vendor", wheel.GetProperty("file").GetString()!);
            Assert.Equal(wheel.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
            Assert.Equal(wheel.GetProperty("bytes").GetInt64(), new FileInfo(path).Length);
        }
        var source = pin.RootElement.GetProperty("independentTestSource");
        Assert.Equal(source.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Root(), source.GetProperty("file").GetString()!)))));
        Assert.Equal("ff58e5823bd095166564a006e47d111130813dcf8bf234ef79fa51a870edb48f", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Data, "inputs/UnicodeData.txt")))));
        Assert.Equal("f3907b395d410f1b97342292ca6bc83dd12eb4b205f2a0c48efdef99e517d7b0", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Data, "inputs/Blocks.txt")))));
    }
}
