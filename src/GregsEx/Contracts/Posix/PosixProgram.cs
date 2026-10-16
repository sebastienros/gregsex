using GregsEx.IR;

namespace GregsEx.Contracts.Posix;

// Validated immutable syntactic tree, not an ordered-core plan. Preserve node
// source/nesting through lowering: subpattern histories, not final group lengths,
// select the winner. A bounded reference verifier may consume this tree directly.
internal sealed class PosixProgram
{
    private readonly PosixNode[] _nodes;
    private readonly PosixByteSet[] _sets;
    private readonly int[] _groupParents;
    internal PosixProfile Profile { get; }
    internal int PatternLength { get; }
    internal RegexOffsetUnit PatternUnit { get; }
    internal int Root => _nodes.Length - 1;
    internal int GroupCount => _groupParents.Length;
    internal long RetainedBytes { get; }
    internal bool HasBackreferences { get; }
    internal ReadOnlySpan<PosixNode> Nodes => _nodes;
    internal ReadOnlySpan<PosixByteSet> Sets => _sets;
    internal ReadOnlySpan<int> GroupParents => _groupParents;

    private PosixProgram(PosixProfile profile, int patternLength, RegexOffsetUnit unit,
        ReadOnlySpan<PosixNode> nodes, ReadOnlySpan<PosixByteSet> sets,
        ReadOnlySpan<int> parents, long bytes, bool references)
    {
        Profile = profile;
        PatternLength = patternLength;
        PatternUnit = unit;
        RetainedBytes = bytes;
        HasBackreferences = references;
        _nodes = nodes.ToArray();
        _sets = sets.ToArray();
        _groupParents = parents.ToArray();
    }

    internal static bool TryCreate(PosixProfile profile, int patternLength, RegexOffsetUnit unit,
        ReadOnlySpan<PosixNode> nodes, ReadOnlySpan<PosixByteSet> sets, ReadOnlySpan<int> groupParents,
        ref CompileBudget budget, out PosixProgram? program, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        program = null;
        diagnostic = default;
        var source = new SourceRange(0, Math.Max(0, patternLength), unit);
        var limits = budget.Limits;
        var code = !Enum.IsDefined(unit) || patternLength < 0 || nodes.IsEmpty ||
            groupParents.IsEmpty || groupParents[0] != -1 ? RegexDiagnosticCode.InvalidProgram :
            patternLength > limits.MaxPatternUnits ? RegexDiagnosticCode.PatternLimitExceeded :
            nodes.Length > limits.MaxInstructions ? RegexDiagnosticCode.ProgramLimitExceeded :
            sets.Length > limits.MaxClassRanges ? RegexDiagnosticCode.ClassLimitExceeded :
            groupParents.Length > limits.MaxCaptureGroups ? RegexDiagnosticCode.CaptureLimitExceeded : RegexDiagnosticCode.None;
        // Conservative payload plus array/object overhead, admitted before copies.
        var bytes = 384L + 48L * nodes.Length + 32L * sets.Length + 4L * groupParents.Length;
        if (code == RegexDiagnosticCode.None && bytes > limits.MaxPlanBytes)
        {
            code = RegexDiagnosticCode.PlanLimitExceeded;
        }

        if (code != RegexDiagnosticCode.None)
        {
            return Fail(code, source, out diagnostic);
        }

        for (var g = 1; g < groupParents.Length; g++)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            if (groupParents[g] < 0 || groupParents[g] >= g)
            {
                return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
            }
        }
        foreach (var set in sets)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            if (set.Contains(0))
            {
                return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
            }
        }
        var scratchBytes = 256L + 12L * nodes.Length + 4L * groupParents.Length;
        if (!budget.TryReserveBytes(scratchBytes, source, out diagnostic))
        {
            return false;
        }

        try
        {
            var depths = new int[nodes.Length];
            var uses = new int[nodes.Length];
            var parents = new int[nodes.Length];
            Array.Fill(parents, -1);
            var captures = new int[groupParents.Length];
            Array.Fill(captures, -1);
            var references = false;
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (!budget.TryCharge(1, n.Source, out diagnostic))
                {
                    return false;
                }

                if (!Enum.IsDefined(n.Kind) || n.Source.Unit != unit || n.Source.Offset < 0 ||
                    n.Source.Length < 0 || (long)n.Source.Offset + n.Source.Length > patternLength)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var binary = n.Kind is PosixNodeKind.Sequence or PosixNodeKind.Alternative;
                var unary = n.Kind is PosixNodeKind.Repeat or PosixNodeKind.Capture;
                if ((binary || unary ? n.X < 0 || n.X >= i : n.X != -1) ||
                    (binary ? n.Y < 0 || n.Y >= i : n.Y != -1))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                depths[i] = 1;
                if (binary || unary)
                { uses[n.X]++; parents[n.X] = i; depths[i] = depths[n.X] + 1; }
                if (binary)
                { uses[n.Y]++; parents[n.Y] = i; depths[i] = Math.Max(depths[i], depths[n.Y] + 1); }
                if (depths[i] > limits.MaxNestingDepth)
                {
                    return Fail(RegexDiagnosticCode.NestingLimitExceeded, n.Source, out diagnostic);
                }

                if ((binary || unary) && !Contains(n.Source, nodes[n.X].Source) ||
                    binary && (!Contains(n.Source, nodes[n.Y].Source) ||
                    nodes[n.X].Source.Offset + (long)nodes[n.X].Source.Length > nodes[n.Y].Source.Offset))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (n.Kind != PosixNodeKind.Repeat && (n.Min != 0 || n.Max != 0))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                var valid = n.Kind switch
                {
                    PosixNodeKind.Literal => n.Value is >= 1 and <= 255,
                    PosixNodeKind.Class => (uint)n.Value < (uint)sets.Length,
                    PosixNodeKind.Capture => n.Value > 0 && n.Value < groupParents.Length,
                    PosixNodeKind.Backreference => profile.Dialect == PosixDialect.Basic &&
                        n.Value is >= 1 and <= 9 && n.Value < groupParents.Length,
                    PosixNodeKind.Alternative => profile.Dialect == PosixDialect.Extended && n.Value == 0,
                    PosixNodeKind.Repeat => n.Value == 0 && n.Min is >= 0 and <= PosixProfile.RepeatMaximum &&
                        (n.Max == -1 || n.Max >= n.Min && n.Max <= PosixProfile.RepeatMaximum),
                    _ => n.Value == 0
                };
                if (!valid)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (n.Kind == PosixNodeKind.Capture)
                {
                    if (captures[n.Value] != -1)
                    {
                        return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                    }

                    captures[n.Value] = i;
                }
                references |= n.Kind == PosixNodeKind.Backreference;
            }
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (!budget.TryCharge(1, n.Source, out diagnostic))
                {
                    return false;
                }

                if (uses[i] != (i == nodes.Length - 1 ? 0 : 1))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (n.Kind == PosixNodeKind.Backreference)
                {
                    var c = captures[n.Value];
                    if (c < 0 || (long)nodes[c].Source.Offset + nodes[c].Source.Length > n.Source.Offset)
                    {
                        return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                    }
                }
            }
            for (var g = 1; g < captures.Length; g++)
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                var c = captures[g];
                if (c < 0 || g > 1 && nodes[captures[g - 1]].Source.Offset >= nodes[c].Source.Offset)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var ancestor = parents[c];
                while (ancestor >= 0 && nodes[ancestor].Kind != PosixNodeKind.Capture)
                {
                    if (!budget.TryCharge(1, source, out diagnostic))
                    {
                        return false;
                    }

                    ancestor = parents[ancestor];
                }
                if (groupParents[g] != (ancestor < 0 ? 0 : nodes[ancestor].Value))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                if (groupParents[g] > 0)
                {
                    var p = captures[groupParents[g]];
                    if (nodes[p].Source.Offset >= nodes[c].Source.Offset ||
                        nodes[c].Source.Offset + (long)nodes[c].Source.Length >= nodes[p].Source.Offset + (long)nodes[p].Source.Length)
                    {
                        return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }
                }
            }
            if (!budget.TryCharge((long)nodes.Length + sets.Length + groupParents.Length, source, out diagnostic) ||
                !budget.TryReserveBytes(bytes, source, out diagnostic))
            {
                return false;
            }

            program = new(profile, patternLength, unit, nodes, sets, groupParents, bytes, references);
            return true;
        }
        finally { budget.ReleaseBytes(scratchBytes); }
    }

    private static bool Contains(SourceRange parent, SourceRange child) => child.Offset >= parent.Offset &&
        child.Offset + (long)child.Length <= parent.Offset + (long)parent.Length;
    private static bool Fail(RegexDiagnosticCode code, SourceRange source, out RegexDiagnostic diagnostic)
    { diagnostic = new(code, source.Offset, source.Length, source.Unit); return false; }
}
