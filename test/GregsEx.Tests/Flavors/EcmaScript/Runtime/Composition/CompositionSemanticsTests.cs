using GregsEx.Contracts.EcmaScript;
using GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;
using Xunit;
using static GregsEx.Tests.Flavors.EcmaScript.Runtime.Composition.CompositionFixture;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Composition;

// Expected outputs frozen before execution from ECMA-262 edition16
// §22.2.2 (matching), §22.2.7/§22.2.6 (builtins), §22.1.3.19.1 (substitution).
public sealed class CompositionSemanticsTests
{
    [Theory]
    [InlineData(EcmaScriptFlags.None)]
    [InlineData(EcmaScriptFlags.Unicode)]
    [InlineData(EcmaScriptFlags.UnicodeSets)]
    public void OrderedAlternativesRollbackAndNumericBackreferences(EcmaScriptFlags mode)
    {
        var p = new Plan(mode);
        p.Seq(p.Alt(p.Cap(1, p.Text("ab")), p.Cap(2, p.Lit('a'))), p.Ref(2));
        using var m = Bind(p);
        Expect(m, "xaa", 1, 2, default, new RegexCapture(true, 1, 1));
        Expect(m, "xab", 1, 2, new RegexCapture(true, 1, 2), default);
    }
    [Fact]
    public void PositiveAssertionsAreAtomicAndNegativeAssertionsPublishNoCaptures()
    {
        var p = new Plan();
        p.Seq(p.Seq(p.Node(EcmaNodeKind.Start), p.Assert(EcmaNodeKind.Lookahead,
            p.Cap(1, p.Alt(p.Lit('a'), p.Text("ab"))))), p.Seq(p.Ref(1), p.Lit('c')));
        using var m = Bind(p);
        Assert.Equal(RegexSearchStatus.NoMatch, m.Test("abc"));
        Expect(m, "ac", 0, 2, new RegexCapture(true, 0, 1));
        var q = new Plan();
        q.Seq(q.Assert(EcmaNodeKind.NegativeLookahead, q.Cap(1, q.Lit('b'))), q.Seq(q.Ref(1), q.Lit('a')));
        using var n = Bind(q);
        Expect(n, "a", 0, 1, default(RegexCapture));
        Assert.Equal(RegexSearchStatus.NoMatch, n.Test("b"));
    }
    [Fact]
    public void AssertionCaptureFeedsBackreferenceAndMaterializedIndices()
    {
        var p = new Plan(EcmaScriptFlags.HasIndices);
        p.Seq(p.Assert(EcmaNodeKind.Lookahead, p.Cap(1, p.Rep(p.Lit('a'), 1))), p.Ref(1));
        using var m = Bind(p);
        var result = m.Match("xaaa")!.Single!;
        Assert.Equal(new(1, 3), result.Range);
        Assert.Equal(new RegexCapture[] { new(true, 1, 3), new(true, 1, 3) }, result.Indices.ToArray());
        Assert.Empty(result.NamedIndices.ToArray());
    }
    [Fact]
    public void ReverseLookbehindKeepsOriginalSubjectAndCapturePriority()
    {
        // (?<=([ab]+)([bc]+))$; reverse greediness captures a / bc.
        var p = new Plan(EcmaScriptFlags.Sticky | EcmaScriptFlags.HasIndices);
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Seq(
            p.Cap(1, p.Rep(p.Set([new('a', 'b')]), 1)),
            p.Cap(2, p.Rep(p.Set([new('b', 'c')]), 1)))), p.Node(EcmaNodeKind.End));
        using var m = Bind(p);
        m.LastIndex = 3;
        Expect(m, "abc", 3, 0, new RegexCapture(true, 0, 1), new RegexCapture(true, 1, 2));
        Assert.Equal(3d, m.LastIndex);
        var q = new Plan(EcmaScriptFlags.Sticky);
        q.Seq(q.Assert(EcmaNodeKind.Lookbehind, q.Seq(q.Ref(1), q.Cap(1, q.Alt(q.Lit('a'), q.Text("aa"))))), q.Lit('b'));
        using var n = Bind(q);
        n.LastIndex = 2;
        Expect(n, "aab", 2, 1, new RegexCapture(true, 1, 1));
        var r = new Plan(EcmaScriptFlags.Sticky);
        r.Seq(r.Assert(EcmaNodeKind.NegativeLookbehind, r.Cap(1, r.Lit('a'))), r.Lit('b'));
        using var negative = Bind(r);
        negative.LastIndex = 1;
        Expect(negative, "cb", 1, 1, default(RegexCapture));
        negative.LastIndex = 1;
        Assert.Equal(RegexSearchStatus.NoMatch, negative.Test("ab"));
    }
    [Theory]
    [InlineData(false, 3, 2)]
    [InlineData(true, 1, 0)]
    public void RepeatPrioritiesAndFinalCaptureSurviveHostBinding(bool lazy, int length, int captureIndex)
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Lit('a')), 1, 3, lazy);
        using var m = Bind(p);
        Expect(m, "aaaa", 0, length, new RegexCapture(true, captureIndex, 1));
    }
    [Fact]
    public void QuantifiedCaptureClearingAndNullableGuardSurviveMaterialization()
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Alt(p.Lit('a'), p.Cap(2, p.Lit('b')))), 1);
        using var m = Bind(p);
        Expect(m, "ba", 0, 2, new RegexCapture(true, 1, 1), default);
        var q = new Plan();
        q.Rep(q.Cap(1, q.Alt(q.Empty(), q.Lit('a'))));
        using var n = Bind(q);
        Expect(n, "aa", 0, 2, new RegexCapture(true, 1, 1));
        Expect(n, "", 0, 0, default(RegexCapture));
        var r = new Plan();
        r.Rep(r.Cap(1, r.Rep(r.Cap(2, r.Empty()), 1)), 1);
        using var empty = Bind(r);
        Expect(empty, "", 0, 0, new RegexCapture(true, 0, 0), new RegexCapture(true, 0, 0));
    }
    [Theory]
    [InlineData(EcmaScriptFlags.Unicode)]
    [InlineData(EcmaScriptFlags.UnicodeSets)]
    public void PinnedCaseTablesFoldSupplementaryBackreferencesAndScopedFlags(EcmaScriptFlags mode)
    {
        // Deseret U+10400 folds to U+10428; non-ASCII source data rather than fixture tables.
        var p = new Plan(mode | EcmaScriptFlags.IgnoreCase);
        p.Seq(p.Cap(1, p.Lit(0x10400)), p.Seq(p.Ref(1), p.Lit('B', EcmaScriptFlags.None)));
        using var m = Bind(p);
        Expect(m, "\U00010428\U00010400B", 0, 5, new RegexCapture(true, 0, 2));
        Assert.Equal(RegexSearchStatus.NoMatch, m.Test("\U00010428\U00010400b"));
        var q = new Plan(mode | EcmaScriptFlags.IgnoreCase);
        q.Seq(q.Node(EcmaNodeKind.WordBoundary), q.Lit('K'));
        using var kelvin = Bind(q);
        Expect(kelvin, "\u212A", 0, 1);
    }
    [Fact]
    public void LegacyCaseTablesDoNotFoldKelvinIntoAsciiWord()
    {
        var p = new Plan(EcmaScriptFlags.IgnoreCase);
        p.Lit('K');
        using var m = Bind(p);
        Expect(m, "k", 0, 1);
        Assert.Equal(RegexSearchStatus.NoMatch, m.Test("\u212A"));
    }
    [Fact]
    public void FiniteStringsRetryAndActualPinnedEmojiStringsMatchInBothDirections()
    {
        var p = new Plan(EcmaScriptFlags.UnicodeSets);
        p.Seq(p.Set([], ['a', 'b'], ['a']), p.Lit('b'));
        using var m = Bind(p);
        Expect(m, "ab", 0, 2);
        var q = new Plan(EcmaScriptFlags.UnicodeSets);
        q.Cap(1, Property(q, "Emoji_Keycap_Sequence"));
        using var keycap = Bind(q);
        Expect(keycap, "x1\uFE0F\u20E3", 1, 3, new RegexCapture(true, 1, 3));
        Assert.Equal(RegexSearchStatus.NoMatch, keycap.Test("1\u20E3"));
        var r = new Plan(EcmaScriptFlags.UnicodeSets | EcmaScriptFlags.Sticky);
        r.Seq(r.Assert(EcmaNodeKind.Lookbehind, r.Cap(1, Property(r, "Emoji_Keycap_Sequence"))), r.Lit('!'));
        using var reverse = Bind(r);
        reverse.LastIndex = 3;
        Expect(reverse, "1\uFE0F\u20E3!", 3, 1, new RegexCapture(true, 0, 3));
    }
    [Theory]
    [InlineData(EcmaScriptFlags.None, 1)]
    [InlineData(EcmaScriptFlags.Unicode, 2)]
    [InlineData(EcmaScriptFlags.UnicodeSets, 2)]
    public void RawEmptyExecAndTerminalProgressDifferFromIteration(EcmaScriptFlags mode, int next)
    {
        var p = new Plan(mode | EcmaScriptFlags.Global);
        p.Empty();
        using var m = Bind(p);
        var output = new RegexCapture[1];
        Expect(m, "\uD83D\uDE00", 0, 0);
        Assert.Equal(0d, m.LastIndex);
        Assert.Equal(RegexSearchStatus.Match, m.ExecNext("\uD83D\uDE00", output, out var first));
        Assert.Equal(new(0, 0), first);
        Assert.Equal((double)next, m.LastIndex);
        if (next == 1)
        { Expect(m, "\uD83D\uDE00", 1, 0); m.LastIndex = 2; }
        Assert.Equal(RegexSearchStatus.Match, m.ExecNext("\uD83D\uDE00", output, out var terminal));
        Assert.Equal(new(2, 0), terminal);
        Assert.Equal(3d, m.LastIndex);
        Assert.Equal(RegexSearchStatus.NoMatch, m.ExecNext("\uD83D\uDE00", output, out _));
        Assert.Equal(0d, m.LastIndex);
        Assert.Equal(default, output[0]);
    }
    [Theory]
    [InlineData(EcmaScriptFlags.Global)]
    [InlineData(EcmaScriptFlags.Sticky)]
    [InlineData(EcmaScriptFlags.Global | EcmaScriptFlags.Sticky)]
    public void NumericLastIndexConversionsAndStateReset(EcmaScriptFlags flags)
    {
        var p = new Plan(flags);
        p.Lit('a');
        using var m = Bind(p);
        foreach (var seed in new[] { double.NaN, -4d, -0d, .9d })
        { m.LastIndex = seed; Expect(m, "a", 0, 1); Assert.Equal(1d, m.LastIndex); }
        m.LastIndex = 1.9;
        Expect(m, "ba", 1, 1);
        Assert.Equal(2d, m.LastIndex);
        foreach (var seed in new[] { double.PositiveInfinity, 9_007_199_254_740_992d, 3d })
        { m.LastIndex = seed; Assert.Equal(RegexSearchStatus.NoMatch, m.Test("a")); Assert.Equal(0d, m.LastIndex); }
        m.LastIndex = 0;
        Assert.Equal(flags == EcmaScriptFlags.Global ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, m.Test("ba"));
    }
    [Fact]
    public void QualifiedC018WitnessKeepsBuiltinMappingSeparateFromGenericLowerBound()
    {
        // Project contract/oracle witness; not a claim of a TC39 erratum or literal
        // edition16 result-index derivation. See interior-surrogate-provenance.md.
        var p = new Plan(EcmaScriptFlags.Unicode | EcmaScriptFlags.Sticky);
        p.Set([new(0, 9), new(11, 12), new(14, 0x2027), new(0x202A, 0x10FFFF)]);
        var vm = Build(p);
        using var m = new EcmaScriptMatcher(vm, frameCapacity: 128);
        m.LastIndex = 1;
        Expect(m, "\uD83D\uDE00", 0, 2);
        Assert.Equal(2d, m.LastIndex);
        var prep = new Prepared(vm);
        Assert.Equal(RegexSearchStatus.InvalidInput, prep.Search("\uD83D\uDE00", 1, true));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, prep.Failure.Code);
        Assert.Equal(RegexSearchStatus.Match, prep.Search("\uD83D\uDE00", 1, true, EcmaStartPolicy.BuiltinLastIndex));
        Assert.Equal(new(0, 2), prep.Match);
    }
    [Theory]
    [InlineData(EcmaScriptFlags.None)]
    [InlineData(EcmaScriptFlags.Unicode)]
    [InlineData(EcmaScriptFlags.UnicodeSets)]
    public void LoneSurrogatesRetainNativeOffsetsAcrossExecAndReplace(EcmaScriptFlags mode)
    {
        var p = new Plan(mode);
        p.Cap(1, p.Lit(0xD800));
        using var m = Bind(p);
        Expect(m, "x\uD800", 1, 1, new RegexCapture(true, 1, 1));
        Assert.Equal("x<\uD800>", m.Replace("x\uD800", "<$1>"));
    }
    [Theory]
    [InlineData('a', 1, 2)]
    [InlineData('b', 2, 1)]
    public void DuplicateNamedCapturesIndicesAndSubstitutionsUseParticipatingAlias(char letter, int group, int absent)
    {
        var p = new Plan(EcmaScriptFlags.HasIndices) { Names = [new("x", 0, 2)], Aliases = [1, 2] };
        p.Seq(p.Alt(p.Cap(1, p.Lit('a'), 0), p.Cap(2, p.Lit('b'), 0)), p.Ref(0, 0));
        using var m = Bind(p);
        string input = new(letter, 2);
        var result = m.Match(input)!.Single!;
        Assert.Equal(new(0, 2), result.Range);
        Assert.True(result.HasIndices);
        Assert.Equal(new(true, 0, 1), result.Captures[group]);
        Assert.Equal(default, result.Captures[absent]);
        Assert.Equal(new EcmaScriptNamedCapture("x", new(true, 0, 1)), result.Names[0]);
        Assert.Equal(result.Names.ToArray(), result.NamedIndices.ToArray());
        Assert.Equal($"{letter}/", m.Replace(input, "$<x>/$<missing>"));
        Assert.Equal("aa", m.Replace("aa", "$1$2$<x>"));
    }
    [Fact]
    public void TemplateDollarNumberFallbackPrefixSuffixAndUndefinedCaptures()
    {
        var p = new Plan();
        p.Seq(p.Cap(1, p.Lit('a')), p.Rep(p.Cap(2, p.Lit('b')), 0, 1));
        using var m = Bind(p);
        Assert.Equal("x$|a|x|y|a||a0|a|$99|$0|$<x>y",
            m.Replace("xay", "$$|$&|$`|$'|$1|$2|$10|$01|$99|$0|$<x>"));
    }
    [Fact]
    public void AllMaterializedOperationsAdvanceEmptyMatchesAndPreserveIndependentSeeds()
    {
        var p = new Plan(EcmaScriptFlags.Global | EcmaScriptFlags.Unicode | EcmaScriptFlags.HasIndices);
        p.Empty();
        using var m = Bind(p);
        const string input = "\uD83D\uDE00";
        m.LastIndex = 99;
        Assert.Equal(new[] { "", "" }, m.Match(input)!.GlobalMatches);
        Assert.Equal(0d, m.LastIndex);
        m.LastIndex = 2;
        var all = m.MatchAll(input);
        m.LastIndex = -7;
        using (var it = all.GetEnumerator())
        {
            Assert.True(it.MoveNext());
            Assert.Equal(new(2, 0), it.Current.Range);
            Assert.True(it.Current.HasIndices);
            Assert.False(it.MoveNext());
            Assert.Equal(RegexSearchStatus.NoMatch, it.TerminalStatus);
        }
        Assert.Equal(-7d, m.LastIndex);
        Assert.Equal("-\uD83D\uDE00-", m.ReplaceAll(input, "-"));
        Assert.Equal(0d, m.LastIndex);
        Assert.Equal(new[] { input }, m.Split(input));
        m.LastIndex = -0d;
        Assert.Equal(0, m.Search(input));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(m.LastIndex));
    }
    [Fact]
    public void SplitInsertsUndefinedAndEmptyCapturesAndPreservesSource()
    {
        var p = new Plan();
        p.Seq(p.Cap(1, p.Lit(',')), p.Seq(
            p.Rep(p.Cap(2, p.Lit('x')), 0, 1), p.Cap(3, p.Empty())));
        using var m = Bind(p);
        m.LastIndex = double.NaN;
        Assert.Equal(new string?[] { "a", ",", null, "", "b" }, m.Split("a,b"));
        Assert.Equal(new string?[] { "a", ",", null }, m.Split("a,b", 3));
        Assert.True(double.IsNaN(m.LastIndex));
        Assert.Equal(new[] { "" }, m.Split(""));
        Assert.Empty(m.Split("a,b", 0));
    }
}
