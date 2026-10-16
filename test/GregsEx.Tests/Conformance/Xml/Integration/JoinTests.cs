using System.Text.Json;
using System.Xml.Linq;
using GregsEx.Tests.Contracts.Xml;
using Xunit;

namespace GregsEx.Tests.Conformance.Xml.Integration;

public sealed class JoinTests
{
    [Fact]
    public void CompletePinnedJoinConsumesEveryOriginalRecipeWithoutNormalization()
    {
        var joined = JoinedCorpus.Read();
        Assert.Equal(5546, joined.Length);
        Assert.Equal(4811, joined.Count(e => e.Fixture.ProfileId == "Xsd11"));
        Assert.Equal(735, joined.Count(e => e.Fixture.ProfileId == "XPath31"));
        Assert.Equal(533, joined.Count(e => e.Fixture.Provenance.Kind == "Imported"));
        Assert.Contains(joined, e => e.Fixture.InputKind == "Utf8" && e.Fixture.InputUnits.Contains(0xc0));
        Assert.Contains(joined, e => e.Fixture.InputUnits.Contains(0xd800));
        var seeds = XmlCorpus.Read(File.ReadAllText(Path.Combine(JoinedCorpus.Root(), "test/GregsEx.Tests/Contracts/Xml/ContractVectors.json")));
        foreach (var c in seeds.Cases)
        {
            var retained = joined.Single(e => e.Fixture.Id == (c.ProfileId == "Xsd11" ? "SEED-" : "") + c.Id).Fixture with { Id = c.Id };
            Assert.Equal(JsonSerializer.Serialize(c, XmlCorpus.JsonOptions), JsonSerializer.Serialize(retained, XmlCorpus.JsonOptions));
        }
        Assert.Equal(2, joined.Single(e => e.Fixture.Id == "XML-C043").Fixture.Permitted.Length);
    }

    [Fact]
    public void IndexRejectsDuplicateMissingMisdirectedAndUnknownComparisonEntries()
    {
        var joined = JoinedCorpus.Read();
        var lanes = joined.GroupBy(e => e.Index.GetProperty("lane").GetString()!).ToDictionary(g => g.Key, g => g.Select(e => e.Fixture).ToArray());
        var entries = joined.Select(e => e.Index).ToArray();
        var duplicate = entries.ToArray();
        duplicate[1] = duplicate[0];
        Assert.Throws<InvalidDataException>(() => JoinedCorpus.Resolve(lanes, duplicate, 5546));
        Assert.Throws<InvalidDataException>(() => JoinedCorpus.Resolve(lanes, entries[..^1], 5546));
        foreach (var text in new[] {
            "{\"id\":\"wrong\",\"lane\":\"xsd\",\"ordinal\":0,\"oracleComparison\":\"agreement\"}",
            "{\"id\":\"wrong\",\"lane\":\"missing\",\"ordinal\":0,\"oracleComparison\":\"agreement\"}",
            "{\"id\":\"wrong\",\"lane\":\"xsd\",\"ordinal\":-1,\"oracleComparison\":\"agreement\"}",
            JsonSerializer.Serialize(new { id = entries[0].GetProperty("id").GetString(), lane = "xsd", ordinal = 0, oracleComparison = "product-pass" }) })
        {
            using var bad = JsonDocument.Parse(text);
            var altered = entries.ToArray();
            altered[0] = bad.RootElement;
            Assert.Throws<InvalidDataException>(() => JoinedCorpus.Resolve(lanes, altered, 5546));
        }
    }

    [Fact]
    public void AllCapabilityRolesJoinToTheirActualRecipes()
    {
        var cases = JoinedCorpus.Read().ToDictionary(e => e.Fixture.Id, e => e.Fixture);
        using var xsd = JoinedCorpus.Document("test/corpora/xml/xsd/coverage.json");
        Assert.Equal(12, xsd.RootElement.GetProperty("capabilities").EnumerateObject().Count());
        foreach (var capability in xsd.RootElement.GetProperty("capabilities").EnumerateObject())
        {
            foreach (var role in new[] { "positive", "negative", "boundary" })
            {
                var witnesses = capability.Value.EnumerateArray().Where(w => w.GetProperty("role").GetString() == role).ToArray();
                Assert.NotEmpty(witnesses);
                foreach (var w in witnesses)
                {
                    Assert.Contains(capability.Name, cases[w.GetProperty("id").GetString()!].Capabilities);
                }
            }
        }

        using var xpath = JoinedCorpus.Document("test/corpora/xml/xpath/inventory.json");
        Assert.Equal(17, xpath.RootElement.GetArrayLength());
        foreach (var capability in xpath.RootElement.EnumerateArray())
        {
            foreach (var role in new[] { "positive", "negative", "boundary" })
            {
                var witnesses = capability.GetProperty("witnesses").GetProperty(role).EnumerateArray().ToArray();
                Assert.NotEmpty(witnesses);
                foreach (var w in witnesses)
                {
                    Assert.Contains(capability.GetProperty("capability").GetString()!, cases[w.GetString()!].Capabilities);
                }
            }
        }
    }

    [Fact]
    public void DiagnosticPoliciesRetainTypedDomainsNativeUnitsBoundsAndExactSeeds()
    {
        var joined = JoinedCorpus.Read();
        using var xsd = JoinedCorpus.Document("test/corpora/xml/xsd/diagnostic-provenance.json");
        var xs = xsd.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        using var xpath = JoinedCorpus.Document("test/corpora/xml/xpath/annotations.json");
        foreach (var (c, index) in joined)
        {
            foreach (var outcome in c.Permitted)
            {
                if (outcome.Error is { } error)
                {
                    c.Validate(); // Includes code namespace, original domain, native units and argument bounds.
                    var policy = index.GetProperty("diagnosticPolicy").GetString();
                    Assert.False(string.IsNullOrWhiteSpace(policy));
                    if (c.ProfileId == "Xsd11")
                    {
                        Assert.Equal(xs[c.Id].GetProperty("coordinateAuthority").GetString(), policy);
                        Assert.False(xs[c.Id].GetProperty("oracleDerivedCoordinate").GetBoolean());
                        Assert.Equal(c.Id.StartsWith("SEED-", StringComparison.Ordinal), policy == "FrozenExactContract");
                    }
                    else
                    {
                        Assert.Equal(xpath.RootElement.GetProperty(c.Id).GetProperty("diagnosticPolicy").GetString(), policy);
                        Assert.Equal(c.Id.StartsWith("XML-C", StringComparison.Ordinal), policy == "exact-frozen-contract-span");
                        if (error.Code.StartsWith("FORX", StringComparison.Ordinal) && !c.Id.StartsWith("XML-C", StringComparison.Ordinal))
                        {
                            Assert.Contains("whole argument", policy);
                            Assert.Equal(0, error.Offset);
                            Assert.Equal(error.Location switch { "Flags" => c.Flags.Length, "Replacement" => c.ReplacementUnits!.Length, _ => c.PatternUnits.Length }, error.Length);
                        }
                    }
                }
            }
        }

        foreach (var code in new[] { "FORX0001", "FORX0002", "FORX0003", "FORX0004" })
        {
            Assert.Contains(joined, e => e.Fixture.Permitted.Any(p => p.Error?.Code == code && p.Error.Namespace == XmlRegexDiagnostic.ErrorNamespace));
        }
    }

    [Fact]
    public void ReviewedDifferencesRetainExactTargetAndOriginalProcessorObservations()
    {
        var joined = JoinedCorpus.Read().ToDictionary(e => e.Fixture.Id);
        using var xs = JoinedCorpus.Document("test/corpora/xml/xsd/disagreements.json");
        using var xr = JoinedCorpus.Document("test/corpora/xml/xsd/processor-results.json");
        var rows = xr.RootElement.GetProperty("results").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);
        Assert.Equal(49, xs.RootElement.GetProperty("cases").GetArrayLength());
        foreach (var d in xs.RootElement.GetProperty("cases").EnumerateArray())
        {
            var entry = joined[d.GetProperty("id").GetString()!];
            Assert.Equal("reviewed-difference", entry.Index.GetProperty("oracleComparison").GetString());
            using var expected = JsonDocument.Parse(JsonSerializer.Serialize(entry.Fixture.Permitted[0], XmlCorpus.JsonOptions));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, d.GetProperty("sourceExpected")));
            Assert.True(JsonElement.DeepEquals(rows[entry.Fixture.Id], d.GetProperty("oracleOutcome")));
            Assert.True(d.GetProperty("requiredOnProduct").GetBoolean());
        }
        using var xp = JoinedCorpus.Document("test/corpora/xml/xpath/dispositions.json");
        var raw = XDocument.Load(Path.Combine(JoinedCorpus.Root(), "test/corpora/xml/xpath/oracle/oracle-results.xml")).Root!.Elements().ToDictionary(e => (string)e.Attribute("id")!);
        Assert.Equal(11, xp.RootElement.EnumerateObject().Count());
        foreach (var d in xp.RootElement.EnumerateObject())
        {
            var entry = joined[d.Name];
            Assert.Equal("reviewed-difference", entry.Index.GetProperty("oracleComparison").GetString());
            using var expected = JsonDocument.Parse(JsonSerializer.Serialize(entry.Fixture.Permitted, XmlCorpus.JsonOptions));
            Assert.True(JsonElement.DeepEquals(expected.RootElement, d.Value.GetProperty("retainedExpectation")));
            var actual = raw[d.Name];
            using var observed = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                status = (string?)actual.Attribute("status"),
                error = (string?)actual.Attribute("error"),
                @namespace = (string?)actual.Attribute("namespace"),
                items = actual.Elements().Select(e => new { type = (string?)e.Attribute("type"), units = e.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray() }).ToArray()
            }));
            Assert.True(JsonElement.DeepEquals(observed.RootElement, d.Value.GetProperty("observed")));
        }
    }

    [Fact]
    public void OracleAccountingPreservesSourceOnlyAndOriginalQuerySkipBoundaries()
    {
        var joined = JoinedCorpus.Read();
        foreach (var (lane, agreement, difference, sourceOnly) in new[] { ("xsd", 4722, 49, 40), ("xpath", 712, 11, 12) })
        {
            var entries = joined.Where(e => e.Index.GetProperty("lane").GetString() == lane).ToArray();
            Assert.Equal(agreement, entries.Count(e => e.Index.GetProperty("oracleComparison").GetString() == "agreement"));
            Assert.Equal(difference, entries.Count(e => e.Index.GetProperty("oracleComparison").GetString() == "reviewed-difference"));
            Assert.Equal(sourceOnly, entries.Count(e => e.Index.GetProperty("oracleComparison").GetString() == "source-only"));
        }
        using var xsdResults = JoinedCorpus.Document("test/corpora/xml/xsd/processor-results.json");
        var xsRows = xsdResults.RootElement.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        using var annotations = JoinedCorpus.Document("test/corpora/xml/xpath/annotations.json");
        using var comparison = JoinedCorpus.Document("test/corpora/xml/xpath/oracle/comparison.json");
        var xpRows = comparison.RootElement.GetProperty("fixtures").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!);
        foreach (var (c, entry) in joined)
        {
            var category = entry.GetProperty("oracleComparison").GetString();
            if (c.ProfileId == "Xsd11")
            {
                var row = xsRows[c.Id];
                Assert.Equal(category == "source-only", row.GetProperty("outcome").GetString() == "not-applicable");
                if (category == "source-only")
                {
                    Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("reason").GetString()));
                }
                else
                {
                    var expected = c.Permitted[0];
                    var same = row.GetProperty("outcome").GetString() switch
                    {
                        "membership" => expected.Status is "Match" or "NoMatch" && row.GetProperty("boolean").GetBoolean() == expected.Boolean,
                        "invalid-pattern" => expected.Status == "InvalidSyntax",
                        "valid-pattern" => expected.Status == "ParseSuccess",
                        _ => false
                    };
                    Assert.Equal(category == "agreement", same);
                }
            }
            else
            {
                Assert.Equal(category == "source-only", annotations.RootElement.GetProperty(c.Id).GetProperty("oracle").ValueKind == JsonValueKind.Null);
                Assert.Equal(category switch { "agreement" => "passed", "reviewed-difference" => "disposition", _ => "source-only" }, xpRows[c.Id].GetProperty("outcome").GetString());
            }
        }
        using var manifest = JoinedCorpus.Document("test/corpora/xml/manifest.json");
        var qt = manifest.RootElement.GetProperty("originalQt3");
        var raw = XDocument.Load(Path.Combine(JoinedCorpus.Root(), "test/corpora/xml/xpath/oracle/oracle-results.xml")).Root!.Elements().Where(e => ((string)e.Attribute("id")!).StartsWith("QT3/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(1367, raw.Length);
        Assert.Equal(1355, raw.Count(e => (string?)e.Attribute("status") == "passed"));
        Assert.Equal(12, qt.GetProperty("skips").GetArrayLength());
        foreach (var skip in qt.GetProperty("skips").EnumerateArray())
        {
            var actual = raw.Single(e => (string?)e.Attribute("id") == skip.GetProperty("id").GetString());
            Assert.Equal("skipped", (string?)actual.Attribute("status"));
            Assert.Equal(skip.GetProperty("reason").GetString(), (string?)actual.Attribute("reason"));
            Assert.False(string.IsNullOrWhiteSpace(skip.GetProperty("reason").GetString()));
        }
        Assert.Equal(509, qt.GetProperty("normalizedImportedFixtures").GetInt32());
        Assert.Contains("overlap", qt.GetProperty("accounting").GetString());
    }
}
