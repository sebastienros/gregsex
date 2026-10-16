using GregsEx.Compilation.Core;
using GregsEx.Contracts;
using Xunit;

namespace GregsEx.Tests.Integration.Core;

public class CoreLifetimeTests
{
    [Fact]
    public void DestinationAdmissionPrecedesCapacityEncodingAndCancellation()
    {
        var p = RegexPattern.Parse("(a)", new RegularCoreProfile());
        using var w = p.CreateWorkspace(new());
        RegexCapture[] shortOutput = [new(true, 7, 8)];
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, w.FindCaptures(['\uD800'], 0,
            shortOutput, out var range, new() { CancellationToken = cancellation.Token }));
        Assert.Equal(default, range);
        Assert.Equal(new(true, 7, 8), shortOutput[0]);
        Assert.Equal(new(RegexExecutionFailureCode.DestinationTooSmall, 0, 0, 2), w.LastFailure);
        RegexCapture[] output = [new(true, 7, 8), new(true, 7, 8), new(true, 7, 8)];
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures(['\uD800'], 0, output, out range));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        Assert.Equal(default, output[0]);
        Assert.Equal(default, output[1]);
        Assert.Equal(new(true, 7, 8), output[2]);
        w.Reserve(p.CaptureCapacity);
        Assert.Equal(RegexSearchStatus.Cancelled, w.FindCaptures("", 0, output, out range, new() { CancellationToken = cancellation.Token }));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, w.LastFailure.Code);
        Assert.Equal(0, w.LastFailure.WorkConsumed);
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("a", 0, output, out range));
        Assert.Equal(default, w.LastFailure);
    }

    [Fact]
    public void NoGrowthHasOperationSpecificAdmissionAndExplicitReserve()
    {
        var p = RegexPattern.Parse("(a)(b)(c)", new RegularCoreProfile());
        using var w = p.CreateWorkspace(new(p.BooleanCapacity));
        Assert.Equal(0, w.Capacity.CaptureSlots);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("abc"));
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.Find("abc", 0, out var range));
        Assert.Equal(default, range);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        w.Reserve(p.RangeCapacity);
        Assert.Equal(RegexSearchStatus.Match, w.Find("abc", 0, out range));
        w.Reserve(p.CaptureCapacity);
        var before = w.Capacity;
        w.Reserve(default);
        Assert.Equal(before, w.Capacity);
        var groups = new RegexCapture[4];
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("abc", 0, groups, out _));
    }

    [Fact]
    public void BooleanConvenienceDoesNotRequireFullCaptureScratch()
    {
        var source = string.Concat(Enumerable.Repeat("(a)", 32));
        var p = RegexPattern.Parse(source, new RegularCoreProfile(), new(defaultMatchLimits: new(maxWorkspaceBytes: 4096)));
        Assert.True(p.CaptureCapacity.RequiredBytes + RegexWorkspace.MetadataBytes > 4096);
        Assert.True(p.IsMatch(new string('a', 32)));
        using var w = p.CreateWorkspace(new(p.BooleanCapacity));
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch(new string('a', 32)));
        Assert.Throws<RegexExecutionException>(() => p.CreateWorkspace());
    }

    [Fact]
    public void GrowthAndReserveFailuresAreTransactionalAndRecoveryIsUsable()
    {
        var p = RegexPattern.Parse("(a)", new RegularCoreProfile(), new(defaultMatchLimits: new(maxWorkspaceBytes: 1000)));
        using var w = p.CreateWorkspace(new(p.BooleanCapacity, allowGrowth: true));
        var before = w.Capacity;
        var failure = Assert.Throws<RegexExecutionException>(() => w.Reserve(new(int.MaxValue, int.MaxValue, int.MaxValue)));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, failure.Failure.Code);
        Assert.Equal(before, w.Capacity);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("a"));
        var groups = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("a", 0, groups, out _));
        Assert.True(w.Capacity.Contains(p.CaptureCapacity));
        var after = w.Capacity;
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.IsMatch("a", options: new() { Limits = new(maxWorkspaceBytes: 224) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, w.LastFailure.Code);
        Assert.Equal(after, w.Capacity);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("a"));
    }

    [Fact]
    public void ReplacementPeakIsAdmittedBeforeAllocation()
    {
        var p = RegexPattern.Parse("a", new RegularCoreProfile(), new(defaultMatchLimits: new(maxWorkspaceBytes: 700)));
        using var w = p.CreateWorkspace(new(new(10, 10, 10)));
        var before = w.Capacity;
        // New retained size 584 fits, but old 344 + three replacements 456 exceeds 700.
        Assert.Throws<RegexExecutionException>(() => w.Reserve(new(30, 30, 30)));
        Assert.Equal(before, w.Capacity);
        Assert.Throws<RegexExecutionException>(() => RegexPattern.Parse("", new RegularCoreProfile(),
            new(defaultMatchLimits: new(maxWorkspaceBytes: 223))).CreateWorkspace(new()));
    }

    [Fact]
    public void GrowthMemoryFailureLeavesBuffersAndClearsOnlyLogicalOutput()
    {
        var p = RegexPattern.Parse("(a)", new RegularCoreProfile(), new(defaultMatchLimits: new(maxWorkspaceBytes: 400)));
        using var w = p.CreateWorkspace(new(p.BooleanCapacity, allowGrowth: true));
        var before = w.Capacity;
        RegexCapture[] groups = [new(true, 1, 1), new(true, 1, 1), new(true, 9, 9)];
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.FindCaptures("a", 0, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, w.LastFailure.Code);
        Assert.Equal(before, w.Capacity);
        Assert.Equal(default, groups[0]);
        Assert.Equal(default, groups[1]);
        Assert.Equal(new(true, 9, 9), groups[2]);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("a"));
    }

    [Fact]
    public void ArgumentAndDisposedPathsReleaseExclusiveAdmission()
    {
        var p = RegexPattern.Parse("a", new RegularCoreProfile());
        var w = p.CreateWorkspace();
        Assert.Throws<ArgumentException>(() => w.IsMatch("a"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Find("a", -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Find("a", 2, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.IsMatch("a", options: new() { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.IsMatch("a", options: new() { Limits = new(10_000_001) }));
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch("a"));
        w.Dispose();
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.IsMatch("a"));
        Assert.Throws<ObjectDisposedException>(() => w.Reserve(default));
        using var fresh = p.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.Match, fresh.IsMatch("a"));
    }

    [Fact]
    public void EverySmallWorkFailureClearsOutputAndCanRecover()
    {
        var p = RegexPattern.Parse("((a)|b)*c", new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        var groups = new RegexCapture[4];
        var passed = false;
        for (var budget = 1; budget < 3000; budget++)
        {
            Array.Fill(groups, new(true, 99, 99));
            var status = w.FindCaptures("ababc", 0, groups, out var range, new() { Limits = new(budget) });
            if (status == RegexSearchStatus.Match)
            { passed = true; break; }
            Assert.Equal(RegexSearchStatus.LimitExceeded, status);
            Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, w.LastFailure.Code);
            Assert.True(w.LastFailure.WorkConsumed <= budget);
            Assert.Equal(default, range);
            Assert.All(groups[..3], g => Assert.Equal(default, g));
            Assert.Equal(new(true, 99, 99), groups[3]);
            Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("ababc", 0, groups, out _));
        }
        Assert.True(passed);
    }

    [Fact]
    public void CancellationDeadlineEncodingAndConvenienceFailuresRemainDistinct()
    {
        var p = RegexPattern.Parse("", new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, w.IsMatch("", options: new() { CancellationToken = token.Token, Timeout = TimeSpan.FromTicks(1) }));
        Assert.Equal(RegexSearchStatus.TimedOut, w.IsMatch("", options: new() { Timeout = TimeSpan.FromTicks(1) }));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, w.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.InvalidInput, w.IsMatch(['a', '\uD800']));
        Assert.Equal(1, w.LastFailure.Offset);
        var e = Assert.Throws<RegexExecutionException>(() => p.IsMatch(['a', '\uD800']));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, e.Failure.Code);
        Assert.Equal(RegexSearchStatus.Match, w.IsMatch(""));
        Assert.Equal(default, w.LastFailure);
    }

    [Fact]
    public async Task SeparateWorkspacesCanUseOnePatternConcurrently()
    {
        var p = RegexPattern.Parse("(ab|a)+c", new RegularCoreProfile());
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            using var w = p.CreateWorkspace();
            var groups = new RegexCapture[2];
            for (var i = 0; i < 200; i++)
            { Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("xaababc", 0, groups, out var r)); Assert.Equal(new(1, 6), r); }
        })));
    }

    [Fact]
    public async Task ConcurrentReentrantReserveAndDisposeAreRejectedDuringActualDispatch()
    {
        var profile = new RegularCoreProfile();
        var options = new RegexCompileOptions();
        Assert.True(ProfileDescriptor.TryCreate(profile, options.InputKind, out var descriptor, out _));
        var budget = new CompileBudget(options.Limits);
        Assert.True(new CoreCompiler().TryCompile("a", descriptor!, ref budget, out var program, out _));
        using var fake = new BlockingExecutor();
        var p = new RegexPattern(program!, profile, options, fake);
        using var w = p.CreateWorkspace();
        fake.Reentrant = () => Assert.Throws<InvalidOperationException>(() => w.IsMatch("a"));
        var running = Task.Run(() => w.IsMatch("a"));
        try
        {
            Assert.True(fake.Entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.Throws<InvalidOperationException>(() => w.Find("a", 0, out _));
            Assert.Throws<InvalidOperationException>(() => w.Reserve(default));
            Assert.Throws<InvalidOperationException>(w.Dispose);
        }
        finally { fake.Release.Set(); }
        Assert.Equal(RegexSearchStatus.NoMatch, await running);
        Assert.Equal(RegexSearchStatus.NoMatch, w.IsMatch("a"));
    }

    // This controlled nonshipping executor makes overlap deterministic. It verifies only
    // the Runtime admission boundary, never counts as a matcher correctness witness.
    private sealed class BlockingExecutor : IRegularExecutor, IDisposable
    {
        internal readonly ManualResetEventSlim Entered = new(); internal readonly ManualResetEventSlim Release = new();
        internal Action? Reentrant;
        public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation) => default;
        public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt, SearchOperation operation,
            ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
            where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
        {
            Reentrant?.Invoke();
            Entered.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException();
            }

            match = default;
            return RegexSearchStatus.NoMatch;
        }
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }

    [Fact]
    public void CapacityQueryOverflowIsMappedBeforeAnyAllocation()
    {
        var profile = new RegularCoreProfile();
        var options = new RegexCompileOptions();
        Assert.True(ProfileDescriptor.TryCreate(profile, options.InputKind, out var descriptor, out _));
        var budget = new CompileBudget(options.Limits);
        Assert.True(new CoreCompiler().TryCompile("", descriptor!, ref budget, out var program, out _));
        var p = new RegexPattern(program!, profile, options, new OverflowExecutor());
        using var w = p.CreateWorkspace(new());
        Assert.Equal(RegexSearchStatus.LimitExceeded, w.IsMatch(""));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, w.LastFailure.Code);
        Assert.Throws<RegexExecutionException>(() => p.CreateWorkspace());
    }
    private sealed class OverflowExecutor : IRegularExecutor
    {
        public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation) => throw new OverflowException();
        public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt, SearchOperation operation,
            ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
            where TUnit : unmanaged where TReader : struct, ITextReader<TUnit> => throw new InvalidOperationException("Must not dispatch.");
    }
}
