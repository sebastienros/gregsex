using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;

public class VmSemanticsTests
{
    [Fact]
    public void AlternativesSelectSourceOrderAndRestoreFailedCaptures()
    {
        var p = new Plan();
        p.Alt(p.Cap(1, p.Text("ab")), p.Cap(2, p.Lit('a')));
        new Prepared(p.Build()).Expect("xa", 1, 1, default, new RegexCapture(true, 1, 1));
        var q = new Plan();
        q.Alt(q.Text("a"), q.Text("aa"));
        new Prepared(q.Build()).Expect("aa", 0, 1);
    }

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void GreedyLazyAndBoundedRepeatKeepPriorities(bool lazy, int length)
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Lit('a')), 1, 3, lazy);
        new Prepared(p.Build()).Expect("aaaa", 0, length, new RegexCapture(true, length - 1, 1));
    }

    [Fact]
    public void RepeatBacktracksToContinuationWithCaptures()
    {
        var p = new Plan();
        p.Seq(p.Rep(p.Cap(1, p.Lit('a'))), p.Lit('a'));
        new Prepared(p.Build()).Expect("aaa", 0, 3, new RegexCapture(true, 1, 1));
    }

    [Fact]
    public void EveryIterationClearsAbsentChildCaptures()
    {
        // (a|(b))+ : final a clears the b capture from the preceding iteration.
        var p = new Plan();
        p.Rep(p.Cap(1, p.Alt(p.Lit('a'), p.Cap(2, p.Lit('b')))), 1);
        new Prepared(p.Build()).Expect("ba", 0, 2, new RegexCapture(true, 1, 1), default);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void NullableRepeatKeepsOnlyRequiredEmptyIterations(int minimum, bool participates)
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Empty()), minimum);
        new Prepared(p.Build()).Expect("", 0, 0, participates ? new RegexCapture(true, 0, 0) : default);
    }

    [Fact]
    public void OptionalEmptyBranchRetriesItsConsumingAlternative()
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Alt(p.Empty(), p.Lit('a'))));
        new Prepared(p.Build()).Expect("aa", 0, 2, new RegexCapture(true, 1, 1));
    }

    [Fact]
    public void NestedNullableRepeatsTerminateAndPreserveRequiredCapture()
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Rep(p.Cap(2, p.Empty()), 1)), 1);
        new Prepared(p.Build()).Expect("", 0, 0, new RegexCapture(true, 0, 0), new RegexCapture(true, 0, 0));
    }

    [Fact]
    public void ForwardAndUnmatchedBackreferencesMatchEmpty()
    {
        var p = new Plan();
        p.Seq(p.Ref(1), p.Cap(1, p.Lit('a')));
        new Prepared(p.Build()).Expect("a", 0, 1, new RegexCapture(true, 0, 1));
        var q = new Plan();
        q.Seq(q.Alt(q.Lit('a'), q.Cap(1, q.Lit('b'))), q.Ref(1));
        new Prepared(q.Build()).Expect("a", 0, 1, default(RegexCapture));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BackreferencesNeedRegistersForEveryOperation(int value)
    {
        var operation = (SearchOperation)value;
        var p = new Plan();
        p.Seq(p.Cap(1, p.Text("ab")), p.Ref(1));
        var prepared = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.Match, prepared.Search("zabab", operation: operation));
        Assert.Equal(new(1, 4), prepared.Match);
        Assert.Equal(RegexSearchStatus.NoMatch, prepared.Search("abac", operation: operation));
    }

    [Fact]
    public void NamedDuplicateAliasSelectsParticipatingGroupAndUnmatchedIsEmpty()
    {
        var p = new Plan { Names = [new("x", 0, 2)], Aliases = [1, 2] };
        p.Seq(p.Alt(p.Cap(1, p.Lit('a'), 0), p.Cap(2, p.Lit('b'), 0)), p.Ref(0, 0));
        var prepared = new Prepared(p.Build());
        prepared.Expect("bb", 0, 2, default, new RegexCapture(true, 0, 1));
        prepared.Expect("aa", 0, 2, new RegexCapture(true, 0, 1), default);
    }

    [Fact]
    public void PositiveLookaheadRetainsCapturesAndIsAtomic()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.Lookahead, p.Cap(1, p.Rep(p.Lit('a'), 1))), p.Ref(1));
        new Prepared(p.Build()).Expect("aaa", 0, 3, new RegexCapture(true, 0, 3));
        // ^(?=(a|ab))\1c : assertion may not revisit its ab alternative.
        var q = new Plan();
        var start = q.Node(EcmaNodeKind.Start);
        var assertion = q.Assert(EcmaNodeKind.Lookahead, q.Cap(1, q.Alt(q.Lit('a'), q.Text("ab"))));
        q.Seq(q.Seq(start, assertion), q.Seq(q.Ref(1), q.Lit('c')));
        Assert.Equal(RegexSearchStatus.NoMatch, new Prepared(q.Build()).Search("abc"));
    }

    [Fact]
    public void NegativeAssertionsPublishNoCapturesAndKeepOriginalPosition()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.NegativeLookahead, p.Cap(1, p.Lit('b'))), p.Seq(p.Ref(1), p.Lit('a')));
        new Prepared(p.Build()).Expect("a", 0, 1, default(RegexCapture));
        var q = new Plan();
        q.Seq(q.Assert(EcmaNodeKind.NegativeLookahead, q.Cap(1, q.Lit('a'))), q.Lit('a'));
        var prep = new Prepared(q.Build());
        Assert.Equal(RegexSearchStatus.NoMatch, prep.Search("a"));
        Assert.All(prep.Output, g => Assert.False(g.Participated));
    }

    [Fact]
    public void FailedOuterBranchRollsBackAssertionCaptures()
    {
        var p = new Plan();
        p.Alt(p.Seq(p.Assert(EcmaNodeKind.Lookahead, p.Cap(1, p.Lit('a'))), p.Lit('b')), p.Cap(2, p.Lit('a')));
        new Prepared(p.Build()).Expect("a", 0, 1, default, new RegexCapture(true, 0, 1));
    }

    [Fact]
    public void UnboundedLookbehindEvaluatesSequenceBackwardAndCapturesLeftmostRepeat()
    {
        // (?<=([ab]+)([bc]+))$ over abc => group1 a, group2 bc.
        var p = new Plan();
        var x = p.Cap(1, p.Rep(p.Set([new('a', 'b')]), 1));
        var y = p.Cap(2, p.Rep(p.Set([new('b', 'c')]), 1));
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Seq(x, y)), p.Node(EcmaNodeKind.End));
        new Prepared(p.Build()).Expect("abc", 3, 0, new RegexCapture(true, 0, 1), new RegexCapture(true, 1, 2));
    }

    [Fact]
    public void ReverseAlternativesKeepSourceOrderAndBackreferenceUsesReverseDirection()
    {
        var p = new Plan();
        var reference = p.Ref(1);
        var capture = p.Cap(1, p.Alt(p.Lit('a'), p.Text("aa")));
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Seq(reference, capture)), p.Lit('b'));
        new Prepared(p.Build()).Expect("aab", 2, 1, new RegexCapture(true, 1, 1));
    }

    [Fact]
    public void LookbehindSeesInputBeforeSearchLowerBoundAndNegativeRollsBack()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Cap(1, p.Text("ab"))), p.Lit('c'));
        var prep = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.Match, prep.Search("abc", 2, true));
        Assert.Equal(new RegexCapture(true, 0, 2), prep.Output[1]);
        var q = new Plan();
        q.Seq(q.Assert(EcmaNodeKind.NegativeLookbehind, q.Cap(1, q.Lit('a'))), q.Lit('b'));
        new Prepared(q.Build()).Expect("cb", 1, 1, default(RegexCapture));
        Assert.Equal(RegexSearchStatus.NoMatch, new Prepared(q.Build()).Search("ab"));
    }

    [Fact]
    public void NestedAssertionsRestoreDirectionAndChoiceBoundary()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Seq(p.Lit('a'), p.Assert(EcmaNodeKind.Lookahead, p.Lit('b')))), p.Lit('b'));
        new Prepared(p.Build()).Expect("ab", 1, 1);
    }

    [Theory]
    [InlineData(EcmaScriptFlags.None, 2)]
    [InlineData(EcmaScriptFlags.Unicode, 4)]
    [InlineData(EcmaScriptFlags.UnicodeSets, 4)]
    public void BackreferencesConsumeProfileElementsWithNativeOffsets(EcmaScriptFlags flags, int length)
    {
        var p = new Plan(flags);
        p.Seq(p.Cap(1, p.Lit(flags == 0 ? 0xD83D : 0x1F600)), p.Ref(1));
        var input = flags == 0 ? "\uD83D\uD83D" : "\uD83D\uDE00\uD83D\uDE00";
        new Prepared(p.Build()).Expect(input, 0, length, new RegexCapture(true, 0, length / 2));
    }

    [Theory]
    [InlineData(EcmaScriptFlags.None)]
    [InlineData(EcmaScriptFlags.Unicode)]
    [InlineData(EcmaScriptFlags.UnicodeSets)]
    public void LoneSurrogatesRemainLegal(EcmaScriptFlags flags)
    {
        var p = new Plan(flags);
        p.Lit(0xD800);
        new Prepared(p.Build()).Expect("x\uD800", 1, 1);
    }

    [Fact]
    public void BuiltinInteriorSurrogateConversionIsSeparateFromLowerBound()
    {
        var p = new Plan(EcmaScriptFlags.Unicode);
        p.Lit(0x1F600);
        var prep = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.InvalidInput, prep.Search("\uD83D\uDE00", 1, true));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, prep.Failure.Code);
        Assert.Equal(RegexSearchStatus.Match, prep.Search("\uD83D\uDE00", 1, true, EcmaStartPolicy.BuiltinLastIndex));
        Assert.Equal(new(0, 2), prep.Match);
    }

    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    [InlineData('\u2028')]
    [InlineData('\u2029')]
    public void MultilineAnchorsUseAllFourTerminators(char terminator)
    {
        var p = new Plan(EcmaScriptFlags.Multiline);
        p.Seq(p.Node(EcmaNodeKind.Start), p.Seq(p.Lit('a'), p.Node(EcmaNodeKind.End)));
        new Prepared(p.Build()).Expect($"x{terminator}a{terminator}y", 2, 1);
        var q = new Plan();
        q.Seq(q.Lit('a'), q.Node(EcmaNodeKind.End));
        Assert.Equal(RegexSearchStatus.NoMatch, new Prepared(q.Build()).Search($"a{terminator}"));
    }

    [Fact]
    public void ScopedCaseFlagsAndCanonicalSetMembershipUseDirectTables()
    {
        var p = new Plan(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase);
        p.Seq(p.Lit('K'), p.Seq(p.Set([new('a', 'a')]), p.Lit('B', EcmaScriptFlags.None)));
        new Prepared(p.Build()).Expect("\u212AaB", 0, 3);
        Assert.Equal(RegexSearchStatus.NoMatch, new Prepared(p.Build()).Search("\u212Aab"));
        var q = new Plan(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase);
        q.Set([new('k', 'k')]);
        new Prepared(q.Build()).Expect("\u212A", 0, 1);
    }

    [Fact]
    public void WordBoundaryUnicodeFoldingAndLegacyNonAsciiRestriction()
    {
        foreach (var flags in new[] { EcmaScriptFlags.IgnoreCase, EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase })
        {
            var p = new Plan(flags);
            p.Seq(p.Node(EcmaNodeKind.WordBoundary), p.Lit(0x212A));
            Assert.Equal(flags == EcmaScriptFlags.IgnoreCase ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match,
                new Prepared(p.Build()).Search("\u212A"));
        }
        var q = new Plan();
        q.Seq(q.Node(EcmaNodeKind.NotWordBoundary), q.Lit('-'));
        new Prepared(q.Build()).Expect("-", 0, 1);
    }

    [Fact]
    public void FiniteStringsPreferLongerButRetryShorterAndEmpty()
    {
        var p = new Plan(EcmaScriptFlags.UnicodeSets);
        p.Seq(p.Set([], ['a', 'b'], ['a']), p.Lit('b'));
        new Prepared(p.Build()).Expect("ab", 0, 2);
        var q = new Plan(EcmaScriptFlags.UnicodeSets);
        q.Set([new('a', 'a')], ['a', 'b'], []);
        new Prepared(q.Build()).Expect("ab", 0, 2);
        new Prepared(q.Build()).Expect("a", 0, 1);
        new Prepared(q.Build()).Expect("", 0, 0);
    }

    [Fact]
    public void ReverseFiniteStringSetsKeepLongestFirstPriority()
    {
        var p = new Plan(EcmaScriptFlags.UnicodeSets);
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Cap(1, p.Set([], ['a', 'b'], ['b']))), p.Lit('c'));
        new Prepared(p.Build()).Expect("abc", 2, 1, new RegexCapture(true, 0, 2));
    }
    [Fact]
    public void NestedOptionalCaptureClearingMatchesTheRepeatMatcherSourceExample()
    {
        // ECMA-262 2025 §22.2.2.3.1 Note 3, independently constructed plan.
        var p = new Plan();
        var z = p.Cap(1, p.Lit('z'));
        var a = p.Rep(p.Cap(3, p.Rep(p.Lit('a'), 1)), 0, 1);
        var b = p.Rep(p.Cap(4, p.Rep(p.Lit('b'), 1)), 0, 1);
        var c = p.Cap(5, p.Lit('c'));
        p.Seq(z, p.Rep(p.Cap(2, p.Seq(p.Seq(a, b), c))));
        new Prepared(p.Build()).Expect("zaacbbbcac", 0, 10, new RegexCapture(true, 0, 1),
            new RegexCapture(true, 8, 2), new RegexCapture(true, 8, 1), default, new RegexCapture(true, 9, 1));
    }

    [Fact]
    public void ReverseRepeatedCapturesKeepTheFinalLeftmostIteration()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Rep(p.Cap(1, p.Lit('a')), 1)), p.Lit('b'));
        new Prepared(p.Build()).Expect("aaab", 3, 1, new RegexCapture(true, 0, 1));
    }

}
