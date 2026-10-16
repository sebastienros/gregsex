using System.Text.Json;
using System.Text.Json.Serialization;

namespace GregsEx.Tests.Contracts.Posix;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixCorpus(string ContractVersion, string DataId, string DataDigest, PosixFixture[] Cases)
{
    internal static JsonSerializerOptions JsonOptions => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    internal static PosixCorpus Read(string json)
    {
        var result = JsonSerializer.Deserialize<PosixCorpus>(json, JsonOptions)
            ?? throw new InvalidDataException("Missing POSIX corpus.");
        if (result.ContractVersion != "posix-fixtures-1" || result.DataId != PosixLocale.C.ProviderId ||
            result.DataDigest != PosixLocale.C.DataDigest || result.Cases is not { Length: > 0 })
        {
            throw new InvalidDataException("Unknown POSIX fixture identity.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in result.Cases)
        {
            if (c is null || !ids.Add(c.Id))
            {
                throw new InvalidDataException("Null or duplicate case.");
            }

            c.Validate();
        }
        return result;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixFixture(string Id, string ProfileId, string Locale, string[] Capabilities,
    PosixFixtureProvenance Provenance, string Classification, string PatternKind, int[] PatternUnits,
    string InputKind, int[] InputUnits, int CompileFlags, int ExecutionFlags, string Operation,
    int StartAt, int? DestinationLength, string? ErrorCode, PosixFixtureLimits? Limits, PosixExpected[] Permitted)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || ProfileId is not ("PosixBre2018-CByte" or "PosixEre2018-CByte") ||
            Locale is null || Capabilities is not { Length: > 0 } || Capabilities.Distinct().Count() != Capabilities.Length ||
            Provenance is null || Provenance.Kind is not ("Specification" or "GregsExDisposition" or "Imported" or "Discovery") ||
            !Uri.TryCreate(Provenance.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            string.IsNullOrWhiteSpace(Provenance.Clause) || string.IsNullOrWhiteSpace(Provenance.License) ||
            Provenance.Kind is "Imported" or "Discovery" && string.IsNullOrWhiteSpace(Provenance.OracleId) ||
            Classification is not ("Defined" or "Invalid" or "Undefined" or "Unspecified" or "Optional" or "GregsEx" or "Extension"))
        {
            throw new InvalidDataException("Profile, classification and provenance required.");
        }

        foreach (var key in Capabilities)
        {
            if (key is null || key.Length != 6 || !key.StartsWith("PX-", StringComparison.Ordinal) ||
                !int.TryParse(key.AsSpan(3), out var n) || n < 1 || n > 20)
            {
                throw new InvalidDataException("Unknown POSIX inventory entry.");
            }
        }

        Units(PatternUnits, PatternKind == "Utf16" ? 65535 : 255);
        Units(InputUnits, InputKind == "Utf16" ? 65535 : 255);
        if (PatternKind is not ("Utf16" or "Bytes") || InputKind is not ("Utf16" or "Utf8" or "Bytes") ||
            CompileFlags < 0 || CompileFlags > 7 || ExecutionFlags < 0 || ExecutionFlags > 3 ||
            Operation is not ("Parse" or "Boolean" or "Find" or "FindCaptures" or "RegExec" or "RegError") ||
            StartAt < 0 || StartAt > InputUnits.Length || DestinationLength < 0 ||
            Operation is "RegExec" or "RegError" && (StartAt != 0 || DestinationLength is null) ||
            Operation == "RegError" && (!Enum.TryParse<PosixErrorCode>(ErrorCode, out var errorCode) ||
                !Enum.IsDefined(errorCode) || errorCode == PosixErrorCode.None) ||
            Operation != "RegError" && ErrorCode is not null ||
            Permitted is not { Length: > 0 })
        {
            throw new InvalidDataException("Invalid units/flags/operation.");
        }

        Limits?.Validate();
        foreach (var e in Permitted)
        {
            if (e is null || e.Status is not ("ParseSuccess" or "InvalidSyntax" or "InvalidPatternEncoding" or
                "UnsupportedFeature" or "UnsupportedRepresentation" or "Match" or "NoMatch" or "InvalidInput" or
                "DestinationTooSmall" or "LimitExceeded" or "Cancelled" or "TimedOut" or "OperationRejected" or "Formatted") ||
                e.Matches is null || e.NativeMatches is null || e.GroupCount <= 0 || e.RequiredBytes <= 0 ||
                e.Status is not ("ParseSuccess" or "Match" or "NoMatch" or "Formatted") && e.Error is null)
            {
                throw new InvalidDataException("Missing or invalid result.");
            }

            if ((e.Status == "Formatted") != (Operation == "RegError") ||
                (e.Status == "ParseSuccess" && Operation != "Parse") ||
                (e.Boolean is not null && Operation != "Boolean") ||
                e.Status == "Match" && Operation == "Boolean" && e.Boolean != true ||
                e.Status == "NoMatch" && Operation == "Boolean" && e.Boolean != false)
            {
                throw new InvalidDataException("Contradictory operation result.");
            }

            foreach (var m in e.Matches)
            {
                if (m is null || !Fits(m.Range.Index, m.Range.Length, InputUnits.Length) ||
                    m.Captures is not { Length: > 0 } || m.Captures[0] != new RegexCapture(true, m.Range.Index, m.Range.Length))
                {
                    throw new InvalidDataException("Invalid range/group zero.");
                }

                foreach (var capture in m.Captures)
                {
                    if (capture.Participated ? !Fits(capture.Index, capture.Length, InputUnits.Length) :
                        capture.Index != 0 || capture.Length != 0)
                    {
                        throw new InvalidDataException("Invalid capture participation.");
                    }
                }
            }
            if (e.Matches.Length > 0 && e.Status != "Match" ||
                e.DestinationUnchanged && e.NativeMatches.Length > 0 ||
                e.NativeMatches.Length > 0 && (Operation != "RegExec" || e.NativeMatches.Length != DestinationLength) ||
                Operation == "RegExec" && ((CompileFlags & 4) != 0 || DestinationLength == 0) && !e.DestinationUnchanged)
            {
                throw new InvalidDataException("Contradictory native publication.");
            }

            foreach (var match in e.NativeMatches)
            {
                if (match.Start == -1 ? match.End != -1 : match.Start < 0 || match.End < match.Start || match.End > InputUnits.Length)
                {
                    throw new InvalidDataException("Invalid native endpoints.");
                }
            }

            if (e.ErrorBytes is not null)
            {
                Units(e.ErrorBytes, 255);
            }

            if (e.Status == "Formatted" && (e.RequiredBytes is null || e.ErrorBytes is null ||
                e.ErrorBytes.Length != DestinationLength || e.RequiredBytes <= e.ErrorBytes.TakeWhile(b => b != 0).Count()))
            {
                throw new InvalidDataException("Invalid native error sizing.");
            }

            if (e.Error is { } error)
            {
                var total = error.Location switch
                {
                    "Pattern" => PatternUnits.Length,
                    "Input" => InputUnits.Length,
                    "Locale" => Locale.Length,
                    "Options" => 0,
                    _ => -1
                };
                var unit = error.Location == "Pattern" ? PatternKind == "Bytes" ? "Bytes" : "Utf16CodeUnits" :
                    error.Location == "Input" ? InputKind == "Utf16" ? "Utf16CodeUnits" : "Bytes" : "Utf16CodeUnits";
                if (string.IsNullOrWhiteSpace(error.Code) || !Fits(error.Offset, error.Length, total) ||
                    error.OffsetUnit != unit || !Enum.TryParse<PosixErrorCode>(error.PosixCode, out var nativeCode) ||
                    Enum.GetName(nativeCode) != error.PosixCode ||
                    !Enum.TryParse<PosixSyntaxDisposition>(error.Disposition, out var disposition) ||
                    Enum.GetName(disposition) != error.Disposition)
                {
                    throw new InvalidDataException("Invalid native diagnostic.");
                }
            }
        }
    }
    private static bool Fits(int offset, int length, int total) => offset >= 0 && length >= 0 && (long)offset + length <= total;
    private static void Units(int[]? values, int max)
    {
        if (values is null || values.Any(u => u < 0 || u > max))
        {
            throw new InvalidDataException("Numeric native units required.");
        }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixFixtureProvenance(string Kind, string Uri, string Clause, string License, string? OracleId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixExpected(string Status, bool? Boolean, int? GroupCount,
    PosixFixtureMatch[] Matches, PosixMatch[] NativeMatches, bool DestinationUnchanged,
    PosixFixtureError? Error, int? RequiredBytes, int[]? ErrorBytes);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixFixtureMatch(RegexMatchRange Range, RegexCapture[] Captures);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixFixtureError(string Code, string PosixCode, string Disposition,
    string Location, int Offset, int Length, string OffsetUnit);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record PosixFixtureLimits(long? MaxWork, long? MaxCompileWork, int? MaxPatternUnits,
    long? MaxPlanBytes, long? MaxCompileMemoryBytes, long? MaxWorkspaceBytes, int? FrameCapacity,
    int? HistoryCapacity, bool Cancelled)
{
    internal void Validate()
    {
        if (MaxWork <= 0 || MaxCompileWork <= 0 || MaxPatternUnits <= 0 || MaxPlanBytes <= 0 ||
            MaxCompileMemoryBytes <= 0 || MaxWorkspaceBytes <= 0 || FrameCapacity <= 0 || HistoryCapacity <= 0)
        {
            throw new InvalidDataException("Ceilings must be positive finite overrides.");
        }
    }
}
