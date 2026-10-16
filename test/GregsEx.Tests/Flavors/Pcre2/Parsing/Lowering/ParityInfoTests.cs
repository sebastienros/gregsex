using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;
using Xunit;
using static GregsEx.Tests.Conformance.Pcre2.IndependentParityTests;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Lowering;

public sealed class ParityInfoTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new() { WriteIndented = true };
    private static string Root
    {
        get { var dir = new DirectoryInfo(AppContext.BaseDirectory); while (!File.Exists(Path.Combine(dir.FullName, "GregsEx.slnx"))) { dir = dir.Parent!; } return dir.FullName; }
    }
    [Fact]
    public void IndependentUpstreamInfoWitnesses()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "test/corpora/pcre2/metadata/witnesses.json")));
        CompareRows(document.RootElement.EnumerateArray(), false);
    }
    [Fact]
    public void EveryRetainedNativeAcceptedCompilationInfo() => CompareRows(ReadUpstream(), true);
    private static IEnumerable<JsonElement> ReadUpstream()
    {
        using var stream = new GZipStream(File.OpenRead(Path.Combine(Root, "test/corpora/pcre2/metadata/upstream-info.jsonl.gz")), CompressionMode.Decompress);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        { using var doc = JsonDocument.Parse(line); yield return doc.RootElement.Clone(); }
    }
    private void CompareRows(IEnumerable<JsonElement> rows, bool full)
    {
        List<string> differences = [];
        int count = 0, bounds = 0, pending = 0, compiled = 0;
        foreach (var row in rows)
        {
            var input = row.GetProperty("input");
            var id = row.GetProperty("id").GetString()!;
            bool auto = true, dot = true, start = true;
            var names = row.GetProperty("controls").EnumerateArray().Select(x => x.GetProperty("control").GetString()).ToArray();
            var contextPending = names.Contains("optimization_none") && names.Contains("optimization_full") ||
                (names.Contains("tables") || names.Contains("locale")) && (!row.TryGetProperty("tables", out var t) || t.ValueKind == JsonValueKind.Null);
            foreach (var item in row.GetProperty("controls").EnumerateArray())
            {
                switch (item.GetProperty("control").GetString())
                {
                    case "optimization_none":
                        auto = dot = start = false;
                        break;
                    case "auto_possess_off":
                        auto = false;
                        break;
                    case "auto_possess":
                        auto = true;
                        break;
                    case "dotstar_anchor_off":
                        dot = false;
                        break;
                    case "dotstar_anchor":
                        dot = true;
                        break;
                    case "start_optimize_off":
                        start = false;
                        break;
                    case "start_optimize":
                        start = true;
                        break;
                }
            }
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
                (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(),
                (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                characterTables: row.TryGetProperty("tables", out var table) && table.ValueKind != JsonValueKind.Null ? new Pcre2CharacterTables(table.EnumerateArray().Select(x => x.GetByte()).ToArray()) : null,
                optimization: new(auto, dot, start), maxVariableLookbehind: input.GetProperty("maxVariableLookbehind").GetInt32());
            var units = input.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            var ok = profile.Width == Pcre2CodeUnitWidth.Eight ?
                Pcre2ExecutorFactory.TryCompile(units.Select(x => (byte)x).ToArray(), profile, ref budget, out var executor, out var diagnostic) :
                Pcre2ExecutorFactory.TryCompile(units.Select(x => (char)x).ToArray(), profile, ref budget, out executor, out diagnostic);
            if (!ok && diagnostic.Kind == Pcre2DiagnosticKind.CompileLimit)
            { bounds++; continue; }
            if (!ok)
            { differences.Add($"{id}: compile {diagnostic}"); continue; }
            compiled++;
            foreach (var info in row.GetProperty("expected").GetProperty("info").EnumerateArray())
            {
                var key = info[0].GetInt32();
                if (contextPending && key is 5 or 6 or 11 or 12 or 16)
                { pending++; continue; }
                var found = executor!.Metadata.TryGetInfo((Pcre2PatternInfoKey)key, out var value, out var d);
                count++;
                if ((found ? 0 : (int)d.SourceError) != info[1].GetInt32() || found && value != info[2].GetInt64())
                {
                    differences.Add($"{id}: key{key} expected{info} actual{(found ? 0 : (int)d.SourceError)}/{value} {row.GetProperty("recipe")}");
                }
            }
        }
        if (full)
        {
            File.WriteAllText(OutputPath("upstream-info.json"), JsonSerializer.Serialize(new { compiled, count, bounds, pending, differences }, JsonOutputOptions));
        }

        foreach (var difference in differences.Take(60))
        {
            output.WriteLine(difference);
        }

        output.WriteLine($"{count} INFO comparisons; {differences.Count} differences; {bounds} managed bounds; {pending} unresolved-context fields");
        Assert.Empty(differences);
    }
    [Fact]
    public void IndependentFourDomainNativeMetadataAndMatching()
    {
        var report = Path.Combine(Root, "test/corpora/pcre2/metadata");
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(report, "native-inputs.json")));
        var observations = new Dictionary<(int, int), JsonElement>();
        foreach (var width in new[] { 8, 16 })
        {
            foreach (var line in File.ReadLines(Path.Combine(report, $"native-{width}.jsonl")))
            { using var doc = JsonDocument.Parse(line); var row = doc.RootElement.Clone(); observations.Add((row.GetProperty("domain").GetInt32(), row.GetProperty("id").GetInt32()), row); }
        }

        List<string> differences = [];
        int comparisons = 0, matches = 0;
        foreach (var input in inputs.RootElement.EnumerateArray())
        {
            int domain = input.GetProperty("domain").GetInt32(), id = input.GetProperty("id").GetInt32();
            var native = observations[(domain, id)];
            var executor = RuntimeFixture.Compile(input.GetProperty("pattern").GetString()!, domain,
                new((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(), (Pcre2CompileFlags)input.GetProperty("flags").GetUInt32()));
            foreach (var info in native.GetProperty("info").EnumerateArray())
            {
                var found = executor.Metadata.TryGetInfo((Pcre2PatternInfoKey)info[0].GetInt32(), out var value, out var d);
                comparisons++;
                if ((found ? 0 : (int)d.SourceError) != info[1].GetInt32() || found && value != info[2].GetInt64())
                {
                    differences.Add($"domain{domain}/id{id} key{info[0]} native{info} managed{value}");
                }
            }
            using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
            var pairs = new Pcre2OffsetPair[executor.Program.GroupCount];
            for (var reuse = 0; reuse < 2; reuse++)
            {
                var result = RuntimeFixture.Match(workspace, domain, input.GetProperty("subject").GetString()!, pairs);
                matches++;
                if (result.NativeReturnCode != native.GetProperty("returnCode").GetInt32())
                {
                    differences.Add($"domain{domain}/id{id} match native{native.GetProperty("returnCode")} managed{result.NativeReturnCode}");
                }

                if (result.NativeReturnCode > 0)
                {
                    var group = 0;
                    foreach (var pair in native.GetProperty("pairs").EnumerateArray())
                    {
                        var expected = pair.ValueKind == JsonValueKind.Null ? default : new Pcre2OffsetPair(true, pair[0].GetInt32(), pair[1].GetInt32());
                        if (pairs[group++] != expected)
                        {
                            differences.Add($"domain{domain}/id{id} group{group - 1} native{expected} managed{pairs[group - 1]}");
                        }
                    }
                }
            }
        }
        foreach (var d in differences)
        {
            output.WriteLine(d);
        }

        output.WriteLine($"{comparisons} four-domain INFO comparisons; {matches} real match/reuse invocations");
        Assert.Empty(differences);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AlternationCalloutsStopAtSuccessfulBranchAndWorkspaceReuses(int domain)
    {
        var executor = RuntimeFixture.Compile("a(?C1)b|ab", domain, RuntimeFixture.Profile(domain, Pcre2CompileFlags.AutoCallout));
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
        List<(int, int)> sites = [];
        Pcre2ByteCallbacks bytes = new(Match: (in c, _) => { sites.Add((c.Info.PatternPosition, c.Info.NextItemLength)); return 0; });
        Pcre2CharCallbacks chars = new(Match: (in c, _) => { sites.Add((c.Info.PatternPosition, c.Info.NextItemLength)); return 0; });
        var pairs = new Pcre2OffsetPair[1];
        for (var run = 0; run < 2; run++)
        {
            sites.Clear();
            var result = domain < 2 ? workspace.Match("ab"u8, 0, pairs, callbacks: bytes) : workspace.Match("ab", 0, pairs, callbacks: chars);
            Assert.Equal(1, result.NativeReturnCode);
            Assert.Equal([(0, 1), (6, 1), (7, 1)], sites);
            Assert.Equal(new(true, 0, 2), pairs[0]);
        }
        Assert.Equal([0, 6, 7, 8, 9, 10], executor.Metadata.GetCalloutSites().ToArray().Select(c => c.PatternPosition));
    }
}
