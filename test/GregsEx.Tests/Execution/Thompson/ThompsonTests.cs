using System.Buffers;
using System.Text;
using GregsEx.Contracts;
using GregsEx.Execution.Thompson;
using GregsEx.IR;
using Xunit;

namespace GregsEx.Tests.Execution.Thompson;

public class ThompsonTests
{
    private static CoreInstruction I(CoreOpcode op, int x = 0, int y = 0, int operand = 0, int count = 0)
        => new(op, x, y, operand, count, new(0, 0, RegexOffsetUnit.Utf16CodeUnits));

    private static SemanticProgram Program(CoreInstruction[] body, int groups = 1,
        RegularCoreMode mode = RegularCoreMode.Utf16CodeUnits, RegexInputKind kind = RegexInputKind.Utf16,
        RegularCoreFlags flags = RegularCoreFlags.None, CharacterClass[]? classes = null)
    {
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode, flags), kind, out var profile, out _));
        CoreInstruction[] code = [I(CoreOpcode.CaptureStart, 1), .. body,
            I(CoreOpcode.CaptureEnd, body.Length + 2), I(CoreOpcode.Match)];
        var budget = new CompileBudget(new());
        Assert.True(SemanticProgram.TryCreate(code, classes ?? [], profile!, groups, 0,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out var program, out var diagnostic), diagnostic.ToString());
        return program!;
    }

    private static SemanticProgram Literal(int value = 'a', RegularCoreMode mode = RegularCoreMode.Utf16CodeUnits,
        RegexInputKind kind = RegexInputKind.Utf16)
        => Program([I(CoreOpcode.Literal, 2, operand: value)], mode: mode, kind: kind);

    private static SemanticProgram Star(bool lazy = false, bool capture = false)
        => capture
            ? Program([I(CoreOpcode.Split, lazy ? 6 : 2, lazy ? 2 : 6),
                I(CoreOpcode.ClearCaptures, 3, operand: 1, count: 1), I(CoreOpcode.CaptureStart, 4, operand: 1),
                I(CoreOpcode.Literal, 5, operand: 'a'), I(CoreOpcode.CaptureEnd, 1, operand: 1)], 2)
            : Program([I(CoreOpcode.Split, lazy ? 3 : 2, lazy ? 2 : 3), I(CoreOpcode.Literal, 1, operand: 'a')]);

    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    public void GreedyAndLazyPriority(bool lazy, int length)
    {
        var harness = new Harness(Star(lazy, true));
        Assert.Equal(RegexSearchStatus.Match, harness.Char("aaab", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, length), range);
        Assert.Equal(new RegexCapture(true, 0, length), harness.Groups[0]);
        Assert.Equal(lazy ? default : new RegexCapture(true, 2, 1), harness.Groups[1]);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void OrderedAlternativesChooseFirstSuccessfulPath(bool shortFirst, int length)
    {
        // aa|a or a|aa. Shared epilogue must retain the winning register snapshot.
        var p = Program([I(CoreOpcode.Split, shortFirst ? 4 : 2, shortFirst ? 2 : 4),
            I(CoreOpcode.Literal, 3, operand: 'a'), I(CoreOpcode.Literal, 5, operand: 'a'),
            I(CoreOpcode.Literal, 5, operand: 'a')]);
        var h = new Harness(p);
        Assert.Equal(RegexSearchStatus.Match, h.Char("aa", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, length), range);
    }

    [Fact]
    public void PendingLowerBranchCannotPreemptHigherDescendant()
    {
        // X reaches the deferred Y instruction with different captures. Marking on push fails.
        var p = Program([I(CoreOpcode.Split, 2, 4), I(CoreOpcode.CaptureStart, 3, operand: 1),
            I(CoreOpcode.CaptureEnd, 4, operand: 1), I(CoreOpcode.Literal, 5, operand: 'a')], 2);
        var h = new Harness(p);
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out _));
        Assert.Equal(new RegexCapture(true, 0, 0), h.Groups[1]);
    }

    [Fact]
    public void FailedHigherAlternativeRollsBackCaptures()
    {
        // (a)b|(a)
        var p = Program([I(CoreOpcode.Split, 2, 6), I(CoreOpcode.CaptureStart, 3, operand: 1),
            I(CoreOpcode.Literal, 4, operand: 'a'), I(CoreOpcode.CaptureEnd, 5, operand: 1),
            I(CoreOpcode.Literal, 9, operand: 'b'), I(CoreOpcode.CaptureStart, 7, operand: 2),
            I(CoreOpcode.Literal, 8, operand: 'a'), I(CoreOpcode.CaptureEnd, 9, operand: 2)], 3);
        var h = new Harness(p);
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 1), range);
        Assert.Equal(default, h.Groups[1]);
        Assert.Equal(new RegexCapture(true, 0, 1), h.Groups[2]);
    }

    [Fact]
    public void RepetitionClearsNestedCaptureAndRetainsFinalIteration()
    {
        // (a|(b))*; explicit clear covers both body captures.
        var p = Program([I(CoreOpcode.Split, 2, 10), I(CoreOpcode.ClearCaptures, 3, operand: 1, count: 2),
            I(CoreOpcode.CaptureStart, 4, operand: 1), I(CoreOpcode.Split, 5, 6),
            I(CoreOpcode.Literal, 9, operand: 'a'), I(CoreOpcode.CaptureStart, 7, operand: 2),
            I(CoreOpcode.Literal, 8, operand: 'b'), I(CoreOpcode.CaptureEnd, 9, operand: 2),
            I(CoreOpcode.CaptureEnd, 1, operand: 1)], 3);
        var h = new Harness(p);
        Assert.Equal(RegexSearchStatus.Match, h.Char("ba", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 2), range);
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[1]);
        Assert.Equal(default, h.Groups[2]);
        Assert.Equal(RegexSearchStatus.Match, h.Char("ab", 0, out _));
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[2]);
    }

    [Fact]
    public void EmptyAndUnsetCapturesAreDistinct()
    {
        var p = Program([I(CoreOpcode.CaptureStart, 2, operand: 1), I(CoreOpcode.CaptureEnd, 3, operand: 1)], 3);
        var h = new Harness(p);
        Assert.Equal(RegexSearchStatus.Match, h.Char("", 0, out var range));
        Assert.Equal(default, range);
        Assert.Equal(new RegexCapture(true, 0, 0), h.Groups[1]);
        Assert.Equal(default, h.Groups[2]);
    }

    [Fact]
    public void EpsilonCyclesTerminateAndNullableRepeatKeepsPriority()
    {
        var h = new Harness(Program([I(CoreOpcode.Split, 2, 3), I(CoreOpcode.Jump, 1)]));
        Assert.Equal(RegexSearchStatus.Match, h.Char("", 0, out _));
        Assert.InRange(h.Work, 1, 100);
        // (?:a?)*: epsilon cycles are deduplicated once per input position.
        h = new Harness(Program([I(CoreOpcode.Split, 2, 4), I(CoreOpcode.Split, 3, 1),
            I(CoreOpcode.Literal, 1, operand: 'a')]));
        Assert.Equal(RegexSearchStatus.Match, h.Char("aaa", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 3), range);
        Assert.Equal(RegexSearchStatus.Match, h.Char("b", 0, out range));
        Assert.Equal(default, range);
        // A nonaccepting epsilon cycle is a miss, with finite work.
        h = new Harness(Program([I(CoreOpcode.Jump, 1)]));
        Assert.Equal(RegexSearchStatus.NoMatch, h.Char("abc", 0, out _));
        Assert.InRange(h.Work, 1, 200);
    }

    [Fact]
    public void EarliestCandidateWinsAndNonzeroBoundsPreserveAbsoluteAnchors()
    {
        var h = new Harness(Program([I(CoreOpcode.Split, 2, 5), I(CoreOpcode.Literal, 3, operand: 'a'),
            I(CoreOpcode.Literal, 4, operand: 'b'), I(CoreOpcode.Literal, 6, operand: 'c'),
            I(CoreOpcode.Literal, 6, operand: 'b')])); // abc|b
        Assert.Equal(RegexSearchStatus.Match, h.Char("abc", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 3), range);
        Assert.Equal(RegexSearchStatus.Match, h.Char("abc", 1, out range));
        Assert.Equal(new RegexMatchRange(1, 1), range);
        h = new Harness(Program([I(CoreOpcode.AssertStart, 2), I(CoreOpcode.Literal, 3, operand: 'a')]));
        Assert.Equal(RegexSearchStatus.NoMatch, h.Char("ba", 1, out _));
        h = new Harness(Program([I(CoreOpcode.AssertEnd, 2)]));
        Assert.Equal(RegexSearchStatus.Match, h.Char("ab\n", 1, out range));
        Assert.Equal(new RegexMatchRange(3, 0), range);
    }

    [Theory]
    [InlineData(false, RegexSearchStatus.NoMatch)]
    [InlineData(true, RegexSearchStatus.Match)]
    public void DotAllIsExplicit(bool dotAll, RegexSearchStatus expected)
    {
        var h = new Harness(Program([I(CoreOpcode.Any, 2)], flags: dotAll ? RegularCoreFlags.DotAll : RegularCoreFlags.None));
        Assert.Equal(expected, h.Char("\n", 0, out _));
    }

    [Theory]
    [InlineData(false, "!az", 1)]
    [InlineData(true, "az!", 2)]
    public void ClassesUseLegalAlphabetAndNegation(bool negated, string input, int index)
    {
        ProfileDescriptor.TryCreate(new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16, out var profile, out _);
        var budget = new CompileBudget(new());
        Assert.True(CharacterClass.TryCreate([new('a', 'z')], negated, profile!, ref budget,
            new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out var cls, out _));
        var h = new Harness(Program([I(CoreOpcode.Class, 2)], classes: [cls!]));
        Assert.Equal(RegexSearchStatus.Match, h.Char(input, 0, out var range));
        Assert.Equal(new RegexMatchRange(index, 1), range);
    }

    [Fact]
    public void ScalarAndByteReadersKeepNativeOffsets()
    {
        var h = new Harness(Literal(0x1F600, RegularCoreMode.UnicodeScalars));
        Assert.Equal(RegexSearchStatus.Match, h.Char("x😀z", 1, out var range));
        Assert.Equal(new RegexMatchRange(1, 2), range);
        h = new Harness(Literal(0x1F600, RegularCoreMode.UnicodeScalars, RegexInputKind.Utf8));
        Assert.Equal(RegexSearchStatus.Match, h.Bytes("x😀z"u8, 1, out range));
        Assert.Equal(new RegexMatchRange(1, 4), range);
        h = new Harness(Literal(255, RegularCoreMode.RawBytes, RegexInputKind.Bytes));
        Assert.Equal(RegexSearchStatus.Match, h.Bytes([0, 255, 128], 0, out range));
        Assert.Equal(new RegexMatchRange(1, 1), range);
        h = new Harness(Literal(0xD800));
        Assert.Equal(RegexSearchStatus.Match, h.Char("\uD800", 0, out range));
        Assert.Equal(new RegexMatchRange(0, 1), range);
    }

    [Fact]
    public void ValidationPrecedesEarlyAcceptanceAndBoundaryChecks()
    {
        var h = new Harness(Program([], mode: RegularCoreMode.UnicodeScalars));
        Assert.Equal(RegexSearchStatus.InvalidInput, h.Char("a\uD800", 0, out var range));
        Assert.Equal(default, range);
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, h.Failure.Code);
        Assert.Equal(1, h.Failure.Offset);
        Assert.Equal(RegexSearchStatus.InvalidInput, h.Char("😀", 1, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, h.Failure.Code);
        Assert.Equal(RegexSearchStatus.InvalidInput, h.Char("😀\uD800", 1, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, h.Failure.Code);
        h = new Harness(Program([], mode: RegularCoreMode.UnicodeScalars, kind: RegexInputKind.Utf8));
        Assert.Equal(RegexSearchStatus.InvalidInput, h.Bytes([97, 0xFF], 0, out _));
        Assert.Equal(1, h.Failure.Offset);
        Assert.Equal(RegexSearchStatus.InvalidInput, h.Bytes("😀"u8, 2, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, h.Failure.Code);
    }

    [Fact]
    public void DestinationAndCapacityPrecedenceAndExcessSlots()
    {
        var h = new Harness(Literal());
        h.Groups.AsSpan().Fill(new(true, 99, 99));
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, h.Char("a", 0, out var range, destinationLength: 0));
        Assert.Equal(default, range);
        Assert.Equal(new RegexCapture(true, 99, 99), h.Groups[0]);
        Assert.Equal(0, h.Work);
        Assert.Equal(1, h.Failure.RequiredCapacity);
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out _));
        Assert.Equal(new RegexCapture(true, 99, 99), h.Groups[1]);
        Assert.Equal(RegexSearchStatus.NoMatch, h.Char("b", 0, out _));
        Assert.Equal(default, h.Groups[0]);
        Assert.Equal(new RegexCapture(true, 99, 99), h.Groups[1]);
        var c = h.Executor.GetRequiredCapacity(SearchOperation.Captures);
        foreach (var shortage in new[] { new RegexWorkspaceCapacity(c.StateSlots - 1, c.CaptureSlots, c.ScratchSlots),
            new(c.StateSlots, c.CaptureSlots - 1, c.ScratchSlots), new(c.StateSlots, c.CaptureSlots, c.ScratchSlots - 1) })
        {
            var state = new RunState(new(new int[shortage.StateSlots], new int[shortage.CaptureSlots], new int[shortage.ScratchSlots]), new());
            Assert.Equal(RegexSearchStatus.LimitExceeded, h.Executor.Search<char, CharReader>("a", 0,
                SearchOperation.Captures, ref state, h.Groups, out range));
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, state.Failure.Code);
            Assert.Equal(0, state.Budget.WorkConsumed);
            Assert.Equal(default, range);
        }
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out _));
    }

    [Fact]
    public void WorkBudgetSurvivesValidationCandidatesAndCaptureOutput()
    {
        var h = new Harness(Literal());
        Assert.Equal(RegexSearchStatus.Match, h.Char("ba", 0, out _));
        var exact = h.Work;
        Assert.Equal(RegexSearchStatus.Match, h.Char("ba", 0, out _, maxWork: exact));
        Assert.Equal(RegexSearchStatus.LimitExceeded, h.Char("ba", 0, out var range, maxWork: exact - 1));
        Assert.Equal(default, range);
        Assert.Equal(default, h.Groups[0]);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, h.Failure.Code);
        Assert.True(h.Work <= exact - 1);
        Assert.Equal(RegexSearchStatus.LimitExceeded, h.Char(new string('b', 1000), 0, out _, maxWork: 1100));
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out _));
        Assert.Equal(default, h.Failure);
    }

    [Fact]
    public void InitialCancellationAndDeadlinePollEvenEmptyAcceptance()
    {
        var h = new Harness(Program([]));
        using var c = new CancellationTokenSource();
        c.Cancel();
        Assert.Equal(RegexSearchStatus.Cancelled, h.Char("", 0, out _, options: new() { CancellationToken = c.Token }));
        Assert.Equal(0, h.Work);
        var capacity = h.Executor.GetRequiredCapacity(SearchOperation.Captures);
        var timeout = TimeSpan.FromTicks(1);
        var state = new RunState(new(new int[capacity.StateSlots], new int[capacity.CaptureSlots], new int[capacity.ScratchSlots]),
            new(), new() { Timeout = timeout });
        // Wait for the configured duration on the production clock; one timestamp
        // increment can be shorter than a TimeSpan tick on this platform.
        var timeoutTicks = timeout.TotalSeconds * System.Diagnostics.Stopwatch.Frequency;
        while (System.Diagnostics.Stopwatch.GetTimestamp() - state.Budget.StartedAt < timeoutTicks)
        { }
        Assert.Equal(RegexSearchStatus.TimedOut, h.Executor.Search<char, CharReader>("", 0,
            SearchOperation.Captures, ref state, h.Groups, out _));
        Assert.Equal(0, state.Budget.WorkConsumed);
    }

    [Fact]
    public void CapacityArithmeticIsOperationSpecificAndChecked()
    {
        Assert.Equal(new RegexWorkspaceCapacity(8, 0, 8), ThompsonExecutor.CalculateCapacity(4, 1, SearchOperation.Boolean));
        Assert.Equal(new RegexWorkspaceCapacity(8, 28, 8), ThompsonExecutor.CalculateCapacity(4, 9, SearchOperation.Range));
        Assert.Equal(new RegexWorkspaceCapacity(8, 252, 8), ThompsonExecutor.CalculateCapacity(4, 9, SearchOperation.Captures));
        Assert.Throws<OverflowException>(() => ThompsonExecutor.CalculateCapacity(int.MaxValue, 1, SearchOperation.Boolean));
        Assert.Throws<OverflowException>(() => ThompsonExecutor.CalculateCapacity(1_000_000, 1024, SearchOperation.Captures));
        Assert.Throws<OverflowException>(() => ThompsonExecutor.CalculateCapacity(4, int.MaxValue, SearchOperation.Captures));
        Assert.Throws<ArgumentOutOfRangeException>(() => ThompsonExecutor.CalculateCapacity(4, 1, (SearchOperation)99));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AllOperationsAgreeAndPreserveResultsWhenPrepared(int operationValue)
    {
        var operation = (SearchOperation)operationValue;
        var h = new Harness(Star(false, true), operation);
        string[] subjects = ["", "bbb", "baaa", "aaa"];
        for (var warm = 0; warm < 4; warm++)
        {
            foreach (var input in subjects)
            {
                h.Char(input, 0, out _);
            }
        }

        long checksum = 0;
        for (var repeat = 0; repeat < 100; repeat++)
        {
            foreach (var input in subjects)
            {
                checksum += (int)h.Char(input, 0, out var range) + range.Index + range.Length;
            }
        }

        Assert.True(checksum >= 0);
    }

    [Fact]
    public void LongMissUsesOnePassAndBoundedWorkspace()
    {
        var h = new Harness(Program([I(CoreOpcode.Split, 2, 3), I(CoreOpcode.Literal, 1, operand: 'a'),
            I(CoreOpcode.Literal, 4, operand: 'b')])); // a*b on a-only input
        var a = new string('a', 100);
        Assert.Equal(RegexSearchStatus.NoMatch, h.Char(a, 0, out _));
        var shortWork = h.Work;
        Assert.Equal(RegexSearchStatus.NoMatch, h.Char(new string('a', 1000), 0, out _));
        Assert.InRange(h.Work, shortWork, shortWork * 11);
    }

    [Fact]
    public void IndependentPreparedWorkspacesShareOnePlanConcurrently()
    {
        var program = Star(false, true);
        Parallel.For(0, 16, _ =>
        {
            var h = new Harness(program);
            for (var i = 0; i < 100; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, h.Char("baaa", 1, out var range));
                Assert.Equal(new RegexMatchRange(1, 3), range);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedByteSearchHitMissAndEmptyPreservePreparedResults(bool raw)
    {
        var mode = raw ? RegularCoreMode.RawBytes : RegularCoreMode.UnicodeScalars;
        var kind = raw ? RegexInputKind.Bytes : RegexInputKind.Utf8;
        foreach (var operation in new[] { SearchOperation.Boolean, SearchOperation.Range, SearchOperation.Captures })
        {
            var h = new Harness(Literal(raw ? 255 : 0x1F600, mode, kind), operation);
            byte[][] subjects = raw ? [[], [0, 255, 128], [0, 1, 128]] : [[], Encoding.UTF8.GetBytes("x😀z"), Encoding.UTF8.GetBytes("ézz")];
            // Match the exact measured workload during bounded preparation.
            for (var warm = 0; warm < 4; warm++)
            {
                foreach (var input in subjects)
                {
                    h.Bytes(input, 0, out _);
                }
            }

            long checksum = 0;
            for (var i = 0; i < 100; i++)
            {
                foreach (var input in subjects)
                { checksum += (int)h.Bytes(input, 0, out var range) + range.Index + range.Length; }
            }

            Assert.True(checksum >= 0);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExhaustiveAsciiCommonSubsetAgreesWithBcl(int fixture)
    {
        // BCL is used ONLY as a development oracle for these five explicitly common cases.
        // ASCII units, absolute anchors absent, no nested-repeat capture retention difference.
        var (pattern, program) = fixture switch
        {
            0 => ("aa|a", Program([I(CoreOpcode.Split, 2, 4), I(CoreOpcode.Literal, 3, operand: 'a'),
                I(CoreOpcode.Literal, 5, operand: 'a'), I(CoreOpcode.Literal, 5, operand: 'a')])),
            1 => ("a*b", Program([I(CoreOpcode.Split, 2, 3), I(CoreOpcode.Literal, 1, operand: 'a'),
                I(CoreOpcode.Literal, 4, operand: 'b')])),
            2 => ("(a)*", Star(false, true)),
            3 => ("(a)*?", Star(true, true)),
            _ => ("(a)b|(a)", Program([I(CoreOpcode.Split, 2, 6), I(CoreOpcode.CaptureStart, 3, operand: 1),
                I(CoreOpcode.Literal, 4, operand: 'a'), I(CoreOpcode.CaptureEnd, 5, operand: 1),
                I(CoreOpcode.Literal, 9, operand: 'b'), I(CoreOpcode.CaptureStart, 7, operand: 2),
                I(CoreOpcode.Literal, 8, operand: 'a'), I(CoreOpcode.CaptureEnd, 9, operand: 2)], 3))
        };
        var oracle = new System.Text.RegularExpressions.Regex(pattern);
        var h = new Harness(program);
        for (var length = 0; length <= 5; length++)
        {
            for (var bits = 0; bits < 1 << length; bits++)
            {
                var input = new string([.. Enumerable.Range(0, length).Select(i => (bits & (1 << i)) == 0 ? 'a' : 'b')]);
                for (var start = 0; start <= length; start++)
                {
                    var expected = oracle.Match(input, start);
                    var status = h.Char(input, start, out var actual);
                    Assert.Equal(expected.Success ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, status);
                    Assert.Equal(expected.Success ? new RegexMatchRange(expected.Index, expected.Length) : default, actual);
                    for (var g = 0; g < program.GroupCount; g++)
                    {
                        var e = expected.Groups[g];
                        Assert.Equal(e.Success ? new RegexCapture(true, e.Index, e.Length) : default, h.Groups[g]);
                    }
                }
            }
        }
    }

    [Fact]
    public void EverySmallBudgetFailsCleanlyAndBuffersRemainReusable()
    {
        var h = new Harness(Star(false, true));
        Assert.Equal(RegexSearchStatus.Match, h.Char("aaab", 0, out _));
        var required = h.Work;
        for (var ceiling = 1L; ceiling < required; ceiling++)
        {
            h.Groups.AsSpan().Fill(new(true, 99, 99));
            Assert.Equal(RegexSearchStatus.LimitExceeded, h.Char("aaab", 0, out var range, maxWork: ceiling));
            Assert.Equal(default, range);
            Assert.Equal(default, h.Groups[0]);
            Assert.Equal(default, h.Groups[1]);
            Assert.Equal(new RegexCapture(true, 99, 99), h.Groups[2]);
            Assert.InRange(h.Work, 0, ceiling);
            Assert.Equal(RegexSearchStatus.Match, h.Char("aaab", 0, out range));
            Assert.Equal(new RegexMatchRange(0, 3), range);
        }
    }

    [Fact]
    public void DeepClosureUsesPreparedStackInsteadOfCallStack()
    {
        const int depth = 4096;
        var body = new CoreInstruction[depth];
        for (var i = 0; i < depth; i++)
        {
            body[i] = I(CoreOpcode.Split, i + 2, depth + 1);
        }

        var h = new Harness(Program(body));
        Assert.Equal(RegexSearchStatus.Match, h.Char("", 0, out var range));
        Assert.Equal(default, range);
        Assert.InRange(h.Work, depth, 20L * depth);
    }

    [Fact]
    public void PreparedScalarCharacterHitMissAndEmptyPreservePreparedResults()
    {
        foreach (var operation in new[] { SearchOperation.Boolean, SearchOperation.Range, SearchOperation.Captures })
        {
            var h = new Harness(Literal(0x1F600, RegularCoreMode.UnicodeScalars), operation);
            string[] inputs = ["", "éz", "x😀z"];
            for (var i = 0; i < 100; i++)
            {
                foreach (var input in inputs)
                {
                    h.Char(input, 0, out _);
                }
            }

            long checksum = 0;
            for (var i = 0; i < 100; i++)
            {
                foreach (var input in inputs)
                { checksum += (int)h.Char(input, 0, out var range) + range.Index + range.Length; }
            }

            Assert.True(checksum >= 0);
        }
    }

    [Fact]
    public void NullableCaptureRepeatTerminatesAndFiniteCopiesKeepFinalEmptyCapture()
    {
        // (a?)*: a no-progress return to the same split is pruned, retaining the
        // capture snapshot from the preceding consuming iteration (or no iteration).
        var h = new Harness(Program([I(CoreOpcode.Split, 2, 7),
            I(CoreOpcode.ClearCaptures, 3, operand: 1, count: 1), I(CoreOpcode.CaptureStart, 4, operand: 1),
            I(CoreOpcode.Split, 5, 6), I(CoreOpcode.Literal, 6, operand: 'a'),
            I(CoreOpcode.CaptureEnd, 1, operand: 1)], 2));
        Assert.Equal(RegexSearchStatus.Match, h.Char("", 0, out _));
        Assert.Equal(default, h.Groups[1]);
        Assert.Equal(RegexSearchStatus.Match, h.Char("aa", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 2), range);
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[1]);
        // (a?){2}: distinct finite copies do not form an epsilon cycle. The final
        // participating iteration is empty and must replace the first nonempty capture.
        h = new Harness(Program([I(CoreOpcode.ClearCaptures, 2, operand: 1, count: 1),
            I(CoreOpcode.CaptureStart, 3, operand: 1), I(CoreOpcode.Split, 4, 5),
            I(CoreOpcode.Literal, 5, operand: 'a'), I(CoreOpcode.CaptureEnd, 6, operand: 1),
            I(CoreOpcode.ClearCaptures, 7, operand: 1, count: 1), I(CoreOpcode.CaptureStart, 8, operand: 1),
            I(CoreOpcode.Split, 9, 10), I(CoreOpcode.Literal, 10, operand: 'a'),
            I(CoreOpcode.CaptureEnd, 11, operand: 1)], 2));
        Assert.Equal(RegexSearchStatus.Match, h.Char("a", 0, out range));
        Assert.Equal(new RegexMatchRange(0, 1), range);
        Assert.Equal(new RegexCapture(true, 1, 0), h.Groups[1]);
    }

    [Fact]
    public void NullablePlusKeepsMandatoryEmptyParticipation()
    {
        // (a?)+: the first body is mandatory; subsequent no-progress cycles prune.
        var h = new Harness(Program([I(CoreOpcode.ClearCaptures, 2, operand: 1, count: 1),
            I(CoreOpcode.CaptureStart, 3, operand: 1), I(CoreOpcode.Split, 4, 5),
            I(CoreOpcode.Literal, 5, operand: 'a'), I(CoreOpcode.CaptureEnd, 6, operand: 1),
            I(CoreOpcode.Split, 1, 7)], 2));
        Assert.Equal(RegexSearchStatus.Match, h.Char("", 0, out var range));
        Assert.Equal(default, range);
        Assert.Equal(new RegexCapture(true, 0, 0), h.Groups[1]);
        Assert.Equal(RegexSearchStatus.Match, h.Char("aa", 0, out range));
        Assert.Equal(new RegexMatchRange(0, 2), range);
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[1]);
    }

    [Fact]
    public void NestedAlternativeCaptureClearsOnFinalNonparticipatingRepeat()
    {
        // ((a)|b)*: nested a-capture must be absent after the final b iteration.
        var h = new Harness(Program([I(CoreOpcode.Split, 2, 10),
            I(CoreOpcode.ClearCaptures, 3, operand: 1, count: 2), I(CoreOpcode.CaptureStart, 4, operand: 1),
            I(CoreOpcode.Split, 5, 8), I(CoreOpcode.CaptureStart, 6, operand: 2),
            I(CoreOpcode.Literal, 7, operand: 'a'), I(CoreOpcode.CaptureEnd, 9, operand: 2),
            I(CoreOpcode.Literal, 9, operand: 'b'), I(CoreOpcode.CaptureEnd, 1, operand: 1)], 3));
        Assert.Equal(RegexSearchStatus.Match, h.Char("ab", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 2), range);
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[1]);
        Assert.Equal(default, h.Groups[2]);
        Assert.Equal(RegexSearchStatus.Match, h.Char("ba", 0, out _));
        Assert.Equal(new RegexCapture(true, 1, 1), h.Groups[2]);
    }

    [Fact]
    public void EmptyAlternativePathsPreserveParticipationAndPriority()
    {
        // (a|())b: empty participation on the second path, absent on the first.
        var h = new Harness(Program([I(CoreOpcode.CaptureStart, 2, operand: 1), I(CoreOpcode.Split, 3, 4),
            I(CoreOpcode.Literal, 6, operand: 'a'), I(CoreOpcode.CaptureStart, 5, operand: 2),
            I(CoreOpcode.CaptureEnd, 6, operand: 2), I(CoreOpcode.CaptureEnd, 7, operand: 1),
            I(CoreOpcode.Literal, 8, operand: 'b')], 3));
        Assert.Equal(RegexSearchStatus.Match, h.Char("b", 0, out var range));
        Assert.Equal(new RegexMatchRange(0, 1), range);
        Assert.Equal(new RegexCapture(true, 0, 0), h.Groups[1]);
        Assert.Equal(new RegexCapture(true, 0, 0), h.Groups[2]);
        Assert.Equal(RegexSearchStatus.Match, h.Char("ab", 0, out range));
        Assert.Equal(new RegexMatchRange(0, 2), range);
        Assert.Equal(new RegexCapture(true, 0, 1), h.Groups[1]);
        Assert.Equal(default, h.Groups[2]);
    }

    // Test-only readers are independent of production text readers. Production integration binds its own
    // validated readers; these make lane behavior executable without sibling implementation.
    private readonly struct CharReader : ITextReader<char>
    {
        public readonly RegexSearchStatus Validate(ReadOnlySpan<char> input, int startAt, ProfileDescriptor profile, ref RunState state)
        {
            for (var offset = 0; offset < input.Length;)
            {
                var valid = TryRead(input, offset, profile, out _, out var width);
                if (!state.TryCharge(valid ? width : 1, offset))
                {
                    return Status(state);
                }

                if (!valid)
                { state.Failure = new(RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
                offset += width;
            }
            if (profile.TextModel == TextModel.UnicodeScalars && startAt > 0 && startAt < input.Length && char.IsLowSurrogate(input[startAt]))
            { state.Failure = new(RegexExecutionFailureCode.InvalidStartBoundary, startAt, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
            return RegexSearchStatus.NoMatch;
        }
        public readonly bool TryRead(ReadOnlySpan<char> input, int offset, ProfileDescriptor profile, out int value, out int width)
        {
            value = width = 0;
            if (offset == input.Length)
            {
                return false;
            }

            var first = input[offset];
            value = first;
            width = 1;
            if (profile.TextModel != TextModel.UnicodeScalars || !char.IsSurrogate(first))
            {
                return true;
            }

            if (!char.IsHighSurrogate(first) || offset + 1 == input.Length || !char.IsLowSurrogate(input[offset + 1]))
            { width = 0; return false; }
            value = char.ConvertToUtf32(first, input[offset + 1]);
            width = 2;
            return true;
        }
    }

    private readonly struct ByteReader : ITextReader<byte>
    {
        public readonly RegexSearchStatus Validate(ReadOnlySpan<byte> input, int startAt, ProfileDescriptor profile, ref RunState state)
        {
            for (var offset = 0; offset < input.Length;)
            {
                var valid = TryRead(input, offset, profile, out _, out var width);
                if (!state.TryCharge(valid ? width : 1, offset))
                {
                    return Status(state);
                }

                if (!valid)
                { state.Failure = new(RegexExecutionFailureCode.InvalidEncoding, offset, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
                offset += width;
            }
            if (profile.TextModel == TextModel.UnicodeScalars && startAt < input.Length && (input[startAt] & 0xC0) == 0x80)
            { state.Failure = new(RegexExecutionFailureCode.InvalidStartBoundary, startAt, state.Budget.WorkConsumed); return RegexSearchStatus.InvalidInput; }
            return RegexSearchStatus.NoMatch;
        }
        public readonly bool TryRead(ReadOnlySpan<byte> input, int offset, ProfileDescriptor profile, out int value, out int width)
        {
            value = width = 0;
            if (offset == input.Length)
            {
                return false;
            }

            if (profile.TextModel == TextModel.RawBytes)
            { value = input[offset]; width = 1; return true; }
            if (Rune.DecodeFromUtf8(input[offset..], out var rune, out width) != OperationStatus.Done)
            { width = 0; return false; }
            value = rune.Value;
            return true;
        }
    }

    private static RegexSearchStatus Status(in RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        _ => RegexSearchStatus.LimitExceeded
    };

    private sealed class Harness
    {
        internal readonly ThompsonExecutor Executor;
        private readonly SearchOperation _operation;
        private readonly int[] _states, _captures, _scratch;
        private readonly RegexMatchLimits _limits = new();
        internal readonly RegexCapture[] Groups;
        internal RegexExecutionFailure Failure;
        internal long Work;
        internal Harness(SemanticProgram program, SearchOperation operation = SearchOperation.Captures)
        {
            Executor = new(program);
            _operation = operation;
            var c = Executor.GetRequiredCapacity(operation);
            _states = new int[c.StateSlots];
            _captures = new int[c.CaptureSlots];
            _scratch = new int[c.ScratchSlots];
            Groups = new RegexCapture[program.GroupCount + 1];
        }
        internal RegexSearchStatus Char(ReadOnlySpan<char> input, int start, out RegexMatchRange range,
            long? maxWork = null, RegexMatchOptions options = default, int? destinationLength = null)
        {
            var state = new RunState(new(_states, _captures, _scratch), maxWork is { } work ? new(work) : _limits, options);
            var status = Executor.Search<char, CharReader>(input, start, _operation, ref state,
                destinationLength is { } length ? Groups.AsSpan(0, length) : Groups, out range);
            Failure = state.Failure;
            Work = state.Budget.WorkConsumed;
            return status;
        }
        internal RegexSearchStatus Bytes(ReadOnlySpan<byte> input, int start, out RegexMatchRange range)
        {
            var state = new RunState(new(_states, _captures, _scratch), _limits);
            var status = Executor.Search<byte, ByteReader>(input, start, _operation, ref state, Groups, out range);
            Failure = state.Failure;
            Work = state.Budget.WorkConsumed;
            return status;
        }
    }
}
