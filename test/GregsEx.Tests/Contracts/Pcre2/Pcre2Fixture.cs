using System.Text.Json;
using System.Text.Json.Serialization;

namespace GregsEx.Tests.Contracts.Pcre2;

public sealed record Pcre2FixtureCorpus
{
    [JsonPropertyName("$schema")] public required string Schema { get; init; }
    public required string SchemaVersion { get; init; }
    public required string ContractVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string SourceCommit { get; init; }
    public required string Origin { get; init; }
    public required Pcre2FixtureCase[] Cases { get; init; }
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };
    public static Pcre2FixtureCorpus Read(string json)
    {
        var corpus = JsonSerializer.Deserialize<Pcre2FixtureCorpus>(json, JsonOptions) ?? throw new InvalidDataException("Missing corpus.");
        if (corpus.SchemaVersion != "1.0" || corpus.ContractVersion != "pcre2-fixtures-1" || corpus.ProfileId != Pcre2Profile.ContractId ||
            corpus.SourceCommit != Pcre2Profile.SourceRevision || corpus.Origin != "source-before-implementation" || corpus.Cases.Length == 0)
        {
            throw new InvalidDataException("Wrong corpus identity.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in corpus.Cases)
        {
            if (!ids.Add(c.Id) || c.Requirements.Length == 0 || c.PatternUnits.Any(v => v < 0 || v > 65535) ||
                c.SubjectUnits.Any(v => v < 0 || v > (c.Width == 8 ? 255 : 65535)) || c.Width is not (8 or 16) ||
                c.StartAt < 0 || c.StartAt > c.SubjectUnits.Length || c.PairCapacity < 1 || c.DestinationCapacity < 0 ||
                c.Provenance.Source.Length == 0 || c.Provenance.Line < 1 || c.Provenance.License.Length == 0 ||
                c.CharacterTables is { } tables && (tables.Length != 1088 || tables.Any(v => v < 0 || v > 255)))
            {
                throw new InvalidDataException("Invalid PCRE case input.");
            }

            if (c.PatternKind is not ("Chars" or "Bytes") || c.PatternKind == "Bytes" && c.PatternUnits.Any(v => v > 255) ||
                c.Optimization.Length != 3 || c.Newline is < 1 or > 6 || c.Bsr is < 1 or > 2 || c.MaxVariableLookbehind < 0 ||
                c.WorkLimit < 0 || c.DepthLimit < 0 || c.HeapBytes < 0 || c.OffsetLimit < 0 ||
                (c.CompileFlags & ~0xEFFFFFFFu) != 0 || (c.ExtraFlags & ~0x1FFFFu) != 0 ||
                (c.MatchFlags & ~0xE004603Fu) != 0 || c.ReplacementUnits.Any(v => v < 0 || v > (c.Width == 8 ? 255 : 65535)) ||
                c.Operation is not ("Match" or "Compile" or "Iterate" or "Substitute" or "Projection" or "ExcludedHost"))
            {
                throw new InvalidDataException("Unknown operation/pattern domain.");
            }

            if (c.Expected.Outcome is not ("Match" or "NoMatch" or "Partial" or "Error" or "CompileAccepted" or
                "CompileError" or "Iterate" or "Substitute" or "CapabilityError" or "LimitExceeded"))
            {
                throw new InvalidDataException("Unknown expected outcome.");
            }

            foreach (var pair in c.InitialPairs.Concat(c.Expected.Pairs).Concat(c.Expected.Iterations.SelectMany(p => p)))
            {
                if (pair.Start < 0 || pair.End < 0 || !pair.Participated && (pair.Start != 0 || pair.End != 0))
                {
                    throw new InvalidDataException("Invalid endpoint representation.");
                }
            }

            if (c.Expected.Outcome == "Partial" && c.Expected.Pairs.Length != 1)
            {
                throw new InvalidDataException("Only group0 is defined after partial.");
            }

            if (c.Expected.Pairs.Length > c.PairCapacity || c.InitialPairs.Length > c.PairCapacity ||
                c.Expected.Outcome == "Match" && c.Expected.NativeReturn is < 0 ||
                c.Expected.Outcome == "Partial" && c.Expected.NativeReturn != -2 ||
                c.Expected.Outcome == "NoMatch" && c.Expected.NativeReturn != -1)
            {
                throw new InvalidDataException("Inconsistent native expectation.");
            }

            foreach (var trace in c.Expected.Callouts)
            {
                if (trace.Number is < 0 or > 255 || trace.PatternPosition < 0 || trace.CurrentPosition < 0 ||
                    trace.CurrentPosition > c.SubjectUnits.Length || trace.CaptureTop < 1 || trace.CaptureLast < 0 ||
                    trace.Flags > 3 || trace.StringUnits.Any(v => v < 0 || v > (c.Width == 8 ? 255 : 65535)))
                {
                    throw new InvalidDataException("Invalid callout trace.");
                }
            }
        }
        return corpus;
    }
}
public sealed record Pcre2FixtureCase
{
    public required string Id { get; init; }
    public required string[] Requirements { get; init; }
    public required string Operation { get; init; }
    public required string PatternKind { get; init; }
    public required int[] PatternUnits { get; init; }
    public required int[] SubjectUnits { get; init; }
    public required int[] ReplacementUnits { get; init; }
    public required int Width { get; init; }
    public required uint CompileFlags { get; init; }
    public required uint ExtraFlags { get; init; }
    public required uint MatchFlags { get; init; }
    public required uint SubstituteFlags { get; init; }
    public required int Newline { get; init; }
    public required int Bsr { get; init; }
    public required bool[] Optimization { get; init; }
    public required int MaxVariableLookbehind { get; init; }
    public required int StartAt { get; init; }
    public required int? OffsetLimit { get; init; }
    public required int PairCapacity { get; init; }
    public required int DestinationCapacity { get; init; }
    public required int[]? CharacterTables { get; init; }
    public required long? WorkLimit { get; init; }
    public required int? DepthLimit { get; init; }
    public required long? HeapBytes { get; init; }
    public required int[] CallbackReturns { get; init; }
    public required int[] SubstituteCallbackReturns { get; init; }
    public required int[][] CaseCallbackOutputs { get; init; }
    public required Pcre2FixturePair[] InitialPairs { get; init; }
    public required Pcre2FixtureProvenance Provenance { get; init; }
    public required Pcre2FixtureExpected Expected { get; init; }
}
public sealed record Pcre2FixtureProvenance(string Kind, string Source, int Line, string License, string Note);
public sealed record Pcre2FixturePair(bool Participated, int Start, int End);
public sealed record Pcre2FixtureCallout(int Version, int Number, bool IsString, int SubjectLength, int CaptureTop, int CaptureLast,
    int StartMatch, int CurrentPosition, int PatternPosition, int NextItemLength, int StringOffset, int[] StringUnits,
    uint Flags, int ReturnCode, int[]? MarkUnits, Pcre2FixturePair[] Captures);
public sealed record Pcre2FixtureSubstituteCallout(int Version, int Count, int OutputStart, int OutputEnd,
    int[] ProposedOutput, Pcre2FixturePair[] Captures, int ReturnCode);
public sealed record Pcre2FixtureCaseCallout(int Mode, int[] InputUnits, int[] OutputUnits, int? OutputCapacity, int RequiredUnits, bool Overlap);
public sealed record Pcre2FixtureExpected(string Outcome, int? NativeReturn, Pcre2FixturePair[] Pairs,
    Pcre2FixturePair[][] Iterations, int[] OutputUnits, int[]? MarkUnits, Pcre2FixtureCallout[] Callouts,
    Pcre2FixtureSubstituteCallout[] SubstituteCallouts, Pcre2FixtureCaseCallout[] CaseCallouts,
    string? NameQuery, int[] NameNumbers, string? DiagnosticKind, int? NativeOffset, int? SourceOffset, long? RequiredCapacity);
