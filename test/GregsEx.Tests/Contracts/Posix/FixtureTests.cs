using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace GregsEx.Tests.Contracts.Posix;

public class FixtureTests
{
    [Fact]
    public void SourceUnitsAndExpectedSelectionRemainIndependentOfEngine()
    {
        var corpus = PosixCorpus.Read(File.ReadAllText(Path.Combine(SourceDirectory(), "ContractVectors.json")));
        Assert.Equal(138, corpus.Cases.Length);
        for (var n = 1; n <= 20; n++)
        {
            Assert.Contains(corpus.Cases, c => c.Capabilities.Contains($"PX-{n:D3}"));
        }

        var ambiguous = corpus.Cases.Single(c => c.PatternUnits.SequenceEqual("(a|aa)(a?)".Select(ch => (int)ch)) && c.Operation == "FindCaptures");
        Assert.Equal(new RegexCapture(true, 0, 2), ambiguous.Permitted[0].Matches[0].Captures[1]);
        Assert.Equal(new RegexCapture(true, 2, 0), ambiguous.Permitted[0].Matches[0].Captures[2]);
        var nested = corpus.Cases.Single(c => c.PatternUnits.SequenceEqual("((a)|b)+".Select(ch => (int)ch)));
        Assert.False(nested.Permitted[0].Matches[0].Captures[2].Participated);
        var nullable = corpus.Cases.Single(c => c.PatternUnits.SequenceEqual("(a*)*".Select(ch => (int)ch)) && c.InputUnits.Length == 2);
        Assert.Equal(new RegexCapture(true, 0, 2), nullable.Permitted[0].Matches[0].Captures[1]);
        Assert.Contains(corpus.Cases, c => c.PatternUnits.SequenceEqual([91, 92, 93]) && c.InputUnits.SequenceEqual([92]));
        Assert.Contains(corpus.Cases, c => c.Operation == "RegExec" && c.DestinationLength == 0 && c.Permitted[0].DestinationUnchanged);
        Assert.Contains(corpus.Cases, c => c.CompileFlags == 4 && c.Permitted[0].DestinationUnchanged);
        Assert.Contains(corpus.Cases, c => c.InputUnits.Contains(255) && c.Classification == "GregsEx");
        Assert.Equal(corpus.Cases[0], PosixCorpus.Read(JsonSerializer.Serialize(corpus, PosixCorpus.JsonOptions)).Cases[0],
            new CaseIdentityComparer());
    }

    [Fact]
    public void InvalidFixturesCannotChangeAdmissionOrHideStaleNativeOutput()
    {
        var json = File.ReadAllText(Path.Combine(SourceDirectory(), "ContractVectors.json"));
        var c = PosixCorpus.Read(json);
        Assert.Throws<JsonException>(() => PosixCorpus.Read(json.Replace("\"contractVersion\"", "\"unknownVersion\"")));
        Assert.Throws<JsonException>(() => PosixCorpus.Read(json.Replace("\"limits\": null,", "")));
        Assert.Throws<InvalidDataException>(() => PosixCorpus.Read(json.Replace("posix-fixtures-1", "posix-fixtures-2")));
        Bad(c with { Cases = [c.Cases[0], c.Cases[0]] });
        Bad(c with { Cases = [c.Cases[0] with { PatternUnits = [256] }] });
        Bad(c with { Cases = [c.Cases[0] with { CompileFlags = 8 }] });
        Bad(c with { Cases = [c.Cases[0] with { Capabilities = ["PX-999"] }] });
        Bad(c with
        {
            Cases = [c.Cases[0] with { Permitted = [c.Cases[0].Permitted[0] with
        { Matches = [new(new(0, 10), [new(true, 0, 10)])] }] }]
        });
        var native = c.Cases.First(x => x.Operation == "RegExec" && x.DestinationLength == 0);
        Bad(c with { Cases = [native with { Permitted = [native.Permitted[0] with { DestinationUnchanged = false }] }] });
        Bad(c with
        {
            Cases = [native with { DestinationLength = 1,
            Permitted = [native.Permitted[0] with { DestinationUnchanged = false, NativeMatches = [new(-1, 0)] }] }]
        });
        var negative = c.Cases.First(x => x.Permitted[0].Error is not null);
        Bad(c with
        {
            Cases = [negative with { Permitted = [negative.Permitted[0] with
        { Error = negative.Permitted[0].Error! with { OffsetUnit = "Utf16CodeUnits" } }] }]
        });
    }

    [Fact]
    public void FrozenSeedSchemaAndTableHashesAreActualBytes()
    {
        var root = Path.GetFullPath(Path.Combine(SourceDirectory(), "../../../.."));
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/posix/sources/contract-lock.json")));
        foreach (var file in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, file.GetProperty("path").GetString()!));
            Assert.Equal(file.GetProperty("bytes").GetInt64(), bytes.LongLength);
            Assert.Equal(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
    }

    private static void Bad(PosixCorpus c) => Assert.Throws<InvalidDataException>(() =>
        PosixCorpus.Read(JsonSerializer.Serialize(c, PosixCorpus.JsonOptions)));
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
    private sealed class CaseIdentityComparer : IEqualityComparer<PosixFixture>
    {
        public bool Equals(PosixFixture? x, PosixFixture? y) =>
            JsonSerializer.Serialize(x, PosixCorpus.JsonOptions) == JsonSerializer.Serialize(y, PosixCorpus.JsonOptions);
        public int GetHashCode(PosixFixture obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }
}
