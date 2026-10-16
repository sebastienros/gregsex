using Xunit;

namespace GregsEx.Tests.Contracts;

public class FixtureTests
{
    [Fact]
    public void HandAuthoredManifestPreservesNativeUnitsAndProvenance()
    {
        using var stream = typeof(FixtureTests).Assembly.GetManifestResourceStream("GregsEx.CoreFixtures.json")!;
        var manifest = CoreFixtureManifest.Read(stream);
        Assert.Equal(12, manifest.Cases.Length);
        Assert.Equal(new[] { 0xD83D, 0xDE00 }, manifest.Cases.Single(c => c.Id == "RC-SUPPLEMENTARY-16").Input);
        Assert.Equal(new[] { 0xF0, 0x9F, 0x98, 0x80 }, manifest.Cases.Single(c => c.Id == "RC-SUPPLEMENTARY-8").Input);
        Assert.Equal(new[] { 0xD800 }, manifest.Cases.Single(c => c.Id == "RC-LONE-UNIT").Input);
        Assert.Equal(new[] { 0, 255 }, manifest.Cases.Single(c => c.Id == "RC-RAW-FF").Input);
        Assert.All(manifest.Cases, fixture => Assert.Equal("hand-authored-contract-v1", fixture.Oracle));
        // This verifies interchange only. No fixture has run against a matching engine here.
    }

    [Fact]
    public void FixtureValidationRejectsLossyRepresentationAndMissingOutcome()
    {
        using var stream = typeof(FixtureTests).Assembly.GetManifestResourceStream("GregsEx.CoreFixtures.json")!;
        var manifest = CoreFixtureManifest.Read(stream);
        var bytes = manifest.Cases.Single(c => c.Id == "RC-RAW-FF");
        Assert.Throws<InvalidDataException>(() => (bytes with { Input = [256] }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { ExpectedStatus = null }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { StartAt = 3 }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { Provenance = "" }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { ProfileId = "future-profile" }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { DataId = "ambient-Unicode" }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (bytes with { Flags = (RegularCoreFlags)2 }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with { Operation = "Iteration" }).Validate());
        Assert.Throws<InvalidDataException>(() => (bytes with
        {
            ExpectedFailure = RegexExecutionFailureCode.InvalidEncoding,
            ExpectedFailureOffset = null
        }).Validate());
    }

    [Fact]
    public void DotAllIsExplicitlyRepresentable()
    {
        using var stream = typeof(FixtureTests).Assembly.GetManifestResourceStream("GregsEx.CoreFixtures.json")!;
        var sample = CoreFixtureManifest.Read(stream).Cases[0] with { Flags = RegularCoreFlags.DotAll };
        sample.Validate();
        Assert.Equal(RegularCoreFlags.DotAll, sample.Flags);
    }

    [Fact]
    public void MissingFlagsCannotSilentlyBecomeNone()
    {
        using var stream = typeof(FixtureTests).Assembly.GetManifestResourceStream("GregsEx.CoreFixtures.json")!;
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(stream)!;
        manifest["cases"]![0]!.AsObject().Remove("flags");
        using var missing = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
        Assert.Throws<System.Text.Json.JsonException>(() => CoreFixtureManifest.Read(missing));
    }
}
