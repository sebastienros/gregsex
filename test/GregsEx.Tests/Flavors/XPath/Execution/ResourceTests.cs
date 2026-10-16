using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Execution;
using Xunit;
using static GregsEx.Tests.Flavors.XPath.Execution.PlanFixture;

namespace GregsEx.Tests.Flavors.XPath.Execution;

public sealed class ResourceTests
{
    [Fact]
    public void CapacityEquationsAreCheckedAndSpareArraysNeverRaiseFrameCeiling()
    {
        var e = Executor(R(L('a')));
        var cap = e.GetRequiredCapacity(SearchOperation.Captures, 1);
        // Literal depth1 + repeat => taskCapacity8, 4 slots/task, 2 slots/choice.
        Assert.Equal(new RegexWorkspaceCapacity(34, 4, 32), cap);
        Assert.Throws<ArgumentOutOfRangeException>(() => e.GetRequiredCapacity(SearchOperation.Range, 0));
        Assert.Throws<OverflowException>(() => e.GetRequiredCapacity(SearchOperation.Range, int.MaxValue));
        var storage = new Storage(e, 8, 100);
        var groups = new RegexCapture[2];
        groups[1] = new(true, 9, 9);
        var state = new RunState(storage.Buffers, new());
        Assert.Equal(RegexSearchStatus.LimitExceeded,
            e.Search<char, XmlCharTextReader>("aa", 0, SearchOperation.Captures, 1, ref state, groups, out var match));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, state.Failure.Code);
        Assert.Equal(default, match);
        Assert.Equal(default, groups[0]);
        Assert.Equal(new(true, 9, 9), groups[1]);
        state = new(storage.Buffers, new());
        Assert.Equal(RegexSearchStatus.Match,
            e.Search<char, XmlCharTextReader>("aa", 0, SearchOperation.Captures, 8, ref state, groups, out match));
        Assert.Equal(new(0, 2), match);
    }
    [Fact]
    public void DestinationCapacityInitialPollSubjectThenBoundaryPrecedence()
    {
        var e = Executor(C(L('a', 1), 1, 0, 3), parents: [-1, 0]);
        using var token = new CancellationTokenSource();
        token.Cancel();
        var options = new RegexMatchOptions { CancellationToken = token.Token };
        var tooSmall = new RunState(new([], [], []), new(), options);
        RegexCapture[] tiny = [new(true, 8, 9)];
        Assert.Equal(RegexSearchStatus.DestinationTooSmall,
            e.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Captures, 1, ref tooSmall, tiny, out _));
        Assert.Equal(new(true, 8, 9), tiny[0]);
        Assert.Equal(0, tooSmall.Budget.WorkConsumed);
        var groups = new RegexCapture[3];
        groups[2] = new(true, 7, 7);
        var insufficient = new RunState(new([], [], []), new(), options);
        Assert.Equal(RegexSearchStatus.LimitExceeded,
            e.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Captures, 1, ref insufficient, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, insufficient.Failure.Code);
        var storage = new Storage(e);
        var cancelled = new RunState(storage.Buffers, new(), options);
        Assert.Equal(RegexSearchStatus.Cancelled,
            e.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Captures, 32, ref cancelled, groups, out _));
        Assert.Equal(0, cancelled.Budget.WorkConsumed);
        foreach (var input in new[] { "😀\0", "😀\uD800" })
        {
            var state = new RunState(storage.Buffers, new());
            Assert.Equal(RegexSearchStatus.InvalidInput,
                e.Search<char, XmlCharTextReader>(input, 1, SearchOperation.Captures, 32, ref state, groups, out _));
            Assert.Equal(input[2] == 0 ? RegexExecutionFailureCode.InvalidInputDomain : RegexExecutionFailureCode.InvalidEncoding, state.Failure.Code);
            Assert.Equal(2, state.Failure.Offset);
            Assert.Equal(default, groups[0]);
            Assert.Equal(default, groups[1]);
            Assert.Equal(new(true, 7, 7), groups[2]);
        }
    }
    [Fact]
    public void WholeSubjectInvalidityWinsOverEarlyLiteralHit()
    {
        foreach (var input in new[] { "a\0", "a" + (char)0xD800 })
        {
            var expected = input[1] == 0 ? RegexExecutionFailureCode.InvalidInputDomain : RegexExecutionFailureCode.InvalidEncoding;
            var e = Executor(L('a'));
            var storage = new Storage(e);
            var state = new RunState(storage.Buffers, new());
            Assert.Equal(RegexSearchStatus.InvalidInput,
                e.Search<char, XmlCharTextReader>(input, 0, SearchOperation.Boolean, 32, ref state, [], out _));
            Assert.Equal(expected, state.Failure.Code);
            Assert.Equal(1, state.Failure.Offset);
        }
    }
    [Fact]
    public void Utf8WholeSubjectAndInteriorBoundaryFailuresAreDistinctAndReusable()
    {
        var e = Executor(L('a'), kind: RegexInputKind.Utf8);
        var storage = new Storage(e);
        var groups = new RegexCapture[1];
        foreach (var (Input, Start, Code) in new[] { (Input: "a😀"u8.ToArray(), Start: 2, Code: RegexExecutionFailureCode.InvalidStartBoundary),
            (Input: [97, 0xF0, 0x9F, 0x98, 0x80, 0], Start: 2, Code: RegexExecutionFailureCode.InvalidInputDomain),
            (Input: [97, 0xFF], Start: 0, Code: RegexExecutionFailureCode.InvalidEncoding) })
        {
            var state = new RunState(storage.Buffers, new());
            Assert.Equal(RegexSearchStatus.InvalidInput,
                e.Search<byte, XmlByteTextReader>(Input, Start, SearchOperation.Captures, 32, ref state, groups, out _));
            Assert.Equal(Code, state.Failure.Code);
            Assert.Equal(default, groups[0]);
        }
        var fresh = new RunState(storage.Buffers, new());
        Assert.Equal(RegexSearchStatus.Match,
            e.Search<byte, XmlByteTextReader>("a"u8, 0, SearchOperation.Captures, 32, ref fresh, groups, out _));
    }
    [Fact]
    public void WorkIsCumulativeAcrossCandidatesAndCallsAndCancellationPollsEmpty()
    {
        var e = Executor(L('a'));
        var storage = new Storage(e);
        var state = new RunState(storage.Buffers, new());
        Assert.Equal(RegexSearchStatus.NoMatch,
            e.Search<char, XmlCharTextReader>("bbb", 0, SearchOperation.Boolean, 32, ref state, [], out _));
        var work = state.Budget.WorkConsumed;
        Assert.True(work > 3);
        Assert.Equal(RegexSearchStatus.Match,
            e.Search<char, XmlCharTextReader>("a", 0, SearchOperation.Boolean, 32, ref state, [], out _));
        Assert.True(state.Budget.WorkConsumed > work);
        var limited = new RunState(storage.Buffers, new(maxWork: work - 1));
        Assert.Equal(RegexSearchStatus.LimitExceeded,
            e.Search<char, XmlCharTextReader>("bbb", 0, SearchOperation.Boolean, 32, ref limited, [], out _));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, limited.Failure.Code);
        var empty = Executor(E());
        storage = new(empty);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new RunState(storage.Buffers, new(), new() { CancellationToken = cancellation.Token });
        Assert.Equal(RegexSearchStatus.Cancelled,
            empty.Search<char, XmlCharTextReader>("", 0, SearchOperation.Boolean, 32, ref cancelled, [], out _));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, cancelled.Failure.Code);
        var timed = new RunState(storage.Buffers, new(), new() { Timeout = TimeSpan.FromTicks(1) });
        Assert.Equal(RegexSearchStatus.TimedOut,
            empty.Search<char, XmlCharTextReader>("", 0, SearchOperation.Boolean, 32, ref timed, [], out _));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, timed.Failure.Code);
        var fresh = new RunState(storage.Buffers, new());
        Assert.Equal(RegexSearchStatus.Match,
            empty.Search<char, XmlCharTextReader>("", 0, SearchOperation.Boolean, 32, ref fresh, [], out _));
    }
    [Fact]
    public async Task ImmutablePlanSupportsConcurrentSeparateWorkspaces()
    {
        var e = Executor(S(C(R(L('a', 1), 1) with { Offset = 1, Length = 1 }, 1, 0, 3), B(1)), parents: [-1, 0]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var storage = new Storage(e);
            var groups = new RegexCapture[2];
            for (var i = 0; i < 30; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, Char(e, "aaaa", out var match, groups, storage: storage));
                Assert.Equal(new(0, 4), match);
                Assert.Equal(new(true, 0, 2), groups[1]);
                Assert.Equal(RegexSearchStatus.NoMatch, Char(e, "b", out _, groups, storage: storage));
                Assert.Equal(default, groups[1]);
            }
        })));
    }
    [Fact]
    public void SubjectStorageGuardPrecedesDestinationCapacityAndPolling()
    {
        var chars = Executor(L('a'));
        var bytes = Executor(L('a'), kind: RegexInputKind.Utf8);
        var state = new RunState(new([], [], []), new());
        var rejected = false;
        try
        { chars.Search<byte, XmlByteTextReader>("a"u8, 0, SearchOperation.Captures, 1, ref state, [], out _); }
        catch (ArgumentException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(0, state.Budget.WorkConsumed);
        Assert.Equal(default, state.Failure);
        rejected = false;
        try
        { bytes.Search<char, XmlCharTextReader>("a", 0, SearchOperation.Captures, 1, ref state, [], out _); }
        catch (ArgumentException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(0, state.Budget.WorkConsumed);
        Assert.Equal(default, state.Failure);
    }
    [Fact]
    public void ExactCaseFactoryDoesNotCopyUnusedCaseMappings()
    {
        var plain = Executor(L('a'));
        var ignore = Executor(L('a'), XPathFlags.IgnoreCase);
        var data = Data();
        var p = new CompileBudget(new());
        var i = new CompileBudget(new());
        Assert.True(new XPathExecutorFactory().TryCreate(plain.Program, data, ref p, out _, out _));
        Assert.True(new XPathExecutorFactory().TryCreate(ignore.Program, data, ref i, out _, out _));
        Assert.Equal(160, p.ReservedBytes);
        Assert.Equal(160 + 12L * data.CaseMappings.Length, i.ReservedBytes);
    }
    [Fact]
    public void FactoryAdmitsRetainedMemoryWorkAndTemporaryStorageBeforeAllocation()
    {
        var e = Executor(S(L('a'), L('b')));
        var p = e.Program;
        var d = Data();
        var work = new CompileBudget(new(maxCompileWork: 1));
        Assert.False(new XPathExecutorFactory().TryCreate(p, d, ref work, out _, out var diag));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, diag.Code);
        Assert.Equal(0, work.ReservedBytes);
        var memory = new CompileBudget(new(maxCompileMemoryBytes: 1));
        Assert.False(new XPathExecutorFactory().TryCreate(p, d, ref memory, out _, out diag));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, diag.Code);
        Assert.Equal(0, memory.ReservedBytes);
        var plan = new CompileBudget(new(maxPlanBytes: p.RetainedBytes));
        Assert.False(new XPathExecutorFactory().TryCreate(p, d, ref plan, out _, out diag));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, diag.Code);
        Assert.Equal(0, plan.ReservedBytes);
    }
}
