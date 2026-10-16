using System.Text.Json;
using GregsEx.Tests.Contracts.Posix;
using Xunit;

namespace GregsEx.Tests.Conformance.Posix;

public class ConformanceArtifactTests
{
    [Fact]
    public void EveryImmutableSeedAndExpandedRecipeIsLoadedWithoutSkips()
    {
        var all = PosixConformanceRunner.ReadAll();
        Assert.Equal(7265, all.Length);
        var seeds = PosixCorpus.Read(File.ReadAllText(Path.Combine(PosixConformanceRunner.Root(), "test/GregsEx.Tests/Contracts/Posix/ContractVectors.json"))).Cases;
        Assert.Equal(138, seeds.Length);
        for (var i = 0; i < seeds.Length; i++)
        {
            Assert.Equal(JsonSerializer.Serialize(seeds[i], PosixCorpus.JsonOptions), JsonSerializer.Serialize(all[i], PosixCorpus.JsonOptions));
        }

        for (var n = 1; n <= 20; n++)
        {
            Assert.Contains(all, c => c.Capabilities.Contains($"PX-{n:D3}"));
        }

        Assert.All(all, c => c.Validate());
    }

    [Fact]
    public void BothGrammarsCoverAllNonNulByteClassRecipes()
    {
        var all = PosixConformanceRunner.ReadAll();
        var classes = all.Where(c => c.Id.StartsWith("POSIX-E-class-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2 * 12 * 255, classes.Length);
        foreach (var dialect in new[] { "PosixBre2018-CByte", "PosixEre2018-CByte" })
        {
            for (var b = 1; b <= 255; b++)
            {
                Assert.Equal(12, classes.Count(c => c.ProfileId == dialect && c.InputUnits.SequenceEqual([b])));
            }
        }

        Assert.All(classes.Where(c => c.InputUnits[0] >= 128), c => Assert.Equal("GregsEx", c.Classification));
    }

    [Fact]
    public void PartialNativeAndNoSubRequestsRetainFullIndependentSelectionExpectations()
    {
        var all = PosixConformanceRunner.ReadAll();
        foreach (var n in new[] { 0, 1, 2, 3, 5 })
        {
            var c = all.Single(c => c.Id == $"POSIX-E-nmatch-choice-reversed-{n}");
            Assert.Equal(n, c.DestinationLength);
            var e = c.Permitted[0];
            Assert.Equal(n == 0, e.DestinationUnchanged);
            if (n >= 2)
            {
                Assert.Equal(new PosixMatch(0, 2), e.NativeMatches[1]);
            }

            if (n >= 3)
            {
                Assert.Equal(new PosixMatch(2, 2), e.NativeMatches[2]);
            }

            Assert.True(all.Single(c => c.Id == $"POSIX-E-nosub-choice-reversed-{n}").Permitted[0].DestinationUnchanged);
        }
    }

    [Fact]
    public void OutcomeComparisonRejectsParticipationAndEveryPublishedEndpointDifference()
    {
        var c = PosixConformanceRunner.ReadAll().Single(c => c.Id == "POSIX-C002");
        var expected = c.Permitted[0];
        Assert.True(PosixConformanceRunner.Equivalent(expected, expected with { GroupCount = 3 }));
        Assert.False(PosixConformanceRunner.Equivalent(expected, expected with { Status = "NoMatch" }));
        var captures = expected.Matches[0].Captures.ToArray();
        captures[1] = new(true, 0, 1);
        captures[2] = new(true, 1, 1);
        Assert.False(PosixConformanceRunner.Equivalent(expected, expected with { Matches = [expected.Matches[0] with { Captures = captures }] }));
        captures = [.. expected.Matches[0].Captures];
        captures[2] = new(false, 0, 0);
        Assert.False(PosixConformanceRunner.Equivalent(expected, expected with { Matches = [expected.Matches[0] with { Captures = captures }] }));
        var native = PosixConformanceRunner.ReadAll().Single(c => c.Id == "POSIX-C120").Permitted[0];
        var endpoints = native.NativeMatches.ToArray();
        endpoints[1] = new(0, 1);
        Assert.False(PosixConformanceRunner.Equivalent(native, native with { NativeMatches = endpoints }));
    }

    [Fact]
    public void OutcomeComparisonKeepsExactDiagnosticsAndRegErrorPublication()
    {
        var all = PosixConformanceRunner.ReadAll();
        var syntax = all.Single(c => c.Id == "POSIX-C019").Permitted[0];
        Assert.False(PosixConformanceRunner.Equivalent(syntax, syntax with { Error = syntax.Error! with { Offset = 1 } }));
        Assert.False(PosixConformanceRunner.Equivalent(syntax, syntax with { Error = syntax.Error! with { Disposition = "UndefinedRejected" } }));
        var formatted = all.Single(c => c.Id == "POSIX-C135").Permitted[0];
        var bytes = formatted.ErrorBytes!.ToArray();
        bytes[^1] = 0;
        Assert.False(PosixConformanceRunner.Equivalent(formatted, formatted with { ErrorBytes = bytes }));
        Assert.False(PosixConformanceRunner.Equivalent(formatted, formatted with { RequiredBytes = formatted.RequiredBytes + 1 }));
    }

    [Fact]
    public void NativeDisagreementsAreVisibleAndNeverAmendSourceExpectations()
    {
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(PosixConformanceRunner.Root(), "test/corpora/posix/oracle/disagreements.json")));
        var diffs = audit.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(35, diffs.Count(d => d.GetProperty("family").GetString() == "glibc"));
        Assert.Equal(55, diffs.Count(d => d.GetProperty("family").GetString() == "musl"));
        var nested = diffs.Where(d => d.GetProperty("id").GetString() == "POSIX-C017").ToArray();
        Assert.Equal(2, nested.Length);
        Assert.All(nested, d =>
        {
            Assert.Equal("normative-reference-parent-scope", d.GetProperty("rule").GetString());
            Assert.Equal("NoMatch", d.GetProperty("expected")[0].GetProperty("status").GetString());
            Assert.Equal("Match", d.GetProperty("observed").GetProperty("status").GetString());
        });
    }

    [Fact]
    public void AnonymousAlternativeChoiceKeepsApprovedProvenanceAndLaterGroupAbsent()
    {
        var all = PosixConformanceRunner.ReadAll();
        foreach (var id in new[] { "POSIX-E-anonymous-vs-capture", "POSIX-E-anonymous-vs-inner-capture" })
        {
            var c = all.Single(c => c.Id == id);
            Assert.Equal("GregsEx", c.Classification);
            Assert.Equal("GregsExDisposition", c.Provenance.Kind);
            Assert.Contains("ratified PX-013", c.Provenance.Clause);
            var captures = c.Permitted[0].Matches[0].Captures;
            Assert.Equal(new RegexCapture(true, 0, 1), captures[0]);
            Assert.Equal(new RegexCapture(false, 0, 0), captures[^1]);
            Assert.Equal("Match", c.Permitted[0].Status);
        }
    }

    [Fact]
    public void NativeSourceOnlyCasesRemainRequiredRealProductRecipes()
    {
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(PosixConformanceRunner.Root(), "test/corpora/posix/oracle/source-only.json")));
        var all = PosixConformanceRunner.ReadAll();
        Assert.Equal(21, source.RootElement.EnumerateObject().Count());
        Assert.All(source.RootElement.EnumerateObject(), p => Assert.Single(all, c => c.Id == p.Name));
        Assert.Contains(all, c => c.Limits?.Cancelled == true);
        Assert.Contains(all, c => c.Limits?.HistoryCapacity == 1);
        Assert.Contains(all, c => c.InputUnits.Contains(0));
        Assert.Contains(all, c => c.StartAt == 1 && c.PatternUnits.SequenceEqual([94, 97]));
    }
}
