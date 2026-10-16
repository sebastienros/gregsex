using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GregsEx.Tests.Contracts.EcmaScript;

// Nonshipping interchange, separate from CoreFixture v1. JSON uses camelCase.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaCorpus(string ContractVersion, string ProfileId, string DataId, EcmaFixture[] Cases)
{
    internal static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static EcmaCorpus Read(string json)
    {
        var corpus = JsonSerializer.Deserialize<EcmaCorpus>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing ECMAScript corpus.");
        if (corpus.ContractVersion != "ecmascript-fixtures-1" || corpus.ProfileId != "EcmaScript2025" ||
            corpus.DataId != new EcmaScriptProfile().DataId || corpus.Cases is null)
        {
            throw new InvalidDataException("Unsupported ECMAScript corpus identity.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in corpus.Cases)
        {
            if (fixture is null || !ids.Add(fixture.Id))
            {
                throw new InvalidDataException("Missing/duplicate fixture identity.");
            }

            fixture.Validate();
        }
        return corpus;
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaFixture(string Id, string[] Capabilities, EcmaProvenance Provenance,
    int[] PatternUnits, int[] InputUnits, string Flags, string Operation, int StartAt,
    int? DestinationLength, EcmaFixtureLimits? Limits, string LastIndexBefore,
    int[]? ReplacementUnits, string? SplitLimit, EcmaExpectation Expected)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Capabilities is not { Length: > 0 } ||
            Provenance is null || string.IsNullOrWhiteSpace(Provenance.Clause) ||
            string.IsNullOrWhiteSpace(Provenance.License) || !Uri.TryCreate(Provenance.Uri, UriKind.Absolute, out _) ||
            Provenance.Kind is not ("Specification" or "Imported" or "Discovery") ||
            (Provenance.Kind != "Specification" && string.IsNullOrWhiteSpace(Provenance.OracleId)))
        {
            throw new InvalidDataException("Exact identity and provenance are required.");
        }

        foreach (var capability in Capabilities)
        {
            if (capability is null || capability.Length != 6 || !capability.StartsWith("ES-", StringComparison.Ordinal) ||
                !int.TryParse(capability.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id is < 1 or > 20)
            {
                throw new InvalidDataException("Unknown capability identity.");
            }
        }

        ValidateUnits(PatternUnits);
        ValidateUnits(InputUnits);
        if (ReplacementUnits is not null)
        {
            ValidateUnits(ReplacementUnits);
        }

        if (StartAt < 0 || StartAt > InputUnits.Length || DestinationLength < 0 ||
            Flags is null || !Numeric(LastIndexBefore) || (SplitLimit is not null && !Numeric(SplitLimit)) ||
            Operation is not ("Parse" or "Boolean" or "Range" or "Captures" or "Exec" or "Test" or "ExecNext" or
                "Match" or "MatchAll" or "Search" or "Replace" or "ReplaceAll" or "Split") || Expected is null ||
            Expected.Matches is null || (Expected.LastIndexAfter is not null && !Numeric(Expected.LastIndexAfter)))
        {
            throw new InvalidDataException("Invalid operation or numeric host state.");
        }

        if (Expected.Status is not ("ParseSuccess" or "InvalidSyntax" or "Match" or "NoMatch" or "HostArgumentError" or
            "HostLimitExceeded" or "InvalidInput" or "DestinationTooSmall" or "LimitExceeded" or "Cancelled" or "TimedOut"))
        {
            throw new InvalidDataException("Unknown outcome.");
        }

        if (Expected.Status is not "Match" && Expected.Matches.Length != 0)
        {
            throw new InvalidDataException("Only a successful match operation has match records.");
        }

        if (Operation == "Parse" && Expected.Status is not ("ParseSuccess" or "InvalidSyntax" or "LimitExceeded"))
        {
            throw new InvalidDataException("Invalid parse outcome.");
        }

        if (Expected.Status is not ("ParseSuccess" or "Match" or "NoMatch") && Expected.Error is null)
        {
            throw new InvalidDataException("A failure needs structured details.");
        }

        if (Expected.Texts is not null)
        {
            foreach (var text in Expected.Texts)
            {
                if (text is not null)
                {
                    ValidateUnits(text);
                }
            }
        }

        Limits?.Validate();
        foreach (var match in Expected.Matches)
        {
            if (match is null || !RangeFits(match.Index, match.Length, InputUnits.Length) ||
                match.Captures is not { Length: > 0 } || match.Names is null ||
                match.Captures[0] != new EcmaCapture(true, match.Index, match.Length))
            {
                throw new InvalidDataException("Invalid match range or group zero.");
            }

            foreach (var cap in match.Captures)
            {
                ValidateCapture(cap);
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in match.Names)
            {
                if (name is null || string.IsNullOrEmpty(name.Name) || !names.Add(name.Name) || name.GroupIds is not { Length: > 0 })
                {
                    throw new InvalidDataException("Invalid name alias map.");
                }

                var previous = 0;
                var selected = new EcmaCapture(false, 0, 0);
                var participants = 0;
                foreach (var id in name.GroupIds)
                {
                    if (id <= previous || id >= match.Captures.Length)
                    {
                        throw new InvalidDataException("Invalid alias group ID.");
                    }

                    previous = id;
                    if (match.Captures[id].Participated)
                    { selected = match.Captures[id]; participants++; }
                }
                ValidateCapture(name.Capture);
                if (participants > 1 || name.Capture != selected)
                {
                    throw new InvalidDataException("Ambiguous named participation.");
                }
            }
        }
        if (Expected.Error is { } error)
        {
            var total = error.Location switch
            {
                "Pattern" => PatternUnits.Length,
                "Flags" => Flags.Length,
                "Input" => InputUnits.Length,
                "Host" => 0,
                _ => -1
            };
            if (string.IsNullOrWhiteSpace(error.Code) || string.IsNullOrWhiteSpace(error.FeatureId) ||
                error.OffsetUnit != "Utf16CodeUnits" || error.WorkConsumed < 0 || error.RequiredCapacity < 0 ||
                !RangeFits(error.Offset, error.Length, total))
            {
                throw new InvalidDataException("Invalid error source domain.");
            }
        }
    }

    private void ValidateCapture(EcmaCapture? capture)
    {
        if (capture is null || (capture.Participated ? !RangeFits(capture.Index, capture.Length, InputUnits.Length) :
            capture.Index != 0 || capture.Length != 0))
        {
            throw new InvalidDataException("Invalid capture participation/range.");
        }
    }
    private static void ValidateUnits(int[]? units)
    {
        if (units is null || units.Any(u => u < 0 || u > 65535))
        {
            throw new InvalidDataException("Exact UTF-16 units required.");
        }
    }
    private static bool RangeFits(int offset, int length, int total)
        => offset >= 0 && length >= 0 && (long)offset + length <= total;
    private static bool Numeric(string? value)
        => value is "NaN" or "+Infinity" or "-Infinity" or "-0" ||
            (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number));
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaFixtureLimits(int? MaxPatternUnits, long? MaxCompileWork,
    long? MaxWork, long? MaxWorkspaceBytes, int? FrameCapacity, int? MaxResults,
    int? MaxOutputUnits, long? MaxHostMemoryBytes, bool Cancelled, long? TimeoutTicks)
{
    internal void Validate()
    {
        if (MaxPatternUnits <= 0 || MaxCompileWork <= 0 || MaxWork <= 0 ||
            MaxWorkspaceBytes <= 0 || FrameCapacity <= 0 || MaxResults <= 0 ||
            MaxOutputUnits <= 0 || MaxHostMemoryBytes <= 0 || TimeoutTicks <= 0)
        {
            throw new InvalidDataException("Fixture limits must be finite positive overrides.");
        }
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaProvenance(string Kind, string Uri, string Clause, string License, string? OracleId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaExpectation(string Status, string? LastIndexAfter, EcmaMatch[] Matches,
    int[]?[]? Texts, EcmaError? Error);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaMatch(int Index, int Length, EcmaCapture[] Captures,
    EcmaNamedGroup[] Names, bool HasIndices);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaCapture(bool Participated, int Index, int Length);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaNamedGroup(string Name, int[] GroupIds, EcmaCapture Capture);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record EcmaError(string Code, string FeatureId, string Location, int Offset,
    int Length, string OffsetUnit, long? WorkConsumed, long RequiredCapacity);

// Special numeric values are the exact tokens NaN, +Infinity, -Infinity, -0;
// finite values use invariant round-trip decimal. Strings preserve JS numeric
// state without JSON nonfinite numbers or lossy UTF-16 string encoding.
