using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;

namespace GregsEx.Tests.Conformance.Xml.Integration;

// A development consumer of original immutable recipes; never changes or executes an expectation.
internal static class JoinedCorpus
{
    internal static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../../.."));
    internal static JsonDocument Document(string path) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), path)));
    internal static void CheckIdentity(JsonElement identity)
    {
        var path = identity.GetProperty("path").GetString()!;
        if (Path.IsPathRooted(path) || path.Split('/').Contains(".."))
        {
            throw new InvalidDataException("Nonlocal artifact.");
        }

        var bytes = File.ReadAllBytes(Path.Combine(Root(), path));
        if (bytes.LongLength != identity.GetProperty("bytes").GetInt64() ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != identity.GetProperty("sha256").GetString())
        {
            throw new InvalidDataException("Changed joined artifact: " + path);
        }
    }
    internal static (XmlFixture Fixture, JsonElement Index)[] Read()
    {
        using var manifest = Document("test/corpora/xml/manifest.json");
        var m = manifest.RootElement;
        if (m.GetProperty("format").GetString() != "xml-joined-manifest-1" ||
            m.GetProperty("contractVersion").GetString() != "xml-fixtures-1" ||
            m.GetProperty("dataId").GetString() != new XsdProfile().DataId)
        {
            throw new InvalidDataException("Unknown joined identity.");
        }

        CheckIdentity(m.GetProperty("index"));
        foreach (var artifact in m.GetProperty("artifacts").EnumerateArray())
        {
            CheckIdentity(artifact);
        }

        using var index = Document(m.GetProperty("index").GetProperty("path").GetString()!);
        if (index.RootElement.GetProperty("format").GetString() != "xml-joined-index-1")
        {
            throw new InvalidDataException("Unknown index.");
        }

        var lanes = m.GetProperty("lanes").EnumerateArray().ToDictionary(l => l.GetProperty("id").GetString()!, l =>
        {
            var corpus = XmlCorpus.Read(File.ReadAllText(Path.Combine(Root(), l.GetProperty("corpus").GetString()!)));
            if (corpus.Cases.Length != l.GetProperty("fixtureCount").GetInt32() ||
                corpus.Cases.Any(c => c.ProfileId != l.GetProperty("profileId").GetString()))
            {
                throw new InvalidDataException("Changed lane identity.");
            }

            return corpus.Cases;
        });
        return Resolve(lanes, [.. index.RootElement.GetProperty("cases").EnumerateArray().Select(e => e.Clone())], m.GetProperty("fixtureCount").GetInt32());
    }
    internal static (XmlFixture Fixture, JsonElement Index)[] Resolve(Dictionary<string, XmlFixture[]> lanes, JsonElement[] entries, int count)
    {
        var result = new List<(XmlFixture, JsonElement)>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<(string, int)>();
        foreach (var entry in entries)
        {
            var lane = entry.GetProperty("lane").GetString()!;
            var ordinal = entry.GetProperty("ordinal").GetInt32();
            if (!lanes.TryGetValue(lane, out var cases) || ordinal < 0 || ordinal >= cases.Length ||
                !positions.Add((lane, ordinal)) || !ids.Add(entry.GetProperty("id").GetString()!) ||
                cases[ordinal].Id != entry.GetProperty("id").GetString() ||
                entry.GetProperty("oracleComparison").GetString() is not ("agreement" or "reviewed-difference" or "source-only"))
            {
                throw new InvalidDataException("Missing/duplicate/misdirected joined recipe.");
            }

            result.Add((cases[ordinal], entry));
        }
        if (result.Count != count || result.Count != lanes.Values.Sum(l => l.Length))
        {
            throw new InvalidDataException("Incomplete joined corpus.");
        }

        return [.. result];
    }
}
