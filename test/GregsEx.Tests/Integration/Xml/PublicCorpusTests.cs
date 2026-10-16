using System.Text.Json;
using GregsEx.Tests.Contracts.Xml;
using GregsEx.Tests.Conformance.Xml.Integration;
using Xunit;

namespace GregsEx.Tests.Integration.Xml;

public sealed class PublicCorpusTests
{
    private static readonly (XmlFixture Fixture, JsonElement Index)[] Recipes = JoinedCorpus.Read();
    private static string Text(int[] units) => new([.. units.Select(u => checked((char)u))]);
    public static IEnumerable<TheoryDataRow<string>> Cases() => Recipes.Select(r => new TheoryDataRow<string>(r.Fixture.Id));

    [Fact]
    public void CompleteImmutableInventory()
    {
        Assert.Equal(5546, Recipes.Length);
        Assert.Equal(4811, Recipes.Count(r => r.Fixture.ProfileId == "Xsd11"));
        Assert.Equal(735, Recipes.Count(r => r.Fixture.ProfileId == "XPath31"));
        Assert.Equal(6, Recipes.Count(r => r.Fixture.PatternKind == "Bytes" || r.Fixture.InputKind == "Bytes"));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryIndependentPublicRecipe(string id)
    {
        var (c, index) = Recipes.Single(r => r.Fixture.Id == id);
        var l = c.Limits;
        var compile = new RegexCompileOptions(limits: new(maxCompileWork: l?.MaxCompileWork ?? 10_000_000));
        var match = new RegexMatchOptions
        {
            Limits = new(maxWork: l?.MaxWork ?? 10_000_000),
            CancellationToken = new(l?.Cancelled == true)
        };
        var host = new XPathHostLimits(l?.MaxResults ?? 1_000_000, l?.MaxOutputUnits ?? 16_777_216,
            l?.MaxHostMemoryBytes ?? 67_108_864);
        var input = c.AbsentInput ? null : c.InputKind == "Utf16" ? Text(c.InputUnits) : null;
        var source = c.PatternKind == "Utf16" ? Text(c.PatternUnits) : "";
        bool? boolean = null;
        string[]? texts = null;
        XPathAnalyzeNode[] tree = [];
        RegexMatchRange range = default;
        RegexCapture[] groups = [];
        string? status = null;
        XmlFixtureError? error = null;
        try
        {
            RegexProfile profile = new XsdProfile();
            if (c.ProfileId == "XPath31")
            {
                if (!XPathProfile.TryParseFlags(c.Flags, out var xpath, out var detail))
                {
                    throw new XPathRegexException(detail);
                }

                profile = xpath!;
            }
            if (c.PatternKind == "Bytes")
            {
                Assert.False(RegexPattern.TryParse(c.PatternUnits.Select(u => checked((byte)u)).ToArray(), profile,
                    out var result, out var diagnostic));
                Assert.Null(result);
                throw new RegexParseException(diagnostic);
            }
            var borrowed = c.ProfileId == "Xsd11" || c.InputKind != "Utf16" || c.Operation is "Parse" or "Find" or "FindCaptures";
            if (borrowed)
            {
                // Raw XPath subject tests the actual byte invocation of a UTF-16 pattern;
                // XSD raw recipes request the explicitly inadmissible compile representation.
                var kind = c.InputKind == "Bytes" && c.ProfileId == "XPath31" ? RegexInputKind.Utf16 : Enum.Parse<RegexInputKind>(c.InputKind);
                var options = new RegexCompileOptions(kind, limits: compile.Limits);
                var pattern = RegexPattern.Parse(source, profile, options);
                Assert.True(RegexPattern.TryParse(source.AsSpan(), profile, out var parsed, out var success, options));
                Assert.Equal(default, success);
                Assert.NotNull(parsed);
                if (c.Operation == "Parse")
                {
                    status = "ParseSuccess";
                }
                else
                {
                    using var workspace = pattern.CreateWorkspace();
                    var captures = c.Operation == "FindCaptures";
                    groups = new RegexCapture[c.DestinationLength ?? pattern.GroupCount];
                    RegexSearchStatus outcome;
                    if (c.InputKind == "Utf16")
                    {
                        outcome = captures ? workspace.FindCaptures(input, c.StartAt, groups, out range, match) :
                            workspace.Find(input, c.StartAt, out range, match);
                    }
                    else
                    {
                        var bytes = c.InputUnits.Select(u => checked((byte)u)).ToArray();
                        outcome = captures ? workspace.FindCaptures(bytes, c.StartAt, groups, out range, match) :
                            workspace.Find(bytes, c.StartAt, out range, match);
                    }
                    status = outcome.ToString();
                    if (outcome is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
                    {
                        throw new RegexExecutionException(workspace.LastFailure);
                    }

                    if (c.ProfileId == "Xsd11" || c.Operation is "Membership" or "Matches")
                    {
                        boolean = outcome == RegexSearchStatus.Match;
                        var existence = c.InputKind == "Utf16" ? workspace.IsMatch(input, c.StartAt, match) :
                            workspace.IsMatch(c.InputUnits.Select(u => (byte)u).ToArray(), c.StartAt, match);
                        Assert.Equal(outcome, existence);
                        if (c.ProfileId == "Xsd11" && c.InputKind == "Utf16")
                        {
                            Assert.Equal(boolean, XsdFunctions.IsValid(input!, source, compile, match));
                        }
                    }
                }
            }
            else
            {
                switch (c.Operation)
                {
                    case "Matches":
                        boolean = XPathFunctions.Matches(input, source, c.Flags, compile, match);
                        break;
                    case "Replace":
                        texts = [XPathFunctions.Replace(input, source, Text(c.ReplacementUnits!), c.Flags, compile, match, host)];
                        break;
                    case "Tokenize":
                        texts = [.. XPathFunctions.Tokenize(input, source, c.Flags, compile, match, host)];
                        break;
                    case "TokenizeOne":
                        texts = [.. XPathFunctions.Tokenize(input, match, host)];
                        break;
                    case "AnalyzeString":
                        var result = XPathFunctions.AnalyzeString(input, source, c.Flags, compile, match, host);
                        Assert.Equal(input ?? "", result.Input);
                        tree = [.. result.Nodes];
                        break;
                    default:
                        throw new InvalidOperationException("Unexecuted recipe operation.");
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
        {
            var d = ex.Diagnostic;
            if (c.ProfileId == "XPath31" && c.Operation == "Parse" && d.Code == RegexDiagnosticCode.InvalidSyntax)
            {
                // Generic parsing reports shared structured syntax diagnostics. Verify
                // the same public typed function also delivers the standard FORX identity.
                var typed = Assert.Throws<XPathRegexException>(() => XPathFunctions.Matches(input, source, c.Flags, compile, match));
                Assert.Equal(d, typed.Diagnostic.Diagnostic);
                error = new(typed.Diagnostic.ErrorCode.ToString(), typed.Diagnostic.Location.ToString(), d.Offset,
                    d.Length, d.LocationUnit.ToString(), XmlRegexDiagnostic.ErrorNamespace);
            }
            else
            {
                error = new(d.Code.ToString(), "Pattern", d.Offset, d.Length, d.LocationUnit.ToString(), null);
            }
        }
        catch (RegexExecutionException ex)
        {
            var f = ex.Failure;
            error = new(f.Code.ToString(), "Input", f.Offset, OffendingLength(c, f),
            c.InputKind == "Utf16" ? "Utf16CodeUnits" : "Bytes", null);
        }
        catch (XPathHostException ex)
        { error = new(ex.Failure.Code.ToString(), "Host", 0, 0, "Utf16CodeUnits", null); }
        catch (ArgumentException ex) when (c.InputKind == "Bytes" && ex.ParamName == "input")
        { error = new("UnsupportedRepresentation", "Input", 0, c.InputUnits.Length, "Bytes", null); }
        var policy = index.GetProperty("diagnosticPolicy").GetString();
        var exact = policy is "FrozenExactContract" or "FirstOffendingNative";
        Assert.True(c.Permitted.Any(e => error is not null
            ? e.Error is { } d && d.Code == error.Code && d.Location == error.Location &&
              d.OffsetUnit == error.OffsetUnit && d.Namespace == error.Namespace &&
              (exact ? d.Offset == error.Offset && d.Length == error.Length : ValidBounds(c, error))
            : e.Error is null && (status is null || e.Status == status) && e.Boolean == boolean &&
              e.Tree.SequenceEqual(tree) && ((e.Texts is null && texts is null) || e.Texts is not null && texts is not null && e.Texts.Select(Text).SequenceEqual(texts)) &&
              (e.Matches.Length == 0 || e.Matches.Length == 1 && e.Matches[0].Range == range &&
               (c.Operation != "FindCaptures" || e.Matches[0].Captures.SequenceEqual(groups.Take(e.Matches[0].Captures.Length))))),
            $"{id}: status={status}, bool={boolean}, range={range}, tree={JsonSerializer.Serialize(tree)}, texts={JsonSerializer.Serialize(texts)}, error={error}");
    }
    // ExecutionFailure exposes only native offset. Derive width from actual failure
    // and original numeric input, never from an expected recipe.
    private static int OffendingLength(XmlFixture c, RegexExecutionFailure failure)
    {
        if (failure.Code is not (RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidInputDomain))
        {
            return 0;
        }

        var at = failure.Offset;
        if (at >= c.InputUnits.Length)
        {
            return 0;
        }

        if (failure.Code == RegexExecutionFailureCode.InvalidEncoding)
        {
            return 1;
        }

        var first = c.InputUnits[at];
        if (c.InputKind == "Utf16")
        {
            return first is >= 0xD800 and <= 0xDBFF ? 2 : 1;
        }

        return first < 0x80 ? 1 : first < 0xE0 ? 2 : first < 0xF0 ? 3 : 4;
    }
    private static bool ValidBounds(XmlFixture c, XmlFixtureError e)
    {
        var total = e.Location switch
        {
            "Flags" => c.Flags.Length,
            "Replacement" => c.ReplacementUnits?.Length ?? 0,
            "Pattern" => c.PatternUnits.Length,
            "Input" => c.InputUnits.Length,
            _ => 0
        };
        return e.Offset >= 0 && e.Length >= 0 && (long)e.Offset + e.Length <= total;
    }
}
