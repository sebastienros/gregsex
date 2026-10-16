using System.IO.Compression;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Lowering;

public sealed class LoweringTests(ITestOutputHelper output)
{
    private static Pcre2Program Lower(string pattern, Pcre2Profile? profile = null)
    {
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile ?? new(), Pcre2Data.Instance, ref budget, out var syntax, out var d), $"{pattern}: {d}");
        Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var plan, out d), $"{pattern}: {d}");
        Assert.NotNull(plan);
        Assert.True(plan.IsExecutable);
        Assert.True(plan.HasLookbehindMetadata);
        Assert.NotNull(plan.SearchHints);
        Assert.Null(d.Diagnostic.Message);
        Assert.Equal(syntax!.RetainedBytes + plan.RetainedBytes, budget.ReservedBytes);
        return plan;
    }
    [Theory]
    [InlineData("")]
    [InlineData("a|bc")]
    [InlineData("(?|(a)(b)|(c)\\g{-1})")]
    [InlineData("(?<n>a)(?&n)")]
    [InlineData("(?&n)(?<n>a)")]
    [InlineData("(?(DEFINE)(?<n>a))(?&n)")]
    [InlineData("(?<=a{300}|b)c")]
    [InlineData("(?<=a{1,3}|bc)d")]
    [InlineData("(?<=(?=\\C)a)b")]
    [InlineData("(?i:\\p{Lu}\\P{Ll}[[:upper:]\\p{Lt}])")]
    [InlineData("(?[ [a-z] - [aeiou] ])")]
    [InlineData("(*scs:(1)(a))(a)")]
    [InlineData("(?(R)a|b)")]
    [InlineData("(?<R>a)(?(R)a|b)")]
    [InlineData("(?C1)a(?C'hi')b")]
    [InlineData("(*MARK:a)a(*SKIP:a)(*F)")]
    public void ExecutableGrammar(string pattern) => Lower(pattern);
    [Fact]
    public void NativeLexicalRelativeAndDuplicateResolution()
    {
        var plan = Lower("(?J)(?|(a)(b)|(c)\\g{-1})(?<x>d)(?<x>e)(?&x)\\k<x>");
        var relative = plan.References.ToArray().First(r => r.Kind == Pcre2ReferenceKind.Relative);
        Assert.Equal(-1, relative.Number);
        Assert.Equal(1, plan.Numbers[relative.Candidates.Offset]);
        Assert.Equal(0, relative.TargetDefinition);
        foreach (var reference in plan.References)
        {
            if (reference.Kind == Pcre2ReferenceKind.Named)
            { Assert.Equal([3, 4], plan.Numbers.Slice(reference.Candidates.Offset, reference.Candidates.Count).ToArray()); Assert.Equal(3, reference.TargetDefinition); }
        }
    }
    [Fact]
    public void FixedAlternativesAndGlobalVariableMinimum()
    {
        var fixedPlan = Lower("(?<=a{300}|b)c");
        var fixedAssert = Assert.Single(fixedPlan.Assertions.ToArray());
        Assert.False(fixedAssert.VariableLength);
        Assert.Equal(1, fixedAssert.MinimumLength);
        Assert.Equal(300, fixedAssert.MaximumLength);
        Assert.Equal([300, 1], fixedPlan.LookbehindBranches.ToArray().Select(b => b.MaximumLength));
        var variable = Lower("(?<=a{1,3}|bc)d");
        Assert.True(variable.Assertions[0].VariableLength);
        Assert.Equal(1, variable.Assertions[0].MinimumLength);
    }
    [Theory]
    [InlineData("(?<=a+)b", 125, 0)]
    [InlineData("(?<=\\X)b", 125, 0)]
    [InlineData("(?<=a{1,300})b", 200, 0)]
    [InlineData("(?<n>a+)(?<=\\k<n>)b", 125, 8)]
    [InlineData("(?<x>a)(?<=(?&x))b", 0, 0)]
    [InlineData("(?&missing)", 115, 3)]
    [InlineData("(?(missing)a|b)", 115, 3)]
    [InlineData("(?=a\\K)", 199, 7)]
    public void SemanticDiagnostics(string pattern, int error, int offset)
    {
        if (error == 0)
        { Lower(pattern); return; }
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), new(), Pcre2Data.Instance, ref budget, out var syntax, out _));
        var retained = budget.ReservedBytes;
        Assert.False(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var plan, out var d));
        Assert.Null(plan);
        Assert.Equal(error, (int)d.SourceError);
        Assert.Equal(offset, d.NativeOffset);
        Assert.Equal(retained, budget.ReservedBytes);
    }
    [Fact]
    public void PrefixIdentityMapsTablesAndLimitsSurvive()
    {
        var plan = Lower("(*NOTEMPTY)(*NOTEMPTY_ATSTART)(*UTF)(*UCP)(*CRLF)(*LIMIT_MATCH=3)😀(?C'\0')",
            new(Pcre2CodeUnitWidth.Eight));
        Assert.Equal(Pcre2CompileFlags.None, plan.ArgumentFlags);
        Assert.Equal(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.NotemptyAtstart, plan.PatternMatchFlags);
        Assert.True(plan.Profile.Utf);
        Assert.True(plan.Profile.Ucp);
        Assert.Equal(Pcre2Newline.Crlf, plan.Profile.Newline);
        Assert.Equal(3, plan.PatternLimits.MatchWork);
        Assert.Contains(plan.SourceMap.ToArray().GroupBy(x => x), g => g.Count() == 4);
        Assert.Equal([0], plan.NativeBytes(plan.Callouts[0].StringUnits).ToArray());
    }
    [Fact]
    public void CaselessPropertyNormalizationIsScopedAndPrecedesComplement()
    {
        var plan = Lower("(?i:\\p{Lu}\\P{Ll}[[:upper:]\\p{Lt}])(?-i:\\p{Lu})", new(flags: Pcre2CompileFlags.Ucp));
        var atoms = plan.Nodes.ToArray().Where(n => n.Kind == Pcre2NodeKind.Class).ToArray();
        Assert.All(atoms, n => Assert.False(n.Flags.HasFlag(Pcre2CompileFlags.Caseless)));
        Assert.Contains(plan.Classes.ToArray(), c => c.Property == new Pcre2PropertyKey(Pcre2PropertyKind.Special, 0));
        Assert.Equal(new Pcre2PropertyKey(Pcre2PropertyKind.ParticularCategory, 9), plan.Classes[atoms[^1].Operand].Property);
    }
    [Fact]
    public void SourceOptimizerControlsAndCommitHint()
    {
        var full = Lower("(*COMMIT)ABC");
        Assert.Equal('A', full.SearchHints!.Value.FirstCodeUnit);
        Assert.Equal(3, full.SearchHints.Value.MinimumLength);
        var none = Lower("(*NO_START_OPT)(*COMMIT)ABC");
        Assert.Equal(-1, none.SearchHints!.Value.FirstCodeUnit);
        Assert.Equal(0, none.SearchHints.Value.MinimumLength);
        Assert.True(Lower("(?s).*a").Profile.Flags.HasFlag(Pcre2CompileFlags.Anchored));
        Assert.False(Lower("(*NO_DOTSTAR_ANCHOR)(?s).*a").Profile.Flags.HasFlag(Pcre2CompileFlags.Anchored));
        Assert.True(Lower(".*a").SearchHints!.Value.StartLine);
        Assert.Equal(Pcre2Quantifier.Possessive, Lower("a+b").Nodes.ToArray().Single(n => n.Kind == Pcre2NodeKind.Repeat).Quantifier);
        Assert.Equal(Pcre2Quantifier.Greedy, Lower("(*NO_AUTO_POSSESS)a+b").Nodes.ToArray().Single(n => n.Kind == Pcre2NodeKind.Repeat).Quantifier);
    }
    [Theory]
    [InlineData("(a+){2}b", "Greedy,Possessive")]
    [InlineData("(a+){2,3}b", "Greedy,Greedy,Possessive")]
    [InlineData("(?:a+){2}b", "Greedy,Possessive")]
    [InlineData("(?:a+?){2}b", "Lazy,Lazy")]
    [InlineData("(a+){2,}b", "Greedy,Greedy")]
    [InlineData("a+(?:b|c)?d", "Possessive")]
    [InlineData("a+(?:b|a)?d", "Greedy")]
    [InlineData("[a-zA-Z]+[0-9]", "Possessive")]
    [InlineData("[ab]+\\z", "Greedy")]
    [InlineData("\\p{L}+\\z", "Possessive")]
    [InlineData("[\\p{L}]+\\p{Nd}", "Greedy")]
    [InlineData("(?[ [a-z] - [aeiou] ])+1", "Possessive")]
    [InlineData("[^a]+a", "Possessive")]
    [InlineData("[^a]+[^b]", "Greedy")]
    [InlineData("[^a]+[aa]", "Greedy")]
    public void IndependentNativeOpcodeOptimizerDecisions(string pattern, string expected)
    {
        var plan = Lower(pattern, new(flags: Pcre2CompileFlags.AutoCallout));
        var actual = plan.Nodes.ToArray().Where(n => n.Kind == Pcre2NodeKind.Repeat &&
            plan.Nodes[plan.Children[n.Children.Offset]].Kind is Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot)
            .Select(n => n.Quantifier.ToString());
        Assert.Equal(expected, string.Join(',', actual));
    }
    [Theory]
    [InlineData("(?>a|aa){2}+b", 3)]
    [InlineData("(?>a|aa){2,3}+b", 4)]
    [InlineData("(?>a|aa){2,}+b", 2)]
    [InlineData("(?>a|aa)++b", 1)]
    [InlineData("(?>a|aa)?+b", 2)]
    public void SourcePossessiveAtomicCopiesPreserveInternalBoundaries(string pattern, int atomics)
    {
        var plan = Lower(pattern);
        Assert.Equal(atomics, plan.Nodes.ToArray().Count(n => n.Kind == Pcre2NodeKind.Atomic));
    }
    [Fact]
    public void ExpandedCopiesRetainFirstDefinitionAndRepeatedLookbehindMetadata()
    {
        var plan = Lower("((?<x>a)(?<=a)){2,3}(?&x)");
        Assert.Equal(6, plan.Groups.Length);
        Assert.Equal(3, plan.LookbehindBranches.Length);
        var reference = Assert.Single(plan.References.ToArray());
        Assert.Equal(1, reference.TargetDefinition);
        Assert.Equal(2, plan.Groups[reference.TargetDefinition].Number);
        var zeros = Lower("(?<x>a){0}(?&x)");
        Assert.Single(zeros.Groups.ToArray());
        Assert.Equal(0, zeros.References[0].TargetDefinition);
    }
    [Fact]
    public void AutomaticSitesBecomeStructuralAndCopyPayloads()
    {
        var plan = Lower("a(b|c)", new(flags: Pcre2CompileFlags.AutoCallout));
        Assert.Equal(7, plan.Callouts.Length);
        Assert.Equal(7, plan.Nodes.ToArray().Count(n => n.Kind == Pcre2NodeKind.Callout));
        Assert.Equal(Enumerable.Range(0, 7), plan.Callouts.ToArray().Select(c => c.PatternPosition));
        var repeat = Lower("(?<n>a)+(?C1)", new(flags: Pcre2CompileFlags.AutoCallout));
        Assert.Equal(5, repeat.Callouts.Length);
    }
    [Fact]
    public void AsymmetricImportedForwardClassClosureAndEscapeTables()
    {
        var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
        tables[256 + 'A'] = (byte)'x';
        tables[256 + 'x'] = (byte)'x';
        tables[832 + 'Q'] |= 8;
        var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight, characterTables: new(tables));
        var positive = new KernelFixture(Lower("(?i)[A-C]", profile));
        var negative = new KernelFixture(Lower("(?i)[^A-C]", profile));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, positive.Match([(byte)'x'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, positive.Match([(byte)'a'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, negative.Match([(byte)'x'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, negative.Match([(byte)'a'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, new KernelFixture(Lower("\\d", profile)).Match([(byte)'Q'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, new KernelFixture(Lower("[\\d]", profile)).Match([(byte)'Q'], pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, new KernelFixture(Lower("(?i)\\p{Lu}")).Match("a", pairs).Status);
    }
    [Fact]
    public void LoweringBudgetsRejectBeforeGrowthAndReleaseEveryReservation()
    {
        var parseBudget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse("(?<=a{1,3})(?<x>b+)\\k<x>".AsSpan(), new(), Pcre2Data.Instance, ref parseBudget, out var syntax, out _));
        var limits = new[]{new RegexCompileLimits(maxCompileWork:1),new RegexCompileLimits(maxCompileMemoryBytes:1),
            new RegexCompileLimits(maxInstructions:1),new RegexCompileLimits(maxPlanBytes:1),new RegexCompileLimits(maxClassRanges:1)};
        foreach (var limit in limits[..4])
        {
            var budget = new CompileBudget(limit);
            Assert.False(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var plan, out var d));
            Assert.Null(plan);
            Assert.Equal(Pcre2DiagnosticKind.CompileLimit, d.Kind);
            Assert.Equal(0, budget.ReservedBytes);
        }
        var budgetOk = new CompileBudget(new());
        Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budgetOk, out var executable, out _));
        Assert.Equal(executable!.RetainedBytes, budgetOk.ReservedBytes);
        Assert.True(budgetOk.PeakBytes > syntax!.RetainedBytes + executable.RetainedBytes);
        var exact = new CompileBudget(new(maxPlanBytes: executable.RetainedBytes));
        Assert.True(new Pcre2Lowerer().TryLower(syntax, Pcre2Data.Instance, ref exact, out _, out _));
        var denied = new CompileBudget(new(maxCompileMemoryBytes: budgetOk.PeakBytes - 1));
        Assert.False(new Pcre2Lowerer().TryLower(syntax, Pcre2Data.Instance, ref denied, out _, out _));
        Assert.Equal(0, denied.ReservedBytes);
    }
    [Fact]
    public async Task ImmutableSyntaxAndConcurrentLoweringHaveIndependentStorage()
    {
        var input = "(*NOTEMPTY)(?<x>a\\n)\\k<x>".ToCharArray();
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(input, new(), Pcre2Data.Instance, ref budget, out var syntax, out _));
        Array.Fill(input, 'z');
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        { var b = new CompileBudget(new()); Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref b, out var p, out _)); return p!; })));
        Assert.All(results, p => { Assert.Equal(syntax!.Units.ToArray(), p.Units.ToArray()); Assert.Equal(Pcre2MatchFlags.Notempty, p.PatternMatchFlags); Assert.True(p.HasExplicitCrOrLf); });
        Assert.Equal(Pcre2PlanStage.Syntax, syntax!.Stage);
        Assert.All(syntax.References.ToArray(), r => Assert.Equal(-1, r.TargetDefinition));
    }
    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void NativeExplicitNewlineMetadataSurvivesLowering(int width, bool utf)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "test/corpora/pcre2/lowering/hascrorlf-inputs.json")));
        foreach (var row in vectors.RootElement.EnumerateArray())
        {
            var pattern = row.GetProperty("pattern").GetString()!;
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0);
            var budget = new CompileBudget(new());
            Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d), $"{row.GetProperty("id")}: {d}");
            var expected = row.GetProperty("expected").GetBoolean();
            Assert.Equal(expected, syntax!.HasExplicitCrOrLf);
            Assert.True(new Pcre2Lowerer().TryLower(syntax, Pcre2Data.Instance, ref budget, out var executable, out d), $"{row.GetProperty("id")}: {d}");
            Assert.Equal(expected, executable!.HasExplicitCrOrLf);
            Assert.Equal(expected, syntax.HasExplicitCrOrLf);
        }
        output.WriteLine($"Explicit CR/LF propagation: {vectors.RootElement.GetArrayLength()} native/source vectors, width{width}, UTF={utf}.");
    }
    [Fact]
    public void IndependentSelectedNativeOptimizerAndTransformedSites()
    {
        var patterns = new Dictionary<string, string>
        {
            ["algebra-empty"] = "(?[ [a] - [a] ])",
            ["algebra-negative"] = "(?[ ![a] ])",
            ["algebra-disjoint"] = "(?[ [a-z] - [aeiou] ])+1",
            ["absolute-start"] = "\\Aabc",
            ["negative-type"] = "\\D",
            ["negative-class"] = "[^ab]",
            ["negative-single"] = "[^a]",
            ["native-type"] = "\\d",
            ["imported-type"] = "\\d",
            ["wide-range"] = "[\\x{100}-\\x{300}]a",
            ["repeat-inner"] = "(a+){2,3}b",
            ["manual-condition"] = "(?(?C1)(?=a)a|b)",
            ["repeat-bounded"] = "(a){2,3}b",
            ["repeat-exact"] = "(a){2}b",
            ["repeat-zero"] = "(?:a){0}b",
            ["literal-zero"] = "a{0}b",
            ["lazy-disjoint"] = "a+?b",
            ["lazy-end"] = "a+?",
            ["greedy-end"] = "a+",
            ["class-pair"] = "[aA]+b",
            ["class-ranges"] = "[a-zA-Z]+[0-9]",
            ["properties"] = "\\p{L}+\\p{Nd}",
            ["nested-alts"] = "a+(b|c)",
            ["assertion-repeat"] = "(?<=(?=.)*)a",
            ["conditional-assertion"] = "(?(?=a)a|b)",
            ["conditional-backward"] = "(?(?<=a)a|b)",
            ["asserted-empty"] = "(?=ab)",
            ["fixed-long-alts"] = "(?<=a{300}|b)c",
            ["commit-full"] = "(*COMMIT)ABC",
            ["commit-none"] = "(*NO_START_OPT)(*COMMIT)ABC",
            ["imported-range"] = "(?i)[A-C]",
            ["imported-single"] = "(?i)[A]",
            ["imported-negative"] = "(?i)[^A-C]"
        };
        var observed = File.ReadAllLines(Path.Combine(Root(), "test/corpora/pcre2/lowering/native-selected.jsonl")).Select(l => JsonDocument.Parse(l)).ToArray();
        try
        {
            var failures = new List<string>();
            foreach (var (id, pattern) in patterns)
            {
                var rows = observed.Select(d => d.RootElement).Where(r => r.GetProperty("id").GetString() == id).ToArray();
                var native = rows.First(r => r.GetProperty("event").GetString() == "info");
                var auto = rows.Any(r => r.GetProperty("event").GetString() == "site");
                Pcre2CharacterTables? tables = null;
                if (id.StartsWith("imported-", StringComparison.Ordinal))
                { var bytes = Pcre2CharacterTables.Default.Bytes.ToArray(); if (id == "imported-type") { bytes[832 + 'Q'] |= 8; } else { bytes[256 + 'A'] = (byte)'x'; bytes[256 + 'x'] = (byte)'x'; } tables = new(bytes); }
                var flags = auto ? Pcre2CompileFlags.AutoCallout : 0;
                if (id == "wide-range")
                {
                    flags |= Pcre2CompileFlags.Utf;
                }

                var plan = Lower(pattern, new(Pcre2CodeUnitWidth.Eight, flags, characterTables: tables));
                var hints = plan.SearchHints!.Value;
                var values = new Dictionary<string, long>
                {
                    ["first"] = Math.Max(0, hints.FirstCodeUnit),
                    ["firstType"] = hints.FirstCodeUnit < 0 ? (hints.StartLine ? 2 : 0) : 1,
                    ["required"] = Math.Max(0, hints.RequiredCodeUnit),
                    ["requiredType"] = hints.RequiredCodeUnit < 0 ? 0 : 1,
                    ["minimum"] = hints.MinimumLength,
                    ["options"] = (uint)plan.Profile.Flags
                };
                foreach (var (key, value) in values)
                {
                    if (value != native.GetProperty(key).GetInt64())
                    {
                        failures.Add($"{id} {key} expected{native.GetProperty(key)} actual{value}");
                    }
                }

                var study = rows.First(r => r.GetProperty("event").GetString() == "study");
                if (hints.MaximumLookbehind != study.GetProperty("behind").GetInt32())
                {
                    failures.Add($"{id} behind");
                }

                if (hints.MatchesEmpty != (study.GetProperty("empty").GetInt32() != 0))
                {
                    failures.Add($"{id} matches-empty");
                }

                if (Convert.ToHexStringLower(plan.StartBitmap) != study.GetProperty("bitmap").GetString())
                {
                    failures.Add($"{id} bitmap expected{study.GetProperty("bitmap")} actual{Convert.ToHexString(plan.StartBitmap)}");
                }

                var sites = rows.Where(r => r.GetProperty("event").GetString() == "site").Select(r => (r.GetProperty("number").GetInt32(), r.GetProperty("position").GetInt32(), r.GetProperty("length").GetInt32())).ToArray();
                var actual = plan.Callouts.ToArray().Select(s => (s.Number, s.PatternPosition, s.NextItemLength)).ToArray();
                if (!sites.SequenceEqual(actual))
                {
                    failures.Add($"{id} sites expected{string.Join(';', sites)} actual{string.Join(';', actual)}");
                }
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            foreach (var row in observed)
            {
                row.Dispose();
            }
        }
    }
    [Fact]
    public void IndependentNativePositivePlansAndInfo()
    {
        var root = Root();
        using var inputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/pcre2/inputs.json")));
        using var outputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "test/corpora/pcre2/observations.json")));
        var observed = outputs.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x);
        var failures = new List<string>();
        var count = 0;
        foreach (var input in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            var id = input.GetProperty("id").GetString()!;
            var events = observed[id].GetProperty("events");
            var result = events.EnumerateArray().FirstOrDefault(x => x.GetProperty("event").GetString() == "compile");
            if (result.ValueKind == JsonValueKind.Undefined || !result.GetProperty("accepted").GetBoolean() || input.GetProperty("nativeDisposition").GetString() == "SourceUndefined")
            {
                continue;
            }

            var tables = input.GetProperty("characterTables");
            var opt = input.GetProperty("optimization").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
            var directive = input.GetProperty("optimizerDirective").GetInt32();
            if (directive == 0)
            {
                Array.Fill(opt, false);
            }
            else if (directive == 1)
            {
                Array.Fill(opt, true);
            }
            else if (directive is >= 64 and <= 69)
            {
                opt[(directive - 64) / 2] = (directive & 1) == 0;
            }

            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(), (Pcre2CompileFlags)input.GetProperty("compileFlags").GetUInt32(),
                (Pcre2ExtraFlags)input.GetProperty("extraFlags").GetUInt32(), (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                tables.ValueKind == JsonValueKind.Null ? null : new Pcre2CharacterTables(tables.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray()), new(opt[0], opt[1], opt[2]), input.GetProperty("maxVariableLookbehind").GetInt32());
            var units = input.GetProperty("patternUnits").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            try
            {
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d) :
                    new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out d);
                if (!ok || !new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var plan, out d))
                { failures.Add($"{id}: {d}"); continue; }
                foreach (var value in events.EnumerateArray().First(x => x.GetProperty("event").GetString() == "info").GetProperty("values").EnumerateArray())
                {
                    var query = value.GetProperty("query").GetInt32();
                    if (value.GetProperty("value").ValueKind != JsonValueKind.Number)
                    {
                        continue;
                    }

                    var hints = plan!.SearchHints!.Value;
                    long actual = query switch
                    {
                        0 => (uint)plan.Profile.Flags,
                        1 => (uint)plan.ArgumentFlags,
                        5 => Math.Max(0, hints.FirstCodeUnit),
                        6 => hints.FirstCodeUnit < 0 ? (hints.StartLine ? 2 : 0) : 1,
                        11 => Math.Max(0, hints.RequiredCodeUnit),
                        12 => hints.RequiredCodeUnit < 0 ? 0 : 1,
                        15 => hints.MaximumLookbehind,
                        16 => hints.MinimumLength,
                        _ => -1
                    };
                    if (actual >= 0 && actual != value.GetProperty("value").GetInt64())
                    {
                        failures.Add($"{id} INFO{query}: expected {value.GetProperty("value")} actual {actual}");
                    }
                }
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name}: {e.Message}"); }
            count++;
        }
        output.WriteLine($"Independent standalone INFO projection: {count} defined positive rows; {failures.Count} disagreements.");
        Assert.True(count > 800);
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(150)) + $"\n{failures.Count} total");
    }
    private static string Root()
    { for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent) { if (File.Exists(Path.Combine(dir.FullName, "GregsEx.slnx"))) { return dir.FullName; } } throw new InvalidOperationException(); }

    [Fact]
    public void UpstreamCompleteCompileProjection()
    {
        using var file = File.OpenRead(Path.Combine(Root(), "test/corpora/pcre2/syntax/upstream-compile-inputs.jsonl.gz"));
        using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(zip);
        var failures = new List<string>();
        int count = 0, bounded = 0, excluded = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            using var row = JsonDocument.Parse(line);
            var input = row.RootElement.GetProperty("input");
            var result = row.RootElement.GetProperty("result");
            var error = result.GetProperty("error").GetInt32();
            var accepted = result.GetProperty("accepted").GetBoolean();
            // Native pointers, physical compile-context/bytecode/stack/OOM
            // capacities are approved boundaries, never parity passes.
            if (!accepted && error is 116 or 119 or 120 or 133 or 184 or 186 or 188 or 197 or 201)
            { excluded++; continue; }
            var units = input.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(), (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(),
                (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(), (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(), maxVariableLookbehind: input.GetProperty("maxVariableLookbehind").GetInt32());
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            var id = row.RootElement.GetProperty("member").GetString() + ":" + row.RootElement.GetProperty("line").GetInt32();
            try
            {
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d) :
                    new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out d);
                if (ok)
                {
                    ok = new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out _, out d);
                }

                if (!ok && d.Kind == Pcre2DiagnosticKind.CompileLimit)
                { bounded++; continue; }
                var bad = accepted ? !ok : ok || (int)d.SourceError != error || d.NativeOffset != result.GetProperty("nativeOffset").GetInt32();
                if (bad)
                {
                    failures.Add($"{id}: {new string([.. units.Select(x => (char)x)])[..Math.Min(units.Length, 140)]}: expected {(accepted ? 0 : error)}@{result.GetProperty("nativeOffset")} actual {d}");
                }
            }
            catch (Exception e) { failures.Add($"{id}: {e.GetType().Name}: {e.Message}"); }
            count++;
        }
        output.WriteLine($"Independent upstream compile projection: {count} compared rows; {bounded} bounded managed outcomes; {excluded} approved boundary exclusions; {failures.Count} disagreements.");
        Assert.True(count > 16000);
        Assert.True(failures.Count == 0, $"{failures.Count} failures, {bounded} bounded, {excluded} excluded\n" + string.Join("\n", failures.Take(80)));
    }
}
