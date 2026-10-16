using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GregsEx.Tests.Contracts.Xml;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlCorpus(string ContractVersion, string DataId, XmlFixture[] Cases)
{
    internal static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static XmlCorpus Read(string json)
    {
        var result = JsonSerializer.Deserialize<XmlCorpus>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing XML corpus.");
        if (result.ContractVersion != "xml-fixtures-1" || result.DataId != new XsdProfile().DataId || result.Cases is null)
        {
            throw new InvalidDataException("Unknown XML fixture identity.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in result.Cases)
        {
            if (c is null || !ids.Add(c.Id))
            {
                throw new InvalidDataException("Missing or duplicate case.");
            }

            c.Validate();
        }
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlFixture(string Id, string ProfileId, string[] Capabilities,
    XmlProvenance Provenance, string PatternKind, int[] PatternUnits, string InputKind, int[] InputUnits,
    bool AbsentInput, string Flags, string Operation, int StartAt, int? DestinationLength,
    int[]? ReplacementUnits, XmlFixtureLimits? Limits, XmlExpected[] Permitted)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || ProfileId is not ("Xsd11" or "XPath31") ||
            Capabilities is not { Length: > 0 } || Provenance is null ||
            Provenance.Kind is not ("Specification" or "GregsExInterpretation" or "Imported" or "Discovery") ||
            !Uri.TryCreate(Provenance.Uri, UriKind.Absolute, out _) ||
            string.IsNullOrWhiteSpace(Provenance.Clause) || string.IsNullOrWhiteSpace(Provenance.License) ||
            (Provenance.Kind is "Imported" or "Discovery" && string.IsNullOrWhiteSpace(Provenance.OracleId)))
        {
            throw new InvalidDataException("Exact profile, case and provenance required.");
        }

        foreach (var key in Capabilities)
        {
            if (key is null || key.Length != 6 || key[..3] is not ("XS-" or "XP-") ||
                !int.TryParse(key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ||
                n < 1 || n > (key.StartsWith("XS-", StringComparison.Ordinal) ? 12 : 17))
            {
                throw new InvalidDataException("Unknown XML inventory ID.");
            }
        }

        Units(PatternUnits, PatternKind == "Utf16" ? 65535 : 255);
        Units(InputUnits, InputKind == "Utf16" ? 65535 : 255);
        if (ReplacementUnits is not null)
        {
            Units(ReplacementUnits, 65535);
        }

        if (PatternKind is not ("Utf16" or "Bytes") || InputKind is not ("Utf16" or "Utf8" or "Bytes") || Flags is null ||
            StartAt < 0 || StartAt > InputUnits.Length || DestinationLength < 0 ||
            AbsentInput && InputUnits.Length != 0 ||
            Operation is not ("Parse" or "Membership" or "Find" or "FindCaptures" or "Matches" or "Replace" or "Tokenize" or "TokenizeOne" or "AnalyzeString") ||
            ProfileId == "Xsd11" && (Flags.Length != 0 || Operation is not ("Parse" or "Membership" or "Find" or "FindCaptures")) ||
            Permitted is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid operation/domain.");
        }

        Limits?.Validate();
        foreach (var expected in Permitted)
        {
            if (expected is null || expected.Status is not ("ParseSuccess" or "InvalidSyntax" or "Match" or "NoMatch" or "FunctionError" or
                "InvalidInput" or "UnsupportedRepresentation" or "DestinationTooSmall" or "LimitExceeded" or "Cancelled" or "TimedOut" or "HostLimitExceeded") ||
                expected.Matches is null || expected.Tree is null ||
                expected.Status is not ("ParseSuccess" or "Match" or "NoMatch") && expected.Error is null)
            {
                throw new InvalidDataException("Unknown/missing outcome.");
            }

            foreach (var m in expected.Matches)
            {
                if (m is null || !Fits(m.Range.Index, m.Range.Length, InputUnits.Length) ||
                    m.Captures is not { Length: > 0 } || m.Captures[0] != new RegexCapture(true, m.Range.Index, m.Range.Length))
                {
                    throw new InvalidDataException("Invalid native range/group zero.");
                }

                foreach (var c in m.Captures)
                {
                    if (c.Participated ? !Fits(c.Index, c.Length, InputUnits.Length) : c.Index != 0 || c.Length != 0)
                    {
                        throw new InvalidDataException("Invalid capture participation.");
                    }
                }
            }
            if (expected.Texts is not null)
            {
                foreach (var t in expected.Texts)
                {
                    Units(t, 65535);
                }
            }

            for (var i = 0; i < expected.Tree.Length; i++)
            {
                var node = expected.Tree[i];
                if (!Enum.IsDefined(node.Kind) || node.ParentIndex < -1 || node.ParentIndex >= i ||
                    !Fits(node.Range.Index, node.Range.Length, InputUnits.Length) ||
                    (node.Kind == XPathAnalyzeNodeKind.Group ? node.GroupNumber < 1 : node.GroupNumber != 0) ||
                    (node.ParentIndex == -1 ? node.Kind is not (XPathAnalyzeNodeKind.Match or XPathAnalyzeNodeKind.NonMatch) :
                        node.Kind is XPathAnalyzeNodeKind.Match or XPathAnalyzeNodeKind.NonMatch))
                {
                    throw new InvalidDataException("Invalid ordered analyze-string tree.");
                }

                if (node.ParentIndex >= 0)
                {
                    var parent = expected.Tree[node.ParentIndex];
                    if (parent.Kind is not (XPathAnalyzeNodeKind.Match or XPathAnalyzeNodeKind.Group) ||
                        node.Range.Index < parent.Range.Index || (long)node.Range.Index + node.Range.Length > (long)parent.Range.Index + parent.Range.Length)
                    {
                        throw new InvalidDataException("Invalid tree containment.");
                    }
                }
            }
            if (expected.Error is { } error)
            {
                var total = error.Location switch
                {
                    "Pattern" => PatternUnits.Length,
                    "Flags" => Flags.Length,
                    "Replacement" => ReplacementUnits?.Length ?? 0,
                    "Input" => InputUnits.Length,
                    "Host" => 0,
                    _ => -1
                };
                if (string.IsNullOrWhiteSpace(error.Code) || !Fits(error.Offset, error.Length, total) ||
                    error.OffsetUnit != ((error.Location == "Input" && InputKind != "Utf16") || (error.Location == "Pattern" && PatternKind == "Bytes") ? "Bytes" : "Utf16CodeUnits") ||
                    (error.Code.StartsWith("FORX", StringComparison.Ordinal) ? error.Namespace != XmlRegexDiagnostic.ErrorNamespace : error.Namespace is not null))
                {
                    throw new InvalidDataException("Invalid diagnostic coordinate/namespace.");
                }
            }
        }
    }
    private static bool Fits(int offset, int length, int total) => offset >= 0 && length >= 0 && (long)offset + length <= total;
    private static void Units(int[]? units, int maximum)
    {
        if (units is null || units.Any(u => u < 0 || u > maximum))
        {
            throw new InvalidDataException("Numeric native units required.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlProvenance(string Kind, string Uri, string Clause, string License, string? OracleId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlExpected(string Status, bool? Boolean, XmlFixtureMatch[] Matches,
    int[][]? Texts, XPathAnalyzeNode[] Tree, XmlFixtureError? Error);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlFixtureMatch(RegexMatchRange Range, RegexCapture[] Captures);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlFixtureError(string Code, string Location, int Offset, int Length, string OffsetUnit, string? Namespace);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record XmlFixtureLimits(long? MaxWork, long? MaxCompileWork, int? FrameCapacity,
    int? MaxResults, int? MaxOutputUnits, long? MaxHostMemoryBytes, bool Cancelled)
{
    internal void Validate()
    {
        if (MaxWork <= 0 || MaxCompileWork <= 0 || FrameCapacity <= 0 || MaxResults <= 0 || MaxOutputUnits <= 0 || MaxHostMemoryBytes <= 0)
        {
            throw new InvalidDataException("Limits must be finite positive overrides.");
        }
    }
}
