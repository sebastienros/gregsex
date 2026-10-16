using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.VM;

public sealed class Pcre2VmCompositionTests
{
    internal static Pcre2MatchOptions Anchored => new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Endanchored };
    internal static Pcre2Profile Profile(int domain, Pcre2CompileFlags extra = default) =>
        new(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen,
            extra | (domain % 2 == 1 ? Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp : 0));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SubroutineRunsRealAssertionGraphemeScriptAndBackreference(int domain)
    {
        // (?(DEFINE)(?<r>(?=(\X))(*sr:\X)\2))((?1))
        var p = new VmPlan(Profile(domain));
        var body = p.Seq(p.Assertion(p.Capture(p.Leaf(Pcre2NodeKind.Grapheme), 2)),
            p.Script(p.Leaf(Pcre2NodeKind.Grapheme)), p.Backref(2));
        var define = p.Define(p.Capture(body, 1));
        p.Seq(define, p.Capture(p.Call(1), 3));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[5];
        pairs[4] = new(true, 8, 9);
        var result = domain < 2 ? f.Match("aa"u8.ToArray(), pairs, Anchored) : f.Match("aa", pairs, Anchored);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        Assert.Equal(new(true, 0, 2), pairs[3]);
        Assert.Equal(default, pairs[1]);
        Assert.Equal(default, pairs[2]);
        Assert.Equal(new(true, 8, 9), pairs[4]);
        Assert.True(f.Released);
        var miss = domain < 2 ? f.Match("ab"u8.ToArray(), pairs, Anchored) : f.Match("ab", pairs, Anchored);
        Assert.Equal(Pcre2MatchStatus.NoMatch, miss.Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("aabb", true)]
    [InlineData("aaabbb", true)]
    [InlineData("aab", false)]
    [InlineData("abba", false)]
    [InlineData("", false)]
    public void RecursiveScriptContainsNestedAheadAndUnicode(string input, bool expected)
    {
        // (*sr:(?=a\X)a(?R)?b), anchored/endanchored.
        var p = new VmPlan(Profile(3));
        p.Script(p.Seq(p.Assertion(p.Seq(p.Lit('a'), p.Leaf(Pcre2NodeKind.Grapheme))),
            p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b')));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(expected ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, f.Match(input, pairs, Anchored).Status);
        if (expected)
        {
            Assert.Equal(new(true, 0, input.Length), pairs[0]);
        }

        Assert.True(f.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConditionalScriptFrameRoutesToRecursionAndRealPredicateTruth(bool negative)
    {
        // (*sr:(?(?=(\X))\1|z)) or (*sr:(?(?!(\X))z|\1)).
        var p = new VmPlan(Profile(3));
        var assertion = p.Assertion(p.Capture(p.Leaf(Pcre2NodeKind.Grapheme), 1),
            negative ? Pcre2AssertionKind.NegativeAhead : Pcre2AssertionKind.PositiveAhead);
        int yes = negative ? p.Lit('z') : p.Backref(1), no = negative ? p.Backref(1) : p.Lit('z');
        p.Script(p.Predicate(assertion, yes, no));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("a\u0301", pairs, Anchored).Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        Assert.Equal(new(true, 0, 2), pairs[1]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("", pairs, Anchored).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAssertionConditionalBranchIsNotUnicodeValidation(bool truth)
    {
        var p = new VmPlan(Profile(3));
        // A real script rejects mixed scripts. Conditional's ScriptRun frame is
        // merely its non-call continuation and must not validate this range.
        p.Version(p.Seq(p.Lit('a'), p.Lit(0x3b1)), p.Lit('b'), truth);
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(truth ? "aα" : "b", pairs, Anchored).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAtomicLookaheadAndScriptRetryAcrossCallerFailure(bool atomic)
    {
        // (?*(a|aa))(*sr:\1)b or atomic lookahead equivalent.
        var p = new VmPlan(Profile(3));
        var ahead = p.Assertion(p.Capture(p.Alt(p.Lit('a'), p.Seq(p.Lit('a'), p.Lit('a'))), 1),
            atomic ? Pcre2AssertionKind.PositiveAhead : Pcre2AssertionKind.NonAtomicAhead);
        p.Seq(ahead, p.Script(p.Backref(1)), p.Lit('b'));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(atomic ? Pcre2MatchStatus.NoMatch : Pcre2MatchStatus.Match, f.Match("aab", pairs, Anchored).Status);
        if (!atomic)
        {
            Assert.Equal(new(true, 0, 2), pairs[1]);
        }
    }

    [Fact]
    public void VariableBehindInsideScriptUsesOriginalPrefixAndRetriesLengths()
    {
        var p = new VmPlan(Profile(3));
        var behind = p.Assertion(p.Repeat(p.Lit('a'), 1, 2), Pcre2AssertionKind.PositiveBehind, 1, 2, true);
        p.Script(p.Seq(behind, p.Leaf(Pcre2NodeKind.Grapheme)));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("aab", pairs, Anchored, start: 2).Status);
        Assert.Equal(new(true, 2, 3), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("xxb", pairs, Anchored, start: 2).Status);
    }

    [Fact]
    public void ScanInsidePositiveAssertionKeepsNoteolAcrossRemainder()
    {
        // (a)(?=(*scs:(1)(*sr:a$))$) with NOTEOL. Scan clears it
        // through the restored-window remainder and the positive assertion.
        var p = new VmPlan();
        var capture = p.Capture(p.Lit('a'), 1);
        var scan = p.Scan(p.Script(p.Seq(p.Lit('a'), p.Leaf(Pcre2NodeKind.End))), 1);
        p.Seq(capture, p.Assertion(p.Seq(scan, p.Leaf(Pcre2NodeKind.End))), p.Leaf(Pcre2NodeKind.End));
        var f = new VmFixture(p.Build());
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("a", pairs, new() { Flags = Pcre2MatchFlags.Anchored | Pcre2MatchFlags.Noteol }).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
        Assert.True(f.Released);
    }

    [Fact]
    public void ControlOpcodeYieldsWithoutPretendingToExecute()
    {
        var p = new VmPlan();
        p.Seq(p.Lit('a'), p.Leaf(Pcre2NodeKind.ResetStart), p.Lit('b'));
        var f = new VmFixture(p.Build());
        var machine = new Pcre2VmState(f.Capacity, new(), 2);
        var state = Pcre2KernelOperation.CreateRunState(new(f.States, f.Captures, f.Scratch), machine.Limits);
        Assert.True(Pcre2KernelOperation.PollEntry(f.Program, ref machine, ref state));
        Assert.True(Pcre2Kernel.BeginCandidate(f.Program, 0, ref machine, ref state));
        var step = Pcre2Vm.RunCandidate<char, Pcre2CharTextReader>(f.Program, "ab",
            Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables), default, ref machine, ref state);
        Assert.Equal(Pcre2StepKind.Control, step.Kind);
        Assert.Equal(1, machine.Position);
        Assert.Equal(0, machine.ReportStart);
        var node = machine.Node;
        Assert.Equal(Pcre2StepKind.Control, Pcre2Vm.Step<char, Pcre2CharTextReader>(f.Program, "ab",
            Pcre2Data.Instance.Bind(f.Program.Profile.CharacterTables), default, ref machine, ref state).Kind);
        Assert.Equal(node, machine.Node);
        Assert.Throws<NotSupportedException>(() => f.Match("ab", new Pcre2OffsetPair[1]));
    }
}
