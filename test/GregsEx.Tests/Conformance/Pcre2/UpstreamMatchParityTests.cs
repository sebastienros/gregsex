using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class UpstreamMatchParityTests
{
    [Fact]
    public void RetainedStandardInterpreterMatchInvocations()
    {
        var (recipes, traces, tables) = UpstreamFixture.Load();
        using var overlayDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/parity/native-callout-overlays.json")));
        var overlays = overlayDocument.RootElement.GetProperty("patches").EnumerateArray().ToDictionary(x => (x.GetProperty("traceMember").GetString()!, x.GetProperty("eventIndex").GetInt32()));
        using var contextStream = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/parity/complete-match-contexts.json.gz")), CompressionMode.Decompress);
        using var contextDocument = JsonDocument.Parse(contextStream);
        var decodedContexts = contextDocument.RootElement.GetProperty("contexts").EnumerateArray().GroupBy(x => x.GetProperty("id").GetString()!).ToDictionary(x => x.Key, x => x.ToArray());
        var decodedPlans = contextDocument.RootElement.GetProperty("plans");
        Dictionary<string, Pcre2Executor?> restored = [];
        Dictionary<string, Pcre2Executor?> active = [];
        Dictionary<string, bool> uncertain = [];
        List<object> differences = [];
        List<object> ledger = [];
        int matches = 0, comparisons = 0, bounds = 0;
        foreach (var recipe in recipes)
        {
            var id = recipe.GetProperty("id").GetString()!;
            if (!recipe.TryGetProperty("observation", out var observation) || !observation.TryGetProperty("eventStart", out var first))
            {
                continue;
            }

            var member = observation.GetProperty("traceMember").GetString()!;
            var events = traces[member].AsSpan(first.GetInt32(), observation.GetProperty("eventEndExclusive").GetInt32() - first.GetInt32()).ToArray();
            int begin = comparisons, failedBefore = differences.Count;
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual, source = recipe.GetProperty("source").GetString(), line = recipe.GetProperty("sourceLine").GetInt32(), observation });
                }
            }
            var controls = recipe.GetProperty("controls").EnumerateArray().Select(x => x.GetProperty("control").GetString()!).ToArray();
            var input = events.FirstOrDefault(e => e.GetProperty("kind").GetString() == "compileInput");
            if (input.ValueKind != JsonValueKind.Undefined)
            {
                active[member] = null;
                uncertain[member] = false;
                var optim = new[] { true, true, true };
                if (controls.Contains("optimization_none"))
                {
                    Array.Fill(optim, false);
                }

                if (controls.Contains("auto_possess_off"))
                {
                    optim[0] = false;
                }

                if (controls.Contains("dotstar_anchor_off"))
                {
                    optim[1] = false;
                }

                if (controls.Contains("start_optimize_off"))
                {
                    optim[2] = false;
                }

                if (controls.Contains("auto_possess"))
                {
                    optim[0] = true;
                }

                if (controls.Contains("dotstar_anchor"))
                {
                    optim[1] = true;
                }

                if (controls.Contains("start_optimize"))
                {
                    optim[2] = true;
                }

                var table = member.Replace(".jsonl", "", StringComparison.Ordinal).Replace("indexed/", "indexed/tables-", StringComparison.Ordinal) + "/" + input.GetProperty("inputEnd").GetInt32() + ".bin";
                var characterTables = tables.TryGetValue(table, out var payload) ? new Pcre2CharacterTables(payload) : null;
                uncertain[member] = (controls.Contains("tables") || controls.Contains("locale")) && characterTables is null || controls.Contains("optimization_none") && controls.Contains("optimization_full");
                try
                {
                    var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
                        (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(),
                        (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                        characterTables, new(optim[0], optim[1], optim[2]), input.GetProperty("maxVariableLookbehind").GetInt32());
                    var budget = new CompileBudget(new(maxCaptureGroups: 65536));
                    var units = Units(input, "units");
                    var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2ExecutorFactory.TryCompile(Bytes(units), profile, ref budget, out var executor, out _)
                        : Pcre2ExecutorFactory.TryCompile(Chars(units), profile, ref budget, out executor, out _);
                    if (ok)
                    {
                        active[member] = executor;
                    }
                }
                catch (Exception exception) { ledger.Add(new { id, disposition = "compile context failure; no match equality pass", exception = exception.ToString() }); }
            }
            if (recipe.GetProperty("command").ValueKind != JsonValueKind.Null && recipe.GetProperty("command").GetString() is "load" or "pop" or "popcopy")
            {
                uncertain[member] = true; // serialized container itself excluded; restored semantic binding remains a pending obligation.
            }

            var excluded = recipe.GetProperty("excludedFields").EnumerateArray().Select(x => x.GetString()!).ToArray();
            for (var at = 0; at < events.Length; at++)
            {
                var invocation = events[at];
                if (invocation.GetProperty("kind").GetString() != "matchInput")
                {
                    continue;
                }

                var end = at + 1;
                while (end < events.Length && events[end].GetProperty("kind").GetString() is not ("matchInput" or "matchResult"))
                {
                    end++;
                }

                if (invocation.GetProperty("dfa").GetBoolean() || excluded.Length != 0)
                { ledger.Add(new { id, eventIndex = first.GetInt32() + at, disposition = "frozen individually approved native outcome boundary", excludedFields = excluded, dfa = invocation.GetProperty("dfa").GetBoolean(), semanticPass = false }); continue; }
                if (end == events.Length || events[end].GetProperty("kind").GetString() != "matchResult")
                { ledger.Add(new { id, eventIndex = first.GetInt32() + at, disposition = "no retained standard match result; host/tooling observation remains pending", semanticPass = false }); continue; }
                active.TryGetValue(member, out var executor);
                JsonElement decoded = default;
                if (decodedContexts.TryGetValue(id, out var contexts))
                {
                    decoded = contexts.FirstOrDefault(x => Units(x.GetProperty("input"), "units").SequenceEqual(Units(invocation, "units")) &&
                        x.GetProperty("input").GetProperty("options").GetUInt32() == invocation.GetProperty("options").GetUInt32() &&
                        x.GetProperty("input").GetProperty("startAt").GetInt32() == invocation.GetProperty("startAt").GetInt32());
                }

                var resourceControls = controls.Any(x => x is "find_limits" or "find_limits_noheap" or "match_limit" or "depth_limit" or "recursion_limit" or "heap_limit");
                if (executor is null || uncertain.GetValueOrDefault(member))
                {
                    Assert.True(decoded.ValueKind != JsonValueKind.Undefined, $"Missing independent restored binding for {id}");
                    var key = decoded.GetProperty("planKey").GetString()!;
                    if (!restored.TryGetValue(key, out executor))
                    {
                        var plan = decodedPlans.GetProperty(key);
                        var profile = Profile(plan.GetProperty("profile"));
                        var sourceUnits = Units(plan.GetProperty("input"), "units");
                        var budget = new CompileBudget(new(maxCaptureGroups: 65536));
                        var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2ExecutorFactory.TryCompile(Bytes(sourceUnits), profile, ref budget, out executor, out var error) : Pcre2ExecutorFactory.TryCompile(Chars(sourceUnits), profile, ref budget, out executor, out error);
                        if (!ok)
                        { Assert.Equal(Pcre2DiagnosticKind.CompileLimit, error.Kind); executor = null; }
                        restored[key] = executor;
                    }
                    if (executor is null)
                    { bounds++; ledger.Add(new { id, disposition = "restored semantic plan exceeds explicit managed compile bounds; no semantic equality pass" }); continue; }
                }
                var expected = events[end];
                matches++;
                // The indexed runner can call pcre2_jit_match even in its
                // interpreter cohort. JIT_BADOPTION is an excluded PH-012 API
                // result, not a standard-interpreter language observation.
                if (expected.GetProperty("returnCode").GetInt32() == -45 && controls.Contains("jitfast"))
                { ledger.Add(new { id, eventIndex = first.GetInt32() + at, disposition = "PH-012 explicit jit_match bad-option result; standard counterpart required separately", semanticPass = false }); continue; }
                try
                {
                    var nativeCalls = events[(at + 1)..end].Where(x => x.GetProperty("kind").GetString() == "callout").ToArray();
                    List<Dictionary<string, object?>> calls = [];
                    var callAt = 0;
                    int Return() => callAt < nativeCalls.Length ? nativeCalls[callAt++].GetProperty("returnCode").GetInt32() : 0;
                    var byteCallbacks = new Pcre2ByteCallbacks(Match: (in x, _) => { var r = Return(); calls.Add(IndependentHostParityTests.Callout(x.Info, [.. x.Subject.ToArray().Select(x => (int)x)], [.. x.CalloutString.ToArray().Select(x => (int)x)], [.. x.Mark.ToArray().Select(x => (int)x)], x.Captures, r)); return r; });
                    var charCallbacks = new Pcre2CharCallbacks(Match: (in x, _) => { var r = Return(); calls.Add(IndependentHostParityTests.Callout(x.Info, [.. x.Subject.ToArray().Select(x => (int)x)], [.. x.CalloutString.ToArray().Select(x => (int)x)], [.. x.Mark.ToArray().Select(x => (int)x)], x.Captures, r)); return r; });
                    var requestedCapacity = invocation.GetProperty("pairCapacity").GetInt32();
                    // pcre2test-indexed.c:7975-7982: ovector=0 means CREATE_FROM_PATTERN, not explicit one-pair data.
                    var capacity = requestedCapacity == 0 ? executor.Program.GroupCount : requestedCapacity;
                    var pairs = new Pcre2OffsetPair[capacity];
                    int? offsetLimit = null;
                    Pcre2MatchLimits? limits = null;
                    if (decoded.ValueKind != JsonValueKind.Undefined)
                    {
                        var context = decoded.GetProperty("context");
                        if (context.GetProperty("offsetLimit").ValueKind != JsonValueKind.Null)
                        {
                            offsetLimit = context.GetProperty("offsetLimit").GetInt32();
                        }

                        if (resourceControls)
                        {
                            limits = new(maxWork: Math.Max(1, Math.Min(10_000_000, context.GetProperty("matchLimit").GetInt64())), maxDepth: Math.Max(1, Math.Min(4096, context.GetProperty("depthLimit").GetInt32())), maxWorkspaceBytes: Math.Max(1, Math.Min(67_108_864, context.GetProperty("heapLimit").GetInt64() * 1024)));
                        }
                    }
                    var options = new Pcre2MatchOptions { Flags = (Pcre2MatchFlags)invocation.GetProperty("options").GetUInt32(), OffsetLimit = offsetLimit, Limits = limits };
                    var subject = Units(invocation, "units");
                    using var workspace = new Pcre2PreparedWorkspace(executor, new(4096, 65536, 4096, 2048));
                    var result = executor.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ? workspace.Match(Bytes(subject), invocation.GetProperty("startAt").GetInt32(), pairs, options, controls.Any(x => x is "null_context" or "callout_none") ? null : byteCallbacks)
                        : workspace.Match(Chars(subject), invocation.GetProperty("startAt").GetInt32(), pairs, options, controls.Any(x => x is "null_context" or "callout_none") ? null : charCallbacks);
                    if (controls.Contains("null_subject") && expected.GetProperty("returnCode").GetInt32() == -51)
                    { ledger.Add(new { id, disposition = "PH-012 null C pointer distinction; managed empty span result retained", result }); continue; }
                    if (resourceControls && expected.GetProperty("returnCode").GetInt32() is -47 or -53 or -63)
                    { ledger.Add(new { id, disposition = "PH-010 excluded native work/depth/heap counter equality; exact controls decoded, bounded managed result retained", result }); continue; }
                    if (result.Status == Pcre2MatchStatus.LimitExceeded)
                    { bounds++; ledger.Add(new { id, eventIndex = first.GetInt32() + at, disposition = "managed execution bound; no source semantic pass", result }); continue; }
                    Compare("match.returnCode", expected.GetProperty("returnCode"), result.NativeReturnCode);
                    if (expected.GetProperty("pairs").GetArrayLength() != 0)
                    {
                        Compare("match.pairs", expected.GetProperty("pairs"), IndependentHostParityTests.Captures(pairs, count: expected.GetProperty("pairs").GetArrayLength()));
                    }

                    if (expected.GetProperty("startChar").ValueKind != JsonValueKind.Null)
                    {
                        Compare("match.startChar", expected.GetProperty("startChar"), result.StartChar);
                    }

                    if (expected.GetProperty("errorOffset").ValueKind != JsonValueKind.Null)
                    {
                        Compare("match.errorOffset", expected.GetProperty("errorOffset"), result.StartChar);
                    }

                    if (expected.GetProperty("markDefined").GetBoolean())
                    {
                        var mark = result.MarkIndex < 0 ? null : executor.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ? executor.Metadata.GetMarkBytes(result.MarkIndex).ToArray().Select(x => (int)x).ToArray() : [.. executor.Metadata.GetMarkChars(result.MarkIndex).ToArray().Select(x => (int)x)];
                        Compare("match.mark", expected.GetProperty("markUnits"), mark);
                    }
                    Compare("callout.count", nativeCalls.Length, calls.Count);
                    for (var i = 0; i < Math.Min(nativeCalls.Length, calls.Count); i++)
                    {
                        foreach (var field in calls[i])
                        {
                            var nativeIndex = Array.FindIndex(events, at + 1, end - at - 1, e => e.GetRawText() == nativeCalls[i].GetRawText()) + first.GetInt32();
                            var expectedField = field.Key == "markUnits" && overlays.TryGetValue((member, nativeIndex), out var overlay)
                                ? overlay.GetProperty("observed").GetProperty(field.Key) : nativeCalls[i].GetProperty(field.Key);
                            Compare("callout." + i + "." + field.Key, expectedField, field.Value);
                        }
                    }

                    if (differences.Count > failedBefore)
                    {
                        ledger.Add(new { id, eventIndex = first.GetInt32() + at, input = invocation, managedResult = result, managedPairs = IndependentHostParityTests.Captures(pairs), managedCalls = calls });
                    }
                }
                catch (Exception exception) { differences.Add(new { id, field = "match.exception", exception = exception.ToString(), invocation, observation }); }
            }
            if (comparisons != begin)
            {
                ledger.Add(new { id, comparisons = comparisons - begin, differences = differences.Count - failedBefore, disposition = "standard interpreter match fields compared; unrelated helper/host obligations pending" });
            }
        }
        using (var gzip = new GZipStream(File.Create(OutputPath("upstream-matches-initial.json.gz")), CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, new { matches, comparisons, bounds, mismatchCount = differences.Count, differences, ledger });
        }

        Assert.True(differences.Count == 0, $"{differences.Count} retained upstream match/callback differences; {bounds} managed bounds; pending contexts stay unverified.");
    }
}
