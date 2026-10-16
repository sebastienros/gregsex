using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Recursion;

public sealed class Pcre2RecursionTests
{
    private static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored };
    [Theory]
    [InlineData("ab", true)]
    [InlineData("aabb", true)]
    [InlineData("aaabbb", true)]
    [InlineData("aab", false)]
    [InlineData("", false)]
    [InlineData("abba", false)]
    public void WholePatternRecursion(string input, bool expected)
    {
        var p = new RecursionPlan();
        p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        var result = f.Match(input, pairs, new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Endanchored });
        Assert.Equal(expected ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, result.Status);
        if (expected)
        {
            Assert.Equal(new(true, 0, input.Length), pairs[0]);
        }

        Assert.Equal(0, f.ReturnDepth);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoutineIsNonAtomicAndDoesNotExportCaptures(bool atomic)
    {
        // ^(a|ab)(?1)c$: the call first consumes a, then must retry ab.
        var p = new RecursionPlan();
        var group = p.Capture(p.Alt(p.Lit('a'), p.Seq(p.Lit('a'), p.Lit('b'))), 1);
        var call = p.Call(1);
        if (atomic)
        {
            call = p.Atomic(call);
        }

        p.Seq(group, call, p.Lit('c'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        var result = f.Match("aabc", pairs, Anchored);
        Assert.Equal(atomic ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, result.Status);
        if (!atomic)
        { Assert.Equal(new(true, 0, 4), pairs[0]); Assert.Equal(new(true, 0, 1), pairs[1]); }
        Assert.Equal(0, f.ReturnDepth);
    }
    [Theory]
    [InlineData((int)Pcre2ReferenceKind.Absolute)]
    [InlineData((int)Pcre2ReferenceKind.Relative)]
    [InlineData((int)Pcre2ReferenceKind.Named)]
    public void DefineCallsResolvedFirstTarget(int kindValue)
    {
        var kind = (Pcre2ReferenceKind)kindValue;
        var p = new RecursionPlan();
        var def = p.Define(p.Capture(p.Seq(p.Lit('a'), p.Lit('b')), 1));
        p.Seq(def, p.Call(1, kind));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs, Anchored).Status);
        Assert.Equal(default, pairs[1]);
        Assert.Equal(0, f.ReturnDepth);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("ac", pairs, Anchored).Status);
    }
    [Theory]
    [InlineData("ab", true)]
    [InlineData("c", true)]
    [InlineData("ac", false)]
    [InlineData("b", false)]
    public void CaptureConditional(string input, bool expected)
    {
        var p = new RecursionPlan();
        var prefix = p.Repeat(p.Capture(p.Lit('a'), 1), 0, 1);
        p.Seq(prefix, p.Condition(Pcre2ConditionKind.CaptureSet, p.Lit('b'), p.Lit('c'), p.Reference(1)));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(expected ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, f.Match(input, pairs, Anchored).Status);
    }
    [Theory]
    [InlineData(10, 46, false, true)]
    [InlineData(10, 45, false, false)]
    [InlineData(10, 45, true, true)]
    [InlineData(10, 47, true, false)]
    [InlineData(9, 99, true, true)]
    [InlineData(11, 0, true, false)]
    public void VersionPinsNative1046(int major, int minor, bool atLeast, bool value)
    {
        var p = new RecursionPlan();
        p.Condition(Pcre2ConditionKind.Version, p.Lit('a'), p.Lit('b'), major: major, minor: minor, atLeast: atLeast);
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(value ? "a" : "b", pairs, Anchored).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match(value ? "b" : "a", pairs, Anchored).Status);
    }
    [Theory]
    [InlineData((int)Pcre2ConditionKind.AnyRecursion)]
    [InlineData((int)Pcre2ConditionKind.Recursion)]
    public void WholeRecursionCondition(int kindValue)
    {
        var kind = (Pcre2ConditionKind)kindValue;
        // (?(R)a|a(?R)?) independent retained native endpoints306/501/696/891.
        var p = new RecursionPlan();
        int yes = p.Lit('a'), no = p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1));
        p.Condition(kind, yes, no, reference: kind == Pcre2ConditionKind.Recursion ? p.Reference(0, Pcre2ReferenceKind.WholePattern) : -1);
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("aa", pairs, Anchored).Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
    }
    [Fact]
    public void RecursionConditionsUseMostRecentTarget()
    {
        var p = new RecursionPlan();
        var g2 = p.Capture(p.Condition(Pcre2ConditionKind.Recursion, p.Lit('x'), p.Lit('b'), p.Reference(1)), 2);
        var g1 = p.Capture(p.Seq(p.Lit('a'), p.Call(2)), 1);
        p.Seq(p.Define(p.Seq(g2, g1)), p.Call(1));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[3];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("ab", pairs, Anchored).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("ax", pairs, Anchored).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursionLoopVersusDisabledFiniteLimit(bool disabled)
    {
        var p = new RecursionPlan();
        p.Call();
        var f = new RecursionFixture(p.Build(), new(64, 128, 8, 16));
        Pcre2OffsetPair[] pairs = [new(true, 7, 8)];
        var r = f.Match("", pairs, new() { Flags = disabled ? Pcre2MatchFlags.DisableRecurseloopCheck : 0 }, new(maxWork: 10000, maxDepth: 32));
        Assert.Equal(disabled ? Pcre2MatchStatus.LimitExceeded : Pcre2MatchStatus.Error, r.Status);
        Assert.Equal(disabled ? (int)Pcre2ErrorCode.Depthlimit : (int)Pcre2ErrorCode.Recurseloop, r.NativeReturnCode);
        Assert.Equal(new(true, 7, 8), pairs[0]);
        var reuse = f.Match("", pairs);
        Assert.Equal((int)Pcre2ErrorCode.Recurseloop, reuse.NativeReturnCode);
    }
    [Fact]
    public void DefineSkipsItsBodyAndAbsentElseIsEmpty()
    {
        var p = new RecursionPlan();
        p.Seq(p.Define(p.Capture(p.Call(1), 1)), p.Condition(Pcre2ConditionKind.Version, p.Lit('x'), major: 99));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("", pairs, Anchored).Status);
        Assert.Equal(default, pairs[1]);
    }
    [Fact]
    public void BranchResetCallsTheResolvedDefinitionRatherThanAnotherSameNumber()
    {
        var p = new RecursionPlan();
        int first = p.Capture(p.Lit('a'), 1), second = p.Capture(p.Lit('b'), 1);
        p.Seq(p.Define(p.Alt(first, second)), p.CallTarget(1, first));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("a", pairs, Anchored).Status);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("b", pairs, Anchored).Status);
        Assert.Equal(default, pairs[1]);
    }

    [Fact]
    public void ShortVectorKeepsNativeZeroSuccessAndUnusedSlots()
    {
        var p = new RecursionPlan();
        p.Seq(p.Capture(p.Lit('a'), 1), p.Call(1));
        var f = new RecursionFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        var r = f.Match("aa", pairs, Anchored);
        Assert.Equal(Pcre2MatchStatus.Match, r.Status);
        Assert.Equal(0, r.NativeReturnCode);
        Assert.Equal(new(true, 0, 2), pairs[0]);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecursivePartialUsesOriginalCandidateCoordinates(bool hard)
    {
        var p = new RecursionPlan();
        p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
        var f = new RecursionFixture(p.Build());
        Pcre2OffsetPair[] pairs = [new(true, 9, 9)];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored | (hard ? Pcre2MatchFlags.PartialHard : Pcre2MatchFlags.PartialSoft) };
        var r = f.Match("xxaa", pairs, options, start: 2);
        Assert.Equal(Pcre2MatchStatus.Partial, r.Status);
        Assert.Equal((int)Pcre2ErrorCode.Partial, r.NativeReturnCode);
        Assert.Equal(new(true, 2, 4), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("aabb", pairs, Anchored).Status);
    }

}
