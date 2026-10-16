using GregsEx.Contracts;
using GregsEx.IR;
using Xunit;

namespace GregsEx.Tests.Contracts;

public class ContractTests
{
    [Theory]
    [InlineData(RegularCoreMode.UnicodeScalars, RegexInputKind.Utf16, true)]
    [InlineData(RegularCoreMode.UnicodeScalars, RegexInputKind.Utf8, true)]
    [InlineData(RegularCoreMode.UnicodeScalars, RegexInputKind.Bytes, false)]
    [InlineData(RegularCoreMode.Utf16CodeUnits, RegexInputKind.Utf16, true)]
    [InlineData(RegularCoreMode.Utf16CodeUnits, RegexInputKind.Utf8, false)]
    [InlineData(RegularCoreMode.Utf16CodeUnits, RegexInputKind.Bytes, false)]
    [InlineData(RegularCoreMode.RawBytes, RegexInputKind.Utf16, false)]
    [InlineData(RegularCoreMode.RawBytes, RegexInputKind.Utf8, false)]
    [InlineData(RegularCoreMode.RawBytes, RegexInputKind.Bytes, true)]
    public void RepresentationAdmissionIsExplicit(RegularCoreMode mode, RegexInputKind kind, bool supported)
    {
        var profile = new RegularCoreProfile(mode);
        Assert.Equal(supported, profile.Supports(kind));
        Assert.Equal(supported, ProfileDescriptor.TryCreate(profile, kind, out var normalized, out var diagnostic));
        Assert.Equal(supported ? RegexDiagnosticCode.None : RegexDiagnosticCode.UnsupportedRepresentation, diagnostic.Code);
        Assert.Equal(supported, normalized is not null);
    }

    [Fact]
    public void PublicDefaultsAreFiniteAndImmutable()
    {
        var options = new RegexCompileOptions();
        Assert.Equal(RegexInputKind.Utf16, options.InputKind);
        Assert.Equal(RegexExecutionMode.Interpreted, options.ExecutionMode);
        Assert.Equal(65_536, options.Limits.MaxPatternUnits);
        Assert.Equal(256, options.Limits.MaxNestingDepth);
        Assert.Equal(1_000_000, options.Limits.MaxInstructions);
        Assert.Equal(67_108_864, options.Limits.MaxPlanBytes);
        Assert.Equal(10_000_000, options.DefaultMatchLimits.MaxWork);
        Assert.Equal(67_108_864, options.DefaultMatchLimits.MaxWorkspaceBytes);
        Assert.False(new RegexWorkspaceOptions().AllowGrowth);
        foreach (var property in typeof(RegexCompileLimits).GetProperties())
        {
            Assert.Null(property.SetMethod);
        }

        Assert.Null(typeof(RegexCompileOptions).GetProperty(nameof(RegexCompileOptions.InputKind))!.SetMethod);
    }

    [Fact]
    public void InvalidConfigurationIsAProgrammerError()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexCompileLimits(maxPatternUnits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexCompileLimits(maxCompileWork: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexMatchLimits(maxWork: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexCompileOptions((RegexInputKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexCompileOptions(executionMode: (RegexExecutionMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegularCoreProfile((RegularCoreMode)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegularCoreProfile(flags: (RegularCoreFlags)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexWorkspaceCapacity(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexMatchOptions { Timeout = TimeSpan.Zero }.Resolve(new()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexMatchOptions { Limits = new(11) }.Resolve(new(10)));
    }

    [Fact]
    public void SafetyOverridesOnlyReducePatternCeilings()
    {
        var ceiling = new RegexMatchLimits(10, 1000);
        Assert.Same(ceiling, default(RegexMatchOptions).Resolve(ceiling));
        var reduced = new RegexMatchLimits(5, 500);
        Assert.Same(reduced, new RegexMatchOptions { Limits = reduced }.Resolve(ceiling));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RegexMatchOptions { Limits = new(5, 1001) }.Resolve(ceiling));
    }

    [Fact]
    public void CapacityArithmeticDoesNotOverflowInt32()
    {
        var maximum = new RegexWorkspaceCapacity(int.MaxValue, int.MaxValue, int.MaxValue);
        Assert.Equal(96L + 12L * int.MaxValue, maximum.RequiredBytes);
        Assert.True(maximum.Contains(new(1, 2, 3)));
        Assert.False(new RegexWorkspaceCapacity(2, 1, 4).Contains(new(1, 2, 3)));
        Assert.Equal(96, default(RegexWorkspaceCapacity).RequiredBytes);
    }

    [Fact]
    public void ScalarAlphabetExcludesSurrogatesButUnitAndRawModesAreDistinct()
    {
        var scalar = Descriptor();
        Assert.True(scalar.Contains(0x1F600));
        Assert.False(scalar.Contains(0xD800));
        Assert.False(scalar.Contains(-1));
        Assert.False(scalar.Contains(0x110000));
        var units = Descriptor(RegularCoreMode.Utf16CodeUnits);
        Assert.True(units.Contains(0xD800));
        Assert.False(units.Contains(0x1F600));
        var bytes = Descriptor(RegularCoreMode.RawBytes, RegexInputKind.Bytes);
        Assert.True(bytes.Contains(255));
        Assert.False(bytes.Contains(256));
        Assert.Equal(RegexOffsetUnit.Bytes, bytes.InputUnit);
        Assert.Equal("GregsEx-ASCII-1", scalar.DataId);
    }

    [Fact]
    public void DiagnosticsAndParticipationHaveUnambiguousDefaults()
    {
        Assert.Equal(RegexDiagnosticCode.None, default(RegexDiagnostic).Code);
        Assert.Equal(RegexExecutionFailureCode.None, default(RegexExecutionFailure).Code);
        Assert.NotEqual(default, new RegexCapture(true, 0, 0));
        var shortDestination = new RegexExecutionFailure(RegexExecutionFailureCode.DestinationTooSmall, 0, 0, 3);
        Assert.Equal(3, shortDestination.RequiredCapacity);
        Assert.Equal(shortDestination, new RegexExecutionException(shortDestination).Failure);
        var syntax = new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, 2, 1, RegexOffsetUnit.Bytes);
        Assert.Equal(syntax, new RegexParseException(syntax).Diagnostic);
        Assert.Throws<ArgumentException>(() => new RegexParseException(default));
        Assert.Throws<ArgumentException>(() => new RegexExecutionException(default));
    }

    [Fact]
    public void WorkAccountingIsCumulativeAndStickyInRunState()
    {
        var state = new RunState(new([], [], []), new(3));
        Assert.True(state.TryCharge(1, 0));
        Assert.True(state.TryCharge(2, 4));
        Assert.False(state.TryCharge(1, 7));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 7, 3), state.Failure);
        Assert.False(state.TryCharge(0, 8));
        Assert.Equal(7, state.Failure.Offset);
        Assert.Equal(3, state.Budget.WorkConsumed);
    }

    [Fact]
    public void CancellationPrecedesDeadlineAndWorkExhaustion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var budget = new RunBudget(new(1), new() { CancellationToken = cancellation.Token, Timeout = TimeSpan.FromTicks(1) });
        Assert.False(budget.TryCharge(2, 9, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.Cancelled, 9, 0), failure);
    }

    [Fact]
    public void MaximumTimeoutDoesNotOverflow()
    {
        var budget = new RunBudget(new(1), new() { Timeout = TimeSpan.MaxValue });
        Assert.True(budget.TryCharge(1, 0, out var failure));
        Assert.Equal(default, failure);
    }

    [Fact]
    public void InitialZeroChargePollObservesDeadlineBeforeEmptyAcceptance()
    {
        var budget = new RunBudget(new(1), new() { Timeout = TimeSpan.FromSeconds(1) });
        Assert.False(budget.TryChargeAt(0, 0, budget.StartedAt + System.Diagnostics.Stopwatch.Frequency, out var failure));
        Assert.Equal(new RegexExecutionFailure(RegexExecutionFailureCode.TimedOut, 0, 0), failure);
        Assert.False(budget.TryChargeAt(2, 0, budget.StartedAt + System.Diagnostics.Stopwatch.Frequency, out failure));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, failure.Code);
    }

    [Fact]
    public void CompileWorkAccountingKeepsNativeSourceUnits()
    {
        var budget = new CompileBudget(new(maxCompileWork: 2));
        var source = new SourceRange(3, 1, RegexOffsetUnit.Bytes);
        Assert.True(budget.TryCharge(2, source, out _));
        Assert.False(budget.TryCharge(1, source, out var diagnostic));
        Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.CompileWorkLimitExceeded, 3, 1, RegexOffsetUnit.Bytes), diagnostic);
        Assert.Equal(2, budget.WorkConsumed);
    }

    [Fact]
    public void AdmittedProgramAndClassesOwnTheirStorage()
    {
        CharacterRange[] ranges = [new('a', 'z')];
        var @class = CreateClass(ranges);
        CharacterClass[] classes = [@class];
        var code = LiteralProgram();
        var budget = new CompileBudget(new());
        Assert.True(SemanticProgram.TryCreate(code, classes, Descriptor(), 1, 1,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out var program, out var diagnostic));
        Assert.Equal(default, diagnostic);
        ranges[0] = new('0', '9');
        code[1] = code[1] with { Operand = 'b' };
        classes[0] = CreateClass([]);
        Assert.Equal('a', program!.Instructions[1].Operand);
        Assert.Equal(new CharacterRange('a', 'z'), program.Classes[0].Ranges[0]);
        Assert.Equal(1, program.GroupCount);
        Assert.Equal(0, program.EntryPoint);
        Assert.Equal(MatchPolicy.OrderedLeftmostFirst, program.Profile.MatchPolicy);
    }

    [Theory]
    [InlineData(0)] // invalid successor
    [InlineData(1)] // illegal scalar
    [InlineData(2)] // source outside the original pattern
    [InlineData(3)] // missing group-zero close
    [InlineData(4)] // unrecognized opcode
    [InlineData(5)] // nonzero unused operand
    public void InvalidProgramsCannotCrossAdmission(int mutation)
    {
        var code = LiteralProgram();
        code[1] = mutation switch
        {
            0 => code[1] with { X = 99 },
            1 => code[1] with { Operand = 0xD800 },
            2 => code[1] with { Source = new(1, 1, RegexOffsetUnit.Utf16CodeUnits) },
            4 => code[1] with { Opcode = (CoreOpcode)99 },
            5 => code[1] with { Y = 1 },
            _ => code[1]
        };
        if (mutation == 3)
        {
            code[2] = code[2] with { Opcode = CoreOpcode.Jump };
        }

        var budget = new CompileBudget(new());
        Assert.False(SemanticProgram.TryCreate(code, [], Descriptor(), 1, 1,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out var program, out var diagnostic));
        Assert.Null(program);
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    [Fact]
    public void ProgramLimitsFailBeforePublishingStorage()
    {
        AssertAdmissionFailure(new(maxPatternUnits: 1), 2, 1, RegexDiagnosticCode.PatternLimitExceeded);
        AssertAdmissionFailure(new(maxInstructions: 3), 1, 1, RegexDiagnosticCode.ProgramLimitExceeded);
        AssertAdmissionFailure(new(maxCaptureGroups: 1), 1, 2, RegexDiagnosticCode.CaptureLimitExceeded);
        AssertAdmissionFailure(new(maxPlanBytes: 1), 1, 1, RegexDiagnosticCode.PlanLimitExceeded);
        AssertAdmissionFailure(new(maxCompileWork: 1), 1, 1, RegexDiagnosticCode.CompileWorkLimitExceeded);
        var budget = new CompileBudget(new(maxClassRanges: 1));
        Assert.False(SemanticProgram.TryCreate(LiteralProgram(), [CreateClass([new(1, 2), new(4, 5)])], Descriptor(), 1, 1,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, diagnostic.Code);
    }

    [Fact]
    public void ClassIntervalsAreCanonicalOwnedData()
    {
        AssertClassRejected([new(4, 2)]);
        AssertClassRejected([new(1, 3), new(3, 5)]);
        AssertClassRejected([new(-1, 0)]);
        AssertClassRejected([new(0, 0x110000)]);
        Assert.True(CreateClass([], negated: true).Negated);
    }

    [Fact]
    public void ProductionAssemblyDeliversPatternsWithoutFixtureOrCompilerDependencies()
    {
        var library = typeof(RegexProfile).Assembly;
        Assert.Equal(typeof(global::GregsEx.RegexPattern), library.GetType("GregsEx.RegexPattern"));
        Assert.Equal(typeof(global::GregsEx.RegexWorkspace), library.GetType("GregsEx.RegexWorkspace"));
        Assert.True(global::GregsEx.RegexPattern.Parse("a", new RegularCoreProfile()).IsMatch("ba"));
        Assert.DoesNotContain(library.GetReferencedAssemblies(), name => name.Name!.Contains("FastExpressionCompiler"));
        Assert.DoesNotContain(library.GetExportedTypes(), type => type.FullName!.StartsWith("GregsEx.Tests.", StringComparison.Ordinal));
    }

    [Fact]
    public void ClassAndProgramAllocationsRequirePriorMemoryAdmission()
    {
        var budget = new CompileBudget(new(maxCompileMemoryBytes: 63));
        Assert.False(CharacterClass.TryCreate([], false, Descriptor(), ref budget, default, out var result, out var diagnostic));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
        AssertAdmissionFailure(new(maxCompileMemoryBytes: 1), 1, 1, RegexDiagnosticCode.CompileMemoryLimitExceeded);
        var temporary = new CompileBudget(new(maxCompileMemoryBytes: 100));
        Assert.True(temporary.TryReserveBytes(60, default, out _));
        Assert.False(temporary.TryReserveBytes(50, default, out _));
        temporary.ReleaseBytes(20);
        Assert.True(temporary.TryReserveBytes(50, default, out _));
        Assert.Equal(90, temporary.ReservedBytes);
        Assert.Equal(90, temporary.PeakBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void AcceptingPathsCannotBypassGroupZero(int target)
    {
        var code = LiteralProgram();
        code[1] = code[1] with { X = target };
        var budget = new CompileBudget(new());
        Assert.False(SemanticProgram.TryCreate(code, [], Descriptor(), 1, 1,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    private static CharacterClass CreateClass(ReadOnlySpan<CharacterRange> ranges, bool negated = false)
    {
        var budget = new CompileBudget(new());
        Assert.True(CharacterClass.TryCreate(ranges, negated, Descriptor(), ref budget, default, out var result, out _));
        return result!;
    }

    private static void AssertClassRejected(ReadOnlySpan<CharacterRange> ranges)
    {
        var budget = new CompileBudget(new());
        Assert.False(CharacterClass.TryCreate(ranges, false, Descriptor(), ref budget, default, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    private static ProfileDescriptor Descriptor(RegularCoreMode mode = RegularCoreMode.UnicodeScalars,
        RegexInputKind kind = RegexInputKind.Utf16)
    {
        Assert.True(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode), kind, out var descriptor, out _));
        return descriptor!;
    }

    private static CoreInstruction[] LiteralProgram()
    {
        var source = new SourceRange(0, 1, RegexOffsetUnit.Utf16CodeUnits);
        return [new(CoreOpcode.CaptureStart, 1, 0, 0, 0, source), new(CoreOpcode.Literal, 2, 0, 'a', 0, source),
            new(CoreOpcode.CaptureEnd, 3, 0, 0, 0, source), new(CoreOpcode.Match, 0, 0, 0, 0, source)];
    }

    private static void AssertAdmissionFailure(RegexCompileLimits limits, int patternLength, int groups, RegexDiagnosticCode expected)
    {
        var budget = new CompileBudget(limits);
        Assert.False(SemanticProgram.TryCreate(LiteralProgram(), [], Descriptor(), groups, patternLength,
            RegexOffsetUnit.Utf16CodeUnits, ref budget, out var program, out var diagnostic));
        Assert.Null(program);
        Assert.Equal(expected, diagnostic.Code);
    }
}
