using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;

public class VmLimitsTests
{
    [Fact]
    public void OneCumulativeBudgetCoversCandidatesAndEmptyAcceptance()
    {
        var p = new Plan();
        p.Lit('a');
        var prep = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.NoMatch, prep.Search("xxx"));
        var total = prep.Work;
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Search("xxx", limits: new(maxWork: total - 1)));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, prep.Failure.Code);
        Assert.Equal(RegexSearchStatus.NoMatch, prep.Search("xxx", limits: new(maxWork: total)));
        Assert.Equal(total, prep.Work);
        Assert.Equal(RegexSearchStatus.Match, prep.Search("a"));
        var q = new Plan();
        q.Empty();
        var empty = new Prepared(q.Build());
        Assert.Equal(RegexSearchStatus.LimitExceeded, empty.Search("", limits: new(maxWork: 1)));
        Assert.Equal(RegexSearchStatus.Match, empty.Search(""));
    }

    [Fact]
    public void NestedAssertionAndBackreferenceWorkIsNotReset()
    {
        var p = new Plan();
        p.Seq(p.Assert(EcmaNodeKind.Lookahead, p.Cap(1, p.Text("abc"))), p.Ref(1));
        var prep = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.Match, prep.Search("abc"));
        var exact = prep.Work;
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Search("abc", limits: new(maxWork: exact - 1)));
        Assert.All(prep.Output, c => Assert.False(c.Participated));
        Assert.Equal(RegexSearchStatus.Match, prep.Search("abc", limits: new(maxWork: exact)));
        Assert.Equal(exact, prep.Work);
    }

    [Fact]
    public void CallerCanShareBudgetAcrossSearchInvocations()
    {
        var p = new Plan();
        p.Lit('a');
        var prep = new Prepared(p.Build());
        Assert.Equal(RegexSearchStatus.Match, prep.Search("a"));
        var state = new RunState(new(prep.States, prep.Captures, prep.Scratch), new(maxWork: prep.Work));
        Assert.Equal(RegexSearchStatus.Match, prep.Executor.Search("a", 0, true, EcmaStartPolicy.LowerBound,
            SearchOperation.Captures, ref state, prep.Output, out _));
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Executor.Search("a", 0, true, EcmaStartPolicy.LowerBound,
            SearchOperation.Captures, ref state, prep.Output, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
    }

    [Fact]
    public void FrameExhaustionDoesNotRetryWithAllocationAndScratchCanBeReused()
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Lit('a')));
        var prep = new Prepared(p.Build(), 2);
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Search("aaaa"));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, prep.Failure.Code);
        Assert.All(prep.Output, c => Assert.False(c.Participated));
        prep.Expect("a", 0, 1, new RegexCapture(true, 0, 1));
        Assert.Equal(RegexSearchStatus.Match, prep.Search(""));
        Assert.False(prep.Output[1].Participated);
    }

    [Fact]
    public void AllPartitionsAndCheckedCapacityMustBeAdmitted()
    {
        var p = new Plan();
        p.Empty();
        var executor = p.Build();
        var required = executor.GetRequiredCapacity(SearchOperation.Boolean, 1);
        Assert.Equal(required, executor.GetRequiredCapacity(SearchOperation.Captures, 1));
        Assert.Equal(96L + 4L * (required.StateSlots + required.CaptureSlots + required.ScratchSlots), required.RequiredBytes);
        Assert.Throws<OverflowException>(() => executor.GetRequiredCapacity(SearchOperation.Range, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.GetRequiredCapacity(SearchOperation.Boolean, 0));
        for (var partition = 0; partition < 3; partition++)
        {
            var states = new int[required.StateSlots - (partition == 0 ? 1 : 0)];
            var captures = new int[required.CaptureSlots - (partition == 1 ? 1 : 0)];
            var scratch = new int[required.ScratchSlots - (partition == 2 ? 1 : 0)];
            var state = new RunState(new(states, captures, scratch), new());
            Assert.Equal(RegexSearchStatus.LimitExceeded, executor.Search("", 0, false, EcmaStartPolicy.LowerBound,
                SearchOperation.Boolean, ref state, [], out _));
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, state.Failure.Code);
        }
        // Memory admission belongs to Runtime, which receives this exact required byte
        // count before allocating. RunState exposes no memory ceiling accessor.
        Assert.True(required.RequiredBytes > new RegexMatchLimits(maxWorkspaceBytes: 1).MaxWorkspaceBytes);
    }

    [Fact]
    public void CancellationPollsEvenEmptySuccessAndReuseIsClean()
    {
        var p = new Plan();
        p.Cap(1, p.Empty());
        var prep = new Prepared(p.Build());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, prep.Search("", options: new() { CancellationToken = cancellation.Token }));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, prep.Failure.Code);
        Assert.All(prep.Output, c => Assert.False(c.Participated));
        prep.Expect("", 0, 0, new RegexCapture(true, 0, 0));
    }

    [Fact]
    public void DeadlineAlreadyElapsedPollsBeforeSuccess()
    {
        var p = new Plan();
        p.Empty();
        var prep = new Prepared(p.Build());
        var state = new RunState(new(prep.States, prep.Captures, prep.Scratch), new(),
            new() { Timeout = TimeSpan.FromTicks(1) });
        // Deterministically establish expiry with the existing shared-budget test seam;
        // the real executor must preserve the same failure rather than accept/reset it.
        Assert.False(state.Budget.TryChargeAt(0, 0, long.MaxValue, out state.Failure));
        Assert.Equal(RegexSearchStatus.TimedOut, prep.Executor.Search("", 0, false, EcmaStartPolicy.LowerBound,
            SearchOperation.Captures, ref state, prep.Output, out _));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, state.Failure.Code);
        Assert.Equal(RegexSearchStatus.Match, prep.Search(""));
    }

    [Fact]
    public void FactoryHonorsCompileWorkMemoryRetentionAndDataIdentity()
    {
        var p = new Plan();
        p.Empty();
        var program = p.Program();
        var factory = new EcmaExecutorFactory();
        var work = new CompileBudget(new(maxCompileWork: 1));
        Assert.False(factory.TryCreate(program, new TestData(), ref work, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, diagnostic.Code);
        Assert.Equal(0, work.ReservedBytes);
        var memory = new CompileBudget(new(maxCompileMemoryBytes: 1));
        Assert.False(factory.TryCreate(program, new TestData(), ref memory, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, diagnostic.Code);
        var retention = new CompileBudget(new(maxPlanBytes: program.RetainedBytes));
        Assert.False(factory.TryCreate(program, new TestData(), ref retention, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, diagnostic.Code);
        var identity = new CompileBudget(new());
        Assert.False(factory.TryCreate(program, new TestData { DataId = "wrong" }, ref identity, out _, out diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
        var valid = new CompileBudget(new());
        Assert.True(factory.TryCreate(program, new TestData(), ref valid, out _, out _));
        Assert.Equal(192 + 64L * program.Nodes.Length, valid.ReservedBytes);
    }

    [Fact]
    public void DestinationAndRangeArgumentsHaveDistinctPrecedence()
    {
        var p = new Plan();
        p.Cap(1, p.Lit('a'));
        var prep = new Prepared(p.Build());
        var state = new RunState(new(prep.States, prep.Captures, prep.Scratch), new());
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, prep.Executor.Search("a", 0, false, EcmaStartPolicy.LowerBound,
            SearchOperation.Captures, ref state, new RegexCapture[1], out _));
        Assert.Equal(2, state.Failure.RequiredCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => prep.Search("a", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => prep.Search("a", -1));
        Assert.Equal(RegexSearchStatus.NoMatch, prep.Search("ba", 0, true));
        Assert.Equal(RegexSearchStatus.Match, prep.Search("ba", 1, true));
    }

    [Fact]
    public void ImmutablePlanCanRunConcurrentlyInSeparatePreparedPartitions()
    {
        var p = new Plan();
        p.Seq(p.Cap(1, p.Rep(p.Lit('a'), 1)), p.Ref(1));
        var executor = p.Build();
        Parallel.For(0, 16, _ =>
        {
            var prep = new Prepared(executor);
            for (var i = 0; i < 20; i++)
            {
                prep.Expect("aaaa", 0, 4, new RegexCapture(true, 0, 2));
            }
        });
    }
    [Fact]
    public void OversizedDestinationPreservesExcessOnEveryOutcome()
    {
        var p = new Plan();
        p.Cap(1, p.Lit('a'));
        var executor = p.Build();
        var prep = new Prepared(executor);
        var sentinel = new RegexCapture(true, 91, 7);
        foreach (var scenario in Enumerable.Range(0, 4))
        {
            RegexCapture[] output = [sentinel, sentinel, sentinel];
            var state = new RunState(scenario == 3 ? new([], [], []) : new(prep.States, prep.Captures, prep.Scratch),
                new(maxWork: scenario == 2 ? 1 : 10_000_000));
            var status = executor.Search(scenario == 1 ? "b" : "a", 0, false, EcmaStartPolicy.LowerBound,
                SearchOperation.Captures, ref state, output, out _);
            Assert.Equal(scenario switch
            {
                0 => RegexSearchStatus.Match,
                1 => RegexSearchStatus.NoMatch,
                _ => RegexSearchStatus.LimitExceeded
            }, status);
            Assert.Equal(sentinel, output[2]);
            if (scenario > 0)
            { Assert.Equal(default, output[0]); Assert.Equal(default, output[1]); }
        }
        RegexCapture[] shortOutput = [sentinel];
        var shortState = new RunState(new(prep.States, prep.Captures, prep.Scratch), new());
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, executor.Search("a", 0, false, EcmaStartPolicy.LowerBound,
            SearchOperation.Captures, ref shortState, shortOutput, out _));
        Assert.Equal(sentinel, shortOutput[0]);
    }

    [Fact]
    public void AmbiguousLateMissExhaustsWorkDeterministicallyThenReusesScratch()
    {
        var p = new Plan();
        p.Seq(p.Rep(p.Cap(1, p.Alt(p.Lit('a'), p.Text("aa")))), p.Lit('b'));
        var prep = new Prepared(p.Build(), 128);
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Search("aaaaaaaaaaaaaaac", limits: new(maxWork: 5_000)));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, prep.Failure.Code);
        var failure = prep.Failure;
        Assert.Equal(RegexSearchStatus.LimitExceeded, prep.Search("aaaaaaaaaaaaaaac", limits: new(maxWork: 5_000)));
        Assert.Equal(failure, prep.Failure);
        prep.Expect("b", 0, 1, default(RegexCapture));
    }

}
