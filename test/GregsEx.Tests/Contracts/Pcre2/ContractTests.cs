using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.IR;
using Xunit;

namespace GregsEx.Tests.Contracts.Pcre2;

public class ContractTests
{
    [Theory]
    [InlineData(false, true)] // a{300}|b is two fixed branches, not globally variable
    [InlineData(true, false)] // true variable branch plus fixed300 exceeds255
    public void NormalizedLookbehindUsesGlobalVariableDistinction(bool variable, bool admitted)
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Literal, 'a'),
            Leaf(Pcre2NodeKind.Repeat) with { Children = new(0,1), Min = variable ? 1 : 300, Max = 300 },
            Leaf(Pcre2NodeKind.Literal, 'b'), Leaf(Pcre2NodeKind.Alternative) with { Children = new(1,2) },
            Leaf(Pcre2NodeKind.Assertion) with { Children = new(3,1) }];
        Pcre2LookbehindBranch[] branches = [new(4, 1, 300), new(4, 2, 1)];
        var budget = new CompileBudget(new());
        var result = Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, [0, 1, 2, 3], assertions: [new(Pcre2AssertionKind.PositiveBehind, 1, 300, variable)],
                units: ['x'], sourceMap: [0, 1], lookbehindBranches: branches, hasLookbehindMetadata: true),
            default, ref budget, out var plan, out var error);
        Assert.Equal(admitted, result);
        if (admitted)
        {
            branches[0] = new(4, 1, 5);
            Assert.Equal(300, plan!.LookbehindBranches[0].MaximumLength);
            Assert.True(plan.HasLookbehindMetadata);
            Assert.False(plan.Assertions[0].VariableLength);
            Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
        }
        else
        { Assert.Equal(RegexDiagnosticCode.InvalidProgram, error.Diagnostic.Code); Assert.Equal(0, budget.ReservedBytes); }
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)] // missing branch
    [InlineData(2, false)] // wrong branch identity
    [InlineData(3, false)] // wrong maximum
    [InlineData(4, false)] // no normalized tag
    [InlineData(5, false)] // impossible global minimum
    public void BranchMetadataIsIndependentlyValidated(int mutation, bool admitted)
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Literal, 'a'), Leaf(Pcre2NodeKind.Assertion) with { Children = new(0, 1) }];
        var budget = new CompileBudget(new());
        var result = Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, [0], assertions: [new(Pcre2AssertionKind.NonAtomicBehind, mutation == 5 ? 2 : 0, 1, true)], units: ['x'], sourceMap: [0, 1],
                lookbehindBranches: mutation == 1 ? [] : [new(1, mutation == 2 ? 1 : 0, mutation == 3 ? 2 : 1)], hasLookbehindMetadata: mutation != 4),
            default, ref budget, out var plan, out var error);
        Assert.Equal(admitted, result);
        if (admitted)
        { Assert.True(plan!.Assertions[0].VariableLength); Assert.Equal(0, plan.Assertions[0].MinimumLength); }
        else
        { Assert.Equal(RegexDiagnosticCode.InvalidProgram, error.Diagnostic.Code); Assert.Equal(0, budget.ReservedBytes); }
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)] // wrong bitmap payload
    [InlineData(2, false)] // out-of-width first unit
    [InlineData(3, false)] // caseless absent unit
    [InlineData(4, true)] // consuming-empty can require visible subject units
    [InlineData(5, false)] // bitmap and first simultaneously
    [InlineData(6, false)] // negative minimum
    [InlineData(7, false)] // insufficient word readback
    public void SearchMetadataCopiesAndValidatesNativePayloads(int mutation, bool admitted)
    {
        var profile = new Pcre2Profile(Pcre2CodeUnitWidth.Eight);
        var hints = new Pcre2SearchHints(mutation == 4 ? 1 : mutation == 6 ? -1 : 0, mutation == 7 ? 0 : 1, true,
            FirstCodeUnit: mutation == 2 ? 256 : mutation == 5 ? 255 : -1, RequiredCodeUnitCaseless: mutation == 3);
        var bitmap = new byte[mutation == 1 ? 31 : 32];
        bitmap[^1] = 128;
        var budget = new CompileBudget(new());
        var result = Pcre2Program.TryCreate(profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new([Leaf(Pcre2NodeKind.WordBoundary)], units: ['x'], sourceMap: [0, 1], hasLookbehindMetadata: true), default,
            ref budget, out var plan, out var error, searchHints: hints, startBitmap: bitmap);
        Assert.Equal(admitted, result);
        if (admitted)
        {
            bitmap[^1] = 0;
            Assert.Equal(128, plan!.StartBitmap[^1]);
            Assert.Equal(hints, plan.SearchHints!.Value);
            Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
            Assert.True(budget.PeakBytes > budget.ReservedBytes);
        }
        else
        { Assert.Equal(RegexDiagnosticCode.InvalidProgram, error.Diagnostic.Code); Assert.Equal(0, budget.ReservedBytes); }
    }
    [Fact]
    public void SearchMetadataAbsentAndWidth16Bit255RemainDistinctAndBudgeted()
    {
        var data = new Pcre2PlanData([Leaf(Pcre2NodeKind.Empty)], units: ['x'], sourceMap: [0, 1]);
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref budget, out var legacy, out _));
        Assert.Null(legacy!.SearchHints);
        Assert.False(legacy.HasLookbehindMetadata);
        Assert.True(legacy.StartBitmap.IsEmpty);
        var bitmap = new byte[32];
        bitmap[31] = 128;
        var copyBudget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref copyBudget, out var plan, out _,
            searchHints: new(0, 0, true, RequiredCodeUnit: 65535), startBitmap: bitmap));
        Assert.Equal(128, plan!.StartBitmap[31]);
        Assert.Equal(65535, plan.SearchHints!.Value.RequiredCodeUnit);
        var peakBudget = new CompileBudget(new(maxCompileMemoryBytes: copyBudget.PeakBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref peakBudget, out _, out var peak,
            searchHints: new(0, 0, true), startBitmap: bitmap));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, peak.Diagnostic.Code);
        Assert.Equal(0, peakBudget.ReservedBytes);
        var retainedBudget = new CompileBudget(new(maxPlanBytes: plan.RetainedBytes - 1));
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1, data, default, ref retainedBudget, out _, out var retained,
            searchHints: new(0, 0, true), startBitmap: bitmap));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, retained.Diagnostic.Code);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UtfCodeUnitInsideAheadStopsOuterBehindButChecksActualNestedBehind(bool nestedBehind, bool admitted)
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.CodeUnit),Leaf(Pcre2NodeKind.Assertion) with { Children=new(0,1) },
            Leaf(Pcre2NodeKind.Assertion,1) with { Children=new(1,1) }];
        var budget = new CompileBudget(new());
        var result = Pcre2Program.TryCreate(new(flags: Pcre2CompileFlags.Utf), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, [0, 1], assertions: [new(nestedBehind?Pcre2AssertionKind.PositiveBehind:Pcre2AssertionKind.PositiveAhead,0,0),
                new(Pcre2AssertionKind.PositiveBehind,0,0)], units: ['x'], sourceMap: [0, 1]), default, ref budget, out _, out _);
        Assert.Equal(admitted, result);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UtfScanZeroWidthBarrierDoesNotWeakenLookaroundResetPolicy(bool reset, bool admitted)
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Literal,'a'),Leaf(Pcre2NodeKind.Capture) with {Children=new(0,1)},
            Leaf(reset?Pcre2NodeKind.ResetStart:Pcre2NodeKind.CodeUnit),Leaf(Pcre2NodeKind.ScanSubstring) with {Children=new(1,1)},
            Leaf(Pcre2NodeKind.Sequence) with {Children=new(2,2)},Leaf(Pcre2NodeKind.Assertion) with {Children=new(4,1)}];
        var budget = new CompileBudget(new());
        var result = Pcre2Program.TryCreate(new(flags: Pcre2CompileFlags.Utf), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 2,
            new(nodes, [0, 2, 1, 3, 4], groups: [new(1, -1, 1)], numbers: [1], references: [new(Pcre2ReferenceKind.Absolute, 1, -1, new(0, 1), -1)],
                assertions: [new(Pcre2AssertionKind.PositiveBehind, 1, 1)], scanReferences: [new(0, 1)], units: ['x'], sourceMap: [0, 1]),
            default, ref budget, out _, out _);
        Assert.Equal(admitted, result);
    }
    [Fact]
    public void VersionThreeFramesRoundTripEveryAddedFieldAndRejectOldCapacity()
    {
        var capacity = new Pcre2ExecutionCapacity(2, 4, 1, 1);
        var size = capacity.Required(1);
        Assert.Equal(3, Pcre2Frame.LayoutVersion);
        Assert.Equal(26, Pcre2Frame.Slots);
        Assert.Throws<ArgumentException>(() => new Pcre2Storage(new(new int[64 + 2 * 16 + 6], new int[2], new int[size.ScratchSlots]), capacity, 1));
        Assert.Throws<ArgumentException>(() => new Pcre2Storage(new(new int[64 + 2 * 25 + 6], new int[2], new int[size.ScratchSlots]), capacity, 1));
        var state = new RunState(new(new int[size.StateSlots], new int[2], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(), 3);
        var storage = new Pcre2Storage(state.Buffers, capacity, 1);
        var frame = new Pcre2Frame(Pcre2FrameKind.Subroutine, 1, 2, 3, 1, 0, 0, 1, 2, 3, 4, 0, 5, 6, Pcre2CalloutFlags.Backtrack, -1,
            RecurseLastUsedPosition: 3, HasSavedWindow: true, SavedWindow: new(2, 1, 3, 1, 0, 0), CallActive: false, NoteolCleared: true);
        Assert.True(storage.PushFrame(frame, ref machine, ref state));
        Assert.Equal(0, machine.CallDepth);
        Assert.True(storage.ReadFrame(0, ref machine, ref state, out var read));
        Assert.Equal(frame, read);
        Assert.True(storage.PopFrame(ref machine, ref state, out var popped));
        Assert.Equal(frame, popped);
        Assert.Equal(0, machine.CallDepth);
    }
    [Fact]
    public void ConsumingEmptyAssertionCanRequireVisibleInput()
    {
        // Retained PCRE-C028: consuming-empty and INFO_MINLENGTH2 coexist.
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Literal,'a'),Leaf(Pcre2NodeKind.Literal,'b'),
            Leaf(Pcre2NodeKind.Sequence) with{Children=new(0,2)},
            Leaf(Pcre2NodeKind.Assertion) with{Children=new(2,1)}];
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 2, 2, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, [0, 1, 2], assertions: [new(Pcre2AssertionKind.PositiveAhead, 0, -1)], units: ['a', 'b'], sourceMap: [0, 1, 2]),
            default, ref budget, out var program, out _, searchHints: new(2, 0, true)));
        Assert.True(program!.SearchHints!.Value.MatchesEmpty);
        Assert.Equal(2, program.SearchHints.Value.MinimumLength);
    }
    [Fact]
    public void ProfilePinsAllFourDomainsWithoutCollapsingUtfAndUcp()
    {
        Assert.Equal("Pcre2-10.46-ManagedStandard8_16-v1", new Pcre2Profile().Id);
        Assert.Equal("b2bd4254b379b9d7dc9a3dda060a7e27009ccdff", Pcre2Profile.SourceRevision);
        foreach (var width in new[] { Pcre2CodeUnitWidth.Eight, Pcre2CodeUnitWidth.Sixteen })
        {
            foreach (var utf in new[] { false, true })
            {
                foreach (var ucp in new[] { false, true })
                {
                    var p = new Pcre2Profile(width, (utf ? Pcre2CompileFlags.Utf : 0) | (ucp ? Pcre2CompileFlags.Ucp : 0));
                    Assert.Equal(utf, p.Utf);
                    Assert.Equal(ucp, p.Ucp);
                    Assert.True(p.Supports(width == Pcre2CodeUnitWidth.Sixteen ? RegexInputKind.Utf16 : utf ? RegexInputKind.Utf8 : RegexInputKind.Bytes));
                    Assert.Equal(255, p.MaxVariableLookbehind);
                }
            }
        }

        Assert.True(new Pcre2Profile(flags: Pcre2CompileFlags.MatchInvalidUtf).Utf);
        Assert.False(new Pcre2Profile().Supports(RegexInputKind.Bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pcre2Profile((Pcre2CodeUnitWidth)32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pcre2Profile(flags: (Pcre2CompileFlags)0x10000000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pcre2Profile(extraFlags: (Pcre2ExtraFlags)0x20000));
        // Known but source-invalid combinations stay available for parser diagnostics.
        _ = new Pcre2Profile(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.NeverUtf);
        Assert.False(new Pcre2Profile(flags: Pcre2CompileFlags.NoStartOptimize).EffectiveOptimization.StartOptimize);
    }
    [Fact]
    public void NativeDataAndBorrowedCallbacksKeepOwnedDataAndInternalBoundaries()
    {
        var table = Pcre2CharacterTables.Default;
        Assert.Equal(1088, table.Bytes.Length);
        Assert.Equal(Pcre2DefaultTables.Digest, table.Digest);
        var bytes = table.Bytes.ToArray();
        var copied = new Pcre2CharacterTables(bytes);
        bytes[65] = 0;
        Assert.Equal(97, copied.Bytes[65]);
        Assert.Equal(table.Digest, copied.Digest);
        Assert.Throws<ArgumentException>(() => new Pcre2CharacterTables(new byte[1087]));
        Assert.Equal(3, (int)Pcre2SubstituteCase.TitleFirst);
        Assert.Equal(-2, (int)Pcre2ErrorCode.Partial);
        var exported = typeof(Pcre2Profile).Assembly.GetExportedTypes();
        Assert.DoesNotContain(exported, t => t.Namespace?.StartsWith("GregsEx.Contracts", StringComparison.Ordinal) == true);
        Assert.True(typeof(Pcre2ByteCalloutContext).IsByRefLike);
        Assert.True(typeof(Pcre2CharSubstituteContext).IsByRefLike);
    }
    [Fact]
    public void IndependentEndpointsKeepReversedEmptyAndUnsetDistinct()
    {
        var reverse = new Pcre2OffsetPair(true, 2, 0);
        Assert.False(reverse.TryGetRange(out _));
        Assert.Equal(2, reverse.Start);
        Assert.Equal(0, reverse.End);
        var empty = new Pcre2OffsetPair(true, 0, 0);
        Assert.True(empty.TryGetRange(out var range));
        Assert.Equal(default, range);
        Assert.False(default(Pcre2OffsetPair).TryGetRange(out _));
        Assert.Throws<ArgumentException>(() => new Pcre2OffsetPair(false, 1, 0));
        Assert.Throws<ArgumentException>(() => new Pcre2ParseException(default));
        Assert.Equal(Pcre2MatchStatus.Match, new Pcre2MatchResult(Pcre2MatchStatus.Match, 0, 0).Status);
    }
    [Fact]
    public void ProgramFactoryCopiesDataAndRetainsExactlyAdmittedPayload()
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Literal, 'a')];
        int[] units = ['a'], map = [0, 1];
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new(nodes, units: units, sourceMap: map), default, ref budget, out var plan, out var error));
        Assert.Equal(default, error);
        nodes[0] = Leaf(Pcre2NodeKind.Literal, 'z');
        units[0] = 'z';
        map[1] = 0;
        Assert.Equal('a', plan!.Nodes[0].Operand);
        Assert.Equal('a', plan.Units[0]);
        Assert.Equal(1, plan.SourceMap[1]);
        Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
        Assert.True(budget.PeakBytes > budget.ReservedBytes);
        Assert.True(plan.TryAdmitProjection(Pcre2ProjectionPolicy.WholeSubjectValidated, out _));
    }
    [Theory]
    [InlineData(Pcre2MatchFlags.None, true)]
    [InlineData(Pcre2MatchFlags.Notempty, true)]
    [InlineData(Pcre2MatchFlags.NotemptyAtstart, true)]
    [InlineData(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.NotemptyAtstart, true)]
    [InlineData(Pcre2MatchFlags.Notbol, false)]
    [InlineData(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.PartialHard, false)]
    [InlineData((Pcre2MatchFlags)0x80000000u, false)]
    public void PatternDirectivesRetainOnlySourceDefinedMatchFlags(Pcre2MatchFlags flags, bool admitted)
    {
        foreach (var stage in new[] { Pcre2PlanStage.Syntax, Pcre2PlanStage.Executable })
        {
            var budget = new CompileBudget(new());
            var result = Pcre2Program.TryCreate(new(), stage, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
                new([Leaf(Pcre2NodeKind.Empty)], units: ['a'], sourceMap: [0, 1]), default, ref budget,
                out var plan, out var diagnostic, patternMatchFlags: flags);
            Assert.Equal(admitted, result);
            if (admitted)
            { Assert.Equal(flags, plan!.PatternMatchFlags); Assert.Equal(default, diagnostic); }
            else
            { Assert.Null(plan); Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Diagnostic.Code); Assert.Equal(0, budget.ReservedBytes); }
        }
    }
    [Theory]
    [InlineData(-1, -1, true)]
    [InlineData(0, -1, false)]
    [InlineData(-1, 0, false)]
    [InlineData(-2, -1, false)]
    public void AnyRecursionConditionIsIndependentOfSpecificReferences(int reference, int assertion, bool admitted)
    {
        foreach (var stage in new[] { Pcre2PlanStage.Syntax, Pcre2PlanStage.Executable })
        {
            var budget = new CompileBudget(new());
            Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Empty), Leaf(Pcre2NodeKind.Conditional) with { Children = new(0, 1) }];
            var result = Pcre2Program.TryCreate(new(), stage, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
                new(nodes, [0], conditions: [new(Pcre2ConditionKind.AnyRecursion, reference, assertion, 0, 0, false)], units: ['a'], sourceMap: [0, 1]),
                default, ref budget, out var plan, out var diagnostic);
            Assert.Equal(admitted, result);
            if (admitted)
            {
                Assert.Equal(Pcre2ConditionKind.AnyRecursion, plan!.Conditions[0].Kind);
            }
            else
            { Assert.Null(plan); Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Diagnostic.Code); Assert.Equal(0, budget.ReservedBytes); }
        }
    }
    [Theory]
    [InlineData(0)] // cyclic child
    [InlineData(1)] // orphan
    [InlineData(2)] // invalid source map
    [InlineData(3)] // duplicate capture node points to a single definition
    [InlineData(4)] // out of width literal
    [InlineData(5)] // invalid class algebra target
    public void MalformedProgramsCannotReachExecution(int mutation)
    {
        Pcre2Node[] nodes = mutation switch
        {
            0 => [Leaf(Pcre2NodeKind.Atomic) with { Children = new(0, 1) }],
            1 => [Leaf(Pcre2NodeKind.Empty), Leaf(Pcre2NodeKind.Empty)],
            3 => [Leaf(Pcre2NodeKind.Empty),Leaf(Pcre2NodeKind.Capture) with {Children=new(0,1)},
                Leaf(Pcre2NodeKind.Empty),Leaf(Pcre2NodeKind.Capture) with {Children=new(1,1)},
                Leaf(Pcre2NodeKind.Alternative) with {Children=new(2,2)}],
            4 => [Leaf(Pcre2NodeKind.Literal, 0x10000)],
            5 => [Leaf(Pcre2NodeKind.Class)],
            _ => [Leaf(Pcre2NodeKind.Literal, 'a')]
        };
        int[] children = mutation == 3 ? [0, 2, 1, 3] : mutation == 0 ? [0] : [];
        Pcre2GroupDefinition[] groups = mutation == 3 ? [new(1, -1, 1)] : [];
        Pcre2ClassNode[] classes = mutation == 5 ? [new(Pcre2ClassKind.Complement, 0, -1, default, default, 0)] : [];
        var budget = new CompileBudget(new());
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            mutation == 3 ? 2 : 1, new(nodes, children, classes, groups: groups, units: ['a'], sourceMap: mutation == 2 ? [0, 0] : [0, 1]),
            default, ref budget, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, error.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void SyntaxMayKeepUnresolvedNamedReferenceButExecutableMayNot()
    {
        var node = Leaf(Pcre2NodeKind.Backreference);
        Pcre2Reference[] refs = [new(Pcre2ReferenceKind.Named, 0, 0, default, -1)];
        Pcre2Name[] names = [new("a", default)];
        var syntax = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Syntax, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new([node], names: names, references: refs, units: ['a'], sourceMap: [0, 1]), default, ref syntax, out var tree, out _));
        Assert.False(tree!.IsExecutable);
        Assert.False(tree.TryAdmitProjection(Pcre2ProjectionPolicy.WholeSubjectValidated, out _));
        var executable = new CompileBudget(new());
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new([node], names: names, references: refs, units: ['a'], sourceMap: [0, 1]), default, ref executable, out _, out _));
    }
    [Fact]
    public void BranchResetDefinitionsMayShareNumberButCaptureEntriesStayDistinct()
    {
        Pcre2Node[] nodes = [Leaf(Pcre2NodeKind.Empty),Leaf(Pcre2NodeKind.Capture) with{Children=new(0,1)},
            Leaf(Pcre2NodeKind.Empty),Leaf(Pcre2NodeKind.Capture,1) with{Children=new(1,1)},
            Leaf(Pcre2NodeKind.Alternative) with{Children=new(2,2)}];
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 2,
            new(nodes, [0, 2, 1, 3], groups: [new(1, -1, 1), new(1, -1, 3)], units: ['a'], sourceMap: [0, 1]),
            default, ref budget, out var plan, out _));
        Assert.Equal(2, plan!.Groups.Length);
        Assert.Equal(2, plan.GroupCount);
    }
    [Theory]
    [InlineData(0, RegexDiagnosticCode.PlanLimitExceeded)]
    [InlineData(1, RegexDiagnosticCode.CompileMemoryLimitExceeded)]
    [InlineData(2, RegexDiagnosticCode.CompileWorkLimitExceeded)]
    public void PlanAdmissionFailuresDoNotRetainTemporaryMemory(int kind, RegexDiagnosticCode code)
    {
        var limits = kind switch { 0 => new RegexCompileLimits(maxPlanBytes: 1), 1 => new RegexCompileLimits(maxCompileMemoryBytes: 1), _ => new RegexCompileLimits(maxCompileWork: 1) };
        var budget = new CompileBudget(limits);
        Assert.False(Pcre2Program.TryCreate(new(), Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits, 1,
            new([Leaf(Pcre2NodeKind.Literal, 'a')], units: ['a'], sourceMap: [0, 1]), default, ref budget, out _, out var error));
        Assert.Equal(code, error.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.False(Pcre2Program.TryAddPlanBytes(long.MaxValue, 1, long.MaxValue, out _));
        Assert.False(Pcre2Program.TryAddPlanBytes(10, 2, 11, out _));
        Assert.True(Pcre2Program.TryAddPlanBytes(long.MaxValue - 1, 1, long.MaxValue, out var total));
        Assert.Equal(long.MaxValue, total);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallerZeroDepthCannotBeBypassedByAdequateFrameOrWindowCapacity(bool window)
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(1);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(maxDepth: 0), 3);
        var storage = new Pcre2Storage(run.Buffers, capacity, 1, new Pcre2OffsetPair[1]);
        Assert.False(window ? storage.PushWindow(default, ref machine, ref run) : storage.PushFrame(default, ref machine, ref run));
        Assert.Equal((int)Pcre2ErrorCode.Depthlimit, machine.SourceReturnCode);
        Assert.Equal(0, machine.FrameCount);
        Assert.Equal(0, machine.WindowDepth);
    }
    [Fact]
    public void SharedSlotsRoundTripAndRollbackWithoutDroppingParticipationOrCallDepth()
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(2);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(maxDepth: 4), 3);
        var storage = new Pcre2Storage(run.Buffers, capacity, 2, new Pcre2OffsetPair[2]);
        Assert.True(storage.InitializeCaptures(ref machine, ref run));
        Assert.False(storage.Capture(1).Participated);
        Assert.True(storage.WriteCapture(1, 0, 0, ref machine, ref run));
        Assert.True(storage.Capture(1).Participated);
        var frame = new Pcre2Frame(Pcre2FrameKind.Subroutine, 2, 1, 1, 0, 0, 0, -1, 3, 4, 5, 0, 1, 2, Pcre2CalloutFlags.Backtrack, 7);
        Assert.True(storage.PushFrame(frame, ref machine, ref run));
        Assert.Equal(1, machine.CallDepth);
        var window = new Pcre2Window(2, 1, 4, 1, 1, 1);
        Assert.True(storage.PushWindow(window, ref machine, ref run));
        Assert.True(storage.WriteCapture(1, 2, 3, ref machine, ref run));
        Assert.True(storage.RestoreCaptures(1, ref machine, ref run));
        Assert.Equal(new Pcre2OffsetPair(true, 0, 0), storage.Capture(1));
        Assert.True(storage.PopWindow(ref machine, ref run, out var restoredWindow));
        Assert.Equal(window, restoredWindow);
        Assert.True(storage.ReadFrame(0, ref machine, ref run, out var peek));
        Assert.Equal(frame, peek);
        Assert.True(storage.PopFrame(ref machine, ref run, out var restoredFrame));
        Assert.Equal(frame, restoredFrame);
        Assert.Equal(0, machine.CallDepth);
        Assert.True(storage.RestoreCaptures(0, ref machine, ref run));
        Assert.False(storage.Capture(1).Participated);
    }
    [Fact]
    public void LogicalDepthIsJointAndWorkLimitReductionIsCumulative()
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(1);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(maxDepth: 1), 3);
        var storage = new Pcre2Storage(run.Buffers, capacity, 1, new Pcre2OffsetPair[1]);
        Assert.True(storage.PushFrame(default, ref machine, ref run));
        Assert.False(storage.PushWindow(default, ref machine, ref run));
        Assert.Equal((int)Pcre2ErrorCode.Depthlimit, machine.SourceReturnCode);
        var work = new RunState(run.Buffers, new());
        var reduced = new Pcre2VmState(capacity, new(), 3, new(MatchWork: 1, Depth: 0, HeapBytes: 0));
        Assert.True(Pcre2Storage.Charge(ref reduced, ref work, 1, 0));
        Assert.False(Pcre2Storage.Charge(ref reduced, ref work, 1, 0));
        Assert.Equal(1, work.Budget.WorkConsumed);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, reduced.SourceReturnCode);
        Assert.Equal(0, reduced.Limits.MaxDepth);
        Assert.Equal(0, reduced.Limits.MaxWorkspaceBytes);
    }
    [Fact]
    public void HeapAndCancellationFailuresAreDistinctAndCapacityArithmeticIsChecked()
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(1);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(maxWorkspaceBytes: 0), 0);
        var storage = new Pcre2Storage(run.Buffers, capacity, 1, new Pcre2OffsetPair[1]);
        Assert.False(storage.InitializeCaptures(ref machine, ref run));
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, machine.SourceReturnCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new RunState(run.Buffers, new(), new() { CancellationToken = cancellation.Token });
        var zero = new Pcre2VmState(capacity, new(maxWork: 0), 0);
        Assert.False(Pcre2Storage.Charge(ref zero, ref cancelled, 0, 0));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, cancelled.Failure.Code);
        Assert.Throws<OverflowException>(() => new Pcre2ExecutionCapacity(int.MaxValue, 1, 1, 1).Required(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pcre2MatchOptions { Limits = new(maxDepth: 2) }.Resolve(new(maxDepth: 1)));
    }
    [Fact]
    public void FullCallbackStagingIsPreparedCountedAndIndependentOfNativeShortOutput()
    {
        const int groups = 4096;
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var requirement = capacity.RequiredStorage(groups);
        Assert.Equal(groups, requirement.CallbackPairSlots);
        Assert.Equal(requirement.Integers.RequiredBytes + 32L + 16L * groups, requirement.RequiredBytes);
        var size = requirement.Integers;
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(), 3);
        var staging = new Pcre2OffsetPair[groups];
        var storage = new Pcre2Storage(run.Buffers, capacity, groups, staging);
        Pcre2OffsetPair[] shortOutput = [new(true, 99, 100)];
        Assert.True(storage.InitializeCaptures(ref machine, ref run));
        Assert.True(storage.WriteCapture(groups - 1, 1, 2, ref machine, ref run));
        Assert.True(storage.PrepareCallbackPairs(true, ref machine, ref run));
        Assert.Equal(groups, storage.CallbackPairs.Length);
        Assert.False(storage.CallbackPairs[0].Participated);
        Assert.Equal(new Pcre2OffsetPair(true, 1, 2), storage.CallbackPairs[^1]);
        Assert.Equal(new Pcre2OffsetPair(true, 99, 100), shortOutput[0]);
        var denied = new Pcre2VmState(capacity, new(maxWorkspaceBytes: requirement.RequiredBytes - 1), 3);
        var other = new RunState(run.Buffers, new());
        Assert.False(storage.InitializeCaptures(ref denied, ref other));
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, denied.SourceReturnCode);
    }
    [Fact]
    public void IntegerOnlyStorageSharesRollbackAndStillChargesFullPreparedHeap()
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var requirement = capacity.RequiredStorage(2);
        var size = requirement.Integers;
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(), 0);
        var kernel = new Pcre2Storage(run.Buffers, capacity, 2);
        var composition = new Pcre2Storage(run.Buffers, capacity, 2, new Pcre2OffsetPair[2]);
        Assert.True(kernel.InitializeCaptures(ref machine, ref run));
        Assert.True(kernel.WriteCapture(1, 1, 2, ref machine, ref run));
        Assert.Equal(new Pcre2OffsetPair(true, 1, 2), composition.Capture(1));
        Assert.True(kernel.RestoreCaptures(0, ref machine, ref run));
        Assert.False(composition.Capture(1).Participated);
        var denied = new Pcre2VmState(capacity, new(maxWorkspaceBytes: requirement.RequiredBytes - 1), 0);
        var other = new RunState(run.Buffers, new());
        Assert.False(kernel.InitializeCaptures(ref denied, ref other));
        Assert.Equal((int)Pcre2ErrorCode.Heaplimit, denied.SourceReturnCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IntegerOnlyStorageCannotExposeOrPrepareCallbacks(bool prepare)
    {
        Assert.Throws<InvalidOperationException>(() => UseUnavailableCallbackView(prepare));
    }
    [Fact]
    public void NestedWindowsCanBeInspectedWithoutPopOrDepthMutationAndDenialPreservesThem()
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(1);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(maxWork: 4, maxDepth: 2), 0);
        var storage = new Pcre2Storage(run.Buffers, capacity, 1);
        var outer = new Pcre2Window(20, 2, 1, 3, 0, 0);
        var inner = new Pcre2Window(10, 5, 2, 6, 0, 0);
        Assert.True(storage.PushWindow(outer, ref machine, ref run));
        Assert.True(storage.PushWindow(inner, ref machine, ref run));
        Assert.True(storage.ReadWindow(0, ref machine, ref run, out var first));
        Assert.Equal(outer, first);
        Assert.True(storage.ReadWindow(1, ref machine, ref run, out var second));
        Assert.Equal(inner, second);
        Assert.Equal(2, machine.WindowDepth);
        Assert.Equal(4, run.Budget.WorkConsumed);
        Assert.False(storage.ReadWindow(0, ref machine, ref run, out var denied));
        Assert.Equal(default, denied);
        Assert.Equal(2, machine.WindowDepth);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, machine.SourceReturnCode);
        // Terminal source denial leaves stored windows reusable without mutation.
        var resumed = new Pcre2VmState(capacity, new(), 0) { WindowDepth = 2 };
        var other = new RunState(run.Buffers, new());
        Assert.True(storage.PopWindow(ref resumed, ref other, out var preserved));
        Assert.Equal(inner, preserved);
        Assert.True(storage.PopWindow(ref resumed, ref other, out preserved));
        Assert.Equal(outer, preserved);
    }
    private static void UseUnavailableCallbackView(bool prepare)
    {
        var capacity = new Pcre2ExecutionCapacity(4, 8, 4, 8);
        var size = capacity.Required(1);
        var run = new RunState(new(new int[size.StateSlots], new int[size.CaptureSlots], new int[size.ScratchSlots]), new());
        var machine = new Pcre2VmState(capacity, new(), 0);
        var storage = new Pcre2Storage(run.Buffers, capacity, 1);
        if (prepare)
        {
            storage.PrepareCallbackPairs(true, ref machine, ref run);
        }
        else
        {
            _ = storage.CallbackPairs.Length;
        }
    }
    private static Pcre2Node Leaf(Pcre2NodeKind kind, int operand = 0) => new(kind, default, operand, 0, 0,
        Pcre2Quantifier.Greedy, 0, 0, new SourceRange(0, 1, RegexOffsetUnit.Utf16CodeUnits));
}
