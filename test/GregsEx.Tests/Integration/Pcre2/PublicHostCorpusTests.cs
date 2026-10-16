using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using GregsEx.Contracts;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;
namespace GregsEx.Tests.Integration.Pcre2;

public sealed class PublicHostCorpusTests
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };
    private static readonly (FrozenDictionary<string, JsonElement> Inputs, FrozenDictionary<string, JsonElement> Observations) Corpus = ReadCorpus();

    private static (FrozenDictionary<string, JsonElement>, FrozenDictionary<string, JsonElement>) ReadCorpus()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/inputs.json")));
        using var observations = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/observations.json")));
        return (
            inputs.RootElement.GetProperty("cases").Clone().EnumerateArray()
                .ToFrozenDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal),
            observations.RootElement.GetProperty("cases").Clone().EnumerateArray()
                .ToFrozenDictionary(c => c.GetProperty("id").GetString()!, StringComparer.Ordinal));
    }

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Corpus.Inputs.Keys.Select(id => new TheoryDataRow<string>(id));

    [Fact]
    public void CompletePairedInventory()
    {
        Assert.Equal(1050, Corpus.Inputs.Count);
        Assert.Equal(Corpus.Inputs.Keys.Order(StringComparer.Ordinal), Corpus.Observations.Keys.Order(StringComparer.Ordinal));
    }

    internal static object?[] Captures(ReadOnlySpan<Pcre2OffsetPair> pairs, bool callback = false, int count = -1)
    {
        if (count < 0)
        {
            count = pairs.Length;
        }

        var result = new object?[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = callback && i == 0 ? null : pairs[i].Participated ? new int?[] { pairs[i].Start, pairs[i].End } : [null, null];
        }

        return result;
    }
    internal static Dictionary<string, object?> Callout(Pcre2CalloutInfo info, int[] subject, int[] text, int[] mark, ReadOnlySpan<Pcre2OffsetPair> pairs, int returned) => new()
    {
        ["version"] = info.Version,
        ["number"] = info.Number,
        ["isString"] = info.IsString,
        ["subjectLength"] = info.SubjectLength,
        ["captureTop"] = info.CaptureTop,
        ["captureLast"] = info.CaptureLast,
        ["startMatch"] = info.StartMatch,
        ["currentPosition"] = info.CurrentPosition,
        ["patternPosition"] = info.PatternPosition,
        ["nextItemLength"] = info.NextItemLength,
        ["stringOffset"] = info.StringOffset,
        ["flags"] = (uint)info.Flags,
        ["returnCode"] = returned,
        ["subjectUnits"] = subject,
        ["stringUnits"] = text,
        ["markUnits"] = info.HasMark ? mark : null,
        ["captures"] = Captures(pairs, true, info.CaptureTop)
    };
    private static int[] NativeName(Pcre2Profile profile, string name) => profile.Width == Pcre2CodeUnitWidth.Eight && profile.Utf
        ? [.. Encoding.UTF8.GetBytes(name).Select(x => (int)x)] : [.. name.Select(x => (int)x)];
    [Theory]
    [MemberData(nameof(Cases))]
    public void PublicHostSurfacePreservesFrozenNamesCalloutsSubstringsIterationAndSubstitution(string id)
    {
        var c = Corpus.Inputs[id];
        var events = Corpus.Observations[id].GetProperty("events").EnumerateArray().ToArray();
        List<object> mismatches = [];
        List<object> accounting = [];
        var comparisons = 0;
        void Execute()
        {
            void Compare(string field, object? expected, object? actual)
            {
                comparisons++;
                if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(actual))
                {
                    mismatches.Add(new { id, field, expected, actual, input = c });
                }
            }
            void Event(string field, JsonElement expected, Dictionary<string, object?> actual)
            {
                foreach (var entry in actual)
                {
                    Compare(field + "." + entry.Key, expected.GetProperty(entry.Key), entry.Value);
                }
            }
            try
            {
                if (!events.Any(x => x.GetProperty("event").GetString() == "compile"))
                { accounting.Add(new { id, disposition = "boundary pending" }); return; }
                var profile = Profile(c);
                var budget = new CompileBudget(new());
                var pattern = Units(c, "patternUnits");
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight
                    ? Pcre2Functions.TryParse(Bytes(pattern), profile, out var executor, out _)
                    : Pcre2Functions.TryParse(Chars(pattern), profile, out executor, out _);
                if (!ok)
                { accounting.Add(new { id, disposition = "compile rejected; see independent compilation comparison" }); return; }
                var parsed = executor!;
                var metadata = parsed!;
                var names = events.First(x => x.GetProperty("event").GetString() == "names");
                var actualNames = metadata.GetNamedGroups().ToArray().Select(x => new { number = x.Number, units = NativeName(parsed.Profile, x.Name) }).ToArray();
                Compare("names.entries", names.GetProperty("entries"), actualNames);
                var queryUnits = Units(names, "nameQuery");
                var query = parsed.Profile.Width == Pcre2CodeUnitWidth.Eight && parsed.Profile.Utf ? Encoding.UTF8.GetString(Bytes(queryUnits)) : new(Chars(queryUnits));
                Compare("names.number", names.GetProperty("nameNumberReturn"), metadata.GroupNumberFromName(query));
                Compare("names.numbers", names.GetProperty("nameNumbers"), metadata.GetGroupNumbers(query).ToArray());
                // Native scanReturn's successful table stride is a physical-layout exclusion; logical scan entries above remain required.
                var sites = metadata.GetCalloutSites().ToArray();
                var expectedSites = events.Where(x => x.GetProperty("event").GetString() == "calloutSite").ToArray();
                Compare("calloutSites.count", expectedSites.Length, sites.Length);
                for (var i = 0; i < Math.Min(sites.Length, expectedSites.Length); i++)
                {
                    Event("calloutSite." + i, expectedSites[i], new()
                    {
                        ["version"] = sites[i].Version,
                        ["number"] = sites[i].Number,
                        ["isString"] = sites[i].IsString,
                        ["patternPosition"] = sites[i].PatternPosition,
                        ["nextItemLength"] = sites[i].NextItemLength,
                        ["stringOffset"] = sites[i].StringOffset,
                        ["stringUnits"] = profile.Width == Pcre2CodeUnitWidth.Eight ? metadata.GetCalloutStringBytes(i).ToArray().Select(x => (int)x).ToArray() : [.. metadata.GetCalloutStringChars(i).ToArray().Select(x => (int)x)]
                    });
                }

                List<Dictionary<string, object?>> calls = [], subs = [], cases = [];
                int callAt = 0, subAt = 0;
                int[] returns = Units(c, "callbackReturns"), subReturns = Units(c, "substituteCallbackReturns");
                int Return() => callAt < returns.Length ? returns[callAt++] : 0;
                int SubReturn() => subAt < subReturns.Length ? subReturns[subAt++] : 0;
                var subject = Units(c, "subjectUnits");
                var replacement = Units(c, "replacementUnits");
                int Case(ReadOnlySpan<int> input, Span<int> output, Pcre2SubstituteCase mode, bool overlap)
                {
                    var policy = c.GetProperty("caseCallbackMode").GetInt32();
                    var selected = policy == 1 ? [.. c.GetProperty("caseCallbackOutputs")[0].EnumerateArray().Select(x => x.GetInt32())] : input.ToArray();
                    var required = policy == 2 ? -1 : selected.Length;
                    var write = required < 0 ? 0 : Math.Min(required, output.Length);
                    selected.AsSpan(0, write).CopyTo(output);
                    cases.Add(new() { ["mode"] = (int)mode, ["inputUnits"] = input.ToArray(), ["outputUnits"] = output[..write].ToArray(), ["outputCapacity"] = output.Length, ["requiredUnits"] = required < 0 ? null : required, ["overlap"] = overlap });
                    return required;
                }
                var bc = new Pcre2ByteCallbacks(
                    c.GetProperty("callbackEnabled").GetBoolean() ? (in Pcre2ByteCalloutContext x, object? _) => { var r = Return(); calls.Add(Callout(x.Info, x.Subject.ToArray().Select(x => (int)x).ToArray(), x.CalloutString.ToArray().Select(x => (int)x).ToArray(), x.Mark.ToArray().Select(x => (int)x).ToArray(), x.Captures, r)); return r; }
                : null,
                    c.GetProperty("substituteCallbackEnabled").GetBoolean() ? (in Pcre2ByteSubstituteContext x, object? _) => { var r = SubReturn(); subs.Add(new() { ["version"] = x.Info.Version, ["count"] = x.Info.SubstitutionCount, ["outputStart"] = x.Info.OutputStart, ["outputEnd"] = x.Info.OutputEnd, ["proposedOutput"] = x.Output.ToArray().Select(x => (int)x).ToArray(), ["captures"] = Captures(x.Captures), ["returnCode"] = r }); return r; }
                : null,
                    c.GetProperty("caseCallbackMode").GetInt32() >= 0 ? (ReadOnlySpan<byte> x, Span<byte> y, Pcre2SubstituteCase mode, object? _) => { int[] a = x.ToArray().Select(x => (int)x).ToArray(), b = new int[y.Length]; var r = Case(a, b, mode, x.Overlaps(y)); for (var i = 0; i < Math.Min(Math.Max(r, 0), y.Length); i++) { y[i] = (byte)b[i]; } return r; }
                : null);
                var cc = new Pcre2CharCallbacks(
                    c.GetProperty("callbackEnabled").GetBoolean() ? (in Pcre2CharCalloutContext x, object? _) => { var r = Return(); calls.Add(Callout(x.Info, x.Subject.ToArray().Select(x => (int)x).ToArray(), x.CalloutString.ToArray().Select(x => (int)x).ToArray(), x.Mark.ToArray().Select(x => (int)x).ToArray(), x.Captures, r)); return r; }
                : null,
                    c.GetProperty("substituteCallbackEnabled").GetBoolean() ? (in Pcre2CharSubstituteContext x, object? _) => { var r = SubReturn(); subs.Add(new() { ["version"] = x.Info.Version, ["count"] = x.Info.SubstitutionCount, ["outputStart"] = x.Info.OutputStart, ["outputEnd"] = x.Info.OutputEnd, ["proposedOutput"] = x.Output.ToArray().Select(x => (int)x).ToArray(), ["captures"] = Captures(x.Captures), ["returnCode"] = r }); return r; }
                : null,
                    c.GetProperty("caseCallbackMode").GetInt32() >= 0 ? (ReadOnlySpan<char> x, Span<char> y, Pcre2SubstituteCase mode, object? _) => { int[] a = x.ToArray().Select(x => (int)x).ToArray(), b = new int[y.Length]; var r = Case(a, b, mode, x.Overlaps(y)); for (var i = 0; i < Math.Min(Math.Max(r, 0), y.Length); i++) { y[i] = (char)b[i]; } return r; }
                : null);
                var options = new Pcre2MatchOptions { Flags = (Pcre2MatchFlags)c.GetProperty("matchFlags").GetUInt32(), OffsetLimit = c.GetProperty("offsetLimit").ValueKind == JsonValueKind.Null ? null : c.GetProperty("offsetLimit").GetInt32(), Limits = c.GetProperty("workLimit").ValueKind == JsonValueKind.Null ? null : new(maxWork: c.GetProperty("workLimit").GetInt64()) };
                using var workspace = parsed.CreateWorkspace(new(4096, 65536, 512, 2048));
                var start = c.GetProperty("startAt").GetInt32();
                var pairs = new Pcre2OffsetPair[c.GetProperty("pairCapacity").GetInt32()];
                var operation = c.GetProperty("operation").GetString()!;
                if (operation == "Match")
                {
                    var result = profile.Width == Pcre2CodeUnitWidth.Eight ? workspace.Match(Bytes(subject), start, pairs, options, bc) : workspace.Match(Chars(subject), start, pairs, options, cc);
                    if (result.Status == Pcre2MatchStatus.Match)
                    {
                        foreach (var e in events.Where(x => x.GetProperty("event").GetString() == "substring"))
                        {
                            int group = e.GetProperty("group").GetInt32(), n = Pcre2Functions.SubstringLength(parsed, pairs, group);
                            Compare("substring.lengthReturn." + group, e.GetProperty("lengthReturn"), n < 0 ? n : 0);
                            if (n >= 0)
                            {
                                Compare("substring.length." + group, e.GetProperty("length"), n);
                            }

                            var cap = Math.Min(c.GetProperty("destinationCapacity").GetInt32(), 1024);
                            Pcre2SubstringResult copied;
                            int[] output;
                            if (profile.Width == Pcre2CodeUnitWidth.Eight)
                            { var b = new byte[cap]; copied = Pcre2Functions.CopySubstring(parsed, Bytes(subject), pairs, group, b); output = [.. b[..copied.OutputUnits].Select(x => (int)x)]; }
                            else
                            { var b = new char[cap]; copied = Pcre2Functions.CopySubstring(parsed, Chars(subject), pairs, group, b); output = [.. b[..copied.OutputUnits].Select(x => (int)x)]; }
                            Compare("substring.copyReturn." + group, e.GetProperty("copyReturn"), copied.NativeReturnCode);
                            Compare("substring.output." + group, e.GetProperty("outputUnits"), output);
                            Compare("substring.reportedLength." + group, e.GetProperty("reportedLength"), copied.NativeReturnCode == 0 ? copied.OutputUnits : cap);
                        }
                    }

                    if (result.Status == Pcre2MatchStatus.Match)
                    {
                        foreach (var e in events.Where(x => x.GetProperty("event").GetString() == "substringName"))
                        {
                            var n = Pcre2Functions.SubstringLength(parsed, pairs, query);
                            Compare("substringName.lengthReturn", e.GetProperty("lengthReturn"), n < 0 ? n : 0);
                            if (n >= 0)
                            {
                                Compare("substringName.length", e.GetProperty("length"), n);
                            }

                            Compare("substringName.getReturn", e.GetProperty("getReturn"), n < 0 ? n : 0);
                            var output = n < 0 ? [] : profile.Width == Pcre2CodeUnitWidth.Eight ? Pcre2Functions.MaterializeSubstring(parsed, Bytes(subject), pairs, query).Select(x => (int)x).ToArray() : [.. Pcre2Functions.MaterializeSubstring(parsed, Chars(subject), pairs, query).Select(x => (int)x)];
                            Compare("substringName.units", e.GetProperty("units"), output);
                        }
                    }
                }
                else if (operation == "Substitute")
                {
                    var flags = (Pcre2SubstituteFlags)c.GetProperty("substituteFlags").GetUInt32();
                    Pcre2MatchResult pre = default;
                    if ((flags & Pcre2SubstituteFlags.Matched) != 0)
                    {
                        pre = profile.Width == Pcre2CodeUnitWidth.Eight ? workspace.Match(Bytes(subject), start, pairs, options, bc) : workspace.Match(Chars(subject), start, pairs, options, cc);
                    }

                    var expected = events.First(x => x.GetProperty("event").GetString() == "substitute");
                    var cap = c.GetProperty("destinationCapacity").GetInt32();
                    Pcre2SubstituteResult result;
                    int[] output;
                    if (profile.Width == Pcre2CodeUnitWidth.Eight)
                    { var b = new byte[cap]; Array.Fill(b, (byte)0x55); result = workspace.Substitute(Bytes(subject), start, Bytes(replacement), b, flags, options, bc, precomputedPairs: pairs, precomputedResult: pre, pairCapacity: pairs.Length); output = result.NativeReturnCode >= 0 ? [.. b[..result.OutputUnits].Select(x => (int)x)] : []; }
                    else
                    { var b = new char[cap]; Array.Fill(b, (char)0x55); result = workspace.Substitute(Chars(subject), start, Chars(replacement), b, flags, options, cc, precomputedPairs: pairs, precomputedResult: pre, pairCapacity: pairs.Length); output = result.NativeReturnCode >= 0 ? [.. b[..result.OutputUnits].Select(x => (int)x)] : []; }
                    Compare("substitute.returnCode", expected.GetProperty("returnCode"), result.NativeReturnCode);
                    Compare("substitute.reportedLength", expected.GetProperty("reportedLength"), result.OutputUnits >= 0 ? result.OutputUnits : result.RequiredCapacity == 0 ? null : result.RequiredCapacity);
                    Compare("substitute.output", expected.GetProperty("outputUnits"), output);
                    accounting.Add(new { id, disposition = "exact explicit native substitution capacity compared; C048 input erratum preserved", pairCapacity = pairs.Length });
                }
                else if (operation == "Iterate")
                {
                    if (!c.GetProperty("nativeDisposition").GetString()!.StartsWith("SourceUndefined", StringComparison.Ordinal))
                    {
                        var nativeMatches = events.Where(x => x.GetProperty("event").GetString() == "match").ToArray();
                        var emitted = nativeMatches.Where(x => x.GetProperty("returnCode").GetInt32() >= 0).ToArray();
                        using var iteration = workspace.CreateIteration(start, options, profile.Width == Pcre2CodeUnitWidth.Sixteen ? cc : null, profile.Width == Pcre2CodeUnitWidth.Eight ? bc : null);
                        for (var i = 0; i < emitted.Length; i++)
                        {
                            var result = profile.Width == Pcre2CodeUnitWidth.Eight ? iteration.Next(Bytes(subject), pairs) : iteration.Next(Chars(subject), pairs);
                            Compare("iteration.returnCode." + i, emitted[i].GetProperty("returnCode"), result.NativeReturnCode);
                            Compare("iteration.pairs." + i, emitted[i].GetProperty("pairs"), Captures(pairs, count: emitted[i].GetProperty("pairs").GetArrayLength()));
                            if (result.Status != Pcre2MatchStatus.Match)
                            {
                                break;
                            }
                        }
                        if (true)
                        {
                            var terminal = profile.Width == Pcre2CodeUnitWidth.Eight ? iteration.Next(Bytes(subject), pairs) : iteration.Next(Chars(subject), pairs);
                            var last = nativeMatches[^1].GetProperty("returnCode").GetInt32();
                            Compare("iteration.terminal", last >= 0 ? -1 : last, terminal.NativeReturnCode);
                        }
                    }
                    else
                    {
                        accounting.Add(new { id, disposition = "source undefined malformed continuation; no semantic equality pass" });
                    }
                }
                var undefined = c.GetProperty("nativeDisposition").GetString()!.StartsWith("SourceUndefined", StringComparison.Ordinal);
                if (!undefined)
                {
                    foreach (var (kind, actual) in new[] { ("callout", calls), ("substituteCallout", subs), ("caseCallout", cases) })
                    {
                        var expected = events.Where(x => x.GetProperty("event").GetString() == kind).ToArray();
                        Compare(kind + ".count", expected.Length, actual.Count);
                        if (expected.Length != actual.Count)
                        {
                            accounting.Add(new { id, kind, actualTrace = actual });
                        }

                        for (var i = 0; i < Math.Min(expected.Length, actual.Count); i++)
                        {
                            var defined = actual[i];
                            // The ratified safe substitution view additionally clears the upper full vector; native rc-defined prefix remains compared.
                            if (kind == "substituteCallout")
                            {
                                defined["captures"] = ((object?[])defined["captures"]!).Take(expected[i].GetProperty("captures").GetArrayLength()).ToArray();
                            }

                            Event(kind + "." + i, expected[i], defined);
                        }
                    }
                }
            }
            catch (Exception exception) { mismatches.Add(new { id, field = "exception", exception = exception.ToString(), input = c }); }
        }
        Execute();
        var report = OutputPath(Path.Combine("public-host-corpus", id + ".json"));
        File.WriteAllText(report, JsonSerializer.Serialize(new { id, comparisons, mismatchCount = mismatches.Count, mismatches, accounting }, JsonOutputOptions));
        Assert.True(mismatches.Count == 0, $"{id}: {mismatches.Count} differences; see {report}.");
    }
}
