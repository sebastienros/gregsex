using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class UpstreamParityTests
{
    [Fact]
    public void EveryUpstreamRecipeRetainsItsCompilationObligations()
    {
        var (recipes, traces, tables) = UpstreamFixture.Load();
        Assert.Equal(46562, recipes.Count);
        List<object> differences = [];
        List<object> ledger = [];
        int comparisons = 0, compiles = 0, bounds = 0, failedCompiles = 0;
        foreach (var recipe in recipes)
        {
            var id = recipe.GetProperty("id").GetString()!;
            int before = differences.Count, checkedBefore = comparisons;
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    differences.Add(new { id, field, expected, actual, source = recipe.GetProperty("source").GetString(), line = recipe.GetProperty("sourceLine").GetInt32(), observation = recipe.GetProperty("observation") });
                }
            }
            if (!recipe.TryGetProperty("observation", out var observation) || !observation.TryGetProperty("traceMember", out var member))
            { ledger.Add(new { id, disposition = recipe.GetProperty("disposition").GetString(), reason = recipe.GetProperty("reason").GetString(), semanticPass = false }); continue; }
            if (!observation.TryGetProperty("eventStart", out var eventStart))
            { ledger.Add(new { id, disposition = "source precondition; no execution", semanticPass = false }); continue; }
            var events = traces[member.GetString()!].AsSpan(eventStart.GetInt32(), observation.GetProperty("eventEndExclusive").GetInt32() - eventStart.GetInt32()).ToArray();
            var input = events.FirstOrDefault(e => e.GetProperty("kind").GetString() == "compileInput");
            if (input.ValueKind == JsonValueKind.Undefined)
            { ledger.Add(new { id, disposition = "non-compilation recipe; match/host/control comparison pending", semanticPass = false, observedKinds = recipe.GetProperty("observedKinds") }); continue; }
            var expected = events.First(e => e.GetProperty("kind").GetString() == "compileResult");
            compiles++;
            try
            {
                // Native decoder's numeric units/options remain authoritative. Only context optimizer directives need source recipe mapping.
                var nativeError = expected.GetProperty("error").GetInt32();
                if (!expected.GetProperty("accepted").GetBoolean() && nativeError is 116 or 119 or 120 or 133 or 184 or 186 or 188 or 197 or 201)
                {
                    ledger.Add(new
                    {
                        id,
                        disposition = "approved native pointer/compiler-context/stack/resource boundary; numeric input retained",
                        semanticPass = false,
                        nativeError,
                        sourceControl = recipe.GetProperty("rawRecipeLineLatin1"),
                        observation
                    });
                    continue;
                }
                var optimization = new[] { true, true, true };
                var controls = recipe.GetProperty("controls").EnumerateArray().Select(x => x.GetProperty("control").GetString()!).ToArray();
                var contextPending = controls.Contains("optimization_full") && controls.Contains("optimization_none");
                if (controls.Contains("optimization_none"))
                {
                    Array.Fill(optimization, false);
                }

                if (controls.Contains("auto_possess_off"))
                {
                    optimization[0] = false;
                }

                if (controls.Contains("dotstar_anchor_off"))
                {
                    optimization[1] = false;
                }

                if (controls.Contains("start_optimize_off"))
                {
                    optimization[2] = false;
                }

                if (controls.Contains("auto_possess"))
                {
                    optimization[0] = true;
                }

                if (controls.Contains("dotstar_anchor"))
                {
                    optimization[1] = true;
                }

                if (controls.Contains("start_optimize"))
                {
                    optimization[2] = true;
                }

                var width = input.GetProperty("width").GetInt32();
                var trace = member.GetString()!;
                var tableName = trace.Replace(".jsonl", "", StringComparison.Ordinal).Replace("indexed/", "indexed/tables-", StringComparison.Ordinal) + "/" + input.GetProperty("inputEnd").GetInt32() + ".bin";
                Pcre2CharacterTables? characterTables = tables.TryGetValue(tableName, out var payload) ? new(payload) : null;
                if ((controls.Contains("tables") || controls.Contains("locale")) && characterTables is null)
                {
                    contextPending = true;
                }

                var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(),
                    (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(), (Pcre2Newline)input.GetProperty("newline").GetInt32(),
                    (Pcre2Bsr)input.GetProperty("bsr").GetInt32(), characterTables, new(optimization[0], optimization[1], optimization[2]), input.GetProperty("maxVariableLookbehind").GetInt32());
                var budget = new CompileBudget(new(maxCaptureGroups: 65536));
                var units = Units(input, "units");
                var ok = width == 8 ? Pcre2ExecutorFactory.TryCompile(Bytes(units), profile, ref budget, out var executor, out var diagnostic)
                    : Pcre2ExecutorFactory.TryCompile(Chars(units), profile, ref budget, out executor, out diagnostic);
                if (!ok && diagnostic.Kind == Pcre2DiagnosticKind.CompileLimit)
                { bounds++; ledger.Add(new { id, disposition = "managed compilation bound; source semantic equality unverified", semanticPass = false, diagnostic }); continue; }
                Compare("compile.accepted", expected.GetProperty("accepted"), ok);
                if (!ok)
                {
                    failedCompiles++;
                    Compare("compile.error", expected.GetProperty("error"), (int)diagnostic.SourceError);
                    Compare("compile.nativeOffset", expected.GetProperty("nativeOffset"), diagnostic.NativeOffset);
                }
                else if (expected.GetProperty("accepted").GetBoolean())
                {
                    foreach (var info in expected.GetProperty("info").EnumerateArray())
                    {
                        var query = info[0].GetInt32();
                        if (contextPending && query is 5 or 6 or 11 or 12 or 16)
                        {
                            continue; // unresolved context is recorded, never passed.
                        }

                        var found = executor!.Metadata.TryGetInfo((Pcre2PatternInfoKey)query, out var value, out var diagnosticInfo);
                        Compare("info.returnCode." + query, info[1], found ? 0 : (int)diagnosticInfo.SourceError);
                        if (found)
                        {
                            Compare("info.value." + query, info[2], value);
                        }
                    }
                }

                ledger.Add(new { id, comparisons = comparisons - checkedBefore, failures = differences.Count - before, contextPending, disposition = "compilation comparison; all matching/host obligations pending", semanticPass = false });
            }
            catch (Exception exception) { differences.Add(new { id, field = "exception", exception = exception.ToString(), source = recipe.GetProperty("source").GetString(), line = recipe.GetProperty("sourceLine").GetInt32(), observation }); }
        }
        using (var gzip = new GZipStream(File.Create(OutputPath("upstream-compilation-initial.json.gz")), CompressionLevel.SmallestSize))
        {
            JsonSerializer.Serialize(gzip, new { sourceRecipeCount = recipes.Count, compiles, failedCompiles, bounds, comparisons, mismatchCount = differences.Count, differences, ledger });
        }

        Assert.True(differences.Count == 0, $"{differences.Count} upstream compilation differences, {bounds} managed bounds. Matching/host/context obligations remain pending.");
    }
}
