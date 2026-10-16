using GregsEx.Contracts;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Unicode;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Host;

public sealed class HostLifetimeTests
{
    [Fact]
    public void MatchAllSnapshotCloneIsIndependentAndTerminalStatusExplicit()
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global | EcmaScriptFlags.HasIndices).Bind();
        source.LastIndex = 1.9;
        var snapshot = source.MatchAll("aba");
        source.LastIndex = 42;
        using var iterator = snapshot.GetEnumerator();
        Assert.Null(iterator.TerminalStatus);
        Assert.True(iterator.MoveNext());
        var result = iterator.Current;
        Assert.Equal(new(2, 1), result.Range);
        Assert.True(result.HasIndices);
        Assert.Equal(42d, source.LastIndex);
        Assert.False(iterator.MoveNext());
        Assert.Equal(RegexSearchStatus.NoMatch, iterator.TerminalStatus);
        Assert.False(iterator.MoveNext());
        Assert.Equal(RegexExecutionFailureCode.None, iterator.Failure.Code);
        source.Dispose();
        Assert.Equal(new(true, 2, 1), result.Captures[0]);
        using var other = snapshot.GetEnumerator();
        Assert.True(other.MoveNext());
        Assert.Equal(result.Range, other.Current.Range);
    }
    [Theory]
    [InlineData(EcmaScriptFlags.None, 3)]
    [InlineData(EcmaScriptFlags.Unicode, 2)]
    [InlineData(EcmaScriptFlags.UnicodeSets, 2)]
    public void MatchAllAdvancesEmptyIncludingTerminalOnce(EcmaScriptFlags mode, int count)
    {
        using var matcher = new ContractExecutor("", mode | EcmaScriptFlags.Global).Bind();
        using var iterator = matcher.MatchAll("\ud83d\ude00").GetEnumerator();
        var seen = 0;
        while (iterator.MoveNext())
        { seen++; Assert.Equal(0, iterator.Current.Range.Length); }
        Assert.Equal(count, seen);
        Assert.Equal(0d, matcher.LastIndex);
        Assert.Equal(RegexSearchStatus.NoMatch, iterator.TerminalStatus);
    }
    [Fact]
    public void MatchAllRequiresGlobalAndEarlyDisposalReleasesClone()
    {
        using var ordinary = new ContractExecutor("a").Bind();
        Assert.Equal(EcmaScriptHostFailureCode.GlobalFlagRequired, Assert.Throws<EcmaScriptHostException>(() => ordinary.MatchAll("a")).Failure.Code);
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        var iterator = source.MatchAll("a").GetEnumerator();
        Assert.True(iterator.MoveNext());
        iterator.Dispose();
        iterator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => iterator.MoveNext());
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
        Assert.Null(iterator.TerminalStatus);
        Assert.Throws<NotSupportedException>(() => iterator.Reset());
    }
    [Fact]
    public void MatchAllSharesOneWorkBudgetAndReportsTerminalFailure()
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        using var iterator = source.MatchAll("aaaa", new() { Limits = new(maxWork: 8) }).GetEnumerator();
        Assert.True(iterator.MoveNext());
        var first = iterator.WorkConsumed;
        var failure = Assert.Throws<RegexExecutionException>(() => iterator.MoveNext());
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, failure.Failure.Code);
        Assert.Equal(failure.Failure, iterator.Failure);
        Assert.Equal(RegexSearchStatus.LimitExceeded, iterator.TerminalStatus);
        Assert.True(iterator.WorkConsumed >= first);
        Assert.Equal(0d, source.LastIndex);
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
    }
    [Fact]
    public void IteratorPollsEveryMoveNextAndDeadlineIncludesIdleTime()
    {
        using var source = new ContractExecutor("", EcmaScriptFlags.Global).Bind();
        using var cancelled = new CancellationTokenSource();
        using var iterator = source.MatchAll("", new() { CancellationToken = cancelled.Token }).GetEnumerator();
        Assert.True(iterator.MoveNext());
        cancelled.Cancel();
        Assert.Equal(RegexExecutionFailureCode.Cancelled, Assert.Throws<RegexExecutionException>(() => iterator.MoveNext()).Failure.Code);
        Assert.Equal(RegexSearchStatus.Cancelled, iterator.TerminalStatus);
        // Timeout determinism uses the same RunBudget boundary hook as M1; no wall-clock sleep assumption.
        var budget = new RunBudget(new(), new() { Timeout = TimeSpan.FromTicks(1) });
        Assert.False(budget.TryChargeAt(0, 0, budget.StartedAt + System.Diagnostics.Stopwatch.Frequency, out var failure));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, failure.Code);
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    public void CompleteHostLoopsDoNotRestartWorkBudget(string operation)
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind();
        var options = new RegexMatchOptions { Limits = new(maxWork: 10) };
        var failure = Assert.Throws<RegexExecutionException>(() =>
        {
            if (operation == "match")
            {
                source.Match("aaaa", options);
            }
            else if (operation == "replace")
            {
                source.Replace("aaaa", "x", options);
            }
            else
            {
                source.Split("aaaa", options: options);
            }
        });
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, failure.Failure.Code);
        Assert.True(failure.Failure.WorkConsumed <= 10);
        Assert.Equal(failure.Failure, source.LastFailure);
        source.LastIndex = 0;
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("search")]
    [InlineData("all")]
    public void EmptyHostOperationsPollCancellation(string operation)
    {
        using var source = new ContractExecutor("", EcmaScriptFlags.Global).Bind();
        var options = new RegexMatchOptions { CancellationToken = new(true) };
        var failure = Assert.Throws<RegexExecutionException>(() =>
        {
            if (operation == "match")
            {
                source.Match("", options);
            }
            else if (operation == "replace")
            {
                source.Replace("", "", options);
            }
            else if (operation == "split")
            {
                source.Split("", 0, options);
            }
            else if (operation == "search")
            {
                source.Search("", options);
            }
            else
            {
                source.MatchAll("", options);
            }
        });
        Assert.Equal(RegexExecutionFailureCode.Cancelled, failure.Failure.Code);
    }
    [Fact]
    public void ShortDestinationPrecedesWorkspaceAndCancellationAndLeavesStateUntouched()
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global, [default]).Bind();
        source.LastIndex = 2;
        var destination = new[] { new RegexCapture(true, 91, 1) };
        var options = new RegexMatchOptions { Limits = new(maxWorkspaceBytes: 1), CancellationToken = new(true) };
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, source.Exec("a", destination, out var range, options));
        Assert.Equal(default, range);
        Assert.Equal(new(true, 91, 1), destination[0]);
        Assert.Equal(2d, source.LastIndex);
        Assert.Equal(2, source.LastFailure.RequiredCapacity);
        Assert.Equal(0, source.LastFailure.WorkConsumed);
        var full = new[] { new RegexCapture(true, 91, 1), new RegexCapture(true, 92, 1) };
        Assert.Equal(RegexSearchStatus.LimitExceeded, source.Exec("a", full, out _, options));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, source.LastFailure.Code);
        Assert.All(full, c => Assert.Equal(default, c));
    }
    [Fact]
    public void CapacityOverflowAndMemoryReserveAreTransactional()
    {
        var executor = new ContractExecutor("a");
        using var source = executor.Bind();
        var initial = source.ReservedBytes;
        executor.OverflowCapacity = true;
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, Assert.Throws<RegexExecutionException>(() => source.Reserve(16)).Failure.Code);
        executor.OverflowCapacity = false;
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, Assert.Throws<RegexExecutionException>(() => source.Reserve(20_000_000)).Failure.Code);
        Assert.Equal(initial, source.ReservedBytes);
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
        source.Reserve(16);
        Assert.True(source.ReservedBytes > initial);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, Assert.Throws<RegexExecutionException>(() => executor.Bind(new(maxWorkspaceBytes: 1))).Failure.Code);
    }
    [Fact]
    public void WorkspaceExactBoundaryAndGrowthPeakAdmission()
    {
        var executor = new ContractExecutor("a");
        using var probe = executor.Bind();
        var bytes = probe.ReservedBytes;
        using var exact = executor.Bind(new(maxWorkspaceBytes: bytes));
        Assert.Equal(bytes, exact.ReservedBytes);
        Assert.Equal(RegexSearchStatus.Match, exact.Test("a"));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, Assert.Throws<RegexExecutionException>(() => executor.Bind(new(maxWorkspaceBytes: bytes - 1))).Failure.Code);
        // New partitions alone fit this ceiling, but the live old + replacement peak does not.
        var capacity = executor.GetRequiredCapacity(SearchOperation.Captures, 16);
        var final = 256 + capacity.RequiredBytes + 32 + 16;
        using var peak = executor.Bind(new(maxWorkspaceBytes: final));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, Assert.Throws<RegexExecutionException>(() => peak.Reserve(16)).Failure.Code);
        Assert.Equal(bytes, peak.ReservedBytes);
    }
    [Fact]
    public void ReentrantOperationsReserveStateAndDisposeAreRejected()
    {
        var executor = new ContractExecutor("a");
        using var source = executor.Bind();
        executor.OnSearch = () =>
        {
            Assert.Throws<InvalidOperationException>(() => source.Test("a"));
            Assert.Throws<InvalidOperationException>(() => source.Reserve(16));
            Assert.Throws<InvalidOperationException>(() => source.LastIndex = 2);
            Assert.Throws<InvalidOperationException>(() => source.LastIndex);
            Assert.Throws<InvalidOperationException>(() => source.Dispose());
        };
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
        executor.OnSearch = null;
        source.Dispose();
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Test("a"));
        Assert.Throws<ObjectDisposedException>(() => source.Match("a"));
        Assert.Throws<ObjectDisposedException>(() => source.LastIndex = 1);
    }
    [Fact]
    public async Task SharedExecutorSupportsSeparateConcurrentSessions()
    {
        var executor = new ContractExecutor("a", EcmaScriptFlags.Global);
        async Task<bool> Run(double seed)
        {
            using var session = executor.Bind();
            session.LastIndex = seed;
            return await Task.Run(() => session.Test("aba") == RegexSearchStatus.Match && session.LastIndex == seed + 1);
        }
        Assert.All(await Task.WhenAll(Run(0), Run(2)), Assert.True);
    }
    [Fact]
    public void RealVmAndPinnedDataComposeThroughInjectedConstructor()
    {
        var plan = new ContractExecutor("", EcmaScriptFlags.Global | EcmaScriptFlags.Unicode).Program;
        var budget = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var data, out _));
        Assert.True(new EcmaExecutorFactory().TryCreate(plan, data!, ref budget, out var executor, out _));
        using var source = new EcmaScriptMatcher(executor!, frameCapacity: 8);
        Assert.Equal(new[] { "", "" }, source.Match("\ud83d\ude00")!.GlobalMatches);
        Assert.Equal("x\ud83d\ude00x", source.Replace("\ud83d\ude00", "x"));
        Assert.Equal(new[] { "\ud83d\ude00" }, source.Split("\ud83d\ude00"));
    }
}
