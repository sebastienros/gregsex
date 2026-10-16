using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Host;

// Source-derived: ECMA-2622025 §22.2.6.11/12/13/14/15/16/17,
// §22.2.7.2/.3 and §22.1.3.19.1; the frozen M2 host contract admits
// typed strings/numeric state only. No parser or BCL engine supplies expectations.
public sealed class HostSemanticsTests
{
    [Theory]
    [InlineData(EcmaScriptFlags.None, 17.5, 0, 17.5)]
    [InlineData(EcmaScriptFlags.Global, 0.9, 0, 1)]
    [InlineData(EcmaScriptFlags.Global, 1.9, 2, 3)]
    [InlineData(EcmaScriptFlags.Sticky, 2.9, 2, 3)]
    [InlineData(EcmaScriptFlags.Global | EcmaScriptFlags.Sticky, 2.1, 2, 3)]
    public void ExecUsesNumericBuiltinState(EcmaScriptFlags flags, double seed, int index, double after)
    {
        var executor = new ContractExecutor("a", flags);
        using var matcher = executor.Bind();
        matcher.LastIndex = seed;
        var captures = new RegexCapture[2];
        captures[1] = new(true, 99, 2);
        Assert.Equal(RegexSearchStatus.Match, matcher.Exec("aba", captures, out var range));
        Assert.Equal(new(index, 1), range);
        Assert.Equal(new(true, index, 1), captures[0]);
        Assert.Equal(new(true, 99, 2), captures[1]);
        Assert.Equal(after, matcher.LastIndex);
        Assert.Equal(EcmaStartPolicy.BuiltinLastIndex, executor.LastPolicy);
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-5.2)]
    [InlineData(-0.0)]
    public void NonpositiveLastIndexStartsAtZero(double seed)
    {
        using var matcher = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        matcher.LastIndex = seed;
        Assert.Equal(RegexSearchStatus.Match, matcher.Test("a"));
        Assert.Equal(1d, matcher.LastIndex);
    }
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(9_007_199_254_740_992d)]
    [InlineData(2.0)]
    public void BeyondEndResetsState(double seed)
    {
        using var matcher = new ContractExecutor("", EcmaScriptFlags.Global).Bind();
        matcher.LastIndex = seed;
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.Test("a"));
        Assert.Equal(0d, matcher.LastIndex);
    }
    [Theory]
    [InlineData(EcmaScriptFlags.Global)]
    [InlineData(EcmaScriptFlags.Sticky)]
    public void FailureResetsGlobalAndSticky(EcmaScriptFlags flags)
    {
        using var matcher = new ContractExecutor("a", flags).Bind();
        matcher.LastIndex = 1;
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.Test("a"));
        Assert.Equal(0d, matcher.LastIndex);
    }
    [Fact]
    public void StickyDoesNotScanAndOrdinaryIgnoresLastIndex()
    {
        using var sticky = new ContractExecutor("a", EcmaScriptFlags.Sticky).Bind();
        Assert.Equal(RegexSearchStatus.NoMatch, sticky.Test("ba"));
        using var ordinary = new ContractExecutor("a").Bind();
        ordinary.LastIndex = double.NaN;
        Assert.Equal(RegexSearchStatus.Match, ordinary.Test("ba"));
        Assert.True(double.IsNaN(ordinary.LastIndex));
    }
    [Theory]
    [InlineData(EcmaScriptFlags.Global, 1)]
    [InlineData(EcmaScriptFlags.Global | EcmaScriptFlags.Unicode, 2)]
    [InlineData(EcmaScriptFlags.Sticky | EcmaScriptFlags.UnicodeSets, 2)]
    public void RawEmptyAndIterationHaveDifferentAdvancement(EcmaScriptFlags flags, int advance)
    {
        using var matcher = new ContractExecutor("", flags).Bind();
        var groups = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.Match, matcher.Exec("\ud83d\ude00", groups, out _));
        Assert.Equal(0d, matcher.LastIndex);
        Assert.Equal(RegexSearchStatus.Match, matcher.ExecNext("\ud83d\ude00", groups, out _));
        Assert.Equal((double)advance, matcher.LastIndex);
        matcher.LastIndex = 2;
        Assert.Equal(RegexSearchStatus.Match, matcher.ExecNext("\ud83d\ude00", groups, out var range));
        Assert.Equal(new(2, 0), range);
        Assert.Equal(3d, matcher.LastIndex);
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.ExecNext("\ud83d\ude00", groups, out _));
        Assert.Equal(0d, matcher.LastIndex);
    }
    [Fact]
    public void ExecNextRequiresStatefulFlag()
    {
        using var matcher = new ContractExecutor("").Bind();
        Assert.Throws<InvalidOperationException>(() => matcher.ExecNext("", new RegexCapture[1], out _));
    }
    [Theory]
    [InlineData(EcmaScriptFlags.Unicode)]
    [InlineData(EcmaScriptFlags.UnicodeSets)]
    public void InteriorSurrogateUsesBuiltinMapping(EcmaScriptFlags unicode)
    {
        using var matcher = new ContractExecutor("", EcmaScriptFlags.Sticky | unicode) { Dot = true }.Bind();
        matcher.LastIndex = 1;
        var groups = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.Match, matcher.Exec("\ud83d\ude00", groups, out var range));
        Assert.Equal(new(0, 2), range);
        Assert.Equal(2d, matcher.LastIndex);
    }
    [Fact]
    public void MatchDistinguishesSingleGlobalAndNull()
    {
        using var single = new ContractExecutor("a", captures: [default, new(true, 1, 0)]).Bind();
        var result = single.Match("a")!;
        Assert.False(result.IsGlobal);
        Assert.Empty(result.GlobalMatches);
        Assert.Equal(default, result.Single!.Captures[1]);
        Assert.Equal(new(true, 1, 0), result.Single.Captures[2]);
        using var global = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        global.LastIndex = 1;
        var matches = global.Match("aba")!;
        Assert.True(matches.IsGlobal);
        Assert.Null(matches.Single);
        Assert.Equal(new[] { "a", "a" }, matches.GlobalMatches);
        Assert.Equal(0d, global.LastIndex);
        Assert.Null(global.Match("z"));
        Assert.Null(single.Match("z"));
    }
    [Theory]
    [InlineData(EcmaScriptFlags.None, 3)]
    [InlineData(EcmaScriptFlags.Unicode, 2)]
    [InlineData(EcmaScriptFlags.UnicodeSets, 2)]
    public void GlobalMatchEmptyAdvanceAndLoneSurrogates(EcmaScriptFlags mode, int count)
    {
        using var matcher = new ContractExecutor("", mode | EcmaScriptFlags.Global).Bind();
        Assert.Equal(count, matcher.Match("\ud83d\ude00")!.GlobalMatches.Count);
        Assert.Equal(2, matcher.Match("\ud800")!.GlobalMatches.Count);
        Assert.Equal(0d, matcher.LastIndex);
    }
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.0)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.5)]
    public void SearchRestoresExactNumericState(double original)
    {
        using var matcher = new ContractExecutor("a", EcmaScriptFlags.Global | EcmaScriptFlags.Sticky).Bind();
        matcher.LastIndex = original;
        Assert.Equal(-1, matcher.Search("ba"));
        Assert.Equal(BitConverter.DoubleToInt64Bits(original), BitConverter.DoubleToInt64Bits(matcher.LastIndex));
        Assert.Equal(0, matcher.Search("a"));
        Assert.Equal(BitConverter.DoubleToInt64Bits(original), BitConverter.DoubleToInt64Bits(matcher.LastIndex));
        Assert.Throws<RegexExecutionException>(() => matcher.Search("a", new() { Limits = new(maxWork: 1) }));
        Assert.Equal(BitConverter.DoubleToInt64Bits(original), BitConverter.DoubleToInt64Bits(matcher.LastIndex));
    }
    [Fact]
    public void NamedDuplicateAliasesAndIndicesAreImmutable()
    {
        using var matcher = new ContractExecutor("b", EcmaScriptFlags.HasIndices,
            [default, new(true, 0, 1)], [new("x", 0, 2)], [1, 2]).Bind();
        var result = matcher.Match("b")!.Single!;
        Assert.True(result.HasIndices);
        Assert.Equal(new EcmaScriptNamedCapture("x", new(true, 0, 1)), result.Names[0]);
        Assert.Equal(result.Captures.ToArray(), result.Indices.ToArray());
        Assert.Null(matcher.Match("z"));
        matcher.Dispose();
        Assert.Equal(new(true, 0, 1), result.Names[0].Capture);
    }
    [Theory]
    [InlineData("$$", "x$z")]
    [InlineData("$&", "xbz")]
    [InlineData("$`", "xxz")]
    [InlineData("$'", "xzz")]
    [InlineData("$1", "xbz")]
    [InlineData("$01", "xbz")]
    [InlineData("$10", "xb0z")]
    [InlineData("$2", "xz")]
    [InlineData("$00", "x$00z")]
    [InlineData("$3", "x$3z")]
    [InlineData("$99", "x$99z")]
    [InlineData("$", "x$z")]
    [InlineData("$<x>", "x$<x>z")]
    public void GetSubstitutionNumberAndLiteralFallback(string template, string expected)
    {
        using var matcher = new ContractExecutor("b", captures: [new(true, 0, 1), default]).Bind();
        Assert.Equal(expected, matcher.Replace("xbz", template));
    }
    [Theory]
    [InlineData("$<x>", "xbz")]
    [InlineData("$<missing>", "xz")]
    [InlineData("$<x", "x$<xz")]
    [InlineData("$<>", "xz")]
    public void NamedSubstitutionResolvesParticipatingAlias(string template, string expected)
    {
        using var matcher = new ContractExecutor("b", captures: [default, new(true, 0, 1)],
            names: [new("x", 0, 2)], aliases: [1, 2]).Bind();
        Assert.Equal(expected, matcher.Replace("xbz", template));
    }
    [Fact]
    public void TwoDigitSubstitutionAndNoRecursiveTokenExpansion()
    {
        var captures = new RegexCapture[12];
        captures[11] = new(true, 0, 1);
        using var matcher = new ContractExecutor("b", captures: captures).Bind();
        Assert.Equal("b-$12", matcher.Replace("b", "$12-$$12"));
    }
    [Fact]
    public void ReplaceCollectsGlobalMatchesAndPreservesOrdinaryState()
    {
        using var matcher = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        matcher.LastIndex = 2;
        Assert.Equal("[a]b[a]", matcher.ReplaceAll("aba", "[$&]"));
        Assert.Equal(0d, matcher.LastIndex);
        using var ordinary = new ContractExecutor("a").Bind();
        ordinary.LastIndex = 19;
        Assert.Equal("xba", ordinary.Replace("aba", "x"));
        Assert.Equal(19d, ordinary.LastIndex);
        Assert.Equal("z", ordinary.Replace("z", "x"));
        Assert.Equal(EcmaScriptHostFailureCode.GlobalFlagRequired, Assert.Throws<EcmaScriptHostException>(() => ordinary.ReplaceAll("a", "")).Failure.Code);
    }
    [Fact]
    public void EmptyGlobalReplacementAdvancesAndIncludesTerminal()
    {
        using var matcher = new ContractExecutor("", EcmaScriptFlags.Global | EcmaScriptFlags.Unicode).Bind();
        Assert.Equal("x\ud83d\ude00x", matcher.Replace("\ud83d\ude00", "x"));
        Assert.Equal(0d, matcher.LastIndex);
    }
    [Theory]
    [InlineData(null, 3)]
    [InlineData(0d, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(1.9, 1)]
    [InlineData(4_294_967_297d, 1)]
    [InlineData(-1d, 3)]
    public void SplitUint32AndSourceState(double? limit, int count)
    {
        using var matcher = new ContractExecutor(",", EcmaScriptFlags.Global).Bind();
        matcher.LastIndex = 42;
        var result = matcher.Split("a,b,c", limit);
        Assert.Equal(count, result.Length);
        Assert.Equal(42d, matcher.LastIndex);
        if (count == 3)
        {
            Assert.Equal(new[] { "a", "b", "c" }, result);
        }
    }
    [Fact]
    public void SplitCapturesAbsentEmptyAndLimitDuringInsertion()
    {
        using var matcher = new ContractExecutor(",", captures: [new(true, 0, 1), default, new(true, 0, 0)]).Bind();
        Assert.Equal(new string?[] { "a", ",", null, "", "b" }, matcher.Split("a,b"));
        Assert.Equal(new string?[] { "a", ",", null }, matcher.Split("a,b", 3));
        Assert.Equal(new string?[] { "", ",", null, "", "" }, matcher.Split(","));
    }
    [Fact]
    public void SplitEmptyPatternDoesNotAddBoundaryEmpties()
    {
        using var matcher = new ContractExecutor("", EcmaScriptFlags.Unicode).Bind();
        Assert.Equal(new[] { "a", "b" }, matcher.Split("ab"));
        Assert.Empty(matcher.Split(""));
        Assert.Equal(new[] { "\ud83d\ude00", "a" }, matcher.Split("\ud83d\ude00a"));
        using var noMatch = new ContractExecutor("z").Bind();
        Assert.Equal(new[] { "" }, noMatch.Split(""));
        Assert.Equal(new[] { "ab" }, noMatch.Split("ab"));
    }
    [Fact]
    public void SplitUsesIndependentStickyCloneEvenWithoutStickyFlag()
    {
        using var matcher = new ContractExecutor("b").Bind();
        matcher.LastIndex = -0d;
        Assert.Equal(new[] { "a", "c" }, matcher.Split("abc"));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(matcher.LastIndex));
    }
}
