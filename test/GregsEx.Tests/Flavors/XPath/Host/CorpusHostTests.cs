using System.Runtime.CompilerServices;
using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;
using Xunit;
using F = GregsEx.Flavors.XPath.Host.XPathHostFunctions;

namespace GregsEx.Tests.Flavors.XPath.Host;

public sealed class CorpusHostTests
{
    private static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../../.."));
    private static string Text(int[] units) => new([.. units.Select(u => (char)u)]);
    private static readonly XmlCorpus Corpus = XmlCorpus.Read(File.ReadAllText(Path.Combine(Root(), "test/corpora/xml/xpath/Corpus.json")));
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        foreach (var c in Corpus.Cases)
        {
            if (c.InputKind == "Utf16" && c.PatternKind == "Utf16" &&
                c.Operation is "Matches" or "Replace" or "Tokenize" or "TokenizeOne" or "AnalyzeString")
            {
                yield return new TheoryDataRow<string>(c.Id);
            }
        }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void PinnedIndependentHostRecipe(string id)
    {
        var c = Corpus.Cases.Single(c => c.Id == id);
        var input = c.AbsentInput ? null : Text(c.InputUnits);
        var pattern = Text(c.PatternUnits);
        var limits = c.Limits;
        var compile = new RegexCompileOptions(limits: new(maxCompileWork: limits?.MaxCompileWork ?? 10_000_000));
        var match = new RegexMatchOptions
        {
            Limits = new(maxWork: limits?.MaxWork ?? 10_000_000),
            CancellationToken = new(limits?.Cancelled == true)
        };
        var host = new XPathHostLimits(limits?.MaxResults ?? 1_000_000, limits?.MaxOutputUnits ?? 16_777_216,
            limits?.MaxHostMemoryBytes ?? 67_108_864);
        bool? boolean = null;
        string[]? texts = null;
        XPathAnalyzeNode[] tree = [];
        XmlFixtureError? error = null;
        try
        {
            switch (c.Operation)
            {
                case "Matches":
                    boolean = F.Matches(input, pattern, c.Flags, compile, match);
                    break;
                case "Replace":
                    texts = [F.Replace(input, pattern, Text(c.ReplacementUnits!), c.Flags, compile, match, host)];
                    break;
                case "Tokenize":
                    texts = [.. F.Tokenize(input, pattern, c.Flags, compile, match, host)];
                    break;
                case "TokenizeOne":
                    texts = [.. F.Tokenize(input, match, host)];
                    break;
                case "AnalyzeString":
                    tree = [.. F.AnalyzeString(input, pattern, c.Flags, compile, match, host).Nodes];
                    break;
            }
        }
        catch (XPathRegexException ex)
        {
            var d = ex.Diagnostic;
            error = new(d.ErrorCode.ToString(), d.Location.ToString(), d.Diagnostic.Offset,
                d.Diagnostic.Length, d.Diagnostic.LocationUnit.ToString(), XmlRegexDiagnostic.ErrorNamespace);
        }
        catch (RegexParseException ex)
        { var d = ex.Diagnostic; error = new(d.Code.ToString(), "Pattern", d.Offset, d.Length, d.LocationUnit.ToString(), null); }
        catch (RegexExecutionException ex)
        {
            var f = ex.Failure;
            error = new(f.Code.ToString(), "Input", f.Offset,
            f.Code is RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidInputDomain ? 1 : 0,
            "Utf16CodeUnits", null);
        }
        catch (XPathHostException ex)
        { error = new(ex.Failure.Code.ToString(), "Host", 0, 0, "Utf16CodeUnits", null); }
        var exact = id.StartsWith("XML-C", StringComparison.Ordinal) || error is { Location: "Input" };
        Assert.True(c.Permitted.Any(expected => error is null
            ? expected.Error is null && expected.Boolean == boolean && expected.Tree.SequenceEqual(tree) &&
              ((expected.Texts is null && texts is null) || expected.Texts is not null && texts is not null &&
               expected.Texts.Select(Text).SequenceEqual(texts))
            : expected.Error is { } e && e.Code == error.Code && e.Location == error.Location &&
              e.OffsetUnit == error.OffsetUnit && e.Namespace == error.Namespace &&
              (exact ? e.Offset == error.Offset && e.Length == error.Length : ValidBounds(c, error))),
            $"{id}: actual bool={boolean}, texts={JsonSerializer.Serialize(texts)}, tree={JsonSerializer.Serialize(tree)}, error={error}");
    }
    private static bool ValidBounds(XmlFixture fixture, XmlFixtureError error)
    {
        var total = error.Location switch
        {
            "Flags" => fixture.Flags.Length,
            "Replacement" => fixture.ReplacementUnits?.Length ?? 0,
            "Pattern" => fixture.PatternUnits.Length,
            "Input" => fixture.InputUnits.Length,
            _ => 0
        };
        return error.Offset >= 0 && error.Length >= 0 && (long)error.Offset + error.Length <= total;
    }
}
