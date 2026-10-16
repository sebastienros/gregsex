using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace GregsEx.Tests.Contracts.Xml;

public class FixtureTests
{
    [Fact]
    public void LiteralSourceUnitsPreserveTheRatifiedEscapeDistinctions()
    {
        var corpus = XmlCorpus.Read(File.ReadAllText(Path.Combine(SourceDirectory(), "ContractVectors.json")));
        Assert.Equal(new[] { 92, 115, 43 }, corpus.Cases.Single(c => c.Id == "XML-C012").PatternUnits);
        Assert.Equal(new[] { 91, 92, 93 }, corpus.Cases.Single(c => c.Id == "XML-C032").PatternUnits);
        Assert.Equal("InvalidSyntax", corpus.Cases.Single(c => c.Id == "XML-C032").Permitted[0].Status);
        Assert.Equal(new[] { 91, 92, 92, 93 }, corpus.Cases.Single(c => c.Id == "XML-C033").PatternUnits);
        Assert.Equal(new[] { 92 }, corpus.Cases.Single(c => c.Id == "XML-C033").InputUnits);
        Assert.Equal(new[] { 40, 97, 124, 40, 98, 41, 41, 92, 50 },
            corpus.Cases.Single(c => c.Id == "XML-C044").PatternUnits);
        Assert.Equal(new[] { 36, 49, 50 }, corpus.Cases.Single(c => c.Id == "XML-C059").ReplacementUnits);
    }
    [Fact]
    public void CorpusPreservesRepresentationsPermittedCapturesAndTrees()
    {
        var json = File.ReadAllText(Path.Combine(SourceDirectory(), "ContractVectors.json"));
        var corpus = XmlCorpus.Read(json);
        Assert.Equal(74, corpus.Cases.Length);
        Assert.Contains(corpus.Cases, c => c.InputKind == "Utf8" && c.InputUnits.SequenceEqual([240, 159, 152, 128]));
        Assert.Contains(corpus.Cases, c => c.PatternKind == "Bytes");
        Assert.Contains(corpus.Cases, c => c.InputUnits.Contains(0xD800));
        Assert.Contains(corpus.Cases, c => c.Permitted.Length == 2);
        Assert.Contains(corpus.Cases, c => c.Permitted.Any(e => e.Tree.Any(n => n.Kind == XPathAnalyzeNodeKind.Group && n.ParentIndex == 2)));
        Assert.Contains(corpus.Cases, c => c.Provenance.Kind == "GregsExInterpretation");
        Assert.Contains(corpus.Cases, c => c.AbsentInput);
        Assert.Equal(corpus.Cases[0].InputUnits,
            XmlCorpus.Read(JsonSerializer.Serialize(corpus, XmlCorpus.JsonOptions)).Cases[0].InputUnits);
        foreach (var prefix in new[] { "XS-", "XP-" })
        {
            for (var i = 1; i <= (prefix == "XS-" ? 12 : 17); i++)
            {
                Assert.Contains(corpus.Cases, c => c.Capabilities.Contains(prefix + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        Assert.Throws<JsonException>(() => XmlCorpus.Read(json.Replace("\"contractVersion\"", "\"unknownVersion\"")));
        Assert.Throws<JsonException>(() => XmlCorpus.Read(json.Replace("\"destinationLength\": null,", "")));
        Assert.Throws<InvalidDataException>(() => XmlCorpus.Read(json.Replace("xml-fixtures-1", "xml-fixtures-2")));
        var bad = corpus with { Cases = [corpus.Cases[0] with { InputUnits = [65536] }] };
        Assert.Throws<InvalidDataException>(() => XmlCorpus.Read(JsonSerializer.Serialize(bad, XmlCorpus.JsonOptions)));
        var badTree = corpus with
        {
            Cases = [corpus.Cases[0] with
        {
            Permitted = [corpus.Cases[0].Permitted[0] with
            { Tree = [new(XPathAnalyzeNodeKind.Group, 0, 1, new(0, 1))] }]
        }]
        };
        Assert.Throws<InvalidDataException>(() => XmlCorpus.Read(JsonSerializer.Serialize(badTree, XmlCorpus.JsonOptions)));
    }
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
