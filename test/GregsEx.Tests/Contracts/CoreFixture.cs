using System.Text.Json;
using System.Text.Json.Serialization;

namespace GregsEx.Tests.Contracts;

// Nonshipping interchange types. Numeric native units preserve bytes and lone surrogates.
public sealed record CoreFixtureManifest(
    [property: JsonPropertyName("$schema")] string Schema,
    [property: JsonPropertyName("schema_version")] string SchemaVersion, CoreFixture[] Cases)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static CoreFixtureManifest Read(Stream stream)
    {
        var result = JsonSerializer.Deserialize<CoreFixtureManifest>(stream, JsonOptions)
            ?? throw new InvalidDataException("Missing fixture manifest.");
        if (result.SchemaVersion != "1.0" || result.Schema != "CoreFixtures.schema.json" || result.Cases is null)
        {
            throw new InvalidDataException("Unsupported fixture contract.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fixture in result.Cases)
        {
            fixture.Validate();
            if (!ids.Add(fixture.Id))
            {
                throw new InvalidDataException("Duplicate fixture ID.");
            }
        }
        return result;
    }
}

public sealed record CoreFixture(
    string Id, string[] Requirements, string Provenance, string License, string Oracle,
    string ProfileId, string DataId, RegularCoreMode Mode, RegularCoreFlags Flags,
    RegexInputKind InputKind, RegexOffsetUnit PatternUnit,
    int[] Pattern, int[] Input, string Operation, int StartAt, int? DestinationLength,
    RegexSearchStatus? ExpectedStatus, RegexMatchRange? ExpectedRange, RegexCapture[]? ExpectedGroups,
    RegexDiagnosticCode? ExpectedDiagnostic, int? ExpectedDiagnosticOffset, int? ExpectedDiagnosticLength,
    RegexExecutionFailureCode? ExpectedFailure, int? ExpectedFailureOffset, long? ExpectedRequiredCapacity)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Requirements is not { Length: > 0 } ||
            string.IsNullOrWhiteSpace(Provenance) || string.IsNullOrWhiteSpace(License) || string.IsNullOrWhiteSpace(Oracle))
        {
            throw new InvalidDataException("Fixture identity and provenance are required.");
        }

        var profile = new RegularCoreProfile(Mode, Flags);
        if (ProfileId != profile.Id || DataId != profile.DataId ||
            !profile.Supports(InputKind) || !Enum.IsDefined(PatternUnit))
        {
            throw new InvalidDataException("Unsupported fixture representation.");
        }

        if (Pattern is null || Input is null)
        {
            throw new InvalidDataException("Exact units are required.");
        }

        foreach (var value in Pattern)
        {
            if (value < 0 || value > (PatternUnit == RegexOffsetUnit.Bytes ? 255 : 65535))
            {
                throw new InvalidDataException("Pattern units exceed the declared representation.");
            }
        }

        foreach (var value in Input)
        {
            if (value < 0 || value > (InputKind == RegexInputKind.Utf16 ? 65535 : 255))
            {
                throw new InvalidDataException("Input units exceed the declared representation.");
            }
        }

        if (StartAt < 0 || StartAt > Input.Length || DestinationLength < 0)
        {
            throw new InvalidDataException("Invalid fixture bounds.");
        }
        // Iteration sequences are deliberately not admitted by v1; integration owns them.
        if (Operation is not ("Parse" or "Boolean" or "Range" or "Captures"))
        {
            throw new InvalidDataException("Unknown fixture operation.");
        }

        if (Operation == "Parse" ? ExpectedDiagnostic is null : ExpectedStatus is null)
        {
            throw new InvalidDataException("Missing expected outcome.");
        }

        if (ExpectedStatus is { } status && !Enum.IsDefined(status) ||
            ExpectedDiagnostic is { } code && !Enum.IsDefined(code) ||
            ExpectedFailure is { } failure && !Enum.IsDefined(failure))
        {
            throw new InvalidDataException("Unknown structured outcome.");
        }

        if (ExpectedRange is { } range && (range.Index < 0 || range.Length < 0 ||
            (long)range.Index + range.Length > Input.Length))
        {
            throw new InvalidDataException("Invalid expected range.");
        }

        if (ExpectedStatus == RegexSearchStatus.Match && Operation != "Boolean" && ExpectedRange is null)
        {
            throw new InvalidDataException("A range/capture match requires an expected range.");
        }

        if (ExpectedRange is not null && ExpectedStatus != RegexSearchStatus.Match)
        {
            throw new InvalidDataException("Only Match carries a range.");
        }

        if (ExpectedGroups is { } groups)
        {
            foreach (var group in groups)
            {
                if (group.Participated ? group.Index < 0 || group.Length < 0 ||
                    (long)group.Index + group.Length > Input.Length : group.Index != 0 || group.Length != 0)
                {
                    throw new InvalidDataException("Invalid capture participation or native range.");
                }
            }
        }

        if (ExpectedDiagnostic is not null && (ExpectedDiagnosticOffset is null or < 0 ||
            ExpectedDiagnosticLength is null or < 0 ||
            (long)ExpectedDiagnosticOffset + ExpectedDiagnosticLength > Pattern.Length))
        {
            throw new InvalidDataException("A diagnostic requires a native pattern source range.");
        }

        if (ExpectedFailure is not null && (ExpectedFailureOffset is null or < 0 || ExpectedFailureOffset > Input.Length))
        {
            throw new InvalidDataException("A failure requires a native input position.");
        }

        if (ExpectedFailure == RegexExecutionFailureCode.DestinationTooSmall && ExpectedRequiredCapacity is null or < 1)
        {
            throw new InvalidDataException("Short capture destinations require a required count.");
        }
    }
}
