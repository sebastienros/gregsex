using GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;
using Xunit;
using static GregsEx.Tests.Flavors.EcmaScript.Runtime.Composition.CompositionFixture;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Composition;

public sealed class CompositionLimitTests
{
    private static Plan Literal(EcmaScriptFlags flags = EcmaScriptFlags.Global)
    { var p = new Plan(flags); p.Cap(1, p.Lit('a')); return p; }
    private static Plan Catastrophic()
    {
        // (a|aa)*b has exponentially many late-miss decompositions.
        var p = new Plan(EcmaScriptFlags.Global);
        p.Seq(p.Rep(p.Cap(1, p.Alt(p.Lit('a'), p.Text("aa")))), p.Lit('b'));
        return p;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExponentialCandidatesExhaustDeterministicallyAndReuseScratch(bool captures)
    {
        using var m = Bind(Catastrophic());
        var output = new RegexCapture[3];
        var sentinel = new RegexCapture(true, 99, 7);
        var options = new RegexMatchOptions { Limits = new(maxWork: 5_000) };
        RegexExecutionFailure? prior = null;
        for (var i = 0; i < 2; i++)
        {
            output.AsSpan().Fill(sentinel);
            m.LastIndex = 0;
            var status = captures ? m.Exec("aaaaaaaaaaaaaaac", output, out _, options) : m.Test("aaaaaaaaaaaaaaac", options);
            Assert.Equal(RegexSearchStatus.LimitExceeded, status);
            var failure = m.LastFailure;
            Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, failure.Code);
            Assert.InRange(failure.WorkConsumed, 1, 5_000);
            if (prior.HasValue)
            {
                Assert.Equal(prior.Value, failure);
            }

            prior = failure;
            Assert.Equal(sentinel, output[2]);
            if (captures)
            { Assert.Equal(default, output[0]); Assert.Equal(default, output[1]); }
        }
        m.LastIndex = 0;
        Expect(m, "ab", 0, 2, new RegexCapture(true, 0, 1));
        Assert.Equal(default, m.LastFailure);
    }
    [Fact]
    public void FrameCapacityFailureDoesNotAllocateRetryAndReserveIsTransactional()
    {
        var p = new Plan();
        p.Rep(p.Cap(1, p.Lit('a')));
        using var m = Bind(p, frames: 2);
        var bytes = m.ReservedBytes;
        var output = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.LimitExceeded, m.Exec("aaaa", output, out var range));
        Assert.Equal(default, range);
        Assert.All(output, c => Assert.Equal(default, c));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, m.LastFailure.Code);
        Assert.Equal(bytes, m.ReservedBytes);
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
        Assert.Throws<RegexExecutionException>(() => m.Reserve(int.MaxValue));
        Assert.Equal(bytes, m.ReservedBytes);
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
        m.Reserve(128);
        Expect(m, "aaaa", 0, 4, new RegexCapture(true, 3, 1));
    }
    [Fact]
    public void PrivateWorkspaceMemoryAdmissionHasExactBoundaryAndFailureLeavesReuse()
    {
        var vm = Build(Literal());
        long bytes;
        using (var original = new EcmaScriptMatcher(vm, frameCapacity: 4))
        {
            bytes = original.ReservedBytes;
        }

        using var exact = new EcmaScriptMatcher(vm, new(maxWorkspaceBytes: bytes), frameCapacity: 4);
        Assert.Equal(bytes, exact.ReservedBytes);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded,
            Assert.Throws<RegexExecutionException>(() => new EcmaScriptMatcher(vm, new(maxWorkspaceBytes: bytes - 1), frameCapacity: 4)).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded,
            Assert.Throws<RegexExecutionException>(() => exact.Reserve(5)).Failure.Code);
        Assert.Equal(bytes, exact.ReservedBytes);
        Expect(exact, "a", 0, 1, new RegexCapture(true, 0, 1));
        exact.LastIndex = 0;
        Assert.Equal(RegexSearchStatus.LimitExceeded, exact.Exec("a", new RegexCapture[2], out _,
            new() { Limits = new(maxWorkspaceBytes: bytes - 1) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, exact.LastFailure.Code);
        Expect(exact, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("all")]
    public void EveryCompleteHostOperationKeepsCumulativeWorkAcrossRetriesAndOutputs(string operation)
    {
        using var m = Bind(Literal());
        // Each single Exec is admitted, but the dense complete operation must not
        // reset MaxWork at each search, sticky retry, output copy or iterator step.
        var options = new RegexMatchOptions { Limits = new(maxWork: 400) };
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
        m.LastIndex = 0;
        var error = Assert.Throws<RegexExecutionException>(() =>
        {
            var input = operation == "split" ? new string('b', 64) : new string('a', 64);
            if (operation == "match")
            {
                m.Match(input, options);
            }
            else if (operation == "replace")
            {
                m.ReplaceAll(input, "$1$&", options);
            }
            else if (operation == "split")
            {
                m.Split(input, options: options);
            }
            else
            {
                using var it = m.MatchAll(input, options).GetEnumerator();
                long before = 0;
                try
                {
                    while (it.MoveNext())
                    { Assert.True(it.WorkConsumed > before); before = it.WorkConsumed; }
                }
                catch (RegexExecutionException e)
                {
                    Assert.Equal(e.Failure, it.Failure);
                    Assert.Equal(RegexSearchStatus.LimitExceeded, it.TerminalStatus);
                    Assert.Throws<InvalidOperationException>(() => it.Current);
                    throw;
                }
            }
        });
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, error.Failure.Code);
        Assert.InRange(error.Failure.WorkConsumed, 1, 400);
        m.LastIndex = 0;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Fact]
    public void TemplatesAndNamedLookupConsumeTheVmBudgetAfterMatching()
    {
        var p = new Plan() { Names = [new("abcdefghijklmnopqrstuvwxyz", 0, 1)], Aliases = [1] };
        p.Cap(1, p.Lit('a'), 0);
        using var m = Bind(p);
        var error = Assert.Throws<RegexExecutionException>(() => m.Replace("a", "$<abcdefghijklmnopqrstuvwxyz>",
            new() { Limits = new(maxWork: 60) }));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, error.Failure.Code);
        Assert.Equal("a", m.Replace("a", "$<abcdefghijklmnopqrstuvwxyz>"));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("all")]
    public void HostResultLimitsRemainDistinctWithRealVmAndSessionReusable(string operation)
    {
        using var m = Bind(Literal(), host: new(maxResults: 1));
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                m.Match("aa");
            }
            else if (operation == "replace")
            {
                m.ReplaceAll("aa", "");
            }
            else if (operation == "split")
            {
                m.Split("ba");
            }
            else
            {
                using var it = m.MatchAll("aa").GetEnumerator();
                Assert.True(it.MoveNext());
                try
                { it.MoveNext(); }
                catch (EcmaScriptHostException e)
                { Assert.Equal(e.Failure, it.HostFailure); Assert.Equal(RegexSearchStatus.LimitExceeded, it.TerminalStatus); throw; }
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.ResultLimitExceeded, error.Failure.Code);
        Assert.Equal(2, error.Failure.RequiredCapacity);
        Assert.True(error.Failure.WorkConsumed > 0);
        m.LastIndex = 0;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    public void OutputUnitLimitsCountRealStringsAndPrefixSuffixExpansion(string operation)
    {
        using var m = Bind(Literal(), host: new(maxOutputUnits: 1));
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                m.Match("aa");
            }
            else if (operation == "replace")
            {
                m.ReplaceAll("xay", "$`$'$&");
            }
            else
            {
                m.Split("bc");
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.OutputLimitExceeded, error.Failure.Code);
        Assert.Equal(2, error.Failure.RequiredCapacity);
        m.LastIndex = 0;
        Assert.Equal(RegexSearchStatus.Match, m.Test("a"));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("all")]
    public void AllocatingHostMemoryLimitsDoNotRelaxPrivateWorkspaceCeiling(string operation)
    {
        using var m = Bind(Literal(), host: new(maxMemoryBytes: 1));
        var bytes = m.ReservedBytes;
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                m.Match("a");
            }
            else if (operation == "replace")
            {
                m.ReplaceAll("a", "");
            }
            else if (operation == "split")
            {
                m.Split("", 0);
            }
            else
            {
                m.MatchAll("a");
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.MemoryLimitExceeded, error.Failure.Code);
        Assert.True(error.Failure.RequiredCapacity > 1);
        Assert.Equal(bytes, m.ReservedBytes);
        m.LastIndex = 0;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Fact]
    public void BorrowedDestinationPrecedenceLogicalClearingAndExcessSentinels()
    {
        using var m = Bind(Literal());
        var sentinel = new RegexCapture(true, 99, 7);
        RegexCapture[] shortOutput = [sentinel];
        var cancelled = new RegexMatchOptions { CancellationToken = new(true) };
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, m.Exec("a", shortOutput, out _, cancelled));
        Assert.Equal(sentinel, shortOutput[0]);
        Assert.Equal(2, m.LastFailure.RequiredCapacity);
        Assert.Equal(0, m.LastFailure.WorkConsumed);
        foreach (var scenario in Enumerable.Range(0, 5))
        {
            m.LastIndex = 0;
            RegexCapture[] output = [sentinel, sentinel, sentinel];
            var status = m.Exec(scenario == 1 ? "b" : "a", output, out var range,
                scenario switch
                {
                    2 => cancelled,
                    3 => new() { Limits = new(maxWork: 1) },
                    4 => new() { Limits = new(maxWorkspaceBytes: 1) },
                    _ => default
                });
            Assert.Equal(scenario switch
            {
                0 => RegexSearchStatus.Match,
                1 => RegexSearchStatus.NoMatch,
                2 => RegexSearchStatus.Cancelled,
                _ => RegexSearchStatus.LimitExceeded
            }, status);
            Assert.Equal(sentinel, output[2]);
            if (scenario > 0)
            { Assert.Equal(default, range); Assert.Equal(default, output[0]); Assert.Equal(default, output[1]); }
        }
        m.LastIndex = 0;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Fact]
    public void SearchRestoresExactNumericStateOnFailureAndNonStatefulExecPreservesIt()
    {
        using var m = Bind(Literal());
        m.LastIndex = -0d;
        var error = Assert.Throws<RegexExecutionException>(() => m.Search("a", new() { CancellationToken = new(true) }));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, error.Failure.Code);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0d), BitConverter.DoubleToInt64Bits(m.LastIndex));
        m.LastIndex = 42.5;
        Assert.Equal(0, m.Search("a"));
        Assert.Equal(42.5, m.LastIndex);
        using var n = Bind(Literal(EcmaScriptFlags.None));
        n.LastIndex = double.NaN;
        Expect(n, "a", 0, 1, new RegexCapture(true, 0, 1));
        Assert.True(double.IsNaN(n.LastIndex));
        Assert.Equal(RegexSearchStatus.NoMatch, n.Test("b"));
        Assert.True(double.IsNaN(n.LastIndex));
    }
    [Fact]
    public void IteratorLifetimeCancellationAndSourceReuseAreIndependent()
    {
        using var m = Bind(Literal());
        using var cancellation = new CancellationTokenSource();
        using var it = m.MatchAll("aa", new() { CancellationToken = cancellation.Token }).GetEnumerator();
        Assert.True(it.MoveNext());
        var first = it.Current;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
        cancellation.Cancel();
        var error = Assert.Throws<RegexExecutionException>(() => it.MoveNext());
        Assert.Equal(RegexExecutionFailureCode.Cancelled, error.Failure.Code);
        Assert.Equal(error.Failure, it.Failure);
        Assert.Equal(RegexSearchStatus.Cancelled, it.TerminalStatus);
        Assert.Equal(new(0, 1), first.Range);
        Assert.Equal(new(true, 0, 1), first.Captures[1]);
        it.Dispose();
        Assert.Throws<ObjectDisposedException>(() => it.MoveNext());
        m.LastIndex = 0;
        Expect(m, "a", 0, 1, new RegexCapture(true, 0, 1));
    }
    [Fact]
    public void ImmutableExecutorSupportsSeparateConcurrentSessionsAndOwnedResults()
    {
        var p = new Plan();
        p.Seq(p.Cap(1, p.Rep(p.Lit('a'), 1)), p.Ref(1));
        var vm = Build(p);
        Parallel.For(0, 8, _ =>
        {
            using var m = new EcmaScriptMatcher(vm, frameCapacity: 128);
            var result = m.Match("aaaa")!.Single!;
            for (var i = 0; i < 4; i++)
            {
                Expect(m, "aa", 0, 2, new RegexCapture(true, 0, 1));
            }

            Assert.Equal(new(0, 4), result.Range);
            Assert.Equal(new(true, 0, 2), result.Captures[1]);
        });
    }
    [Fact]
    public void MatchAllIteratorsOwnIndependentSeedsAndRetainedResults()
    {
        using var m = Bind(Literal());
        m.LastIndex = 1.9;
        var snapshot = m.MatchAll("baa");
        using var left = snapshot.GetEnumerator();
        using var right = snapshot.GetEnumerator();
        Assert.True(left.MoveNext());
        var first = left.Current;
        Assert.True(left.MoveNext());
        Assert.Equal(new(2, 1), left.Current.Range);
        Assert.True(right.MoveNext());
        Assert.Equal(new(1, 1), right.Current.Range);
        Assert.False(left.MoveNext());
        Assert.Equal(new(1, 1), first.Range);
        Assert.Equal(1.9, m.LastIndex);
        right.Dispose();
        Assert.Throws<ObjectDisposedException>(() => right.MoveNext());
        m.Dispose();
        Assert.Throws<ObjectDisposedException>(() => m.Test("a"));
        Assert.Equal(new(1, 1), first.Range);
    }
}
