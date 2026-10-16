using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace GregsEx.Tests.Integration.EcmaScript;

public sealed class ProductCorpusTests(ITestOutputHelper output)
{
    private static readonly (string File, int Count)[] CorpusFiles =
    [
        ("oracle-corpus.json", 939),
        ("imported-corpus.json", 1381),
        ("contract-corpus.json", 27),
        ("resource-corpus.json", 10)
    ];
    private static readonly FrozenDictionary<string, FrozenDictionary<string, JsonElement>> Corpora =
        CorpusFiles.ToFrozenDictionary(c => c.File,
            c => ProductCorpusRunner.ReadCorpus(DirectoryPath(), c.File).GetProperty("cases").EnumerateArray()
                .ToFrozenDictionary(f => f.GetProperty("id").GetString()!, StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static string DirectoryPath([CallerFilePath] string file = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../../test/corpora/ecmascript"));

    public static IEnumerable<TheoryDataRow<string, int>> Inventories() =>
        CorpusFiles.Select(c => new TheoryDataRow<string, int>(c.File, c.Count));

    public static IEnumerable<TheoryDataRow<string, string>> Cases() =>
        CorpusFiles.SelectMany(c => Corpora[c.File].Keys.Select(id => new TheoryDataRow<string, string>(c.File, id)));

    [Theory]
    [MemberData(nameof(Inventories))]
    public void CompleteImmutableInventory(string file, int count) => Assert.Equal(count, Corpora[file].Count);

    [Theory]
    [MemberData(nameof(Cases))]
    public void FrozenExpectationsExecuteAgainstActualProduct(string file, string id)
        => ProductCorpusRunner.Execute(Corpora[file][id], file == "resource-corpus.json", output.WriteLine);

    [Fact]
    public void NumericRawFlagReachesActualFlagParser() => ProductCorpusRunner.RawFlag(DirectoryPath());
}
