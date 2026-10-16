using System.Runtime.CompilerServices;
using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;
using Xunit;
using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Integration;
using F = GregsEx.Flavors.XPath.Host.XPathHostFunctions;

namespace GregsEx.Tests.Flavors.XPath.Integration;

public sealed class CompositionCorpusTests
{
    private static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../../../.."));
    private static string Text(int[] units) => new([.. units.Select(u => (char)u)]);
    private static readonly XmlCorpus Corpus = XmlCorpus.Read(File.ReadAllText(Path.Combine(Root(), "test/corpora/xml/xpath/Corpus.json")));
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        foreach (var c in Corpus.Cases)
        {
            if (c.InputKind != "Bytes" && c.PatternKind == "Utf16")
            {
                yield return new TheoryDataRow<string>(c.Id);
            }
        }
    }
    [Theory]
    [MemberData(nameof(Cases))]
    public void PinnedIndependentCompositionRecipe(string id)
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
            if (c.InputKind == "Utf8" || c.Operation is "Parse" or "Find" or "FindCaptures")
            {
                Assert.True(XPathProfile.TryParseFlags(c.Flags, out var profile, out var detail));
                var budget = new CompileBudget(compile.Limits);
                if (!XPathBinding.TryCreate(pattern, profile!, Enum.Parse<RegexInputKind>(c.InputKind),
                    ref budget, out var binding, out detail))
                {
                    if (detail.ErrorCode != XPathErrorCode.None)
                    {
                        throw new XPathRegexException(detail);
                    }

                    throw new RegexParseException(detail.Diagnostic);
                }
                if (c.Operation == "Parse")
                {
                    Assert.Contains(c.Permitted, e => e.Status == "ParseSuccess");
                }
                else
                {
                    var operation = c.Operation == "Matches" ? SearchOperation.Boolean : SearchOperation.Captures;
                    var cap = binding!.RequiredCapacity(operation);
                    var state = new RunState(new(new int[cap.StateSlots], new int[cap.CaptureSlots], new int[cap.ScratchSlots]),
                        compile.DefaultMatchLimits, match);
                    var groups = new RegexCapture[binding.Program.GroupCount];
                    var status = c.InputKind == "Utf8"
                        ? binding.Executor.Search<byte, XmlByteTextReader>(c.InputUnits.Select(u => (byte)u).ToArray(),
                            c.StartAt, operation, XPathBinding.FrameCapacity, ref state, groups, out var range)
                        : binding.Executor.Search<char, XmlCharTextReader>(input, c.StartAt, operation,
                            XPathBinding.FrameCapacity, ref state, groups, out range);
                    if (status is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
                    {
                        var failure = state.Failure;
                        error = new(failure.Code.ToString(), "Input", failure.Offset,
                            failure.Code is RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidInputDomain ? 1 : 0,
                            c.InputKind == "Utf16" ? "Utf16CodeUnits" : "Bytes", null);
                    }
                    else if (operation == SearchOperation.Boolean)
                    {
                        boolean = status == RegexSearchStatus.Match;
                    }
                    else
                    {
                        Assert.Contains(c.Permitted, e => e.Status == status.ToString() &&
                            (status == RegexSearchStatus.NoMatch || e.Matches.Length == 1 &&
                             e.Matches[0].Range == range && e.Matches[0].Captures.SequenceEqual(groups)));
                        return;
                    }
                }
            }
            else
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
    [Fact]
    public void PublicOverloadObligationsRemainInTheCompleteInventory()
    {
        Assert.Equal(735, Corpus.Cases.Length);
        Assert.Equal(new[] { "XP-0189", "XP-0190" }, Corpus.Cases.Where(c => c.PatternKind == "Bytes" || c.InputKind == "Bytes").Select(c => c.Id));
        // Public byte-pattern rejection and raw-subject exception mapping are
        // checked separately through the public API.
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
