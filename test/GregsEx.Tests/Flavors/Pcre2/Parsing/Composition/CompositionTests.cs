using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Parsing.Composition;

public sealed class CompositionTests
{
    private const long CallerBytes = 97;
    private static CompileBudget Budget(RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.True(budget.TryReserveBytes(CallerBytes, default, out _));
        Assert.True(budget.TryCharge(13, default, out _));
        return budget;
    }

    private static Pcre2Program Compile(string pattern, Pcre2Profile? profile = null)
    {
        var budget = Budget();
        Assert.True(new Pcre2Compiler().TryCompile(pattern.AsSpan(), profile ?? new(), Pcre2Data.Instance,
            ref budget, out var program, out var diagnostic), diagnostic.ToString());
        Assert.NotNull(program);
        Assert.True(program.IsExecutable);
        Assert.Equal(default, diagnostic);
        Assert.Equal(CallerBytes + program.RetainedBytes, budget.ReservedBytes);
        return program;
    }

    [Theory]
    [InlineData(8, false, false)]
    [InlineData(8, false, true)]
    [InlineData(8, true, false)]
    [InlineData(8, true, true)]
    [InlineData(16, false, false)]
    [InlineData(16, false, true)]
    [InlineData(16, true, false)]
    [InlineData(16, true, true)]
    public void NativeDomainsComposeVerifiedPlansWithoutChangingMetadata(int width, bool utf, bool ucp)
    {
        var tableInput = Pcre2CharacterTables.Default.Bytes.ToArray();
        tableInput[256 + 'A'] = (byte)'x';
        var tables = new Pcre2CharacterTables(tableInput);
        Array.Fill(tableInput, (byte)0);
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width,
            (utf ? Pcre2CompileFlags.Utf : 0) | (ucp ? Pcre2CompileFlags.Ucp : 0) | Pcre2CompileFlags.AutoCallout,
            characterTables: tables);
        const string pattern = "(*NOTEMPTY)(*NOTEMPTY_ATSTART)(*CRLF)(*BSR_ANYCRLF)(*LIMIT_MATCH=3)(*LIMIT_DEPTH=2)(*LIMIT_HEAP=1)(?<x>a\\n){2}(?<=a{1,3})(?&x)\\k<x>(?C'\0')(*MARK:tag)";
        var expectedBudget = Budget();
        var parser = new Pcre2SyntaxParser();
        var parsed = width == 8 ? parser.TryParse(Encoding.ASCII.GetBytes(pattern), profile, Pcre2Data.Instance,
            ref expectedBudget, out var syntax, out var diagnostic) : parser.TryParse(pattern.AsSpan(), profile, Pcre2Data.Instance,
            ref expectedBudget, out syntax, out diagnostic);
        Assert.True(parsed, diagnostic.ToString());
        Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref expectedBudget,
            out var expected, out diagnostic), diagnostic.ToString());
        var overlap = expectedBudget.ReservedBytes;
        expectedBudget.ReleaseBytes(syntax!.RetainedBytes);
        var actualBudget = Budget();
        IPcre2Compiler compiler = new Pcre2Compiler();
        bool ok;
        Pcre2Program? actual;
        if (width == 8)
        {
            var native = Encoding.ASCII.GetBytes(pattern);
            ok = compiler.TryCompile(native, profile, Pcre2Data.Instance, ref actualBudget, out actual, out diagnostic);
            Assert.Equal(RegexOffsetUnit.Bytes, actual!.SourceUnit);
            Array.Fill(native, (byte)'z');
        }
        else
        {
            var native = pattern.ToCharArray();
            ok = compiler.TryCompile(native, profile, Pcre2Data.Instance, ref actualBudget, out actual, out diagnostic);
            Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, actual!.SourceUnit);
            Array.Fill(native, 'z');
        }
        Assert.True(ok, diagnostic.ToString());
        Assert.Equal(default, diagnostic);
        Assert.Equal(expected!.Nodes.ToArray(), actual.Nodes.ToArray());
        EqualMetadata(expected, actual);
        Assert.Equal(expectedBudget.WorkConsumed, actualBudget.WorkConsumed);
        Assert.Equal(expectedBudget.PeakBytes, actualBudget.PeakBytes);
        Assert.Equal(expectedBudget.ReservedBytes, actualBudget.ReservedBytes);
        Assert.True(actualBudget.PeakBytes >= overlap);
        Assert.Same(tables, actual.Profile.CharacterTables);
        Assert.Equal(tables.Digest, actual.Profile.CharacterTables.Digest);
        Assert.Equal(utf, actual.Profile.Utf);
        Assert.Equal(ucp, actual.Profile.Ucp);
        Assert.Equal(profile.Flags, actual.ArgumentFlags);
        Assert.Equal(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.NotemptyAtstart, actual.PatternMatchFlags);
        Assert.True(actual.HasExplicitCrOrLf);
        Assert.True(actual.HasLookbehindMetadata);
        Assert.NotNull(actual.SearchHints);
        Assert.Equal(new Pcre2PatternLimits(3, 2, 1024, 3, 2, 1), actual.PatternLimits);
        Assert.Equal(Pcre2Newline.Crlf, actual.Profile.Newline);
        Assert.Equal(Pcre2Bsr.Anycrlf, actual.Profile.Bsr);
        var site = actual.Callouts.ToArray().Single(c => c.IsString);
        if (width == 8)
        {
            Assert.Equal(new byte[] { 0 }, actual.NativeBytes(site.StringUnits).ToArray());
        }
        else
        {
            Assert.Equal(new char[] { '\0' }, actual.NativeChars(site.StringUnits).ToArray());
        }
    }

    private static void EqualMetadata(Pcre2Program expected, Pcre2Program actual)
    {
        Assert.Equal(expected.Children.ToArray(), actual.Children.ToArray());
        Assert.Equal(expected.Classes.ToArray(), actual.Classes.ToArray());
        Assert.Equal(expected.Intervals.ToArray(), actual.Intervals.ToArray());
        Assert.Equal(expected.Groups.ToArray(), actual.Groups.ToArray());
        Assert.Equal(expected.Names.ToArray(), actual.Names.ToArray());
        Assert.Equal(expected.Numbers.ToArray(), actual.Numbers.ToArray());
        Assert.Equal(expected.References.ToArray(), actual.References.ToArray());
        Assert.Equal(expected.Assertions.ToArray(), actual.Assertions.ToArray());
        Assert.Equal(expected.Conditions.ToArray(), actual.Conditions.ToArray());
        Assert.Equal(expected.LookbehindBranches.ToArray(), actual.LookbehindBranches.ToArray());
        Assert.Equal(expected.Callouts.ToArray(), actual.Callouts.ToArray());
        Assert.Equal(expected.Controls.ToArray(), actual.Controls.ToArray());
        Assert.Equal(expected.Marks.ToArray(), actual.Marks.ToArray());
        Assert.Equal(expected.ScanReferences.ToArray(), actual.ScanReferences.ToArray());
        Assert.Equal(expected.Units.ToArray(), actual.Units.ToArray());
        Assert.Equal(expected.SourceMap.ToArray(), actual.SourceMap.ToArray());
        Assert.Equal(expected.StartBitmap.ToArray(), actual.StartBitmap.ToArray());
        Assert.Equal(expected.SearchHints, actual.SearchHints);
        Assert.Equal(expected.ProjectionRestrictions, actual.ProjectionRestrictions);
        Assert.Equal(expected.RetainedBytes, actual.RetainedBytes);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void SourceNewlineProvenanceAndOptimizerDisableControlsSurvive(int width, bool utf)
    {
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0);
        Assert.True(Compile("a\\n{0}", profile).HasExplicitCrOrLf);
        Assert.False(Compile("[^\\n]", profile).HasExplicitCrOrLf);
        Assert.True(Compile("[^\\n\\n]", profile).HasExplicitCrOrLf);
        Assert.False(Compile("\\s", profile).HasExplicitCrOrLf);
        var normal = Compile("(*COMMIT)ABC", profile);
        var disabled = Compile("(*NO_START_OPT)(*NO_AUTO_POSSESS)(*NO_DOTSTAR_ANCHOR)(*COMMIT)ABC", profile);
        Assert.Equal('A', normal.SearchHints!.Value.FirstCodeUnit);
        Assert.Equal(-1, disabled.SearchHints!.Value.FirstCodeUnit);
        Assert.Equal(Pcre2OptimizationPolicy.None, disabled.Profile.EffectiveOptimization);
    }

    [Fact]
    public void StrictUtf8ConvenienceAndNativeSourcesKeepDistinctCoordinates()
    {
        var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight);
        const string pattern = "(*UTF)😀(?C'😀')(?<x>a)\\k<x>";
        var chars = Compile(pattern, profile);
        var budget = Budget();
        var bytes = Encoding.UTF8.GetBytes(pattern);
        Assert.True(new Pcre2Compiler().TryCompile(bytes, profile, Pcre2Data.Instance, ref budget, out var native, out _));
        Assert.Equal(chars.Units.ToArray(), native!.Units.ToArray());
        Assert.Equal(pattern.Length, chars.SourceLength);
        Assert.Equal(bytes.Length, chars.NativePatternLength);
        Assert.Equal(bytes.Length, native.SourceLength);
        Assert.Equal(Enumerable.Range(0, bytes.Length + 1), native.SourceMap.ToArray());
        Assert.Equal(pattern.Length, chars.SourceMap[^1]);
        Assert.Contains(chars.SourceMap.ToArray().GroupBy(x => x), g => g.Count() == 4);
        var site = chars.Callouts.ToArray().Single(c => c.IsString);
        Assert.Equal(Encoding.UTF8.GetBytes("😀"), chars.NativeBytes(site.StringUnits).ToArray());
        var failed = Budget();
        Assert.False(new Pcre2Compiler().TryCompile("(*UTF)😀(?&missing)".AsSpan(), profile, Pcre2Data.Instance,
            ref failed, out var absent, out var diagnostic));
        Assert.Null(absent);
        Assert.Equal(115, (int)diagnostic.SourceError);
        Assert.Equal(13, diagnostic.NativeOffset);
        Assert.Equal(11, diagnostic.Diagnostic.Offset);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.Diagnostic.LocationUnit);
        Assert.Equal(CallerBytes, failed.ReservedBytes);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void NativePatternDomainAndPayloadCopiesAreLossless(int width, bool utf)
    {
        var pattern = width == 8 ? "\0ÿ(?C'ÿ')" : utf ? "\0😀(?C'😀')" : "\0\ud800(?C'\ud800')";
        var plan = Compile(pattern, new((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0));
        Assert.Equal(0, plan.Units[0]);
        Assert.Equal(pattern.Length, plan.NativePatternLength);
        Assert.Equal(Enumerable.Range(0, pattern.Length + 1), plan.SourceMap.ToArray());
        var site = plan.Callouts[0];
        if (width == 8)
        {
            Assert.Equal(new byte[] { 255 }, plan.NativeBytes(site.StringUnits).ToArray());
        }
        else
        {
            Assert.Equal((utf ? "😀" : "\ud800").ToCharArray(), plan.NativeChars(site.StringUnits).ToArray());
        }
    }

    [Theory]
    [InlineData("(?|(a)(b)|(c)\\g{-1})")]
    [InlineData("(?(DEFINE)(?<n>a))(?&n)")]
    [InlineData("(?&n)(?<n>a)")]
    [InlineData("(?(?=a)a|b)")]
    [InlineData("(*scs:(1)(a))(a)")]
    [InlineData("(?[ [a-z] - [aeiou] ])")]
    [InlineData("(?i:\\p{Lu}\\P{Ll}[[:upper:]\\p{Lt}])(?-i:\\p{Lu})")]
    public void AdvancedSemanticPlansAreResolvedBeforeFactoryHandoff(string pattern)
    {
        var plan = Compile(pattern);
        Assert.True(plan.HasLookbehindMetadata);
        Assert.NotNull(plan.SearchHints);
        Assert.All(plan.References.ToArray(), r => Assert.True(r.Kind == Pcre2ReferenceKind.WholePattern || r.TargetDefinition >= 0));
    }

    [Theory]
    [InlineData("(", 1)]
    [InlineData("(*UNKNOWN)", 1)]
    [InlineData("(?<=a+)b", 1)]
    [InlineData("(?&missing)", 1)]
    [InlineData("(?=a\\K)", 1)]
    [InlineData("a", 2)]
    [InlineData("Ā", 4)]
    public void SyntaxOptionsEncodingAndSemanticDiagnosticsAreForwardedExactly(string pattern, int kind)
    {
        var profile = kind == 2 ? new Pcre2Profile(flags: Pcre2CompileFlags.Literal | Pcre2CompileFlags.Ucp) :
            kind == 4 ? new Pcre2Profile(Pcre2CodeUnitWidth.Eight) : new Pcre2Profile();
        var separate = Budget();
        var parsed = new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile, Pcre2Data.Instance, ref separate, out var syntax, out var expected);
        if (parsed)
        {
            Assert.False(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref separate, out _, out expected));
        }

        var budget = Budget();
        Assert.False(new Pcre2Compiler().TryCompile(pattern.AsSpan(), profile, Pcre2Data.Instance, ref budget, out var plan, out var actual));
        Assert.Null(plan);
        Assert.Equal(expected, actual);
        Assert.Equal(kind, (int)actual.Kind);
        Assert.Equal(separate.WorkConsumed, budget.WorkConsumed);
        Assert.Equal(separate.PeakBytes, budget.PeakBytes);
        Assert.Equal(CallerBytes, budget.ReservedBytes);
    }

    [Fact]
    public void UndefinedUncheckedPatternIsCapabilityRatherThanSyntaxOrResourceFailure()
    {
        var budget = Budget();
        Assert.False(new Pcre2Compiler().TryCompile([0x80],
            new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf | Pcre2CompileFlags.NoUtfCheck),
            Pcre2Data.Instance, ref budget, out var plan, out var diagnostic));
        Assert.Null(plan);
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
        Assert.Equal(RegexDiagnosticCode.UnsupportedFeature, diagnostic.Diagnostic.Code);
        Assert.Equal("PC-002/PH-012", diagnostic.CapabilityId);
        Assert.Equal(0, diagnostic.NativeOffset);
        Assert.Equal(CallerBytes, budget.ReservedBytes);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void CumulativeWorkPeakOverlapAndCallerReservationsHaveExactBoundaries(int width, bool utf)
    {
        const string pattern = "(?<=a{1,3})(?<x>b+)\\k<x>";
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0);
        var baseline = Budget();
        Assert.True(new Pcre2Compiler().TryCompile(pattern.AsSpan(), profile, Pcre2Data.Instance, ref baseline, out var plan, out _));
        var parserBudget = Budget();
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), profile, Pcre2Data.Instance, ref parserBudget, out var syntax, out _));
        var exact = Budget(new(maxCompileWork: baseline.WorkConsumed, maxCompileMemoryBytes: baseline.PeakBytes, maxPlanBytes: plan!.RetainedBytes));
        Assert.True(new Pcre2Compiler().TryCompile(pattern.AsSpan(), profile, Pcre2Data.Instance, ref exact, out _, out _));
        Assert.Equal(baseline.WorkConsumed, exact.WorkConsumed);
        Assert.Equal(baseline.PeakBytes, exact.PeakBytes);
        Assert.True(baseline.PeakBytes > CallerBytes + syntax!.RetainedBytes + plan.RetainedBytes);
        foreach (var limits in new[] { new RegexCompileLimits(maxCompileWork: baseline.WorkConsumed - 1),
            new RegexCompileLimits(maxCompileWork: parserBudget.WorkConsumed),
            new RegexCompileLimits(maxCompileMemoryBytes: baseline.PeakBytes - 1),
            new RegexCompileLimits(maxPlanBytes: plan.RetainedBytes - 1),
            new RegexCompileLimits(maxInstructions: 1), new RegexCompileLimits(maxCaptureGroups: 1) })
        {
            var denied = Budget(limits);
            Assert.False(new Pcre2Compiler().TryCompile(pattern.AsSpan(), profile, Pcre2Data.Instance, ref denied, out var absent, out var diagnostic));
            Assert.Null(absent);
            Assert.Equal(Pcre2DiagnosticKind.CompileLimit, diagnostic.Kind);
            Assert.Equal(CallerBytes, denied.ReservedBytes);
            Assert.True(denied.PeakBytes <= limits.MaxCompileMemoryBytes);
            Assert.True(denied.WorkConsumed <= limits.MaxCompileWork);
        }
        // A second compile uses the same budget without releasing the first plan.
        Assert.True(new Pcre2Compiler().TryCompile("z".AsSpan(), profile, Pcre2Data.Instance, ref baseline, out var second, out _));
        Assert.Equal(CallerBytes + plan.RetainedBytes + second!.RetainedBytes, baseline.ReservedBytes);
        Assert.True(baseline.WorkConsumed > exact.WorkConsumed);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void ProjectionIsAnExplicitCompileAdmissionAndPreservesTypedNativePlans(int width)
    {
        var compiler = new Pcre2Compiler();
        foreach (var (pattern, flags, extras, restriction) in new[] {
            ("\\C", Pcre2CompileFlags.Utf, Pcre2ExtraFlags.None, Pcre2ProjectionRestrictions.UtfCodeUnit),
            ("a", Pcre2CompileFlags.MatchInvalidUtf, Pcre2ExtraFlags.None, Pcre2ProjectionRestrictions.InvalidUtf),
            ("a", Pcre2CompileFlags.Utf | Pcre2CompileFlags.NoUtfCheck, Pcre2ExtraFlags.None, Pcre2ProjectionRestrictions.NoUtfCheck),
            ("(?=ab\\K)", Pcre2CompileFlags.None, Pcre2ExtraFlags.AllowLookaroundBsk, Pcre2ProjectionRestrictions.LookaroundReset) })
        {
            var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, flags, extras);
            var typed = Compile(pattern, profile);
            Assert.Equal(restriction, typed.ProjectionRestrictions);
            var budget = Budget();
            var ok = width == 8 ? compiler.TryCompile(Encoding.ASCII.GetBytes(pattern), profile, Pcre2Data.Instance,
                Pcre2ProjectionPolicy.WholeSubjectValidated, ref budget, out var projected, out var diagnostic) :
                compiler.TryCompile(pattern.AsSpan(), profile, Pcre2Data.Instance,
                Pcre2ProjectionPolicy.WholeSubjectValidated, ref budget, out projected, out diagnostic);
            Assert.False(ok);
            Assert.Null(projected);
            Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
            Assert.Equal(RegexDiagnosticCode.UnsupportedFeature, diagnostic.Diagnostic.Code);
            Assert.Equal("PH-002/PH-003/PC-017/PC-023", diagnostic.CapabilityId);
            Assert.Equal(CallerBytes, budget.ReservedBytes);
            Assert.True(budget.WorkConsumed > 13);
        }
        var admitted = Budget();
        Assert.True(compiler.TryCompile("a\\Kb".AsSpan(), new((Pcre2CodeUnitWidth)width), Pcre2Data.Instance,
            Pcre2ProjectionPolicy.WholeSubjectValidated, ref admitted, out var plan, out _));
        Assert.Equal(Pcre2ProjectionRestrictions.None, plan!.ProjectionRestrictions);
        Assert.Equal(CallerBytes + plan.RetainedBytes, admitted.ReservedBytes);
        // A source-valid raw code-unit atom does not split UTF and remains eligible.
        Assert.True(Compile("\\C", new((Pcre2CodeUnitWidth)width)).TryAdmitProjection(Pcre2ProjectionPolicy.WholeSubjectValidated, out _));
    }

    [Fact]
    public async Task StatelessCompilerSupportsConcurrentIndependentBudgetsAndCopiedInputs()
    {
        var compiler = new Pcre2Compiler();
        var plans = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            var source = $"(?<n>{(char)('a' + i)})\\k<n>".ToCharArray();
            var budget = Budget();
            Assert.True(compiler.TryCompile(source, new(), Pcre2Data.Instance, ref budget, out var plan, out _));
            Array.Fill(source, 'z');
            Assert.Equal(CallerBytes + plan!.RetainedBytes, budget.ReservedBytes);
            Assert.Equal('a' + i, plan.Nodes.ToArray().Single(n => n.Kind == Pcre2NodeKind.Literal).Operand);
            Assert.Equal(0, plan.References[0].TargetDefinition);
            return plan;
        })));
        Assert.Equal(8, plans.Distinct().Count());
        Assert.All(plans, p => Assert.True(p.IsExecutable));
    }

    [Fact]
    public void UnexpectedProviderFailurePropagatesAndReleasesSyntaxStorage()
    {
        var provider = new ThrowingData();
        const string pattern = "(?i)[a-c]";
        var separate = Budget();
        // This shape reaches the provider during lowering, after Syntax copying.
        Assert.True(new Pcre2SyntaxParser().TryParse(pattern.AsSpan(), new(), provider, ref separate, out var syntax, out _));
        Assert.True(separate.ReservedBytes > CallerBytes);
        var budget = Budget();
        try
        {
            new Pcre2Compiler().TryCompile(pattern.AsSpan(), new(), provider, ref budget, out _, out _);
            Assert.Fail("Provider failure was swallowed.");
        }
        catch (InvalidOperationException error) { Assert.Equal("Provider failure", error.Message); }
        Assert.Equal(CallerBytes, budget.ReservedBytes);
        Assert.True(budget.PeakBytes > syntax!.RetainedBytes);
        Assert.True(new Pcre2Compiler().TryCompile("ok".AsSpan(), new(), Pcre2Data.Instance, ref budget, out var reused, out _));
        Assert.Equal(CallerBytes + reused!.RetainedBytes, budget.ReservedBytes);
    }

    private sealed class ThrowingData : IPcre2Data
    {
        public string DataId => Pcre2Data.Instance.DataId;
        public string SourceDigest => Pcre2Data.Instance.SourceDigest;
        public bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value, out Pcre2PropertyKey property)
            => Pcre2Data.Instance.TryResolveProperty(name, value, out property);
        public bool IsValidGroupName(ReadOnlySpan<char> name, bool utf) => Pcre2Data.Instance.IsValidGroupName(name, utf);
        public Pcre2DataView Bind(Pcre2CharacterTables tables) => throw new InvalidOperationException("Provider failure");
    }

    [Fact]
    public void InvalidProjectionRequestThrowsBeforeCompilationOrReservation()
    {
        var budget = Budget();
        try
        {
            new Pcre2Compiler().TryCompile("a".AsSpan(), new(), Pcre2Data.Instance,
                (Pcre2ProjectionPolicy)123, ref budget, out _, out _);
            Assert.Fail("Invalid policy was accepted.");
        }
        catch (ArgumentOutOfRangeException error) { Assert.Equal("projection", error.ParamName); }
        Assert.Equal(CallerBytes, budget.ReservedBytes);
        Assert.Equal(13, budget.WorkConsumed);
        Assert.Equal(CallerBytes, budget.PeakBytes);
    }
}
