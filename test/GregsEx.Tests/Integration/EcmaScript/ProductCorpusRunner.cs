using System.Globalization;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;

namespace GregsEx.Tests.Integration.EcmaScript;

// Shared, reflection-free product procedure linked into the native consumer.
// Expectations are read only from independently frozen corpus files.
internal static class ProductCorpusRunner
{
    internal static JsonElement ReadCorpus(string directory, string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file)));
        var corpus = document.RootElement;
        Equal("ecmascript-fixtures-1", Text(corpus, "contractVersion"), "corpus version");
        Equal("EcmaScript2025", Text(corpus, "profileId"), "profile identity");
        Equal(new EcmaScriptProfile().DataId, Text(corpus, "dataId"), "data identity");
        return corpus.Clone();
    }

    internal static int Run(string directory, string file, Action<string>? observe = null)
    {
        var corpus = ReadCorpus(directory, file);
        var failures = new List<string>();
        var count = 0;
        foreach (var fixture in corpus.GetProperty("cases").EnumerateArray())
        {
            var id = Text(fixture, "id");
            count++;
            try
            { Execute(fixture, file == "resource-corpus.json", observe); }
            catch (Exception error) { failures.Add(id + ": " + error.Message); }
        }
        if (failures.Count != 0)
        {
            throw new InvalidOperationException($"{file}: {failures.Count}/{count} failures\n" + string.Join("\n", failures));
        }

        return count;
    }

    internal static void RawFlag(string directory)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "raw-flag-boundary.json")));
        var f = doc.RootElement;
        Check(!EcmaScriptProfile.TryParseFlags(Units(f.GetProperty("rawFlagUnits")), out var profile, out var d), "raw flag rejected");
        Check(profile is null, "no raw flag descriptor");
        Equal(RegexDiagnosticCode.InvalidSyntax, d.Diagnostic.Code, "raw flag category");
        Equal(EcmaScriptDiagnosticLocation.Flags, d.Location, "raw flag domain");
        Equal(0, d.Diagnostic.Offset, "raw flag offset");
        Equal(1, d.Diagnostic.Length, "raw flag length");
        Equal("ES-001", d.FeatureId, "raw flag feature");
    }

    internal static void Execute(JsonElement f, bool resource, Action<string>? observe)
    {
        var expected = f.GetProperty("expected");
        var error = expected.GetProperty("error");
        var expectedStatus = Text(expected, "status");
        var operation = Text(f, "operation");
        string source = Units(f.GetProperty("patternUnits")), input = Units(f.GetProperty("inputUnits"));
        var flags = Text(f, "flags");
        var l = f.GetProperty("limits");
        var compile = new RegexCompileOptions(limits: new(maxPatternUnits: Int(l, "maxPatternUnits") ?? 65_536,
            maxCompileWork: Long(l, "maxCompileWork") ?? 10_000_000),
            defaultMatchLimits: new(maxWork: Long(l, "maxWork") ?? 10_000_000,
                maxWorkspaceBytes: Long(l, "maxWorkspaceBytes") ?? 67_108_864));
        if (!EcmaScriptProfile.TryParseFlags(flags, out var profile, out var flagDiagnostic))
        {
            Equal("InvalidSyntax", expectedStatus, "flag status");
            Diagnostic(flagDiagnostic, error, flags.Length, exact: true);
            return;
        }
        if (!RegexPattern.TryParse(source, profile!, out var pattern, out var diagnostic, compile))
        {
            var status = diagnostic.Code == RegexDiagnosticCode.InvalidSyntax ? "InvalidSyntax" : "LimitExceeded";
            Equal(expectedStatus, status, "parse status");
            Check(!EcmaScriptMatcher.TryParse(source, profile!, out _, out var detailed, compile), "typed parse agrees");
            Equal(diagnostic, detailed.Diagnostic, "public parse diagnostics agree");
            Diagnostic(detailed, error, source.Length, exact: !resource && Text(f.GetProperty("provenance"), "kind") == "Specification");
            observe?.Invoke(Text(f, "id") + " diagnostic=" + diagnostic.Code + " domain=" + detailed.Location +
                " offset=" + diagnostic.Offset + " length=" + diagnostic.Length + " feature=" + detailed.FeatureId);
            return;
        }
        if (operation == "Parse")
        { Equal("ParseSuccess", expectedStatus, "successful parse status"); return; }
        var frames = Int(l, "frameCapacity") ?? 4096;
        var host = new EcmaScriptHostLimits(Int(l, "maxResults") ?? 1_000_000,
            Int(l, "maxOutputUnits") ?? 16_777_216, Long(l, "maxHostMemoryBytes") ?? 67_108_864);
        using var cancelled = new CancellationTokenSource();
        if (l.ValueKind == JsonValueKind.Object && l.GetProperty("cancelled").GetBoolean())
        {
            cancelled.Cancel();
        }

        var ticks = Long(l, "timeoutTicks");
        var options = new RegexMatchOptions { CancellationToken = cancelled.Token, Timeout = ticks is null ? null : TimeSpan.FromTicks(ticks.Value) };
        EcmaScriptMatcher? matcher = null;
        try
        {
            if (ticks is not null)
            {
                // Deterministic control: expire the existing cumulative budget at the VM seam,
                // then execute the actual VM and public Exec failure/clearing path. No sleeps,
                // zero timeout or race with a fast empty-subject return.
                var controlled = new RegexPattern(new ExpiredExecutor(pattern!.EcmaExecutor!), compile, pattern.RetainedBytes);
                matcher = EcmaScriptMatcher.Bind(controlled, frameCapacity: frames, hostLimits: host);
            }
            else
            {
                matcher = EcmaScriptMatcher.Bind(pattern!, frameCapacity: frames, hostLimits: host);
            }

            matcher.LastIndex = Number(Text(f, "lastIndexBefore"));
            var matches = expected.GetProperty("matches");
            string actualStatus;
            switch (operation)
            {
                case "Exec":
                case "ExecNext":
                    {
                        var captures = new RegexCapture[Int(f, "destinationLength") ?? pattern!.GroupCount];
                        var status = operation == "Exec" ? matcher.Exec(input, captures, out var range, options)
                            : matcher.ExecNext(input, captures, out range, options);
                        actualStatus = status.ToString();
                        if (status == RegexSearchStatus.Match)
                        {
                            Equal(1, matches.GetArrayLength(), "borrowed result count");
                            Captures(pattern!, matches[0], range, captures, profile!.Flags);
                        }
                        else
                        {
                            Equal(0, matches.GetArrayLength(), "failed result count");
                            if (status != RegexSearchStatus.DestinationTooSmall)
                            {
                                foreach (var capture in captures)
                                {
                                    Equal(default, capture, "failure clears captures");
                                }
                            }
                        }
                        if (error.ValueKind != JsonValueKind.Null)
                        {
                            Failure(matcher.LastFailure, error, resource, observe, Text(f, "id"));
                        }

                        if (!resource)
                        {
                            GenericWitness(pattern!, input, Number(Text(f, "lastIndexBefore")), status, range, captures);
                        }

                        break;
                    }
                case "Test":
                    actualStatus = matcher.Test(input, options).ToString();
                    break;
                case "MatchAll":
                    {
                        var i = 0;
                        using var iterator = matcher.MatchAll(input, options).GetEnumerator();
                        while (iterator.MoveNext())
                        {
                            if (expectedStatus == "HostLimitExceeded")
                            {
                                continue; // Reach the actual finite output failure; no partial result is an expectation.
                            }

                            Check(i < matches.GetArrayLength(), "extra iterator match");
                            Materialized(pattern!, matches[i++], iterator.Current);
                        }
                        Equal(matches.GetArrayLength(), i, "iterator count");
                        Equal(RegexSearchStatus.NoMatch, iterator.TerminalStatus, "iterator terminal status");
                        actualStatus = "Match";
                        break; // Successful iterable includes the empty sequence.
                    }
                case "Match":
                    {
                        var result = matcher.Match(input, options);
                        actualStatus = result is null ? "NoMatch" : "Match";
                        if (result is { IsGlobal: false })
                        {
                            Materialized(pattern!, matches[0], result.Single!);
                        }
                        else if (result is { IsGlobal: true })
                        {
                            Equal(matches.GetArrayLength(), result.GlobalMatches.Count, "global string count");
                            for (var i = 0; i < result.GlobalMatches.Count; i++)
                            {
                                Equal(input.Substring(matches[i].GetProperty("index").GetInt32(), matches[i].GetProperty("length").GetInt32()), result.GlobalMatches[i], "global match text");
                            }

                            using var witness = EcmaScriptMatcher.Bind(pattern!);
                            var captures = new RegexCapture[pattern!.GroupCount];
                            for (var i = 0; i < matches.GetArrayLength(); i++)
                            {
                                Equal(RegexSearchStatus.Match, witness.ExecNext(input, captures, out var range), "global range witness");
                                Projection(matches[i], range, captures[0]);
                            }
                            Equal(RegexSearchStatus.NoMatch, witness.ExecNext(input, captures, out _), "global witness terminates");
                        }
                        break;
                    }
                case "Search":
                    {
                        var index = matcher.Search(input, options);
                        actualStatus = index < 0 ? "NoMatch" : "Match";
                        if (index >= 0)
                        {
                            Equal(matches[0].GetProperty("index").GetInt32(), index, "Search index");
                            using var witness = EcmaScriptMatcher.Bind(pattern!);
                            var captures = new RegexCapture[pattern!.GroupCount];
                            Equal(RegexSearchStatus.Match, witness.Exec(input, captures, out var range), "Search range witness");
                            Projection(matches[0], range, captures[0]);
                        }
                        break;
                    }
                case "Replace":
                case "ReplaceAll":
                    var replacement = Units(f.GetProperty("replacementUnits"));
                    var output = operation == "Replace" ? matcher.Replace(input, replacement, options) : matcher.ReplaceAll(input, replacement, options);
                    Texts(expected.GetProperty("texts"), [output]);
                    actualStatus = "Match";
                    break;
                case "Split":
                    Texts(expected.GetProperty("texts"), matcher.Split(input, TextOrNull(f, "splitLimit") is { } limit ? Number(limit) : null, options));
                    actualStatus = "Match";
                    break;
                default:
                    throw new InvalidOperationException("Unknown product operation: " + operation);
            }
            Equal(expectedStatus, actualStatus, "operation status");
            if (TextOrNull(expected, "lastIndexAfter") is { } after)
            {
                LastIndex(after, matcher.LastIndex, "LastIndex");
            }
        }
        catch (EcmaScriptHostException e)
        {
            Equal(e.Failure.Code == EcmaScriptHostFailureCode.GlobalFlagRequired ? "HostArgumentError" : "HostLimitExceeded", expectedStatus, "host status");
            Equal(Text(error, "code"), e.Failure.Code.ToString(), "host code");
            Equal(error.GetProperty("requiredCapacity").GetInt64(), e.Failure.RequiredCapacity, "host capacity");
            observe?.Invoke(Text(f, "id") + " host=" + e.Failure.Code + " work=" + e.Failure.WorkConsumed);
            if (TextOrNull(expected, "lastIndexAfter") is { } after)
            {
                LastIndex(after, matcher!.LastIndex, "host exception LastIndex");
            }
        }
        catch (RegexExecutionException e)
        {
            Equal(e.Failure.Code == RegexExecutionFailureCode.Cancelled ? "Cancelled" : e.Failure.Code == RegexExecutionFailureCode.TimedOut ? "TimedOut" : "LimitExceeded", expectedStatus, "execution exception status");
            Failure(e.Failure, error, resource, observe, Text(f, "id"));
            if (TextOrNull(expected, "lastIndexAfter") is { } after)
            {
                LastIndex(after, matcher!.LastIndex, "execution exception LastIndex");
            }
        }
        finally { matcher?.Dispose(); }
    }

    private static void Diagnostic(EcmaScriptDiagnostic actual, JsonElement expected, int sourceLength, bool exact)
    {
        Equal(Text(expected, "code"), actual.Diagnostic.Code.ToString(), "diagnostic category");
        Equal(Text(expected, "location"), actual.Location.ToString(), "diagnostic domain");
        Equal(RegexOffsetUnit.Utf16CodeUnits, actual.Diagnostic.LocationUnit, "diagnostic units");
        Check(actual.Diagnostic.Offset >= 0 && actual.Diagnostic.Length >= 0 && (long)actual.Diagnostic.Offset + actual.Diagnostic.Length <= sourceLength, "valid diagnostic source span");
        if (exact)
        {
            Equal(expected.GetProperty("offset").GetInt32(), actual.Diagnostic.Offset, "diagnostic offset");
            Equal(expected.GetProperty("length").GetInt32(), actual.Diagnostic.Length, "diagnostic length");
            Equal(Text(expected, "featureId"), actual.FeatureId, "diagnostic feature");
        }
    }
    private static void Failure(RegexExecutionFailure actual, JsonElement expected, bool resource, Action<string>? observe, string id)
    {
        Equal(Text(expected, "code"), actual.Code.ToString(), "execution failure code");
        Equal(expected.GetProperty("requiredCapacity").GetInt64(), actual.RequiredCapacity, "failure capacity");
        if (!resource && Long(expected, "workConsumed") is { } work)
        {
            Equal(work, actual.WorkConsumed, "failure work");
        }

        observe?.Invoke(id + " failure=" + actual.Code + " offset=" + actual.Offset + " work=" + actual.WorkConsumed);
    }
    private static void Captures(RegexPattern pattern, JsonElement expected, RegexMatchRange range, ReadOnlySpan<RegexCapture> captures, EcmaScriptFlags flags)
    {
        Equal(new RegexMatchRange(expected.GetProperty("index").GetInt32(), expected.GetProperty("length").GetInt32()), range, "range");
        var groups = expected.GetProperty("captures");
        Equal(groups.GetArrayLength(), captures.Length, "capture count");
        for (var i = 0; i < captures.Length; i++)
        {
            Equal(Capture(groups[i]), captures[i], "capture " + i);
        }

        Equal((flags & EcmaScriptFlags.HasIndices) != 0, expected.GetProperty("hasIndices").GetBoolean(), "indices flag");
        var names = expected.GetProperty("names");
        var program = pattern.EcmaExecutor!.Program;
        Equal(names.GetArrayLength(), program.Names.Length, "name count");
        for (var i = 0; i < names.GetArrayLength(); i++)
        {
            var name = program.Names[i];
            var e = names[i];
            Equal(Text(e, "name"), name.Name, "name");
            var aliases = program.NameAliases.Slice(name.Offset, name.Length);
            var ids = e.GetProperty("groupIds");
            Equal(ids.GetArrayLength(), aliases.Length, "alias count");
            var selected = default(RegexCapture);
            for (var j = 0; j < aliases.Length; j++)
            {
                Equal(ids[j].GetInt32(), aliases[j], "alias ID");
                if (captures[aliases[j]].Participated)
                {
                    selected = captures[aliases[j]];
                }
            }
            Equal(Capture(e.GetProperty("capture")), selected, "named participation");
        }
    }
    private static void Materialized(RegexPattern pattern, JsonElement expected, EcmaScriptExecResult actual)
    {
        Captures(pattern, expected, actual.Range, actual.Captures, ((EcmaScriptProfile)pattern.Profile).Flags);
        Equal(expected.GetProperty("hasIndices").GetBoolean(), actual.HasIndices, "materialized indices flag");
        Equal(actual.HasIndices ? actual.Captures.Length : 0, actual.Indices.Length, "numbered indices length");
        Equal(actual.HasIndices ? actual.Names.Length : 0, actual.NamedIndices.Length, "named indices length");
        var names = expected.GetProperty("names");
        Equal(names.GetArrayLength(), actual.Names.Length, "materialized names count");
        for (var i = 0; i < actual.Names.Length; i++)
        { Equal(Text(names[i], "name"), actual.Names[i].Name, "materialized name"); Equal(Capture(names[i].GetProperty("capture")), actual.Names[i].Capture, "materialized named capture"); }
        if (actual.HasIndices)
        {
            for (var i = 0; i < actual.Indices.Length; i++)
            {
                Equal(Capture(expected.GetProperty("captures")[i]), actual.Indices[i], "public numbered index " + i);
            }

            for (var i = 0; i < actual.NamedIndices.Length; i++)
            { Equal(Text(names[i], "name"), actual.NamedIndices[i].Name, "public named index name"); Equal(Capture(names[i].GetProperty("capture")), actual.NamedIndices[i].Capture, "public named index value"); }
        }
    }
    private static void Projection(JsonElement expected, RegexMatchRange range, RegexCapture zero)
    {
        Equal(new RegexMatchRange(expected.GetProperty("index").GetInt32(), expected.GetProperty("length").GetInt32()), range, "projected whole range");
        Equal(1, expected.GetProperty("captures").GetArrayLength(), "whole-match projection count");
        Equal(Capture(expected.GetProperty("captures")[0]), zero, "projected group zero");
        Equal(0, expected.GetProperty("names").GetArrayLength(), "whole-match projection names");
        Check(!expected.GetProperty("hasIndices").GetBoolean(), "whole-match projection has no indices");
    }
    private static void GenericWitness(RegexPattern pattern, string input, double lastIndex,
        RegexSearchStatus builtinStatus, RegexMatchRange builtinRange, ReadOnlySpan<RegexCapture> builtinCaptures)
    {
        var profile = (EcmaScriptProfile)pattern.Profile;
        var start = (profile.Flags & (EcmaScriptFlags.Global | EcmaScriptFlags.Sticky)) != 0 ? EcmaScriptMatcher.ToLength(lastIndex) : 0;
        if (start > input.Length || builtinStatus == RegexSearchStatus.DestinationTooSmall)
        {
            return; // Host conversion/destination contracts are tested separately.
        }

        using var workspace = pattern.CreateWorkspace();
        var captures = new RegexCapture[pattern.GroupCount + 1];
        var sentinel = new RegexCapture(true, 999, 999);
        captures[^1] = sentinel;
        var status = workspace.FindCaptures(input, (int)start, captures, out var range);
        if (profile.IsUnicodeAware && start > 0 && start < input.Length && char.IsLowSurrogate(input[(int)start]) && char.IsHighSurrogate(input[(int)start - 1]))
        {
            Equal(RegexSearchStatus.InvalidInput, status, "generic interior lower bound");
            Equal(RegexExecutionFailureCode.InvalidStartBoundary, workspace.LastFailure.Code, "generic interior failure");
            Equal(default, range, "generic interior range");
            foreach (var capture in captures.AsSpan(0, pattern.GroupCount))
            {
                Equal(default, capture, "generic interior clear");
            }
        }
        else
        {
            Equal(builtinStatus, status, "generic/typed corpus status");
            Equal(builtinRange, range, "generic/typed corpus range");
            for (var i = 0; i < pattern.GroupCount; i++)
            {
                Equal(builtinCaptures[i], captures[i], "generic/typed capture " + i);
            }

            Equal(status, workspace.Find(input, (int)start, out var selected), "generic range status");
            Equal(range, selected, "generic range result");
            Equal(status, workspace.IsMatch(input, (int)start), "generic Boolean status");
        }
        Equal(sentinel, captures[^1], "generic corpus excess sentinel");
    }
    private static void Texts(JsonElement expected, string?[] actual)
    {
        Equal(expected.GetArrayLength(), actual.Length, "text count");
        for (var i = 0; i < actual.Length; i++)
        {
            Equal(expected[i].ValueKind == JsonValueKind.Null ? null : Units(expected[i]), actual[i], "exact text/null " + i);
        }
    }
    private static RegexCapture Capture(JsonElement element) => new(element.GetProperty("participated").GetBoolean(), element.GetProperty("index").GetInt32(), element.GetProperty("length").GetInt32());
    private static string Units(JsonElement element) { var chars = new char[element.GetArrayLength()]; var i = 0; foreach (var unit in element.EnumerateArray()) { var u = unit.GetInt32(); Check((uint)u <= 65535, "UTF-16 unit range"); chars[i++] = (char)u; } return new(chars); }
    private static string Text(JsonElement element, string key) => element.GetProperty(key).GetString()!;
    private static string? TextOrNull(JsonElement element, string key) => element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : Text(element, key);
    private static int? Int(JsonElement element, string key) => element.ValueKind != JsonValueKind.Object || element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetInt32();
    private static long? Long(JsonElement element, string key) => element.ValueKind != JsonValueKind.Object || element.GetProperty(key).ValueKind == JsonValueKind.Null ? null : element.GetProperty(key).GetInt64();
    private static double Number(string text) => text switch { "NaN" => double.NaN, "+Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity, "-0" => BitConverter.Int64BitsToDouble(long.MinValue), _ => double.Parse(text, CultureInfo.InvariantCulture) };
    private static void LastIndex(string expected, double actual, string label)
    {
        var number = Number(expected);
        Equal(number, actual, label);
        if (number == 0)
        {
            Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits(actual), label + " zero sign");
        }
    }
    private static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(label + $": expected {expected}; actual {actual}");
        }
    }
    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(label);
        }
    }

    private sealed class ExpiredExecutor(IEcmaExecutor actual) : IEcmaExecutor
    {
        public EcmaProgram Program => actual.Program;
        public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, int frames) => actual.GetRequiredCapacity(operation, frames);
        public RegexSearchStatus Search(ReadOnlySpan<char> input, int startAt, bool sticky, EcmaStartPolicy policy, SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        {
            Check(!state.Budget.TryChargeAt(0, startAt, state.Budget.StartedAt + System.Diagnostics.Stopwatch.Frequency, out var failure), "controlled deadline expired");
            state.Failure = failure;
            return actual.Search(input, startAt, sticky, policy, operation, ref state, groups, out match);
        }
    }
}
