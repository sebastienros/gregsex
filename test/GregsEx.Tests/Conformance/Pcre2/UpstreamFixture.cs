using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

internal static class UpstreamFixture
{
    internal static (List<JsonElement> Recipes, Dictionary<string, JsonElement[]> Traces, Dictionary<string, byte[]> Tables) Load()
    {
        List<JsonElement> recipes = [];
        using (var stream = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/upstream-recipes.jsonl.gz")), CompressionMode.Decompress))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            { using var doc = JsonDocument.Parse(line); recipes.Add(doc.RootElement.Clone()); }
        }

        var members = recipes.Where(r => r.TryGetProperty("observation", out var o) && o.TryGetProperty("traceMember", out _))
            .Select(r => r.GetProperty("observation").GetProperty("traceMember").GetString()!).ToHashSet();
        Dictionary<string, JsonElement[]> traces = [];
        Dictionary<string, byte[]> tables = [];
        using (var gzip = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/oracle/native-evidence.tar.gz")), CompressionMode.Decompress))
        using (var tar = new TarReader(gzip))
        {
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (entry.DataStream is null)
                {
                    continue;
                }

                if (members.Contains(entry.Name))
                {
                    List<JsonElement> events = [];
                    using var reader = new StreamReader(entry.DataStream, leaveOpen: true);
                    while (reader.ReadLine() is { } line)
                    { using var doc = JsonDocument.Parse(line); events.Add(doc.RootElement.Clone()); }
                    traces.Add(entry.Name, [.. events]);
                }
                else if (entry.Name.StartsWith("indexed/tables-", StringComparison.Ordinal) && entry.Name.EndsWith(".bin", StringComparison.Ordinal))
                { using var memory = new MemoryStream(); entry.DataStream.CopyTo(memory); tables.Add(entry.Name, memory.ToArray()); }
            }
        }
        return (recipes, traces, tables);
    }
}
