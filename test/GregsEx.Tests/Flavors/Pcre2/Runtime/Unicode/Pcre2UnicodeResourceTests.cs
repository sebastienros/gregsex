using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Execution.Pcre2.Unicode;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Unicode;

public sealed class Pcre2UnicodeResourceTests
{
    private static UnicodeFixture Retry(Pcre2ExecutionCapacity? capacity = null, Pcre2PatternLimits limits = default)
    {
        var p = Pcre2UnicodeTests.Plan(3);
        var body = p.Add(Pcre2NodeKind.ScriptRun, [p.Capture(p.Repeat(p.Leaf(Pcre2NodeKind.Grapheme), 1, 3))]);
        p.Seq(body, p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        return new(p.Build(limits: limits), capacity);
    }
    [Fact]
    public void CumulativeWorkDepthHeapCaptureAndFrameDenialsRemainReusable()
    {
        var f = Retry();
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 7, 8), 2).ToArray();
        var work = f.Match("aaa", pairs, options: Pcre2UnicodeTests.Anchored, limits: new(maxWork: 35));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, work.Status);
        Assert.All(pairs, p => Assert.Equal(new(true, 7, 8), p));
        Assert.Equal((int)Pcre2ErrorCode.Depthlimit, f.Match("aaa", pairs, options: Pcre2UnicodeTests.Anchored, limits: new(maxDepth: 1)).NativeReturnCode);
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, f.Match("aaa", pairs, limits: new(maxWorkspaceBytes: 1)).NativeReturnCode);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("aaa", pairs, limits: new(maxCaptureEvents: 1)).Status);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, f.Match("", pairs, limits: new(maxWork: 0)).NativeReturnCode);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, Retry(new(2, 128, 1, 64)).Match("aaa", pairs).Status);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("aaa", pairs, options: Pcre2UnicodeTests.Anchored).Status);
        Assert.Equal(new(true, 0, 2), pairs[1]);
        var reduced = Retry(limits: new(MatchWork: 35, Depth: null, HeapBytes: null));
        var r = reduced.Match("aaa", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, r.Status);
        Assert.True(r.Failure.WorkConsumed <= 35);
    }
    [Fact]
    public void LongClusterAndScriptValidationConsumeSameOperationBudget()
    {
        var p = Pcre2UnicodeTests.Plan(3);
        p.Leaf(Pcre2NodeKind.Grapheme);
        var f = new UnicodeFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("a" + new string('\u0301', 200), pairs, limits: new(maxWork: 100)).Status);
        // Literal child costs are finite, and script's separate validation pass
        // is charged rather than silently starting its own budget.
        var q = Pcre2UnicodeTests.Plan(3);
        q.Add(Pcre2NodeKind.ScriptRun, [q.Repeat(q.Literal('a'), 80, 80)]);
        var g = new UnicodeFixture(q.Build());
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, g.Match(new string('a', 80), pairs, limits: new(maxWork: 700)).Status);
        Assert.Equal(Pcre2MatchStatus.Match, g.Match(new string('a', 80), pairs).Status);
    }
    [Fact]
    public void CancellationDeadlineAndMalformedUnicodeHaveExplicitOutcomes()
    {
        var p = Pcre2UnicodeTests.Plan(3);
        p.Add(Pcre2NodeKind.ScriptRun, [p.Leaf(Pcre2NodeKind.Empty)]);
        var f = new UnicodeFixture(p.Build());
        var pairs = new Pcre2OffsetPair[1];
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(Pcre2MatchStatus.Cancelled, f.Match("", pairs, options: new() { CancellationToken = cts.Token }).Status);
        Assert.Equal(Pcre2MatchStatus.TimedOut, f.Match("", pairs, options: new() { Timeout = TimeSpan.FromTicks(1) }).Status);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match("", pairs).Status);
        var q = Pcre2UnicodeTests.Plan(3);
        q.Leaf(Pcre2NodeKind.Grapheme);
        Assert.Equal(Pcre2MatchStatus.Error, new UnicodeFixture(q.Build()).Match("\ud800", pairs).Status);
        var b = Pcre2UnicodeTests.Plan(2);
        b.Leaf(Pcre2NodeKind.Grapheme);
        Assert.Equal(Pcre2MatchStatus.Error, new UnicodeFixture(b.Build()).Match([0xf0, 0x9f], pairs).Status);
    }
    [Fact]
    public void BorrowedInputChangesAndPrivateWorkspacesShareOnlyImmutablePlan()
    {
        var p = Pcre2UnicodeTests.Plan(2);
        p.Add(Pcre2NodeKind.ScriptRun, [p.Repeat(p.Leaf(Pcre2NodeKind.Dot), 2, 2)]);
        var f = new UnicodeFixture(p.Build());
        var bytes = "éé"u8.ToArray();
        var pairs = new Pcre2OffsetPair[1];
        var states = f.States;
        var scratch = f.Scratch;
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(bytes, pairs).Status);
        bytes[2] = 0xce;
        bytes[3] = 0xb1; // Replace Latin é with Greek alpha.
        Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match(bytes, pairs, options: Pcre2UnicodeTests.Anchored).Status);
        bytes[2] = 0xc3;
        bytes[3] = 0xa9;
        Parallel.For(0, 8, _ => Assert.Equal(Pcre2MatchStatus.Match, new UnicodeFixture(f.Program).Match(bytes, new Pcre2OffsetPair[1]).Status));
        Assert.Same(states, f.States);
        Assert.Same(scratch, f.Scratch);
        Assert.Equal(Pcre2MatchStatus.Match, f.Match(bytes, pairs).Status);
    }
    [Fact]
    public void SuccessfulInlineScriptKetDoesNotRaiseRecursionWatermark()
    {
        var p = Pcre2UnicodeTests.Plan(3);
        p.Add(Pcre2NodeKind.ScriptRun, [p.Literal('a')]);
        var program = p.Build();
        var fixture = new UnicodeFixture(program);
        var machine = new Pcre2VmState(fixture.Capacity, new(), 1);
        var state = GregsEx.Flavors.Pcre2.Runtime.Kernel.Pcre2KernelOperation.CreateRunState(new(fixture.States, fixture.Captures, fixture.Scratch), machine.Limits, default);
        Assert.True(Pcre2Kernel.BeginCandidate(program, 0, ref machine, ref state));
        var data = Pcre2Data.Instance.Bind(program.Profile.CharacterTables);
        Assert.Equal(Pcre2StepKind.Unicode, Pcre2Kernel.Step<char, Pcre2CharTextReader>(program, "a", data, default, ref machine, ref state).Kind);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2UnicodeOps.Step<char, Pcre2CharTextReader>(program, "a", data, ref machine, ref state).Kind);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2Kernel.Step<char, Pcre2CharTextReader>(program, "a", data, default, ref machine, ref state).Kind);
        Assert.Equal(Pcre2StepKind.Return, Pcre2Kernel.Step<char, Pcre2CharTextReader>(program, "a", data, default, ref machine, ref state).Kind);
        Assert.Equal(Pcre2StepKind.Continue, Pcre2UnicodeOps.Resume<char, Pcre2CharTextReader>(program, "a", data, Pcre2StepKind.Return, ref machine, ref state).Kind);
        Assert.Equal(0, machine.LastUsedPosition);
        Assert.Equal(1, machine.Position);
    }
}
