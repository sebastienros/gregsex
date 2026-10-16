using GregsEx.IR;

namespace GregsEx.Contracts.Xml;

// A validated immutable bottom-up tree, shared by both XML flavors.
// Classes contain only XML alphabet intervals, normalized before admission.
// GroupParents binds syntactic nesting for analyze-string; zero has parent -1.
internal sealed class XmlProgram
{
    private readonly XmlNode[] _nodes;
    private readonly XmlSetSlice[] _sets;
    private readonly XmlInterval[] _intervals;
    private readonly int[] _groupParents;
    internal RegexProfile Profile { get; }
    internal XmlDialect Dialect { get; }
    internal RegexInputKind InputKind { get; }
    internal int Root => _nodes.Length - 1;
    internal int GroupCount => _groupParents.Length;
    internal int PatternLength { get; }
    internal long RetainedBytes { get; }
    internal ReadOnlySpan<XmlNode> Nodes => _nodes;
    internal ReadOnlySpan<XmlSetSlice> Sets => _sets;
    internal ReadOnlySpan<XmlInterval> Intervals => _intervals;
    internal ReadOnlySpan<int> GroupParents => _groupParents;

    private XmlProgram(RegexProfile profile, XmlDialect dialect, RegexInputKind kind, int patternLength,
        ReadOnlySpan<XmlNode> nodes, ReadOnlySpan<XmlSetSlice> sets,
        ReadOnlySpan<XmlInterval> intervals, ReadOnlySpan<int> parents, long bytes)
    {
        Profile = profile;
        Dialect = dialect;
        InputKind = kind;
        PatternLength = patternLength;
        RetainedBytes = bytes;
        _nodes = nodes.ToArray();
        _sets = sets.ToArray();
        _intervals = intervals.ToArray();
        _groupParents = parents.ToArray();
    }

    internal static bool TryCreate(RegexProfile profile, RegexInputKind inputKind, int patternLength,
        ReadOnlySpan<XmlNode> nodes, ReadOnlySpan<XmlSetSlice> sets,
        ReadOnlySpan<XmlInterval> intervals, ReadOnlySpan<int> groupParents,
        ref CompileBudget budget, out XmlProgram? program, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        program = null;
        diagnostic = default;
        var source = new SourceRange(0, Math.Max(0, patternLength), RegexOffsetUnit.Utf16CodeUnits);
        var dialect = profile is XsdProfile ? XmlDialect.Xsd : XmlDialect.XPath;
        if (profile is not (XsdProfile or XPathProfile) ||
            inputKind is not (RegexInputKind.Utf16 or RegexInputKind.Utf8))
        {
            return Fail(RegexDiagnosticCode.UnsupportedRepresentation, source, out diagnostic);
        }

        var limits = budget.Limits;
        var code = patternLength < 0 || nodes.IsEmpty || groupParents.IsEmpty ||
            groupParents[0] != -1 || (dialect == XmlDialect.Xsd && groupParents.Length != 1)
            ? RegexDiagnosticCode.InvalidProgram :
            patternLength > limits.MaxPatternUnits ? RegexDiagnosticCode.PatternLimitExceeded :
            nodes.Length > limits.MaxInstructions ? RegexDiagnosticCode.ProgramLimitExceeded :
            intervals.Length > limits.MaxClassRanges ? RegexDiagnosticCode.ClassLimitExceeded :
            groupParents.Length > limits.MaxCaptureGroups ? RegexDiagnosticCode.CaptureLimitExceeded : RegexDiagnosticCode.None;
        var bytes = 384L + 48L * nodes.Length + 8L * sets.Length + 8L * intervals.Length + 4L * groupParents.Length;
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
        foreach (var interval in intervals)
        {
            if (!budget.TryCharge(1, source, out diagnostic))
            {
                return false;
            }

            if (!XmlAlphabet.ContainsRange(interval))
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

            if (set.Offset < 0 || set.Count < 0 || (long)set.Offset + set.Count > intervals.Length)
            {
                return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
            }

            var previous = -2;
            foreach (var interval in intervals.Slice(set.Offset, set.Count))
            {
                if (!budget.TryCharge(1, source, out diagnostic))
                {
                    return false;
                }

                if (!XmlAlphabet.ContainsRange(interval) || interval.First <= previous + 1)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                previous = interval.Last;
            }
        }
        // Admit bounded validator scratch before allocation; old live caller
        // buffers remain charged. Depth/parent checks cannot use recursion.
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
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (!budget.TryCharge(1, n.Source, out diagnostic))
                {
                    return false;
                }

                if (!Enum.IsDefined(n.Kind) || n.Source.Unit != RegexOffsetUnit.Utf16CodeUnits ||
                    n.Source.Offset < 0 || n.Source.Length < 0 || (long)n.Source.Offset + n.Source.Length > patternLength)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var binary = n.Kind is XmlNodeKind.Sequence or XmlNodeKind.Alternative;
                var unary = n.Kind is XmlNodeKind.Repeat or XmlNodeKind.Capture;
                if ((binary || unary ? n.X < 0 || n.X >= i : n.X != -1) ||
                    (binary ? n.Y < 0 || n.Y >= i : n.Y != -1))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                depths[i] = 1;
                if (binary || unary)
                { uses[n.X]++; parents[n.X] = i; depths[i] = depths[n.X] + 1; }
                if (binary)
                { uses[n.Y]++; parents[n.Y] = i; depths[i] = Math.Max(depths[i], depths[n.Y] + 1); }
                if ((binary || unary) && !ContainsSource(n.Source, nodes[n.X].Source) ||
                    binary && !ContainsSource(n.Source, nodes[n.Y].Source))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (depths[i] > limits.MaxNestingDepth)
                {
                    return Fail(RegexDiagnosticCode.NestingLimitExceeded, n.Source, out diagnostic);
                }

                if (dialect == XmlDialect.Xsd && (n.Lazy || n.Kind is XmlNodeKind.Capture or XmlNodeKind.Backreference or XmlNodeKind.Start or XmlNodeKind.End))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (profile is XPathProfile xpath && (xpath.Flags & XPathFlags.Quote) != 0 &&
                    n.Kind is not (XmlNodeKind.Empty or XmlNodeKind.Literal or XmlNodeKind.Class or XmlNodeKind.Sequence))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (n.Kind != XmlNodeKind.Repeat && (n.Min != 0 || n.Max != 0 || n.Lazy))
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                var valid = n.Kind switch
                {
                    XmlNodeKind.Literal => XmlAlphabet.Contains(n.Value),
                    XmlNodeKind.Class => (uint)n.Value < (uint)sets.Length,
                    XmlNodeKind.Capture or XmlNodeKind.Backreference => n.Value > 0 && n.Value < groupParents.Length,
                    XmlNodeKind.Repeat => n.Value == 0 && n.Min >= 0 && (n.Max == -1 || n.Max >= n.Min),
                    _ => n.Value == 0
                };
                if (!valid)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                }

                if (n.Kind == XmlNodeKind.Capture)
                {
                    if (captures[n.Value] != -1)
                    {
                        return Fail(RegexDiagnosticCode.InvalidProgram, n.Source, out diagnostic);
                    }

                    captures[n.Value] = i;
                }
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

                if (n.Kind == XmlNodeKind.Backreference)
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
                if (c < 0)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }

                var ancestor = parents[c];
                while (ancestor >= 0 && nodes[ancestor].Kind != XmlNodeKind.Capture)
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

                if (g > 1 && nodes[captures[g - 1]].Source.Offset >= nodes[c].Source.Offset)
                {
                    return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                }
                // Source nesting must agree with numbered syntactic ancestry.
                var p = groupParents[g];
                if (p > 0)
                {
                    var pc = captures[p];
                    if (pc < 0 || nodes[pc].Source.Offset >= nodes[c].Source.Offset ||
                        (long)nodes[c].Source.Offset + nodes[c].Source.Length >= (long)nodes[pc].Source.Offset + nodes[pc].Source.Length)
                    {
                        return Fail(RegexDiagnosticCode.InvalidProgram, source, out diagnostic);
                    }
                }
            }
            if (!budget.TryCharge((long)nodes.Length + sets.Length + intervals.Length + groupParents.Length,
                    source, out diagnostic) || !budget.TryReserveBytes(bytes, source, out diagnostic))
            {
                return false;
            }

            program = new(profile, dialect, inputKind, patternLength, nodes, sets, intervals, groupParents, bytes);
            return true;
        }
        finally { budget.ReleaseBytes(scratchBytes); }
    }

    private static bool Fail(RegexDiagnosticCode code, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = new(code, source.Offset, source.Length, source.Unit);
        return false;
    }
    private static bool ContainsSource(SourceRange parent, SourceRange child)
        => child.Offset >= parent.Offset && (long)child.Offset + child.Length <= (long)parent.Offset + parent.Length;
}
