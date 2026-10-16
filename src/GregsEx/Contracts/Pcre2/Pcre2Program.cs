using GregsEx.IR;

namespace GregsEx.Contracts.Pcre2;

// Immutable copied syntax/executable tree. Grammar admissibility and conservative
// lookbehind/reference length proofs are lowerer obligations; this boundary
// independently rejects malformed structure/metadata/capacities before dispatch.
internal sealed class Pcre2Program
{
    private readonly Pcre2Node[] _nodes;
    private readonly int[] _children, _numbers, _units, _sourceMap;
    private readonly byte[] _byteUnits, _startBitmap;
    private readonly Pcre2LookbehindBranch[] _lookbehindBranches;
    private readonly char[] _charUnits;
    private readonly Pcre2ClassNode[] _classes;
    private readonly Pcre2Interval[] _intervals;
    private readonly Pcre2GroupDefinition[] _groups;
    private readonly Pcre2Name[] _names;
    private readonly Pcre2Reference[] _references;
    private readonly Pcre2Assertion[] _assertions;
    private readonly Pcre2Condition[] _conditions;
    private readonly Pcre2CalloutSite[] _callouts;
    private readonly Pcre2Control[] _controls;
    private readonly Pcre2Slice[] _marks, _scanReferences;
    internal Pcre2Profile Profile { get; }
    internal Pcre2CompileFlags ArgumentFlags { get; }
    // Source-prefix NOTEMPTY directives are compile-time inputs with match-time effects.
    internal Pcre2MatchFlags PatternMatchFlags { get; }
    internal Pcre2SearchHints? SearchHints { get; }
    internal ReadOnlySpan<byte> StartBitmap => _startBitmap;
    // Pinned PCRE2_INFO_HASCRORLF compile provenance, not normalized class membership.
    internal bool HasExplicitCrOrLf { get; }
    // Pinned PCRE2_INFO_JCHANGED lexical option provenance, even for redundant J/-J.
    internal bool HasDuplicateNameOptionChange { get; }
    // Pinned PCRE2_INFO_HASBACKSLASHC provenance before raw/zero-repeat lowering.
    internal bool HasBackslashC { get; }
    internal bool HasLookbehindMetadata { get; }
    internal ReadOnlySpan<Pcre2LookbehindBranch> LookbehindBranches => _lookbehindBranches;
    internal Pcre2PlanStage Stage { get; }
    internal int SourceLength { get; }
    internal int NativePatternLength { get; }
    internal RegexOffsetUnit SourceUnit { get; }
    internal int GroupCount { get; }
    internal int Root => _nodes.Length - 1;
    internal long RetainedBytes { get; }
    internal Pcre2PatternLimits PatternLimits { get; }
    internal Pcre2ProjectionRestrictions ProjectionRestrictions { get; }
    internal ReadOnlySpan<Pcre2Node> Nodes => _nodes;
    internal ReadOnlySpan<int> Children => _children;
    internal ReadOnlySpan<int> Numbers => _numbers;
    internal ReadOnlySpan<int> Units => _units;
    internal ReadOnlySpan<int> SourceMap => _sourceMap;
    internal ReadOnlySpan<Pcre2ClassNode> Classes => _classes;
    internal ReadOnlySpan<Pcre2Interval> Intervals => _intervals;
    internal ReadOnlySpan<Pcre2GroupDefinition> Groups => _groups;
    internal ReadOnlySpan<Pcre2Name> Names => _names;
    internal ReadOnlySpan<Pcre2Reference> References => _references;
    internal ReadOnlySpan<Pcre2Assertion> Assertions => _assertions;
    internal ReadOnlySpan<Pcre2Condition> Conditions => _conditions;
    internal ReadOnlySpan<Pcre2CalloutSite> Callouts => _callouts;
    internal ReadOnlySpan<Pcre2Control> Controls => _controls;
    internal ReadOnlySpan<Pcre2Slice> Marks => _marks;
    internal ReadOnlySpan<Pcre2Slice> ScanReferences => _scanReferences;
    internal bool IsExecutable => Stage == Pcre2PlanStage.Executable;
    private Pcre2Program(Pcre2Profile profile, Pcre2PlanStage stage, int sourceLength,
        int nativeLength, RegexOffsetUnit sourceUnit, int groupCount, Pcre2PlanData data,
        Pcre2PatternLimits patternLimits, Pcre2ProjectionRestrictions restrictions, long retainedBytes, Pcre2CompileFlags argumentFlags,
        Pcre2MatchFlags patternMatchFlags, Pcre2SearchHints? searchHints, ReadOnlySpan<byte> startBitmap,
        bool hasExplicitCrOrLf = false, bool hasDuplicateNameOptionChange = false, bool hasBackslashC = false)
    {
        Profile = profile;
        Stage = stage;
        SourceLength = sourceLength;
        NativePatternLength = nativeLength;
        SourceUnit = sourceUnit;
        GroupCount = groupCount;
        PatternLimits = patternLimits;
        ArgumentFlags = argumentFlags;
        PatternMatchFlags = patternMatchFlags;
        SearchHints = searchHints;
        HasExplicitCrOrLf = hasExplicitCrOrLf;
        HasDuplicateNameOptionChange = hasDuplicateNameOptionChange;
        HasBackslashC = hasBackslashC;
        _startBitmap = startBitmap.ToArray();
        _lookbehindBranches = data.LookbehindBranches.ToArray();
        HasLookbehindMetadata = data.HasLookbehindMetadata;
        ProjectionRestrictions = restrictions;
        RetainedBytes = retainedBytes;
        _nodes = data.Nodes.ToArray();
        _children = data.Children.ToArray();
        _classes = data.Classes.ToArray();
        _intervals = data.Intervals.ToArray();
        _groups = data.Groups.ToArray();
        _names = data.Names.ToArray();
        _numbers = data.Numbers.ToArray();
        _references = data.References.ToArray();
        _assertions = data.Assertions.ToArray();
        _conditions = data.Conditions.ToArray();
        _callouts = data.Callouts.ToArray();
        _controls = data.Controls.ToArray();
        _marks = data.Marks.ToArray();
        _scanReferences = data.ScanReferences.ToArray();
        _units = data.Units.ToArray();
        _sourceMap = data.SourceMap.ToArray();
        _byteUnits = profile.Width == Pcre2CodeUnitWidth.Eight ? new byte[data.Units.Length] : [];
        _charUnits = profile.Width == Pcre2CodeUnitWidth.Sixteen ? new char[data.Units.Length] : [];
        for (var i = 0; i < data.Units.Length; i++)
        {
            if (profile.Width == Pcre2CodeUnitWidth.Eight)
            {
                _byteUnits[i] = (byte)data.Units[i];
            }
            else
            {
                _charUnits[i] = (char)data.Units[i];
            }
        }
    }
    internal ReadOnlySpan<byte> NativeBytes(Pcre2Slice slice)
    {
        if (Profile.Width != Pcre2CodeUnitWidth.Eight)
        {
            throw new InvalidOperationException("Native byte view requires width8.");
        }

        return _byteUnits.AsSpan(slice.Offset, slice.Count);
    }
    internal ReadOnlySpan<char> NativeChars(Pcre2Slice slice)
    {
        if (Profile.Width != Pcre2CodeUnitWidth.Sixteen)
        {
            throw new InvalidOperationException("Native char view requires width16.");
        }

        return _charUnits.AsSpan(slice.Offset, slice.Count);
    }

    internal bool TryAdmitProjection(Pcre2ProjectionPolicy policy, out Pcre2Diagnostic diagnostic)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        diagnostic = default;
        if (!IsExecutable || ProjectionRestrictions != Pcre2ProjectionRestrictions.None)
        {
            diagnostic = new(new(RegexDiagnosticCode.UnsupportedFeature, 0, SourceLength, SourceUnit),
                Pcre2DiagnosticKind.Capability, CapabilityId: "PH-002/PH-003/PC-017/PC-023");
            return false;
        }
        return true;
    }

    internal static bool TryCreate(Pcre2Profile profile, Pcre2PlanStage stage, int sourceLength,
        int nativeLength, RegexOffsetUnit sourceUnit, int groupCount, Pcre2PlanData data,
        Pcre2PatternLimits patternLimits, ref CompileBudget budget,
        out Pcre2Program? program, out Pcre2Diagnostic diagnostic, Pcre2CompileFlags? argumentFlags = null,
        Pcre2MatchFlags patternMatchFlags = default, Pcre2SearchHints? searchHints = null,
        ReadOnlySpan<byte> startBitmap = default, bool hasExplicitCrOrLf = false, bool hasDuplicateNameOptionChange = false, bool hasBackslashC = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        program = null;
        diagnostic = default;
        var source = new SourceRange(0, Math.Max(0, sourceLength), sourceUnit);
        var limits = budget.Limits;
        var code = !Enum.IsDefined(stage) || !Enum.IsDefined(sourceUnit) || sourceLength < 0 || nativeLength < 0 ||
            (!data.HasLookbehindMetadata && !data.LookbehindBranches.IsEmpty) ||
            (stage != Pcre2PlanStage.Executable && (data.HasLookbehindMetadata || searchHints.HasValue)) ||
            (!startBitmap.IsEmpty && (startBitmap.Length != 32 || !searchHints.HasValue)) ||
            data.Nodes.IsEmpty || groupCount is < 1 or > 65536 || patternLimits.MatchWork < 0 ||
            patternLimits.Depth < 0 || patternLimits.HeapBytes < 0 ||
            (patternMatchFlags & ~(Pcre2MatchFlags.Notempty | Pcre2MatchFlags.NotemptyAtstart)) != 0 ||
            ((uint)(argumentFlags ?? profile.Flags) & ~0xEFFFFFFFu) != 0 ? RegexDiagnosticCode.InvalidProgram :
            Math.Max(sourceLength, nativeLength) > limits.MaxPatternUnits ? RegexDiagnosticCode.PatternLimitExceeded :
            data.Nodes.Length + (long)data.Classes.Length > limits.MaxInstructions ? RegexDiagnosticCode.ProgramLimitExceeded :
            data.Intervals.Length > limits.MaxClassRanges ? RegexDiagnosticCode.ClassLimitExceeded :
            groupCount > limits.MaxCaptureGroups ? RegexDiagnosticCode.CaptureLimitExceeded : RegexDiagnosticCode.None;
        if (code != RegexDiagnosticCode.None)
        {
            return Reject(code, source, out diagnostic);
        }

        long bytes;
        try
        {
            bytes = checked(1536L + 64L * data.Nodes.Length + 4L * data.Children.Length + 48L * data.Classes.Length +
                8L * data.Intervals.Length + 16L * data.Groups.Length + 48L * data.Names.Length + 4L * data.Numbers.Length +
                32L * data.References.Length + 24L * data.Assertions.Length + 16L * data.LookbehindBranches.Length +
                64L + startBitmap.Length + 32L * data.Conditions.Length +
                40L * data.Callouts.Length + 8L * data.Controls.Length + 8L * (data.Marks.Length + (long)data.ScanReferences.Length) +
                4L * (data.Units.Length + (long)data.SourceMap.Length) + 64L +
                (profile.Width == Pcre2CodeUnitWidth.Eight ? 1L : 2L) * data.Units.Length);
        }
        catch (OverflowException) { return Reject(RegexDiagnosticCode.PlanLimitExceeded, source, out diagnostic); }
        if (bytes > limits.MaxPlanBytes)
        {
            return Reject(RegexDiagnosticCode.PlanLimitExceeded, source, out diagnostic);
        }

        foreach (var name in data.Names)
        {
            if (name.Name is null)
            {
                return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
            }

            if (!budget.TryCharge(1, source, out var charge))
            {
                return Wrap(charge, out diagnostic);
            }

            if (!TryAddPlanBytes(bytes, checked(32L + 2L * name.Name.Length), limits.MaxPlanBytes, out bytes))
            {
                return Reject(RegexDiagnosticCode.PlanLimitExceeded, source, out diagnostic);
            }
        }
        if (bytes > limits.MaxPlanBytes)
        {
            return Reject(RegexDiagnosticCode.PlanLimitExceeded, source, out diagnostic);
        }

        if (data.Units.Length < nativeLength || data.SourceMap.Length != (long)nativeLength + 1 ||
            data.SourceMap[0] != 0 || data.SourceMap[^1] != sourceLength)
        {
            return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
        }

        var scratch = checked(256L + 12L * data.Nodes.Length + 4L * groupCount);
        if (!budget.TryReserveBytes(scratch, source, out var generic))
        {
            return Wrap(generic, out diagnostic);
        }

        try
        {
            var depths = new int[data.Nodes.Length];
            var uses = new int[data.Nodes.Length];
            var parents = new int[data.Nodes.Length];
            Array.Fill(parents, -1);
            var seenGroups = new int[groupCount];
            var restrictions = (profile.Flags & Pcre2CompileFlags.MatchInvalidUtf) != 0
                ? Pcre2ProjectionRestrictions.InvalidUtf : Pcre2ProjectionRestrictions.None;
            if ((profile.Flags & Pcre2CompileFlags.NoUtfCheck) != 0)
            {
                restrictions |= Pcre2ProjectionRestrictions.NoUtfCheck;
            }

            for (var i = 0; i < data.SourceMap.Length; i++)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (data.SourceMap[i] < 0 || data.SourceMap[i] > sourceLength || i > 0 && data.SourceMap[i] < data.SourceMap[i - 1])
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var unit in data.Units)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (unit < 0 || unit > (profile.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            for (var i = 0; i < data.Nodes.Length; i++)
            {
                var node = data.Nodes[i];
                if (!budget.TryCharge(1, node.Source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (!Enum.IsDefined(node.Kind) || !Enum.IsDefined(node.Quantifier) ||
                    !Slice(node.Children, data.Children.Length) || node.Source.Unit != sourceUnit ||
                    node.Source.Offset < 0 || node.Source.Length < 0 || (long)node.Source.Offset + node.Source.Length > sourceLength ||
                    ((uint)node.Flags & ~0xEFFFFFFFu) != 0 || ((uint)node.ExtraFlags & ~0x1FFFFu) != 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, node.Source, out diagnostic);
                }

                var count = node.Children.Count;
                var validCount = node.Kind switch
                {
                    Pcre2NodeKind.Sequence => count >= 1,
                    Pcre2NodeKind.Alternative => count >= 2,
                    Pcre2NodeKind.Conditional => count is >= 1 and <= 3,
                    Pcre2NodeKind.Repeat or Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.Assertion or
                    Pcre2NodeKind.ScanSubstring or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Define => count == 1,
                    _ => count == 0
                };
                if (!validCount || node.Kind != Pcre2NodeKind.Repeat &&
                    (node.Min != 0 || node.Max != 0 || node.Quantifier != Pcre2Quantifier.Greedy))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, node.Source, out diagnostic);
                }

                depths[i] = 1;
                foreach (var child in data.Children.Slice(node.Children.Offset, count))
                {
                    if (!budget.TryCharge(1, node.Source, out generic))
                    {
                        return Wrap(generic, out diagnostic);
                    }

                    if (child < 0 || child >= i)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, node.Source, out diagnostic);
                    }

                    uses[child]++;
                    parents[child] = i;
                    depths[i] = Math.Max(depths[i], depths[child] + 1);
                    var range = data.Nodes[child].Source;
                    if (range.Offset < node.Source.Offset || (long)range.Offset + range.Length > (long)node.Source.Offset + node.Source.Length)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, node.Source, out diagnostic);
                    }
                }
                if (depths[i] > limits.MaxNestingDepth)
                {
                    return Reject(RegexDiagnosticCode.NestingLimitExceeded, node.Source, out diagnostic);
                }

                var validOperand = node.Kind switch
                {
                    Pcre2NodeKind.Literal => node.Operand >= 0 && node.Operand <= (profile.Utf ? 0x10FFFF : profile.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535) &&
                        (!profile.Utf || node.Operand is not (>= 0xD800 and <= 0xDFFF) ||
                        profile.Width == Pcre2CodeUnitWidth.Eight && profile.ExtraFlags.HasFlag(Pcre2ExtraFlags.AllowSurrogateEscapes)),
                    Pcre2NodeKind.Class => Index(node.Operand, data.Classes.Length),
                    Pcre2NodeKind.Capture => Index(node.Operand, data.Groups.Length) && data.Groups[node.Operand].EntryNode == i,
                    Pcre2NodeKind.Backreference => Index(node.Operand, data.References.Length) &&
                        data.References[node.Operand].Kind != Pcre2ReferenceKind.WholePattern,
                    Pcre2NodeKind.Subroutine => Index(node.Operand, data.References.Length) &&
                        (stage == Pcre2PlanStage.Syntax || data.References[node.Operand].Kind == Pcre2ReferenceKind.WholePattern ||
                        Index(data.References[node.Operand].TargetDefinition, data.Groups.Length)),
                    Pcre2NodeKind.Assertion => Index(node.Operand, data.Assertions.Length),
                    Pcre2NodeKind.Conditional => Index(node.Operand, data.Conditions.Length) &&
                        (data.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? count is 2 or 3 &&
                        data.Children[node.Children.Offset] == data.Conditions[node.Operand].AssertionNode : count is 1 or 2),
                    Pcre2NodeKind.Callout => Index(node.Operand, data.Callouts.Length),
                    Pcre2NodeKind.Verb => Index(node.Operand, data.Controls.Length),
                    Pcre2NodeKind.ScanSubstring => Index(node.Operand, data.ScanReferences.Length),
                    Pcre2NodeKind.ScriptRun => node.Operand is 0 or 1,
                    Pcre2NodeKind.Repeat => node.Operand == 0 && node.Min is >= 0 and <= 65535 &&
                        (node.Max == -1 || node.Max >= node.Min && node.Max <= 65535),
                    _ => node.Operand == 0
                };
                if (!validOperand)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, node.Source, out diagnostic);
                }

                if (profile.Utf && node.Kind == Pcre2NodeKind.CodeUnit)
                {
                    restrictions |= Pcre2ProjectionRestrictions.UtfCodeUnit;
                }
            }
            for (var i = 0; i < uses.Length; i++)
            {
                if (!budget.TryCharge(1, data.Nodes[i].Source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (uses[i] != (i == data.Nodes.Length - 1 ? 0 : 1))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var zeroWidthBarrier = false;
                if (data.Nodes[i].Kind is Pcre2NodeKind.ResetStart or Pcre2NodeKind.CodeUnit)
                {
                    for (var parent = parents[i]; parent >= 0; parent = parents[parent])
                    {
                        if (!budget.TryCharge(1, source, out generic))
                        {
                            return Wrap(generic, out diagnostic);
                        }

                        if (data.Nodes[parent].Kind == Pcre2NodeKind.ScanSubstring)
                        {
                            zeroWidthBarrier = true;
                        }

                        if (data.Nodes[parent].Kind == Pcre2NodeKind.Assertion)
                        {
                            if (data.Nodes[i].Kind == Pcre2NodeKind.ResetStart)
                            {
                                if (stage == Pcre2PlanStage.Executable && !profile.ExtraFlags.HasFlag(Pcre2ExtraFlags.AllowLookaroundBsk))
                                {
                                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                                }

                                restrictions |= Pcre2ProjectionRestrictions.LookaroundReset;
                            }
                            else if (stage == Pcre2PlanStage.Executable && profile.Utf && !zeroWidthBarrier &&
                                data.Assertions[data.Nodes[parent].Operand].Kind is Pcre2AssertionKind.PositiveBehind or
                                Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind)
                            {
                                return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                            }

                            if (data.Assertions[data.Nodes[parent].Operand].Kind is Pcre2AssertionKind.PositiveAhead or
                                Pcre2AssertionKind.NegativeAhead or Pcre2AssertionKind.NonAtomicAhead)
                            {
                                zeroWidthBarrier = true;
                            }
                        }
                    }
                }
            }
            for (var i = 0; i < data.Groups.Length; i++)
            {
                var group = data.Groups[i];
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (group.Number < 1 || group.Number >= groupCount || group.ParentDefinition < -1 || group.ParentDefinition >= i ||
                    !Index(group.EntryNode, data.Nodes.Length) || data.Nodes[group.EntryNode].Kind != Pcre2NodeKind.Capture ||
                    data.Nodes[group.EntryNode].Operand != i)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                seenGroups[group.Number]++;
                var actualParent = parents[group.EntryNode];
                while (actualParent >= 0 && data.Nodes[actualParent].Kind != Pcre2NodeKind.Capture)
                { if (!budget.TryCharge(1, source, out generic)) { return Wrap(generic, out diagnostic); } actualParent = parents[actualParent]; }
                if ((actualParent < 0 ? -1 : data.Nodes[actualParent].Operand) != group.ParentDefinition)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            for (var number = 1; number < groupCount; number++)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (seenGroups[number] == 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var number in data.Numbers)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (number < 1 || number >= groupCount)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var name in data.Names)
            {
                if (!budget.TryCharge(1L + name.Name.Length, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (name.Name.Length == 0 || name.Name.Length > sourceLength || !Slice(name.Numbers, data.Numbers.Length) ||
                    stage == Pcre2PlanStage.Executable && name.Numbers.Count == 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var previous = 0;
                foreach (var number in data.Numbers.Slice(name.Numbers.Offset, name.Numbers.Count))
                {
                    if (!budget.TryCharge(1, source, out generic))
                    {
                        return Wrap(generic, out diagnostic);
                    }

                    if (number <= previous)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }

                    previous = number;
                }
            }
            foreach (var reference in data.References)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (!Enum.IsDefined(reference.Kind) || !Slice(reference.Candidates, data.Numbers.Length) ||
                    (reference.Kind == Pcre2ReferenceKind.Named ? !Index(reference.NameIndex, data.Names.Length) : reference.NameIndex != -1) ||
                    reference.TargetDefinition < -1 || reference.TargetDefinition >= data.Groups.Length ||
                    reference.Kind == Pcre2ReferenceKind.Absolute && (reference.Number < 1 || reference.Number >= groupCount) ||
                    reference.Kind == Pcre2ReferenceKind.Relative && reference.Number == 0 ||
                    stage == Pcre2PlanStage.Executable && reference.Kind != Pcre2ReferenceKind.WholePattern && reference.Candidates.Count == 0 ||
                    reference.Kind == Pcre2ReferenceKind.WholePattern && (reference.Number != 0 || reference.TargetDefinition != -1 || reference.Candidates.Count != 0))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                if (stage == Pcre2PlanStage.Executable)
                {
                    var candidates = data.Numbers.Slice(reference.Candidates.Offset, reference.Candidates.Count);
                    if (reference.Kind == Pcre2ReferenceKind.Named)
                    {
                        var name = data.Names[reference.NameIndex];
                        if (!budget.TryCharge(candidates.Length, source, out generic))
                        {
                            return Wrap(generic, out diagnostic);
                        }

                        if (!candidates.SequenceEqual(data.Numbers.Slice(name.Numbers.Offset, name.Numbers.Count)))
                        {
                            return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                        }
                    }
                    if (reference.Kind == Pcre2ReferenceKind.Absolute &&
                        (candidates.Length != 1 || candidates[0] != reference.Number) ||
                        reference.TargetDefinition >= 0 && (candidates.IsEmpty || data.Groups[reference.TargetDefinition].Number != candidates[0]))
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }
                }
            }
            foreach (var assertion in data.Assertions)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                var behind = assertion.Kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind;
                if (!Enum.IsDefined(assertion.Kind) || assertion.MinimumLength < 0 || assertion.MaximumLength < -1 ||
                    assertion.MaximumLength >= 0 && assertion.MaximumLength < assertion.MinimumLength ||
                    stage == Pcre2PlanStage.Executable && behind && (assertion.MaximumLength < 0 ||
                    assertion.MaximumLength > 65535 ||
                    !data.HasLookbehindMetadata && assertion.MinimumLength != assertion.MaximumLength && assertion.MaximumLength > profile.MaxVariableLookbehind))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            // Canonical branch order: ascending assertion node, then source alternative order.
            // Absence is a deliberate legacy-fixture fallback; normalized production plans
            // carry all top-level behind branches, even fixed branches of unequal length.
            int branchCursor = 0, maximumLookbehind = 0;
            for (var id = 0; id < data.Nodes.Length; id++)
            {
                var node = data.Nodes[id];
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (node.Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary)
                {
                    maximumLookbehind = Math.Max(maximumLookbehind, 1);
                }

                if (node.Kind != Pcre2NodeKind.Assertion)
                {
                    continue;
                }

                var assertion = data.Assertions[node.Operand];
                var behind = assertion.Kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind;
                if (!behind)
                {
                    if (assertion.VariableLength)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }

                    continue;
                }
                maximumLookbehind = Math.Max(maximumLookbehind, assertion.MaximumLength);
                if (!data.HasLookbehindMetadata)
                {
                    continue;
                }

                if (assertion.VariableLength && (assertion.MinimumLength == assertion.MaximumLength ||
                    assertion.MaximumLength > profile.MaxVariableLookbehind))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var childId = data.Children[node.Children.Offset];
                var child = data.Nodes[childId];
                var count = child.Kind == Pcre2NodeKind.Alternative ? child.Children.Count : 1;
                int minimum = int.MaxValue, maximum = 0;
                for (var ordinal = 0; ordinal < count; ordinal++)
                {
                    if (!budget.TryCharge(1, source, out generic))
                    {
                        return Wrap(generic, out diagnostic);
                    }

                    if (branchCursor >= data.LookbehindBranches.Length)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }

                    var branch = data.LookbehindBranches[branchCursor++];
                    var expected = child.Kind == Pcre2NodeKind.Alternative ? data.Children[child.Children.Offset + ordinal] : childId;
                    if (branch.AssertionNode != id || branch.BranchNode != expected || branch.MaximumLength is < 0 or > 65535 ||
                        branch.MaximumLength < assertion.MinimumLength)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }

                    minimum = Math.Min(minimum, branch.MaximumLength);
                    maximum = Math.Max(maximum, branch.MaximumLength);
                }
                if (maximum != assertion.MaximumLength || !assertion.VariableLength && minimum != assertion.MinimumLength)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            if (branchCursor != data.LookbehindBranches.Length)
            {
                return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
            }

            if (searchHints is { } hints)
            {
                var widest = profile.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535;
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (hints.MaximumBackreference is < 0 or > 65535 || hints.MaximumBackreference >= groupCount || hints.MinimumLength < 0 || hints.MaximumLookbehind is < 0 or > 65535 ||
                    hints.MaximumLookbehind < maximumLookbehind ||
                    hints.FirstCodeUnit < -1 || hints.FirstCodeUnit > widest || hints.RequiredCodeUnit < -1 || hints.RequiredCodeUnit > widest ||
                    hints.FirstCodeUnitCaseless && hints.FirstCodeUnit < 0 || hints.RequiredCodeUnitCaseless && hints.RequiredCodeUnit < 0 ||
                    hints.StartLine && hints.FirstCodeUnit >= 0 || !startBitmap.IsEmpty && hints.FirstCodeUnit >= 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var condition in data.Conditions)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                var valid = condition.Kind switch
                {
                    Pcre2ConditionKind.AnyRecursion => condition.ReferenceIndex == -1 && condition.AssertionNode == -1,
                    Pcre2ConditionKind.CaptureSet or Pcre2ConditionKind.Recursion => Index(condition.ReferenceIndex, data.References.Length) && condition.AssertionNode == -1,
                    Pcre2ConditionKind.Assertion => condition.ReferenceIndex == -1 && Index(condition.AssertionNode, data.Nodes.Length) &&
                        data.Nodes[condition.AssertionNode].Kind == Pcre2NodeKind.Assertion &&
                        data.Assertions[data.Nodes[condition.AssertionNode].Operand].Kind is not (Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind),
                    Pcre2ConditionKind.Version => condition.ReferenceIndex == -1 && condition.AssertionNode == -1 && condition.VersionMajor >= 0 && condition.VersionMinor >= 0,
                    _ => false
                };
                if (!valid)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var site in data.Callouts)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (site.Number is < 0 or > 255 || site.Automatic && site.Number != 255 || site.IsString && site.Number != 0 ||
                    !Slice(site.StringUnits, data.Units.Length) || !site.IsString && site.StringUnits.Count != 0 ||
                    site.PatternPosition < 0 || site.NextItemLength < 0 || (long)site.PatternPosition + site.NextItemLength > nativeLength ||
                    site.StringSourceOffset < 0 || site.StringSourceOffset > nativeLength)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var mark in data.Marks)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (!Slice(mark, data.Units.Length))
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            foreach (var scan in data.ScanReferences)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (!Slice(scan, data.References.Length) || scan.Count == 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                foreach (var reference in data.References.Slice(scan.Offset, scan.Count))
                {
                    if (!budget.TryCharge(1, source, out generic))
                    {
                        return Wrap(generic, out diagnostic);
                    }

                    if (reference.Kind == Pcre2ReferenceKind.WholePattern || stage == Pcre2PlanStage.Executable && reference.Candidates.Count == 0)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }
                }
            }
            foreach (var control in data.Controls)
            {
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                if (!Enum.IsDefined(control.Kind) || control.MarkIndex < -1 || control.MarkIndex >= data.Marks.Length ||
                    control.Kind == Pcre2VerbKind.Mark && control.MarkIndex < 0)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
            }
            for (var i = 0; i < data.Classes.Length; i++)
            {
                var item = data.Classes[i];
                if (!budget.TryCharge(1, source, out generic))
                {
                    return Wrap(generic, out diagnostic);
                }

                var binary = item.Kind is Pcre2ClassKind.Union or Pcre2ClassKind.Intersection or Pcre2ClassKind.Subtraction or Pcre2ClassKind.SymmetricDifference;
                var unary = item.Kind == Pcre2ClassKind.Complement;
                if (!Enum.IsDefined(item.Kind) || (binary || unary ? item.Left < 0 || item.Left >= i : item.Left != -1) ||
                    (binary ? item.Right < 0 || item.Right >= i : item.Right != -1) ||
                    !Slice(item.Intervals, data.Intervals.Length) || item.Kind != Pcre2ClassKind.Intervals && item.Intervals.Count != 0 ||
                    item.Kind == Pcre2ClassKind.Property && !Pcre2DataAbi.IsValidProperty(item.Property) ||
                    item.Kind == Pcre2ClassKind.TableClass && item.TableClass is < 0 or > 9)
                {
                    return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var previous = -2;
                foreach (var interval in data.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count))
                {
                    if (!budget.TryCharge(1, source, out generic))
                    {
                        return Wrap(generic, out diagnostic);
                    }

                    if (interval.First < 0 || interval.Last < interval.First || interval.Last > 0x10FFFF || interval.First <= previous + 1)
                    {
                        return Reject(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }

                    previous = interval.Last;
                }
            }
            var copyWork = checked((long)data.Nodes.Length + data.Children.Length + data.Classes.Length + data.Intervals.Length +
                data.Groups.Length + data.Names.Length + data.Numbers.Length + data.References.Length + data.Assertions.Length +
                data.Conditions.Length + data.Callouts.Length + data.Controls.Length + data.Marks.Length + data.ScanReferences.Length +
                2L * data.Units.Length + data.SourceMap.Length + data.LookbehindBranches.Length + startBitmap.Length);
            if (!budget.TryCharge(copyWork, source, out generic) ||
                !budget.TryReserveBytes(bytes, source, out generic))
            {
                return Wrap(generic, out diagnostic);
            }

            program = new(profile, stage, sourceLength, nativeLength, sourceUnit, groupCount, data, patternLimits, restrictions, bytes, argumentFlags ?? profile.Flags, patternMatchFlags, searchHints, startBitmap, hasExplicitCrOrLf, hasDuplicateNameOptionChange, hasBackslashC);
            return true;
        }
        finally { budget.ReleaseBytes(scratch); }
    }
    private static bool Slice(Pcre2Slice slice, int length) => slice.Offset >= 0 && slice.Count >= 0 && (long)slice.Offset + slice.Count <= length;
    private static bool Index(int index, int length) => (uint)index < (uint)length;
    internal static bool TryAddPlanBytes(long current, long addition, long ceiling, out long total)
    {
        total = 0;
        if (current < 0 || addition < 0 || ceiling < 0 || current > ceiling || addition > ceiling - current)
        {
            return false;
        }

        total = checked(current + addition);
        return true;
    }
    private static bool Wrap(RegexDiagnostic generic, out Pcre2Diagnostic diagnostic)
    { diagnostic = new(generic, Pcre2DiagnosticKind.CompileLimit); return false; }
    private static bool Reject(RegexDiagnosticCode code, SourceRange source, out Pcre2Diagnostic diagnostic)
    {
        diagnostic = new(new(code, source.Offset, source.Length, source.Unit),
            code == RegexDiagnosticCode.InvalidProgram ? Pcre2DiagnosticKind.InvalidProgram : Pcre2DiagnosticKind.CompileLimit);
        return false;
    }
}
