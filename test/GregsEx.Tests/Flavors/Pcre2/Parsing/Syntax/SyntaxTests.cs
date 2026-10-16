using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;
using System.Text;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Syntax;

public sealed class SyntaxTests
{
    private static Pcre2Program Parse(string pattern, Pcre2Profile? profile = null, RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        var ok = new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile ?? new(), Pcre2Data.Instance, ref budget, out var syntax, out var diagnostic);
        Assert.True(ok, $"{pattern}: {diagnostic}");
        Assert.NotNull(syntax);
        Assert.Equal(Pcre2PlanStage.Syntax, syntax.Stage);
        Assert.Null(diagnostic.Diagnostic.Message);
        Assert.Equal(syntax.RetainedBytes, budget.ReservedBytes);
        return syntax;
    }
    [Theory]
    [InlineData("")]
    [InlineData("a|bc")]
    [InlineData("(a)(b)\\2\\1")]
    [InlineData("(?<n>a)(?&n)")]
    [InlineData("(?(DEFINE)(?<n>a))(?&n)")]
    [InlineData("(?|(a)(b)|(c)\\g{-1})")]
    [InlineData("(?i:a)(?-i:b)")]
    [InlineData("(?<=(a{1,3}))b")]
    [InlineData("(*napla:a)")]
    [InlineData("(*scs:(1)(a))(a)")]
    [InlineData("(*asr:a)")]
    [InlineData("(?C1)a(?C'hi')b")]
    [InlineData("(*MARK:a)a(*SKIP:a)(*F)")]
    [InlineData("[a-z\\p{L}]")]
    [InlineData("(?[ [a-z] - [aeiou] ])")]
    public void StructuralGrammar(string pattern) => Parse(pattern);

    [Fact]
    public void LexicalRelativeAnchorSurvivesBranchReset()
    {
        var syntax = Parse("(?|(a)(b)|(c)\\g{-1})");
        var reference = Assert.Single(syntax.References.ToArray());
        Assert.Equal(Pcre2ReferenceKind.Relative, reference.Kind);
        Assert.Equal(-1, reference.Number);
        Assert.Equal(1, syntax.Numbers[reference.Candidates.Offset]);
        Assert.Equal(3, syntax.GroupCount);
        Assert.Equal(3, syntax.Groups.Length);
    }
    [Fact]
    public void NativePositiveIntakeAdmission()
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
            var result = observed[id].GetProperty("events").EnumerateArray().FirstOrDefault(x => x.GetProperty("event").GetString() == "compile");
            if (result.ValueKind == JsonValueKind.Undefined)
            {
                continue;
            }

            if (!result.GetProperty("accepted").GetBoolean() || input.GetProperty("nativeDisposition").GetString() == "SourceUndefined")
            {
                continue;
            }

            var tables = input.GetProperty("characterTables");
            var optimizer = input.GetProperty("optimization").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
                (Pcre2CompileFlags)input.GetProperty("compileFlags").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraFlags").GetUInt32(),
                (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                tables.ValueKind == JsonValueKind.Null ? null : new Pcre2CharacterTables(tables.EnumerateArray().Select(x => (byte)x.GetInt32()).ToArray()),
                new(optimizer[0], optimizer[1], optimizer[2]), input.GetProperty("maxVariableLookbehind").GetInt32());
            var units = input.GetProperty("patternUnits").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            bool ok;
            try
            {
                ok = profile.Width == Pcre2CodeUnitWidth.Eight ? new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var diagnostic) :
                    new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out diagnostic);
                if (!ok)
                {
                    failures.Add($"{id} {new string([.. units.Select(x => (char)x)])}: {diagnostic}");
                }
                else
                {
                    var info = observed[id].GetProperty("events").EnumerateArray().First(x => x.GetProperty("event").GetString() == "info");
                    foreach (var value in info.GetProperty("values").EnumerateArray())
                    {
                        var query = value.GetProperty("query").GetInt32();
                        if (value.GetProperty("value").ValueKind != JsonValueKind.Number)
                        {
                            continue;
                        }

                        long actual = query switch
                        {
                            0 => (uint)syntax!.Profile.Flags,
                            1 => (uint)syntax!.ArgumentFlags,
                            3 => (int)syntax!.Profile.Bsr,
                            4 => syntax!.GroupCount - 1,
                            20 => (int)syntax!.Profile.Newline,
                            26 => (uint)syntax!.Profile.ExtraFlags,
                            _ => -1
                        };
                        var expected = value.GetProperty("value").GetInt64();
                        // Native overall options include optimizer-inferred anchoring;
                        // source Syntax retains argument/directive flags, lowering owns inference.
                        if (query == 0 && !syntax!.Profile.Flags.HasFlag(Pcre2CompileFlags.Anchored))
                        {
                            expected &= ~(long)(uint)Pcre2CompileFlags.Anchored;
                        }

                        if (actual >= 0 && actual != expected)
                        {
                            failures.Add($"{id} INFO{query} expected {value.GetProperty("value")} actual {actual}");
                        }
                    }
                    var nameEvent = observed[id].GetProperty("events").EnumerateArray().First(x => x.GetProperty("event").GetString() == "names");
                    var expectedNames = nameEvent.GetProperty("entries").EnumerateArray().Select(x =>
                        x.GetProperty("number").GetInt32() + "/" + string.Join(",", x.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()))).ToArray();
                    var actualNames = new List<string>();
                    foreach (var name in syntax!.Names)
                    {
                        var encoded = profile.Width == Pcre2CodeUnitWidth.Eight && syntax.Profile.Utf ? Encoding.UTF8.GetBytes(name.Name) : [];
                        var native = encoded.Length > 0 ? string.Join(",", encoded) : string.Join(",", name.Name.Select(c => (int)c));
                        foreach (var number in syntax.Numbers.Slice(name.Numbers.Offset, name.Numbers.Count))
                        {
                            actualNames.Add(number + "/" + native);
                        }
                    }
                    if (!expectedNames.SequenceEqual(actualNames))
                    {
                        failures.Add($"{id} names differ");
                    }
                }
            }
            catch (Exception e) { failures.Add($"{id}: {e}"); }
            count++;
        }
        Assert.True(count > 800);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
    [Fact]
    public void UpstreamSourceAdmissionAndDiagnostics()
    {
        using var file = File.OpenRead(Path.Combine(Root(), "test/corpora/pcre2/syntax/upstream-compile-inputs.jsonl.gz"));
        using var zip = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(zip);
        var failures = new Dictionary<string, string>();
        int count = 0, failed = 0, bounded = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            using var row = JsonDocument.Parse(line);
            var input = row.RootElement.GetProperty("input");
            var result = row.RootElement.GetProperty("result");
            var accepted = result.GetProperty("accepted").GetBoolean();
            var nativeError = result.GetProperty("error").GetInt32();
            if (!accepted && DeferredCompileError(nativeError))
            {
                continue;
            }

            var units = input.GetProperty("units").EnumerateArray().Select(x => x.GetInt32()).ToArray();
            if (units.Length > 65536)
            { bounded++; continue; }
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
                (Pcre2CompileFlags)input.GetProperty("options").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraOptions").GetUInt32(),
                (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32(),
                maxVariableLookbehind: input.GetProperty("maxVariableLookbehind").GetInt32());
            var budget = new CompileBudget(new(maxCaptureGroups: 65536));
            string text = new([.. units.Select(x => (char)x)]);
            var id = row.RootElement.GetProperty("member").GetString() + ":" + row.RootElement.GetProperty("line").GetInt32();
            try
            {
                var ok = profile.Width == Pcre2CodeUnitWidth.Eight ? new Pcre2SyntaxParser().TryParse(units.Select(x => (byte)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out var syntax, out var d) :
                    new Pcre2SyntaxParser().TryParse(units.Select(x => (char)x).ToArray(), profile, Pcre2Data.Instance, ref budget, out syntax, out d);
                if (!ok && d.Kind == Pcre2DiagnosticKind.CompileLimit)
                { bounded++; continue; }
                var bad = accepted ? !ok : ok || (int)d.SourceError != nativeError || d.NativeOffset != result.GetProperty("nativeOffset").GetInt32();
                if (bad)
                { failed++; failures.TryAdd(nativeError + "/" + text, $"{id} opts={profile.Flags} extras={profile.ExtraFlags}: {text[..Math.Min(text.Length, 160)]}: expected {nativeError}@{result.GetProperty("nativeOffset")} actual {d}"); }
            }
            catch (Exception e) { failed++; failures.TryAdd(text, $"{id}: {text}: {e}"); }
            count++;
        }
        Assert.True(count > 10000);
        Assert.True(failed == 0, $"{failed} failed; {bounded} bounded; {failures.Count} unique\n" + string.Join("\n", failures.Values.Take(250)));
    }
    [Fact]
    public void SourceMapsAndOwnedNativePayloads()
    {
        var chars = "😀(?C'\0')a".ToCharArray();
        var budget = new CompileBudget(new());
        Assert.True(new Pcre2SyntaxParser().TryParse(chars, new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf), Pcre2Data.Instance, ref budget, out var syntax, out var d));
        Assert.NotNull(syntax);
        Assert.Equal([0, 0, 0, 0, 2], syntax.SourceMap[..5].ToArray());
        Assert.Equal(0x1F600, syntax.Nodes[0].Operand);
        var callout = Assert.Single(syntax.Callouts.ToArray());
        Assert.True(callout.IsString);
        Assert.Equal([0], syntax.NativeBytes(callout.StringUnits).ToArray());
        var copy = syntax.Units.ToArray();
        Array.Fill(chars, 'x');
        Assert.Equal(copy, syntax.Units.ToArray());
        Assert.Equal(chars.Length, syntax.SourceMap[^1]);
        Assert.Equal(Pcre2DiagnosticKind.None, d.Kind);
    }
    [Fact]
    public void ProfileNormalizationAndArgumentIdentity()
    {
        var syntax = Parse("(*NOTEMPTY)(*NOTEMPTY_ATSTART)(*UTF)(*UCP)(*CRLF)(*BSR_ANYCRLF)(*LIMIT_MATCH=0)(*LIMIT_DEPTH=3)(*LIMIT_HEAP=2)a",
            new(Pcre2CodeUnitWidth.Eight));
        Assert.True(syntax.Profile.Utf);
        Assert.True(syntax.Profile.Ucp);
        Assert.Equal(Pcre2CompileFlags.None, syntax.ArgumentFlags);
        Assert.Equal(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.NotemptyAtstart, syntax.PatternMatchFlags);
        Assert.Equal(Pcre2Newline.Crlf, syntax.Profile.Newline);
        Assert.Equal(Pcre2Bsr.Anycrlf, syntax.Profile.Bsr);
        Assert.Equal(new Pcre2PatternLimits(0, 3, 2048, 0, 3, 2), syntax.PatternLimits);
    }
    [Fact]
    public void ScopedAndResetFlagsStayOnTheirAtoms()
    {
        var syntax = Parse("(?imnrsxU)(?-i:a)(?^:b)c");
        var atoms = syntax.Nodes.ToArray().Where(n => n.Kind == Pcre2NodeKind.Literal).ToArray();
        Assert.False(atoms[0].Flags.HasFlag(Pcre2CompileFlags.Caseless));
        Assert.True(atoms[0].Flags.HasFlag(Pcre2CompileFlags.Multiline));
        Assert.False(atoms[1].Flags.HasFlag(Pcre2CompileFlags.Multiline));
        Assert.False(atoms[1].ExtraFlags.HasFlag(Pcre2ExtraFlags.CaselessRestrict));
        Assert.True(atoms[2].Flags.HasFlag(Pcre2CompileFlags.Caseless));
    }
    [Theory]
    [InlineData("(", 114, 1)]
    [InlineData("[]", 106, 2)]
    [InlineData("\\q", 103, 1)]
    [InlineData("\\u0061", 137, 2)]
    [InlineData("(?<n>a)|(?<n>b)", 143, 13)]
    [InlineData("\\p{IsGreek}", 147, 11)]
    public void SourceDiagnostics(string pattern, int error, int offset)
    {
        var budget = new CompileBudget(new());
        Assert.False(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), new(), Pcre2Data.Instance, ref budget, out var syntax, out var d));
        Assert.Null(syntax);
        Assert.Equal(error, (int)d.SourceError);
        Assert.Equal(offset, d.NativeOffset);
        Assert.Equal(offset, d.Diagnostic.Offset);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void PatternDomainsRemainDistinct()
    {
        Parse("\uD800\0", new(Pcre2CodeUnitWidth.Sixteen));
        var budget = new CompileBudget(new());
        var parser = new Pcre2SyntaxParser();
        Assert.False(parser.TryParse("\uD800".AsSpan(), new(Pcre2CodeUnitWidth.Sixteen, Pcre2CompileFlags.Utf), Pcre2Data.Instance, ref budget, out _, out var d));
        Assert.Equal(-24, (int)d.SourceError);
        Assert.Equal(Pcre2DiagnosticKind.PatternEncoding, d.Kind);
        budget = new(new());
        Assert.False(parser.TryParse("Ā".AsSpan(), new(Pcre2CodeUnitWidth.Eight), Pcre2Data.Instance, ref budget, out _, out d));
        Assert.Equal(Pcre2DiagnosticKind.PatternEncoding, d.Kind);
        budget = new(new());
        Assert.False(parser.TryParse([0xC0, 0x80], new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf), Pcre2Data.Instance, ref budget, out _, out d));
        Assert.Equal(-17, (int)d.SourceError);
        budget = new(new());
        Assert.False(parser.TryParse([0x80], new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf | Pcre2CompileFlags.NoUtfCheck), Pcre2Data.Instance, ref budget, out _, out d));
        Assert.Equal(Pcre2DiagnosticKind.Capability, d.Kind);
    }
    [Fact]
    public void AdmissionFailsBeforeInputDerivedGrowthAndReleasesStorage()
    {
        var cases = new (string Pattern, RegexCompileLimits Limits, RegexDiagnosticCode Code)[]
        {
            ("abcd",new(maxPatternUnits:3),RegexDiagnosticCode.PatternLimitExceeded),
            ("a",new(maxPlanBytes:1),RegexDiagnosticCode.PlanLimitExceeded),
            ("a",new(maxCompileMemoryBytes:1),RegexDiagnosticCode.CompileMemoryLimitExceeded),
            ("a",new(maxCompileWork:1),RegexDiagnosticCode.CompileWorkLimitExceeded),
            ("abcd",new(maxInstructions:3),RegexDiagnosticCode.ProgramLimitExceeded),
            ("(a)(b)",new(maxCaptureGroups:2),RegexDiagnosticCode.CaptureLimitExceeded),
            ("[a-bc-d]",new(maxClassRanges:1),RegexDiagnosticCode.ClassLimitExceeded),
            ("((a))",new(maxNestingDepth:2),RegexDiagnosticCode.NestingLimitExceeded)
        };
        foreach (var (Pattern, Limits, Code) in cases)
        {
            var budget = new CompileBudget(Limits);
            Assert.False(new Pcre2SyntaxParser().TryParse(Pattern.AsSpan(), new(), Pcre2Data.Instance, ref budget, out _, out var d));
            Assert.Equal(Code, d.Diagnostic.Code);
            Assert.Equal(0, budget.ReservedBytes);
            Assert.True(budget.PeakBytes <= Limits.MaxCompileMemoryBytes);
            Assert.True(budget.WorkConsumed <= Limits.MaxCompileWork);
        }
        var noCopy = new CompileBudget(new(maxPlanBytes: 1));
        Assert.False(new Pcre2SyntaxParser().TryParse("abc".AsSpan(), new(), Pcre2Data.Instance, ref noCopy, out _, out _));
        Assert.Equal(0, noCopy.PeakBytes);
    }
    [Theory]
    [InlineData("[a-z&&[^aeiou]]", true, "b", false, "a")]
    [InlineData("(?[ [a-z] - [aeiou] ])", true, "b", false, "a")]
    [InlineData("(?[ ![a] & [a-z] ])", true, "b", false, "a")]
    [InlineData("(?[ [a] | [b] & [c] ])", true, "a", false, "b")]
    public void ClassAlgebraMatchesSourcePrecedence(string pattern, bool yes, string hit, bool no, string miss)
    {
        var syntax = Parse(pattern, new(flags: Pcre2CompileFlags.AltExtendedClass));
        var root = syntax.Nodes.ToArray().Single(n => n.Kind == Pcre2NodeKind.Class).Operand;
        Assert.Equal(yes, Contains(syntax, root, hit[0]));
        Assert.Equal(no, Contains(syntax, root, miss[0]));
    }
    private static bool Contains(Pcre2Program syntax, int root, int cp)
    {
        var values = new bool[syntax.Classes.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var c = syntax.Classes[i];
            values[i] = c.Kind switch
            {
                Pcre2ClassKind.Intervals => syntax.Intervals.Slice(c.Intervals.Offset, c.Intervals.Count).ToArray().Any(r => cp >= r.First && cp <= r.Last),
                Pcre2ClassKind.Union => values[c.Left] || values[c.Right],
                Pcre2ClassKind.Intersection => values[c.Left] && values[c.Right],
                Pcre2ClassKind.Subtraction => values[c.Left] && !values[c.Right],
                Pcre2ClassKind.SymmetricDifference => values[c.Left] != values[c.Right],
                Pcre2ClassKind.Complement => !values[c.Left],
                _ => throw new InvalidOperationException()
            };
        }
        return values[root];
    }
    // These independent native failures are semantic proof/resolution (lowerer)
    // or explicitly excluded physical/native-ABI admission. Their recorded
    // observations remain intact; this Syntax-stage check cannot pass them.
    private static bool DeferredCompileError(int code) => code is 115 or 116 or 119 or 120 or 125 or 133 or 135 or 136 or
        184 or 186 or 187 or 188 or 197 or 199 or 200 or 201;
    [Fact]
    public void RecursionConditionsAndForwardNameOverrides()
    {
        var any = Parse("(?(R)a|b)");
        Assert.Equal(Pcre2ConditionKind.AnyRecursion, any.Conditions[0].Kind);
        var whole = Parse("(?(R0)a|b)");
        Assert.Equal(Pcre2ConditionKind.Recursion, whole.Conditions[0].Kind);
        Assert.Equal(Pcre2ReferenceKind.WholePattern, whole.References[whole.Conditions[0].ReferenceIndex].Kind);
        var named = Parse("(?(R)a|b)(?<R>a)");
        Assert.Equal(Pcre2ConditionKind.CaptureSet, named.Conditions[0].Kind);
        var quoted = Parse("(?(<DEFINE>)a|b)(?<DEFINE>a)");
        Assert.Contains(quoted.Nodes.ToArray(), n => n.Kind == Pcre2NodeKind.Conditional);
        Assert.DoesNotContain(quoted.Nodes.ToArray(), n => n.Kind == Pcre2NodeKind.Define);
        var define = Parse("(?(DEFINE)a)(?<DEFINE>a)");
        Assert.Contains(define.Nodes.ToArray(), n => n.Kind == Pcre2NodeKind.Define);
        Assert.DoesNotContain(define.Nodes.ToArray(), n => n.Kind == Pcre2NodeKind.Conditional);
    }
    [Fact]
    public void ManualAndAutomaticCalloutSiteGeometry()
    {
        const string pattern = "(?<n>a)(b)?(?C0)c|a(?C\"tag\")d";
        var syntax = Parse(pattern, new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.AutoCallout));
        var expected = new (int Position, int Length, int Number, bool String, int Offset)[]
        {(0,5,255,false,0),(5,1,255,false,0),(6,1,255,false,0),(7,1,255,false,0),(8,1,255,false,0),
            (9,2,255,false,0),(16,1,0,false,0),(17,1,255,false,0),(18,1,255,false,0),(28,1,0,true,23),(29,0,255,false,0)};
        Assert.Equal(expected, syntax.Callouts.ToArray().Select(c => (c.PatternPosition, c.NextItemLength, c.Number, c.IsString, c.StringSourceOffset)).ToArray());
        var text = syntax.Callouts.ToArray().Single(c => c.IsString);
        Assert.Equal("tag"u8.ToArray(), syntax.NativeBytes(text.StringUnits).ToArray());
    }
    [Fact]
    public async Task ParserAndCopiedPlanHaveIndependentConcurrentLifetimes()
    {
        var parser = new Pcre2SyntaxParser();
        var jobs = Enumerable.Range(0, 16).Select(n => Task.Run(() =>
        {
            var input = ("(?<n>" + (char)('a' + n) + ")\\g{n}").ToCharArray();
            var budget = new CompileBudget(new());
            Assert.True(parser.TryParse(input, new(), Pcre2Data.Instance, ref budget, out var syntax, out var diagnostic));
            Array.Fill(input, 'x');
            Assert.NotNull(syntax);
            Assert.Equal(2, syntax.GroupCount);
            Assert.Equal("n", syntax.Names[0].Name);
            Assert.Contains(syntax.Nodes.ToArray(), node => node.Kind == Pcre2NodeKind.Literal && node.Operand == 'a' + n);
            Assert.Equal(Pcre2DiagnosticKind.None, diagnostic.Kind);
        }));
        await Task.WhenAll(jobs);
    }
    [Fact]
    public void AlternateUnicodeEscapeCannotCreateAQuantifier()
    {
        var syntax = Parse("\\u{ 12}", new(extraFlags: Pcre2ExtraFlags.AltBsux));
        Assert.DoesNotContain(syntax.Nodes.ToArray(), n => n.Kind == Pcre2NodeKind.Repeat);
        Assert.Equal("u{ 12}", new string([.. syntax.Nodes.ToArray().Where(n => n.Kind == Pcre2NodeKind.Literal).Select(n => (char)n.Operand)]));
    }
    private static string Root()
    { var p = AppContext.BaseDirectory; while (p != null) { if (File.Exists(Path.Combine(p, "GregsEx.slnx"))) { return p; } p = Directory.GetParent(p)?.FullName; } throw new InvalidOperationException(); }
}
