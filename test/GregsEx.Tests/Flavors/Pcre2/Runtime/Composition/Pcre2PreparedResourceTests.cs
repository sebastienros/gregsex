using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

public class Pcre2PreparedResourceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ZeroLimitsAndEntryCancellationPrecedeTextAndPreserveDestination(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("", domain), RuntimeFixture.Capacity);
        var sentinel = new Pcre2OffsetPair(true, 7, 9);
        var pairs = new[] { sentinel };
        var zero = new Pcre2MatchOptions { Limits = new(maxWork: 0, maxDepth: 0, maxWorkspaceBytes: 0) };
        var result = RuntimeFixture.Match(workspace, domain, "", pairs, zero);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, result.NativeReturnCode);
        Assert.Equal(sentinel, pairs[0]);
        Assert.Equal(0, workspace.LastWork);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        result = RuntimeFixture.Match(workspace, domain, "", pairs, zero with { CancellationToken = cancelled.Token });
        Assert.Equal(Pcre2MatchStatus.Cancelled, result.Status);
        Assert.Equal(sentinel, pairs[0]);
        var expired = workspace.BeginIteration(options: zero with { Timeout = TimeSpan.FromTicks(1) });
        try
        {
            // Start the actual whole-operation budget before allowing its finite
            // deadline to expire; do not assume an immediate poll takes100ns.
            Thread.Sleep(1);
            result = domain < 2 ? expired.Next(ReadOnlySpan<byte>.Empty, pairs) : expired.Next(ReadOnlySpan<char>.Empty, pairs);
            Assert.Equal(Pcre2MatchStatus.TimedOut, result.Status);
        }
        finally { expired.Dispose(); }
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeWorkDepthHeapAndCaptureDenialsRemainDistinct(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)+b", domain), RuntimeFixture.Capacity);
        var sentinel = new Pcre2OffsetPair(true, 9, 10);
        var pairs = new[] { sentinel, sentinel };
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, RuntimeFixture.Match(workspace, domain, "ab", pairs, new() { Limits = new(maxWork: 1) }).NativeReturnCode);
        Assert.Equal((int)Pcre2ErrorCode.Depthlimit, RuntimeFixture.Match(workspace, domain, "ab", pairs, new() { Limits = new(maxDepth: 0) }).NativeReturnCode);
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, RuntimeFixture.Match(workspace, domain, "ab", pairs,
            new() { Limits = new(maxWorkspaceBytes: workspace.ReservedBytes - 1) }).NativeReturnCode);
        Assert.Equal(0, workspace.LastWork);
        Assert.All(pairs, p => Assert.Equal(sentinel, p));
        var events = RuntimeFixture.Match(workspace, domain, "ab", pairs, new() { Limits = new(maxCaptureEvents: 0) });
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, events.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, events.Failure.Code);
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(workspace, domain, "ab", pairs).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PatternLimitsReducePreparedOperationBeforeAnyNativeWork(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(*LIMIT_MATCH=0)", domain), RuntimeFixture.Capacity);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, RuntimeFixture.Match(workspace, domain, "", new Pcre2OffsetPair[1]).NativeReturnCode);
        Assert.Equal(0, workspace.LastWork);
        using var heap = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(*LIMIT_HEAP=1)a", domain), RuntimeFixture.Capacity);
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, RuntimeFixture.Match(heap, domain, "a", new Pcre2OffsetPair[1]).NativeReturnCode);
        Assert.Equal(0, heap.LastWork);
    }

    [Fact]
    public void CallbackStagingIsIncludedEvenWhenSearchHasNoCallbacks()
    {
        var executor = RuntimeFixture.Compile("(a)(b)(c)(d)(e)");
        var integers = RuntimeFixture.Capacity.Required(executor.Program.GroupCount).RequiredBytes;
        var final = Pcre2PreparedWorkspace.MetadataBytes + executor.GetRequiredCapacity(RuntimeFixture.Capacity).RequiredBytes;
        Assert.True(final > integers + Pcre2PreparedWorkspace.MetadataBytes);
        Assert.Throws<RegexExecutionException>(() => new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity,
            new(maxWorkspaceBytes: integers + Pcre2PreparedWorkspace.MetadataBytes)));
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity, new(maxWorkspaceBytes: final));
        Assert.Equal(final, workspace.ReservedBytes);
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("abcde", 0, new Pcre2OffsetPair[6]).Status);
    }

    [Fact]
    public void ReserveAdmitsPeakAndPublishesTransactionally()
    {
        var executor = RuntimeFixture.Compile("a");
        var small = new Pcre2ExecutionCapacity(16, 64, 8, 32);
        var large = new Pcre2ExecutionCapacity(32, 128, 16, 64);
        var largeFinal = Pcre2PreparedWorkspace.MetadataBytes + large.RequiredStorage(1).RequiredBytes;
        using var bounded = new Pcre2PreparedWorkspace(executor, small, new(maxWorkspaceBytes: largeFinal));
        var original = bounded.Capacity;
        var bytes = bounded.ReservedBytes;
        Assert.Throws<RegexExecutionException>(() => bounded.Reserve(large));
        Assert.Equal(original, bounded.Capacity);
        Assert.Equal(bytes, bounded.ReservedBytes);
        Assert.Equal(Pcre2MatchStatus.Match, bounded.Find("a", 0, out _).Status);
        using var workspace = new Pcre2PreparedWorkspace(executor, small);
        workspace.Reserve(large);
        Assert.Equal(large, workspace.Capacity);
        Assert.Equal(largeFinal, workspace.ReservedBytes);
        workspace.Reserve(small);
        Assert.Equal(large, workspace.Capacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.Reserve(default));
    }

    [Fact]
    public void ReentryReserveAndDisposeAreRejectedAndUserExceptionsReleaseOwnership()
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(?C1)(a)"), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[2];
        var calls = 0;
        var callbacks = new Pcre2CharCallbacks(Match: (in _, __) =>
        {
            calls++;
            Assert.Throws<InvalidOperationException>(() => workspace.Match("a", 0, pairs));
            Assert.Throws<InvalidOperationException>(() => workspace.Reserve(RuntimeFixture.Capacity));
            Assert.Throws<InvalidOperationException>(() => workspace.Dispose());
            throw new ApplicationException("user");
        });
        Assert.Throws<ApplicationException>(() => workspace.Match("a", 0, pairs, callbacks: callbacks));
        Assert.True(workspace.IsIdle);
        Assert.Equal(1, calls);
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a", 0, pairs).Status);
        Assert.Throws<ApplicationException>(() => workspace.Substitute("a", 0, "x", new char[8], callbacks: callbacks));
        Assert.True(workspace.IsIdle);
        Assert.Equal(1, workspace.Substitute("a", 0, "x", new char[8]).NativeReturnCode);
        var session = workspace.BeginIteration(chars: callbacks);
        try
        { session.Next("a", pairs); Assert.Fail("User exception expected."); }
        catch (ApplicationException) { }
        finally { session.Dispose(); }
        Assert.True(workspace.IsIdle);
        workspace.Dispose();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.Match("a", 0, pairs));
        Assert.Throws<ObjectDisposedException>(() => workspace.Reserve(RuntimeFixture.Capacity));
    }

    [Fact]
    public async Task ConcurrentUseRejectedAndIndependentWorkspacesShareThePlan()
    {
        var executor = RuntimeFixture.Compile("(?C1)(a)");
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cancellation = TestContext.Current.CancellationToken;
        var callbacks = new Pcre2CharCallbacks(Match: (in _, __) =>
        { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10), cancellation)) { throw new TimeoutException(); } return 0; });
        var operation = Task.Run(() => workspace.Match("a", 0, new Pcre2OffsetPair[2], callbacks: callbacks), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => workspace.Match("a", 0, new Pcre2OffsetPair[2]));
            using var independent = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
            Assert.Equal(Pcre2MatchStatus.Match, independent.Find("a", 0, out var pair).Status);
            Assert.Equal(new(true, 0, 1), pair);
        }
        finally { release.Set(); }
        Assert.Equal(Pcre2MatchStatus.Match, (await operation).Status);
        Assert.True(workspace.IsIdle);
    }

    [Fact]
    public void InvalidUtfBorrowedMutationAndWidthArgumentsDoNotPoisonReuse()
    {
        using var bytes = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("a", 1), RuntimeFixture.Capacity);
        byte[] input = [97];
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, bytes.Match(input, 0, pairs).Status);
        input[0] = 0x80;
        Assert.Equal(Pcre2MatchStatus.Error, bytes.Match(input, 0, pairs).Status);
        input[0] = 98;
        Assert.Equal(Pcre2MatchStatus.NoMatch, bytes.Match(input, 0, pairs).Status);
        input[0] = 97;
        Assert.Equal(Pcre2MatchStatus.Match, bytes.Match(input, 0, pairs).Status);
        Assert.Throws<ArgumentException>(() => bytes.Match("a", 0, pairs));
        var beforeBadOffset = pairs.ToArray();
        var badOffset = bytes.Match(input, 2, pairs);
        Assert.Equal(Pcre2MatchStatus.Error, badOffset.Status);
        Assert.Equal((int)Pcre2ErrorCode.Badoffset, badOffset.NativeReturnCode);
        Assert.Equal(beforeBadOffset, pairs);
        Assert.Throws<ArgumentOutOfRangeException>(() => bytes.Match(input, 0, pairs, new() { Limits = new(maxWork: bytes.Ceiling.MaxWork + 1) }));
        Assert.True(bytes.IsIdle);
    }

    [Fact]
    public void IterationKeepsOneTotalBudgetAndRejectsCopiedOrAbandonedSessions()
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(@"\G(a)"), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("aaa", 0, pairs).Status);
        var one = workspace.LastWork;
        var session = workspace.BeginIteration(options: new() { Limits = new(maxWork: one * 2 - 1) });
        var copy = session;
        Assert.Equal(Pcre2MatchStatus.Match, session.Next("aaa", pairs).Status);
        var first = session.WorkConsumed;
        try
        { copy.Next("aaa", pairs); Assert.Fail("Stale copy expected."); }
        catch (InvalidOperationException) { }
        try
        { copy.Dispose(); Assert.Fail("Stale copy cannot dispose active owner."); }
        catch (InvalidOperationException) { }
        Assert.False(workspace.IsIdle);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, session.Next("aaa", pairs).Status);
        Assert.True(session.WorkConsumed >= first);
        Assert.True(session.WorkConsumed <= one * 2 - 1);
        Assert.True(workspace.IsIdle);
        session.Dispose();
        var abandoned = workspace.BeginIteration();
        Assert.Throws<InvalidOperationException>(() => workspace.Match("a", 0, pairs));
        abandoned.Dispose();
        Assert.True(workspace.IsIdle);
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a", 0, pairs).Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void NativeUtfCodeUnitAndInvalidFragmentPathsAreReal(int domain)
    {
        using var codeUnit = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(@"\C", domain), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, RuntimeFixture.Match(codeUnit, domain, "😀", pairs).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
        using var fragments = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("a", domain,
            RuntimeFixture.Profile(domain, Pcre2CompileFlags.MatchInvalidUtf)), RuntimeFixture.Capacity);
        var result = domain == 1 ? fragments.Match([0x80, 97], 0, pairs) : fragments.Match("\uD800a", 0, pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 1, 2), pairs[0]);
    }
}
