using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;
using Xunit;

namespace GregsEx.Tests.Conformance.Xml.XPath;

public sealed class CorpusTests
{
    private static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../../.."));
    private static string CorpusDirectory => Path.Combine(Root(), "test/corpora/xml/xpath");
    private static XmlCorpus Read() => XmlCorpus.Read(File.ReadAllText(Path.Combine(CorpusDirectory, "Corpus.json")));

    [Fact]
    public void FullFrozenInterchangeHasIndependentSourcesAndEveryInventoryWitness()
    {
        var corpus = Read();
        Assert.Equal(735, corpus.Cases.Length);
        using var annotations = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDirectory, "annotations.json")));
        for (var i = 1; i <= 17; i++)
        {
            var capability = $"XP-{i:D3}";
            foreach (var role in new[] { "positive", "negative", "boundary" })
            {
                Assert.Contains(corpus.Cases, c => c.Capabilities.Contains(capability) &&
                    annotations.RootElement.GetProperty(c.Id).GetProperty("role").GetString() == role);
            }
        }
        Assert.Equal(509, corpus.Cases.Count(c => c.Provenance.Kind == "Imported"));
        Assert.All(corpus.Cases, c => Assert.Equal("XPath31", c.ProfileId));
        Assert.Contains(corpus.Cases, c => c.AbsentInput && c.Operation == "Matches");
        Assert.Contains(corpus.Cases, c => c.InputKind == "Utf8" && c.Permitted.Any(e => e.Matches.Any(m => m.Range.Length == 4)));
        Assert.Contains(corpus.Cases, c => c.Permitted.Length == 2);
        Assert.Contains(corpus.Cases, c => c.Permitted.Any(e => e.Tree.Any(n => n.Kind == XPathAnalyzeNodeKind.Group && n.ParentIndex == 2)));
        foreach (var code in new[] { "FORX0001", "FORX0002", "FORX0003", "FORX0004" })
        {
            Assert.Contains(corpus.Cases, c => c.Permitted.Any(e => e.Error?.Code == code && e.Error.Namespace == XmlRegexDiagnostic.ErrorNamespace));
        }

        foreach (var op in new[] { "Matches", "Replace", "Tokenize", "TokenizeOne", "AnalyzeString", "FindCaptures" })
        {
            Assert.Contains(corpus.Cases, c => c.Operation == op);
        }
    }

    [Fact]
    public void SourceLockPreservesOriginalLicensedQt3AndUnicodeBytes()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDirectory, "source-lock.json")));
        foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(CorpusDirectory, file.GetProperty("path").GetString()!));
            Assert.Equal(file.GetProperty("bytes").GetInt64(), bytes.LongLength);
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    [Fact]
    public void EveryDispositionRetainsItsTargetOutcomeAndExactProcessorObservation()
    {
        var corpus = Read();
        using var dispositions = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDirectory, "dispositions.json")));
        Assert.Equal(11, dispositions.RootElement.EnumerateObject().Count());
        foreach (var entry in dispositions.RootElement.EnumerateObject())
        {
            var fixture = corpus.Cases.Single(c => c.Id == entry.Name);
            var retained = JsonSerializer.Deserialize<XmlExpected[]>(entry.Value.GetProperty("retainedExpectation").GetRawText(), XmlCorpus.JsonOptions)!;
            Assert.Equal(JsonSerializer.Serialize(fixture.Permitted, XmlCorpus.JsonOptions), JsonSerializer.Serialize(retained, XmlCorpus.JsonOptions));
            Assert.False(string.IsNullOrWhiteSpace(entry.Value.GetProperty("reason").GetString()));
            Assert.True(entry.Value.TryGetProperty("observed", out _));
        }
    }

    [Fact]
    public void DiagnosticSidecarSeparatesExactSpansFromProcessorArgumentEnvelopes()
    {
        using var annotations = JsonDocument.Parse(File.ReadAllText(Path.Combine(CorpusDirectory, "annotations.json")));
        foreach (var c in Read().Cases)
        {
            foreach (var e in c.Permitted)
            {
                if (e.Error is { } error)
                {
                    var policy = annotations.RootElement.GetProperty(c.Id).GetProperty("diagnosticPolicy").GetString()!;
                    if (c.Id.StartsWith("XML-C", StringComparison.Ordinal))
                    {
                        Assert.Equal("exact-frozen-contract-span", policy);
                    }
                    else if (error.Code.StartsWith("FORX", StringComparison.Ordinal))
                    {
                        Assert.Contains("whole argument", policy);
                        Assert.Equal(0, error.Offset);
                        Assert.Equal(error.Location switch
                        {
                            "Flags" => c.Flags.Length,
                            "Replacement" => c.ReplacementUnits!.Length,
                            _ => c.PatternUnits.Length
                        }, error.Length);
                    }
                }
            }
        }
    }

    [Fact]
    public void MalformedNativeUnitsAndBudgetPoliciesRemainDistinctFromOraclePasses()
    {
        var corpus = Read();
        Assert.Contains(corpus.Cases, c => c.InputKind == "Utf8" && c.InputUnits.SequenceEqual([0xc0, 0xaf]) && c.Permitted[0].Error?.OffsetUnit == "Bytes");
        Assert.Contains(corpus.Cases, c => c.InputUnits.Contains(0xd800));
        Assert.Contains(corpus.Cases, c => c.Permitted[0].Error?.Code == "InvalidInputDomain");
        Assert.Contains(corpus.Cases, c => c.Limits?.MaxResults == 1 && c.Permitted[0].Status == "HostLimitExceeded");
        Assert.Equal(36, corpus.Cases.Count(c => c.Id.StartsWith("XML-C", StringComparison.Ordinal)));
        var seed = XmlCorpus.Read(File.ReadAllText(Path.Combine(Root(), "test/GregsEx.Tests/Contracts/Xml/ContractVectors.json")));
        foreach (var c in seed.Cases.Where(c => c.ProfileId == "XPath31"))
        {
            Assert.Equal(JsonSerializer.Serialize(c, XmlCorpus.JsonOptions),
                JsonSerializer.Serialize(corpus.Cases.Single(x => x.Id == c.Id), XmlCorpus.JsonOptions));
        }
    }
}
