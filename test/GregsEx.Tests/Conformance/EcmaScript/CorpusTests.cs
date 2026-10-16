using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GregsEx.Tests.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Conformance.EcmaScript;

// These tests validate corpus provenance independently of product execution.
public sealed class CorpusTests
{
    private static string Root([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../.."));
    private static string CorpusPath(string name) => Path.Combine(Root(), "test/corpora/ecmascript", name);
    private static EcmaCorpus Read(string name) => EcmaCorpus.Read(File.ReadAllText(CorpusPath(name)));

    [Theory]
    [InlineData("oracle-corpus.json", 939)]
    [InlineData("imported-corpus.json", 1381)]
    [InlineData("contract-corpus.json", 27)]
    [InlineData("resource-corpus.json", 10)]
    public void FrozenCorporaLoadWithExactNativeUnitAndNullableOutputContracts(string name, int expected)
    {
        var corpus = Read(name);
        Assert.Equal(expected, corpus.Cases.Length);
        var roundTrip = EcmaCorpus.Read(JsonSerializer.Serialize(corpus, EcmaCorpus.JsonOptions));
        Assert.Equal(corpus.Cases.Length, roundTrip.Cases.Length);
        Assert.Equal(JsonSerializer.Serialize(corpus, EcmaCorpus.JsonOptions),
            JsonSerializer.Serialize(roundTrip, EcmaCorpus.JsonOptions));
        Assert.All(corpus.Cases, fixture => Assert.NotEmpty(fixture.Provenance.Clause));
    }

    [Fact]
    public void EverySourceAdapterCorpusAndUnicodeArtifactHasItsFrozenIdentity()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(CorpusPath("artifacts.json")));
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.True(files.Length > 35);
        foreach (var item in files)
        {
            Verify(Path.Combine(Root(), item.GetProperty("path").GetString()!), item);
        }
    }

    [Fact]
    public void EveryImportedSourceHarnessAndLicenseMatchesItsActualPinnedCommitManifest()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(CorpusPath("test262-intake.json")));
        Assert.Equal("6a73fa8f114611269ce4442e8f19a31239cb3827", manifest.RootElement.GetProperty("commit").GetString());
        var files = manifest.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(2711, files.Length);
        var source = Path.Combine(Root(), "tools/oracles/ecmascript/test262");
        foreach (var item in files)
        {
            Assert.NotEmpty(item.GetProperty("copyright").GetString()!);
            Verify(Path.Combine(source, item.GetProperty("path").GetString()!), item);
        }
        foreach (var item in manifest.RootElement.GetProperty("harness").EnumerateArray())
        {
            Verify(Path.Combine(source, item.GetProperty("path").GetString()!), item);
        }

        Verify(Path.Combine(source, "LICENSE"), manifest.RootElement.GetProperty("license"));
    }

    [Fact]
    public void OracleCorpusKeepsAbsentEmptyNamesIndicesLoneSurrogatesAndAllTypedOperations()
    {
        var cases = Read("oracle-corpus.json").Cases;
        Assert.Contains(cases, c => c.InputUnits.Contains(0xd800));
        Assert.Contains(cases, c => c.Expected.Matches.Any(m => m.Captures.Any(x => !x.Participated)));
        Assert.Contains(cases, c => c.Expected.Matches.Any(m => m.Captures.Any(x => x.Participated && x.Length == 0)));
        Assert.Contains(cases, c => c.Expected.Matches.Any(m => m.Names.Any(n => n.GroupIds.Length == 2)));
        Assert.Contains(cases, c => c.Expected.Matches.Any(m => m.HasIndices));
        Assert.Contains(cases, c => c.Expected.Texts is { } texts && texts.Contains(null));
        foreach (var operation in new[] { "Exec", "Test", "Match", "MatchAll", "Search", "Replace", "ReplaceAll", "Split" })
        {
            Assert.Contains(cases, c => c.Operation == operation);
        }

        var inside = cases.Single(c => c.Id == "ES-R-inside-pair-u");
        Assert.Equal([55357, 56832], inside.InputUnits);
        Assert.Equal("1", inside.LastIndexBefore);
        Assert.Equal("2", inside.Expected.LastIndexAfter);
        Assert.Equal(new EcmaCapture(true, 0, 2), inside.Expected.Matches[0].Captures[0]);
    }

    [Fact]
    public void RequirementInventoryRetainsAllTwentyRowsAndExplicitProductBindingObligations()
    {
        using var coverage = JsonDocument.Parse(File.ReadAllText(CorpusPath("coverage.json")));
        var rows = coverage.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
        Assert.Equal(20, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            Assert.Equal($"ES-{i + 1:000}", rows[i].GetProperty("requirementId").GetString());
            Assert.Contains("requires actual parser/VM/host execution", rows[i].GetProperty("productExecution").GetString()!);
            if (i < 18)
            {
                foreach (var kind in new[] { "positiveCases", "negativeCases", "boundaryCases" })
                {
                    Assert.NotEqual(0, rows[i].GetProperty(kind).GetArrayLength());
                }
            }
            else
            {
                Assert.NotEqual(0, rows[i].GetProperty("portableResources").GetArrayLength());
            }
        }
    }

    [Fact]
    public void EveryRetainedOracleFailureHasAnExactExistingObjectBoundaryDisposition()
    {
        using var results = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "test/corpora/ecmascript/oracle/test262-recheck-results.json")));
        using var dispositions = JsonDocument.Parse(File.ReadAllText(CorpusPath("test262-dispositions.json")));
        var rows = dispositions.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
            x => x.GetProperty("path").GetString()!, StringComparer.Ordinal);
        Assert.Equal(2711, rows.Count);
        var failures = results.RootElement.GetProperty("results").EnumerateArray().Where(x => x.GetProperty("outcome").GetString() == "failed").ToArray();
        Assert.Equal(103, failures.Length);
        foreach (var failure in failures)
        {
            var row = rows[failure.GetProperty("path").GetString()!];
            Assert.Equal("excluded-javascript-object-boundary", row.GetProperty("productDisposition").GetString());
            Assert.NotNull(row.GetProperty("rule").GetString());
            Assert.NotEmpty(row.GetProperty("reason").GetString()!);
        }
    }

    [Theory]
    [InlineData("missing-nullable")]
    [InlineData("unknown-field")]
    [InlineData("version")]
    [InlineData("units")]
    [InlineData("duplicate-id")]
    [InlineData("absent-range")]
    [InlineData("ambiguous-name")]
    [InlineData("missing-import-oracle")]
    public void NegativeMutationsCannotSilentlyAlterCorpusMeaning(string mutation)
    {
        var node = JsonNode.Parse(File.ReadAllText(CorpusPath("imported-corpus.json")))!;
        var cases = node["cases"]!.AsArray();
        var fixture = cases[0]!;
        switch (mutation)
        {
            case "missing-nullable":
                fixture.AsObject().Remove("replacementUnits");
                break;
            case "unknown-field":
                fixture["futureField"] = true;
                break;
            case "version":
                node["contractVersion"] = "ecmascript-fixtures-2";
                break;
            case "units":
                fixture["inputUnits"] = new JsonArray(65536);
                break;
            case "duplicate-id":
                cases.Add(fixture.DeepClone());
                break;
            case "absent-range":
                var capture = fixture["expected"]!["matches"]![0]!["captures"]![0]!;
                capture["participated"] = false;
                capture["index"] = 1;
                break;
            case "ambiguous-name":
                var namesCase = Read("oracle-corpus.json").Cases.First(c => c.Expected.Matches.Any(m => m.Names.Length > 0));
                fixture = JsonNode.Parse(JsonSerializer.Serialize(namesCase, EcmaCorpus.JsonOptions))!;
                fixture["expected"]!["matches"]![0]!["names"]![0]!["groupIds"] = new JsonArray(1, 1);
                cases[0] = fixture;
                break;
            case "missing-import-oracle":
                fixture["provenance"]!["oracleId"] = null;
                break;
        }
        Assert.ThrowsAny<Exception>(() => EcmaCorpus.Read(node.ToJsonString()));
    }

    [Fact]
    public void PortableLimitsNeverPretendToReproduceAnExternalInstructionCounter()
    {
        var cases = Read("resource-corpus.json").Cases;
        Assert.All(cases.Where(c => c.Expected.Status != "Match"), c =>
        {
            Assert.NotNull(c.Limits);
            Assert.NotNull(c.Expected.Error);
            Assert.Null(c.Expected.Error.WorkConsumed);
            Assert.Null(c.Provenance.OracleId);
        });
        Assert.Contains(cases, c => c.Expected.Status == "Cancelled");
        Assert.Contains(cases, c => c.Expected.Status == "TimedOut");
        Assert.Contains(cases, c => c.Expected.Error!.Code == "OutputLimitExceeded");
    }

    [Fact]
    public void UnpairedFlagBoundaryUsesExactUnitsWithoutChangingTheFrozenFixtureSchema()
    {
        using var input = JsonDocument.Parse(File.ReadAllText(CorpusPath("raw-flag-boundary.json")));
        var units = input.RootElement.GetProperty("rawFlagUnits").EnumerateArray().Select(x => (char)x.GetInt32()).ToArray();
        Assert.Equal(new[] { '\ud800' }, units);
        Assert.False(EcmaScriptProfile.TryParseFlags(units, out var profile, out var error));
        Assert.Null(profile);
        Assert.Equal("ES-001", error.FeatureId);
        Assert.Equal(EcmaScriptDiagnosticLocation.Flags, error.Location);
        Assert.Equal(RegexDiagnosticCode.InvalidSyntax, error.Diagnostic.Code);
        Assert.Equal(0, error.Diagnostic.Offset);
        Assert.Equal(1, error.Diagnostic.Length);
    }

    private static void Verify(string path, JsonElement expected)
    {
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(expected.GetProperty("length").GetInt64(), bytes.LongLength);
        Assert.Equal(expected.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }
}
