using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Tests.Contracts.Posix;

namespace GregsEx.Tests.Conformance.Posix;

// Test-only callable consumer. Integration supplies real operations; this never manufactures an observation.
internal static class PosixConformanceRunner
{
    internal static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../.."));

    internal static PosixFixture[] ReadAll()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "test/corpora/posix/manifest.json")));
        var m = manifest.RootElement;
        if (m.GetProperty("format").GetString() != "posix-conformance-manifest-1" ||
            m.GetProperty("contractVersion").GetString() != "posix-fixtures-1" ||
            m.GetProperty("dataId").GetString() != PosixLocale.C.ProviderId ||
            m.GetProperty("dataDigest").GetString() != PosixLocale.C.DataDigest)
        {
            throw new InvalidDataException("Unknown POSIX conformance identity.");
        }

        foreach (var artifact in m.GetProperty("artifacts").EnumerateArray())
        {
            CheckIdentity(artifact);
        }

        var fixtures = new List<PosixFixture>();
        foreach (var lane in m.GetProperty("lanes").EnumerateArray())
        {
            var corpus = PosixCorpus.Read(File.ReadAllText(LocalPath(lane.GetProperty("corpus").GetString()!)));
            if (corpus.Cases.Length != lane.GetProperty("count").GetInt32())
            {
                throw new InvalidDataException("Changed lane count.");
            }

            fixtures.AddRange(corpus.Cases);
        }
        if (fixtures.Count != m.GetProperty("fixtureCount").GetInt32() || fixtures.Select(f => f.Id).Distinct().Count() != fixtures.Count)
        {
            throw new InvalidDataException("Incomplete or duplicate corpus.");
        }

        using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "test/corpora/posix/index.json")));
        if (index.RootElement.GetProperty("format").GetString() != "posix-conformance-index-1")
        {
            throw new InvalidDataException("Unknown POSIX comparison index.");
        }

        var entries = index.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        if (entries.Length != fixtures.Count)
        {
            throw new InvalidDataException("Incomplete index.");
        }

        for (var n = 0; n < entries.Length; n++)
        {
            var e = entries[n];
            if (e.GetProperty("id").GetString() != fixtures[n].Id || e.GetProperty("ordinal").GetInt32() != n ||
                e.GetProperty("diagnosticPolicy").GetString() != "exact" ||
                e.GetProperty("oracleComparison").GetString() is not ("agreement" or "reviewed-difference" or "source-only"))
            {
                throw new InvalidDataException("Misdirected or unreviewed recipe.");
            }
        }
        return [.. fixtures];
    }

    internal static void CheckIdentity(JsonElement artifact)
    {
        var bytes = File.ReadAllBytes(LocalPath(artifact.GetProperty("path").GetString()!));
        if (bytes.LongLength != artifact.GetProperty("bytes").GetInt64() ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != artifact.GetProperty("sha256").GetString())
        {
            throw new InvalidDataException("Changed POSIX artifact.");
        }
    }

    private static string LocalPath(string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Contains(".."))
        {
            throw new InvalidDataException("Nonlocal POSIX artifact.");
        }

        return Path.Combine(Root(), path);
    }

    // A single invocation of the real consumer for every seed and expansion, including source-only native cases.
    // Any exception/failure terminates the run with the recipe ID. No skip, native fallback or oracle majority.
    internal static int Run(Func<PosixFixture, PosixExpected> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        var count = 0;
        foreach (var fixture in ReadAll())
        {
            RunCase(fixture, observe);
            count++;
        }
        return count;
    }

    internal static void RunCase(PosixFixture fixture, Func<PosixFixture, PosixExpected> observe)
    {
        ArgumentNullException.ThrowIfNull(observe);
        PosixExpected actual;
        try
        { actual = observe(fixture); }
        catch (Exception e) { throw new InvalidDataException($"POSIX real-consumer failure: {fixture.Id}", e); }
        if (!fixture.Permitted.Any(expected => Equivalent(expected, actual)))
        {
            throw new InvalidDataException($"POSIX mismatch {fixture.Id}: observed " + JsonSerializer.Serialize(actual, PosixCorpus.JsonOptions));
        }
    }

    internal static bool Equivalent(PosixExpected expected, PosixExpected actual)
    {
        // Null groupCount means the fixture does not assert metadata; all specified fields remain exact.
        // Generic Find/Boolean observations publish precisely their requested output, never invented captures.
        if (expected.Status != actual.Status || expected.Boolean != actual.Boolean ||
            expected.GroupCount is not null && expected.GroupCount != actual.GroupCount ||
            expected.DestinationUnchanged != actual.DestinationUnchanged || expected.Error != actual.Error ||
            expected.RequiredBytes != actual.RequiredBytes)
        {
            return false;
        }

        if (!expected.NativeMatches.SequenceEqual(actual.NativeMatches) ||
            !(expected.ErrorBytes ?? []).SequenceEqual(actual.ErrorBytes ?? []) || expected.Matches.Length != actual.Matches.Length)
        {
            return false;
        }

        for (var i = 0; i < expected.Matches.Length; i++)
        {
            if (expected.Matches[i].Range != actual.Matches[i].Range ||
                !expected.Matches[i].Captures.SequenceEqual(actual.Matches[i].Captures))
            {
                return false;
            }
        }

        return true;
    }
}
