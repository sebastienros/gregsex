using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

public sealed class ParityOffsetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DefinedPositiveOffsetErrorDoesNotPublishPairsAndWorkspaceCanBeReused(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(?C1)abc", domain), RuntimeFixture.Capacity);
        Pcre2OffsetPair[] pairs = [new(true, 7, 9), new(true, 11, 13)];
        var before = pairs.ToArray();
        foreach (var start in new[] { 4, int.MaxValue })
        {
            foreach (var policy in new[] { Pcre2ValidationPolicy.NativeWindow, Pcre2ValidationPolicy.WholeSubjectProjection })
            {
                var result = domain < 2
                    ? workspace.Match("abc"u8, start, pairs, policy: policy,
                        callbacks: new(Match: (in _, __) => throw new ApplicationException()))
                    : workspace.Match("abc", start, pairs, policy: policy,
                        callbacks: new(Match: (in _, __) => throw new ApplicationException()));
                AssertBadOffset(result);
                Assert.Equal(before, pairs);
                Assert.Equal(0, workspace.LastWork);
                Assert.True(workspace.IsIdle);
            }
        }
        // Offset rejection precedes UTF text inspection; no source-undefined
        // match metadata is inferred from an invalid subject or the bad offset.
        AssertBadOffset(domain < 2 ? workspace.Match([255], 2, pairs)
            : workspace.Match(['\ud800'], 2, pairs));
        Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeFixture.Match(workspace, domain, "abc", pairs, start: -1));
        Assert.Equal(before, pairs);
        Assert.True(workspace.IsIdle);
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "abc", pairs).Status);
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => RuntimeFixture.Match(workspace, domain, "abc", pairs, start: 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DirectExecutorReportsErrorAfterResourceCancellationAndDeadlineAdmission(int domain)
    {
        var executor = RuntimeFixture.Compile("abc", domain);
        var required = RuntimeFixture.Capacity.Required(executor.Program.GroupCount);
        var buffers = new WorkspaceBuffers(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]);
        Pcre2OffsetPair[] pairs = [new(true, 7, 9)];
        var callback = new Pcre2OffsetPair[executor.Program.GroupCount];
        foreach (var options in new[] { new Pcre2MatchOptions { Limits = new() }, new Pcre2MatchOptions { Limits = new(maxWork: 0) },
            new Pcre2MatchOptions { Limits = new(maxDepth: 0) }, new Pcre2MatchOptions { Limits = new(maxWorkspaceBytes: 0) } })
        {
            var state = Pcre2KernelOperation.CreateRunState(buffers, Pcre2OperationLimits.Resolve(options.Limits!, default), options);
            var result = Direct(executor, domain, options, ref state, callback, pairs);
            if (options.Limits!.MaxWork == 0)
            {
                Assert.Equal((int)Pcre2ErrorCode.Matchlimit, result.NativeReturnCode);
            }
            else if (options.Limits.MaxDepth == 0)
            {
                Assert.Equal((int)Pcre2ErrorCode.Depthlimit, result.NativeReturnCode);
            }
            else if (options.Limits.MaxWorkspaceBytes == 0)
            {
                Assert.Equal((int)Pcre2ErrorCode.Heaplimit, result.NativeReturnCode);
            }
            else
            {
                AssertBadOffset(result);
            }

            Assert.Equal(new(true, 7, 9), pairs[0]);
            Assert.Equal(0, state.Budget.WorkConsumed);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new Pcre2MatchOptions { Limits = new(), CancellationToken = cancellation.Token };
        var cancelledState = Pcre2KernelOperation.CreateRunState(buffers, Pcre2OperationLimits.Resolve(cancelled.Limits, default), cancelled);
        Assert.Equal(Pcre2MatchStatus.Cancelled, Direct(executor, domain, cancelled, ref cancelledState, callback, pairs).Status);
        var timeout = new Pcre2MatchOptions { Limits = new(), Timeout = TimeSpan.FromTicks(1) };
        var timedState = Pcre2KernelOperation.CreateRunState(buffers, Pcre2OperationLimits.Resolve(timeout.Limits, default), timeout);
        Thread.Sleep(1);
        Assert.Equal(Pcre2MatchStatus.TimedOut, Direct(executor, domain, timeout, ref timedState, callback, pairs).Status);
        Assert.Equal(new(true, 7, 9), pairs[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PreparedResourceAndLifetimePrecedenceIsRetainedForBadOffsets(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("abc", domain), RuntimeFixture.Capacity);
        Pcre2OffsetPair[] pairs = [new(true, 7, 9)];
        var zero = new Pcre2MatchOptions { Limits = new(maxWork: 0) };
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, RuntimeFixture.Match(workspace, domain, "abc", pairs, zero, 4).NativeReturnCode);
        var heap = new Pcre2MatchOptions { Limits = new(maxWorkspaceBytes: workspace.ReservedBytes - 1) };
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, RuntimeFixture.Match(workspace, domain, "abc", pairs, heap, 4).NativeReturnCode);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(Pcre2MatchStatus.Cancelled, RuntimeFixture.Match(workspace, domain, "abc", pairs,
            zero with { CancellationToken = cts.Token }, 4).Status);
        Assert.Equal(new(true, 7, 9), pairs[0]);
        Assert.True(workspace.IsIdle);
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "abc", pairs).Status);
    }

    private static Pcre2MatchResult Direct(Pcre2Executor executor, int domain, Pcre2MatchOptions options,
        ref RunState state, Pcre2OffsetPair[] callback, Pcre2OffsetPair[] pairs) => domain < 2
        ? executor.Match("abc"u8, 4, options, null, RuntimeFixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, callback, pairs)
        : executor.Match("abc", 4, options, null, RuntimeFixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, callback, pairs);

    private static void AssertBadOffset(Pcre2MatchResult result)
    {
        Assert.Equal(Pcre2MatchStatus.Error, result.Status);
        Assert.Equal(-33, result.NativeReturnCode);
        Assert.Equal(0, result.StartChar);
        Assert.Equal(-1, result.MarkIndex);
        Assert.Equal(default, result.Failure);
    }
}
