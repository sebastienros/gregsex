using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Execution.Posix;
using Xunit;
using static GregsEx.Tests.Execution.Posix.PlanFixture;

namespace GregsEx.Tests.Execution.Posix;

public sealed class ExecutorTests
{
    [Fact]
    public void WholeLongestIsIndependentOfAlternativeOrderAndEarliestStartWins()
    {
        foreach (var t in new[] { A(L('a'), S(L('a'), L('a'))), A(S(L('a'), L('a')), L('a')) })
        {
            var e = Executor(t);
            Assert.Equal(RegexSearchStatus.Match, Search(e, "xaa"u8, new RegexCapture[1], out var m, out _));
            Assert.Equal(new(1, 2), m);
            Assert.Equal(RegexSearchStatus.Match, Search(e, "axaaxx"u8, new RegexCapture[1], out m, out _));
            Assert.Equal(new(0, 1), m);
        }
    }
    [Fact]
    public void EnclosingEarlierCaptureWinsAndEmptyParticipates()
    {
        var e = Executor(S(C(A(L('a'), S(L('a'), L('a'))), 1), C(R(L('a'), 0, 1), 2)), parents: [-1, 0, 0]);
        var groups = new RegexCapture[4];
        groups[3] = new(true, 77, 88);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aa"u8, groups, out var m, out _));
        Assert.Equal(new(0, 2), m);
        Assert.Equal(new(true, 0, 2), groups[1]);
        Assert.Equal(new(true, 2, 0), groups[2]);
        Assert.Equal(new(true, 77, 88), groups[3]);
    }
    [Fact]
    public void ConcatenationAssociationCannotCreateArtificialPrefixPriority()
    {
        foreach (var leftAssociated in new[] { false, true })
        {
            Tree a = C(A(L('a'), S(L('a'), L('a'))), 1),
                b = R(C(S(L('a'), L('b')), 2)), c = C(R(new(PosixNodeKind.Any)), 3);
            var e = Executor(leftAssociated ? S(S(a, b), c) : S(a, S(b, c)), parents: [-1, 0, 0, 0]);
            var g = new RegexCapture[4];
            Assert.Equal(RegexSearchStatus.Match, Search(e, "aab"u8, g, out var m, out _));
            Assert.Equal(new(0, 3), m);
            Assert.Equal(new(true, 0, 2), g[1]);
            Assert.Equal(default, g[2]);
            Assert.Equal(new(true, 2, 1), g[3]);
        }
    }
    [Fact]
    public void ContractAnonymousBranchTieRetainsEarlierLiteralParticipation()
    {
        // Contract-derived GregsExDisposition, not a uniquely portable native
        // outcome: anonymous lexical priority in a|(a) and (a|(a)).
        var e = Executor(A(L('a'), C(L('a'), 1)), parents: [-1, 0]);
        var g = new RegexCapture[3];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "a"u8, g, out var m, out _));
        Assert.Equal(new(0, 1), m);
        Assert.Equal(default, g[1]);
        e = Executor(C(A(L('a'), C(L('a'), 2)), 1), parents: [-1, 0, 1]);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "a"u8, g, out m, out _));
        Assert.Equal(new(0, 1), m);
        Assert.Equal(new(true, 0, 1), g[1]);
        Assert.Equal(default, g[2]);
    }
    [Fact]
    public void OccurrenceHistoryBeatsTheFinalCaptureVector()
    {
        // On aaa the first occurrence of (a|aa) wins aa, leaving final a.
        // Comparing only the final vector would choose a followed by aa.
        var e = Executor(R(C(A(L('a'), S(L('a'), L('a'))), 1), 1), parents: [-1, 0]);
        var groups = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aaa"u8, groups, out var m, out _));
        Assert.Equal(new(0, 3), m);
        Assert.Equal(new(true, 2, 1), groups[1]);
    }
    [Fact]
    public void NestedReportingDiscardsEscapedCapturesAndAbsentBranchGroups()
    {
        var e = Executor(R(C(A(C(L('a'), 2), L('b')), 1), 1), parents: [-1, 0, 1]);
        var g = new RegexCapture[3];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "ab"u8, g, out _, out _));
        Assert.Equal(new(true, 1, 1), g[1]);
        Assert.Equal(default, g[2]);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "ba"u8, g, out _, out _));
        Assert.Equal(new(true, 1, 1), g[2]);
        e = Executor(A(C(L('a'), 1), L('b')), parents: [-1, 0]);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "b"u8, g, out _, out _));
        Assert.Equal(default, g[1]);
    }
    [Theory]
    [InlineData("aa", 0, 2)]
    [InlineData("b", 0, 0)]
    [InlineData("", 0, 0)]
    public void NullableRepeatHasNoFabricatedEmptyTail(string input, int index, int length)
    {
        var e = Executor(R(C(R(L('a')), 1)), parents: [-1, 0]);
        var g = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Search(e, System.Text.Encoding.ASCII.GetBytes(input), g, out _, out _));
        Assert.Equal(new(true, index, length), g[1]);
    }
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 3, true)]
    [InlineData(1, 3, true)]
    [InlineData(3, 3, true)]
    [InlineData(255, 255, true)]
    public void FiniteMandatoryEmptyIterationsAreBounded(int min, int max, bool participates)
    {
        var e = Executor(R(C(E(), 1), min, max), parents: [-1, 0]);
        var g = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Search(e, ""u8, g, out var m, out _));
        Assert.Equal(new(0, 0), m);
        Assert.Equal(participates ? new(true, 0, 0) : default, g[1]);
    }
    [Theory]
    [InlineData("abab", true, 4)]
    [InlineData("aba", false, 0)]
    [InlineData("xxabab", true, 4)]
    public void BreReferencesVerifyFeasibility(string input, bool success, int length)
    {
        var e = Executor(S(C(S(L('a'), L('b')), 1), B(1)), parents: [-1, 0], dialect: PosixDialect.Basic);
        Assert.Equal(success ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
            Search(e, System.Text.Encoding.ASCII.GetBytes(input), new RegexCapture[2], out var m, out _));
        Assert.Equal(length, m.Length);
    }
    [Fact]
    public void ReferenceUsesLastMatchedStringAndParentScope()
    {
        var e = Executor(S(R(C(L('a'), 1), 1), B(1)), parents: [-1, 0], dialect: PosixDialect.Basic);
        var g = new RegexCapture[3];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aaa"u8, g, out var m, out _));
        Assert.Equal(new(0, 3), m);
        Assert.Equal(new(true, 1, 1), g[1]);
        // Frozen XBD9.3.6 witness: \(a\(b\)*\)*\2 on abab fails.
        e = Executor(S(R(C(S(L('a'), R(C(L('b'), 2))), 1)), B(2)), parents: [-1, 0, 1], dialect: PosixDialect.Basic);
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "abab"u8, g, out _, out _));
        Assert.Equal(RegexSearchStatus.Match, Search(e, "abb"u8, g, out m, out _));
        Assert.Equal(new(0, 3), m);
    }
    [Fact]
    public void ReferenceEmptySucceedsAndAbsentFails()
    {
        var e = Executor(S(C(R(L('a')), 1), B(1)), parents: [-1, 0], dialect: PosixDialect.Basic);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "b"u8, new RegexCapture[2], out var m, out _));
        Assert.Equal(new(0, 0), m);
        e = Executor(S(R(C(L('a'), 1), 0, 1), B(1)), parents: [-1, 0], dialect: PosixDialect.Basic);
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "b"u8, new RegexCapture[2], out _, out _));
    }
    [Fact]
    public void ReferenceRepetitionAndGreedyCandidateRetriesShareOneBudget()
    {
        var e = Executor(S(C(R(L('a'), 1), 1), R(B(1), 2, 3)), parents: [-1, 0], dialect: PosixDialect.Basic);
        var g = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aaaaaa"u8, g, out var m, out _));
        Assert.Equal(new(0, 6), m);
        Assert.Equal(new(true, 0, 2), g[1]);
        Assert.Equal(RegexSearchStatus.LimitExceeded, Search(e, "xxxxaaaaaa"u8, g, out m, out var f, limits: new(maxWork: 90)));
        Assert.Equal(default, m);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, f.Code);
    }
    [Fact]
    public void EmptyParticipationOutranksAbsentAlternativeAndAnonymousRepeatHasPriority()
    {
        var e = Executor(A(C(R(L('a')), 1), E()), parents: [-1, 0]);
        var g = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "b"u8, g, out _, out _));
        Assert.Equal(new(true, 0, 0), g[1]);
        e = Executor(S(R(L('a')), C(R(L('a')), 1)), parents: [-1, 0]);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aaa"u8, g, out _, out _));
        Assert.Equal(new(true, 3, 0), g[1]);
    }
    [Fact]
    public void CaptureCountIsNotRestrictedToNineAndFullOperationAgreementHolds()
    {
        var tree = C(L('a'), 1);
        for (var g = 2; g <= 12; g++)
        {
            tree = S(tree, C(L('a'), g));
        }

        var e = Executor(tree, parents: [-1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var groups = new RegexCapture[13];
        var storage = new Storage(e);
        foreach (var operation in Enum.GetValues<SearchOperation>())
        {
            Assert.Equal(RegexSearchStatus.Match, Search(e, "xaaaaaaaaaaaa"u8, groups, out var m, out _, operation, storage: storage));
            Assert.Equal(new(1, 12), m);
            Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "bbb"u8, groups, out m, out _, operation, storage: storage));
            Assert.Equal(default, m);
        }
        Assert.Equal(RegexSearchStatus.Match, Search(e, "xaaaaaaaaaaaa"u8, groups, out _, out _, storage: storage));
        for (var g = 1; g <= 12; g++)
        {
            Assert.Equal(new(true, g, 1), groups[g]);
        }
    }
    [Fact]
    public void IgnoreCaseIncludesReferencesAndLeavesOpaqueBytesAlone()
    {
        var e = Executor(S(C(L('a'), 1), B(1)), PosixCompileFlags.IgnoreCase, [-1, 0], PosixDialect.Basic);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "Aa"u8, new RegexCapture[2], out _, out _));
        e = Executor(L(200), PosixCompileFlags.IgnoreCase);
        Assert.Equal(RegexSearchStatus.Match, Search(e, [200], new RegexCapture[1], out _, out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, [232], new RegexCapture[1], out _, out _));
    }
    [Fact]
    public void AnchorsSeeOriginalInputAndInternalLfIgnoresEndpointSuppression()
    {
        var e = Executor(S(Anchor(false), S(L('a'), Anchor(true))), PosixCompileFlags.Newline);
        var g = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.Match, Search(e, "x\na\ny"u8, g, out var m, out _, start: 2, flags: PosixExecutionFlags.NotBol | PosixExecutionFlags.NotEol));
        Assert.Equal(new(2, 1), m);
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "xa"u8, g, out _, out _, start: 1));
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "a"u8, g, out _, out _, flags: PosixExecutionFlags.NotBol));
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "a\r"u8, g, out _, out _));
        e = Executor(S(L('a'), Anchor(true)));
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "a\n"u8, g, out _, out _));
    }
    [Fact]
    public void DotNewlineAndNormalizedClassesRemainDistinct()
    {
        var g = new RegexCapture[1];
        var e = Executor(new(PosixNodeKind.Any), PosixCompileFlags.Newline);
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "\n"u8, g, out _, out _));
        Assert.Equal(RegexSearchStatus.Match, Search(e, "\r"u8, g, out _, out _));
        e = Executor(new(PosixNodeKind.Class, Value: 0), PosixCompileFlags.Newline | PosixCompileFlags.IgnoreCase,
            sets: [new(1UL << 10, 0, 0, 0)]);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "\n"u8, g, out _, out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Search(e, "A"u8, g, out _, out _));
    }
    [Fact]
    public void LogicalFrameAndHistoryCeilingsApplyWithOversizedArraysAndReuse()
    {
        var e = Executor(R(C(L('a'), 1)), parents: [-1, 0]);
        var g = new RegexCapture[3];
        g[2] = new(true, 77, 88);
        foreach (var capacity in new[] { new PosixExecutionCapacity(3, 200), new PosixExecutionCapacity(200, 3) })
        {
            var storage = new Storage(e, capacity, 2000);
            Assert.Equal(RegexSearchStatus.LimitExceeded, Search(e, "aaaa"u8, g, out var m, out var f, capacity: capacity, storage: storage));
            Assert.Equal(default, m);
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, f.Code);
            Assert.Equal(default, g[0]);
            Assert.Equal(default, g[1]);
            Assert.Equal(new(true, 77, 88), g[2]);
            Assert.Equal(RegexSearchStatus.Match, Search(e, "aaaa"u8, g, out m, out _, capacity: new(200, 200), storage: storage));
            Assert.Equal(new(0, 4), m);
        }
    }
    [Fact]
    public void BudgetExhaustionCannotPublishEarlierPartialWinnerOrSkipCandidate()
    {
        var e = Executor(A(L('a'), R(L('a'), 1)));
        var g = new RegexCapture[1];
        var storage = new Storage(e);
        Assert.Equal(RegexSearchStatus.LimitExceeded, Search(e, "aaaa"u8, g, out var m, out var f, storage: storage, limits: new(maxWork: 35)));
        Assert.Equal(default, m);
        Assert.Equal(default, g[0]);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, f.Code);
        Assert.Equal(RegexSearchStatus.Match, Search(e, "aaaa"u8, g, out m, out _, storage: storage));
        Assert.Equal(new(0, 4), m);
    }
    [Fact]
    public void PollAndFullSubjectNulValidationPrecedeImmediateMatch()
    {
        var e = Executor(E());
        var g = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.InvalidInput, Search(e, [97, 0], g, out _, out var f));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, f.Code);
        Assert.Equal(1, f.Offset);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, Search(e, [], g, out _, out _, options: new() { CancellationToken = cts.Token }));
        Assert.Equal(RegexSearchStatus.TimedOut, Search(e, [], g, out _, out _, options: new() { Timeout = TimeSpan.FromTicks(1) }));
    }
    [Fact]
    public void DestinationPrecedenceCapacityAndCheckedFormulas()
    {
        var e = Executor(C(L('a'), 1), parents: [-1, 0]);
        var g = new RegexCapture[1];
        g[0] = new(true, 1, 2);
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, Search(e, [0], g, out _, out var f));
        Assert.Equal(0, f.WorkConsumed);
        Assert.Equal(new(true, 1, 2), g[0]);
        var state = new RunState(new([], [], []), new());
        Assert.Equal(RegexSearchStatus.LimitExceeded, e.Search([0], 0, SearchOperation.Range, 0, PosixExecutionCapacity.Default, ref state, [], out _));
        Assert.Equal(0, state.Failure.WorkConsumed);
        Assert.Throws<OverflowException>(() => e.GetRequiredCapacity(SearchOperation.Range, new(int.MaxValue, int.MaxValue)));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.GetRequiredCapacity(SearchOperation.Range, default));
    }
    [Fact]
    public void ProviderIsVerifiedBeforeBindingAndPlanMemoryIsAdmitted()
    {
        var e = Executor(L('a'));
        var locale = new Locale();
        locale.Fold[65] = 65;
        var b = new CompileBudget(new());
        Assert.False(new PosixExecutorFactory().TryCreate(e.Program, locale, ref b, out _, out var d));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, d.Code);
        b = new(new(maxPlanBytes: e.Program.RetainedBytes));
        Assert.False(new PosixExecutorFactory().TryCreate(e.Program, new Locale(), ref b, out _, out d));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Code);
    }
    [Fact]
    public async Task ImmutableExecutorSupportsIndependentConcurrentStorage()
    {
        var e = Executor(R(C(A(L('a'), S(L('a'), L('a'))), 1), 1), parents: [-1, 0]);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            var storage = new Storage(e);
            var g = new RegexCapture[2];
            for (var i = 0; i < 12; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, Search(e, "aaa"u8, g, out var m, out _, storage: storage));
                Assert.Equal(new(0, 3), m);
                Assert.Equal(new(true, 2, 1), g[1]);
            }
        })));
    }
}
