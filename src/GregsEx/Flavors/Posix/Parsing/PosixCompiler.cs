using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Locale;
using GregsEx.IR;

namespace GregsEx.Flavors.Posix.Parsing;

internal sealed class PosixCompiler : IPosixCompiler
{
    public bool TryCompile(ReadOnlySpan<byte> pattern, PosixProfile profile, RegexInputKind inputKind,
        IPosixLocaleData locale, ref CompileBudget budget, out PosixProgram? program, out PosixRegexDiagnostic diagnostic)
        => Compile(pattern, default, false, profile, inputKind, locale, ref budget, out program, out diagnostic);
    public bool TryCompile(ReadOnlySpan<char> pattern, PosixProfile profile, RegexInputKind inputKind,
        IPosixLocaleData locale, ref CompileBudget budget, out PosixProgram? program, out PosixRegexDiagnostic diagnostic)
        => Compile(default, pattern, true, profile, inputKind, locale, ref budget, out program, out diagnostic);

    private static bool Compile(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> chars, bool character,
        PosixProfile profile, RegexInputKind inputKind, IPosixLocaleData locale, ref CompileBudget budget,
        out PosixProgram? program, out PosixRegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(locale);
        program = null;
        diagnostic = default;
        var length = character ? chars.Length : bytes.Length;
        var whole = new SourceRange(0, length, character ? RegexOffsetUnit.Utf16CodeUnits : RegexOffsetUnit.Bytes);
        if (!profile.Supports(inputKind))
        {
            diagnostic = new(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Utf16CodeUnits),
                Location: PosixDiagnosticLocation.Options, Disposition: PosixSyntaxDisposition.ProfileExclusion, CapabilityId: "PX-018");
            return false;
        }
        if (length > budget.Limits.MaxPatternUnits)
        { diagnostic = new(new(RegexDiagnosticCode.PatternLimitExceeded, 0, length, whole.Unit)); return false; }
        // Domain validation precedes grammar publication and never transcodes characters.
        if (!budget.TryCharge(length, whole, out var failure))
        { diagnostic = new(failure); return false; }
        for (var i = 0; i < length; i++)
        {
            int value = character ? chars[i] : bytes[i];
            if (value is < 1 or > 255)
            {
                diagnostic = new(new(RegexDiagnosticCode.InvalidPatternEncoding, i, 1, whole.Unit),
                    Disposition: PosixSyntaxDisposition.PatternDomain, CapabilityId: "PX-018");
                return false;
            }
        }
        if (!budget.TryCharge(768, whole, out failure))
        { diagnostic = new(failure); return false; }
        if (locale.Identity.ProviderId != profile.Locale.ProviderId || locale.Identity.DataDigest != profile.Locale.DataDigest ||
            !locale.ClassMasks.SequenceEqual(CByteLocale.Instance.ClassMasks) ||
            !locale.FoldToLower.SequenceEqual(CByteLocale.Instance.FoldToLower) ||
            !locale.CollationOrdinals.SequenceEqual(CByteLocale.Instance.CollationOrdinals))
        {
            throw new ArgumentException("Locale data must match the frozen C-byte provider.", nameof(locale));
        }

        if (!budget.TryReserveBytes(192, whole, out failure))
        { diagnostic = new(failure); return false; }
        var retainedAtEntry = budget.ReservedBytes - 192;
        var storage = new ParserStorage(budget) { Source = whole };
        try
        {
            var parser = new PosixParser(bytes, chars, character, profile, storage);
            parser.Parse();
            storage.Source = whole;
            var retainedBytes = 384L + 48L * parser.Nodes.Length + 32L * parser.Sets.Length + 4L * parser.Parents.Length;
            if (retainedBytes > storage.Budget.Limits.MaxPlanBytes - retainedAtEntry)
            {
                storage.Fail(RegexDiagnosticCode.PlanLimitExceeded);
            }

            if (!PosixProgram.TryCreate(profile, length, whole.Unit, parser.Nodes, parser.Sets, parser.Parents,
                ref storage.Budget, out program, out failure))
            { diagnostic = new(failure); return false; }
            return true;
        }
        catch (ParseFailure ex) { diagnostic = ex.Diagnostic; return false; }
        finally { storage.ReleaseAll(); storage.Budget.ReleaseBytes(192); budget = storage.Budget; }
    }
}

internal struct GroupFrame
{ internal int Start, Group, PieceBase, BranchBase, BranchStart; }
internal ref partial struct PosixParser
{
    private readonly ReadOnlySpan<byte> _bytes;
    private readonly ReadOnlySpan<char> _chars;
    private readonly bool _character;
    private readonly PosixProfile _profile;
    private readonly ParserStorage _storage;
    private readonly Buffer<PosixNode> _nodes;
    private readonly Buffer<PosixByteSet> _sets;
    private readonly Buffer<int> _depths, _parents, _pieces, _branches;
    private readonly Buffer<bool> _closed;
    private readonly Buffer<GroupFrame> _groups;
    private int _at;
    private readonly int Length => _character ? _chars.Length : _bytes.Length;
    private readonly bool Basic => _profile.Dialect == PosixDialect.Basic;
    private readonly bool IgnoreCase => (_profile.Flags & PosixCompileFlags.IgnoreCase) != 0;
    internal readonly ReadOnlySpan<PosixNode> Nodes => _nodes.Items;
    internal readonly ReadOnlySpan<PosixByteSet> Sets => _sets.Items;
    internal readonly ReadOnlySpan<int> Parents => _parents.Items;
    internal PosixParser(ReadOnlySpan<byte> bytes, ReadOnlySpan<char> chars, bool character,
        PosixProfile profile, ParserStorage storage)
    {
        _bytes = bytes;
        _chars = chars;
        _character = character;
        _profile = profile;
        _storage = storage;
        _at = 0;
        _nodes = storage.Buffer<PosixNode>();
        _sets = storage.Buffer<PosixByteSet>();
        _depths = storage.Buffer<int>();
        _parents = storage.Buffer<int>();
        _pieces = storage.Buffer<int>();
        _branches = storage.Buffer<int>();
        _closed = storage.Buffer<bool>();
        _groups = storage.Buffer<GroupFrame>();
    }
    private readonly int At(int index)
    {
        // Every grammar lookahead/read is charged, including repeated inspection.
        _storage.Work();
        return index < Length ? _character ? _chars[index] : _bytes[index] : 0;
    }
    private readonly int Peek(int delta = 0) => At(_at + delta);
    private readonly SourceRange Range(int start, int end) => new(start, end - start,
        _character ? RegexOffsetUnit.Utf16CodeUnits : RegexOffsetUnit.Bytes);
    private readonly void Here(int start, int end) => _storage.Source = Range(start, end);
    private void Advance(int count = 1) { _storage.Work(count); _at += count; }
    private readonly int Node(PosixNodeKind kind, int start, int end, int x = -1, int y = -1, int value = 0, int min = 0, int max = 0)
    {
        Here(start, end);
        _storage.Work();
        if (_nodes.Count == _storage.Budget.Limits.MaxInstructions)
        {
            _storage.Fail(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var depth = x < 0 ? 1 : _depths[x] + 1;
        if (y >= 0)
        {
            depth = Math.Max(depth, _depths[y] + 1);
        }

        if (depth > _storage.Budget.Limits.MaxNestingDepth)
        {
            _storage.Fail(RegexDiagnosticCode.NestingLimitExceeded);
        }

        var id = _nodes.Count;
        _nodes.Add(new(kind, x, y, value, min, max, Range(start, end)));
        _depths.Add(depth);
        return id;
    }
    private readonly int Pair(PosixNodeKind kind, int x, int y)
        => Node(kind, _nodes[x].Source.Offset, _nodes[y].Source.Offset + _nodes[y].Source.Length, x, y);
    private readonly int Reduce(Buffer<int> buffer, int start, PosixNodeKind kind)
    {
        var count = buffer.Count - start;
        if (count == 0)
        {
            return Node(PosixNodeKind.Empty, _at, _at);
        }

        while (count > 1)
        {
            var written = 0;
            for (var i = 0; i < count; i += 2)
            { _storage.Work(); buffer[start + written++] = i + 1 == count ? buffer[start + i] : Pair(kind, buffer[start + i], buffer[start + i + 1]); }
            count = written;
        }
        var root = buffer[start];
        buffer.Truncate(start);
        return root;
    }
    private readonly void FinishBranch()
    {
        var f = _groups[_groups.Count - 1];
        if (_pieces.Count == f.PieceBase && Length != 0)
        {
            // Report the syntactic empty branch in its enclosing expression.
            var start = f.Group == 0 ? Math.Max(0, f.BranchStart - 1) : _branches.Count == f.BranchBase ? f.Start : f.Start + (Basic ? 2 : 1);
            var end = _at < Length && IsClose() ? _at + (Basic ? 2 : 1) : Length;
            Here(start, end);
            _storage.Syntax(PosixErrorCode.BadPattern, capability: Basic ? "PX-007" : "PX-008");
        }
        _branches.Add(Reduce(_pieces, f.PieceBase, PosixNodeKind.Sequence));
    }
    private readonly bool IsClose() => Basic ? Peek() == '\\' && Peek(1) == ')' : Peek() == ')' && _groups.Count > 1;
    internal void Parse()
    {
        _parents.Add(-1);
        _closed.Add(true);
        _groups.Add(new() { Start = 0, Group = 0, PieceBase = 0, BranchBase = 0, BranchStart = 0 });
        while (true)
        {
            Here(_at, Math.Min(Length, _at + 1));
            _storage.Work();
            if (_at == Length || IsClose())
            {
                if (_at == Length && _groups.Count != 1)
                { Here(_groups[_groups.Count - 1].Start, Length); _storage.Syntax(PosixErrorCode.UnbalancedParenthesis, capability: Basic ? "PX-007" : "PX-008"); }
                if (_at != Length && _groups.Count == 1)
                {
                    _storage.Syntax(PosixErrorCode.UnbalancedParenthesis, capability: Basic ? "PX-007" : "PX-008");
                }

                FinishBranch();
                var f = _groups.Pop();
                var root = Reduce(_branches, f.BranchBase, PosixNodeKind.Alternative);
                if (_at == Length)
                {
                    return;
                }

                Advance(Basic ? 2 : 1);
                root = Node(PosixNodeKind.Capture, f.Start, _at, x: root, value: f.Group);
                _closed[f.Group] = true;
                _pieces.Add(Quantify(root));
                continue;
            }
            if (!Basic && Peek() == '|')
            {
                FinishBranch();
                Advance();
                _groups[_groups.Count - 1].BranchStart = _at;
                continue;
            }
            if (Basic ? Peek() == '\\' && Peek(1) == '(' : Peek() == '(')
            {
                var start = _at;
                Advance(Basic ? 2 : 1);
                if (Peek() == '?' && !Basic)
                {
                    Here(start, Math.Min(Length, _at + 1));
                    var feature = Peek(1) switch
                    {
                        ':' => "Noncapturing group",
                        '=' or '!' => "Lookahead",
                        '>' => "Atomic group",
                        '<' => Peek(2) is '=' or '!' ? "Lookbehind" : "Named group",
                        'P' or '\'' => "Named group",
                        _ => "Group modifier or nonstandard group"
                    };
                    _storage.Extension(feature, "PX-008");
                }
                Here(start, _at);
                if (_parents.Count == _storage.Budget.Limits.MaxCaptureGroups)
                {
                    _storage.Fail(RegexDiagnosticCode.CaptureLimitExceeded);
                }

                if (_groups.Count >= _storage.Budget.Limits.MaxNestingDepth)
                {
                    _storage.Fail(RegexDiagnosticCode.NestingLimitExceeded);
                }

                var group = _parents.Count;
                _parents.Add(_groups[_groups.Count - 1].Group);
                _closed.Add(false);
                _groups.Add(new() { Start = start, Group = group, PieceBase = _pieces.Count, BranchBase = _branches.Count, BranchStart = _at });
                continue;
            }
            int first = _at, atom;
            var c = Peek();
            if (c == '[')
            {
                atom = Bracket();
            }
            else if (c == '.')
            { Advance(); atom = Node(PosixNodeKind.Any, first, _at); }
            else if (c == '^' && (!Basic || _at == _groups[_groups.Count - 1].BranchStart))
            { Advance(); atom = Node(PosixNodeKind.Start, first, _at); }
            else if (c == '$' && (!Basic || _at + 1 == Length || At(_at + 1) == '\\' && At(_at + 2) == ')' && _groups.Count > 1))
            { Advance(); atom = Node(PosixNodeKind.End, first, _at); }
            else if (c == '\\')
            {
                atom = Escape();
            }
            else
            {
                if (!Basic && c is '*' or '+' or '?' or '{')
                { Here(first, Length); _storage.Syntax(PosixErrorCode.BadRepeat, capability: "PX-009"); }
                if (Basic && c == '*' && _pieces.Count != _groups[_groups.Count - 1].PieceBase &&
                    _nodes[_pieces[_pieces.Count - 1]].Kind != PosixNodeKind.Start)
                { Here(first, Length); _storage.Syntax(PosixErrorCode.BadRepeat, capability: "PX-009"); }
                Advance();
                atom = Literal(c, first, _at);
            }
            _pieces.Add(Quantify(atom));
        }
    }
    private int Escape()
    {
        var first = _at;
        Advance();
        var c = Peek();
        if (c == 0)
        { Here(first, _at); _storage.Syntax(PosixErrorCode.TrailingEscape); }
        Advance();
        Here(first, _at);
        if (Basic && c is >= '1' and <= '9')
        {
            var group = c - '0';
            if (group >= _closed.Count || !_closed[group])
            {
                _storage.Syntax(PosixErrorCode.InvalidBackreference, PosixSyntaxDisposition.Invalid, "PX-011");
            }

            return Node(PosixNodeKind.Backreference, first, _at, value: group);
        }
        if (Basic ? c is '.' or '[' or '\\' or '*' or '^' or '$' : c is '.' or '[' or '\\' or '*' or '^' or '$' or '(' or ')' or '+' or '?' or '{' or '|')
        {
            return Literal(c, first, _at);
        }

        if (c is 'b' or 'B' or 'w' or 'W' or 's' or 'S' or '<' or '>' or '`' or '\'')
        {
            _storage.Extension(c switch
            {
                'b' or 'B' => "GNU/BSD word boundary",
                'w' or 'W' => "GNU/BSD word class",
                's' or 'S' => "GNU/BSD whitespace class",
                '<' => "GNU/BSD word start",
                '>' => "GNU/BSD word end",
                '`' => "GNU/BSD input beginning",
                _ => "GNU/BSD input end"
            }, "PX-002");
        }

        if (Basic && c == '{')
        {
            _storage.Syntax(PosixErrorCode.BadRepeat, capability: "PX-009");
        }

        _storage.Syntax(PosixErrorCode.BadPattern);
        return -1;
    }
    private readonly bool Repetition() => Peek() == '*' || (Basic ? Peek() == '\\' && Peek(1) == '{' : Peek() is '+' or '?' or '{');
    private int Quantify(int atom)
    {
        if (!Repetition())
        {
            return atom;
        }
        // A BRE star directly after the optional initial anchor is a literal.
        if (Basic && _nodes[atom].Kind == PosixNodeKind.Start && Peek() == '*')
        {
            return atom;
        }

        int first = _at, min, max;
        if (_nodes[atom].Kind is PosixNodeKind.Start or PosixNodeKind.End)
        { Here(first, Length); _storage.Syntax(PosixErrorCode.BadRepeat, capability: "PX-009"); }
        if (Peek() == '*')
        { Advance(); min = 0; max = -1; }
        else if (!Basic && Peek() == '+')
        { Advance(); min = 1; max = -1; }
        else if (!Basic && Peek() == '?')
        { Advance(); min = 0; max = 1; }
        else
        {
            Advance(Basic ? 2 : 1);
            min = Decimal(first);
            max = min;
            if (Peek() == ',')
            { Advance(); max = IsIntervalClose() ? -1 : Decimal(first); }
            if (!IsIntervalClose())
            { Here(first, IntervalEnd()); _storage.Syntax(_at == Length ? PosixErrorCode.UnbalancedBrace : PosixErrorCode.BadInterval, capability: "PX-009"); }
            Advance(Basic ? 2 : 1);
            Here(first, _at);
            if (min > 255 || max > 255 || max != -1 && max < min)
            {
                _storage.Syntax(PosixErrorCode.BadInterval, capability: "PX-009");
            }
        }
        if (Repetition())
        { Here(first, RepetitionEnd()); _storage.Syntax(PosixErrorCode.BadRepeat, capability: "PX-009"); }
        return Node(PosixNodeKind.Repeat, _nodes[atom].Source.Offset, _at, x: atom, min: min, max: max);
    }
    private readonly bool IsIntervalClose() => Basic ? Peek() == '\\' && Peek(1) == '}' : Peek() == '}';
    private readonly int IntervalEnd()
    {
        var end = _at;
        while (end < Length)
        {
            _storage.Work();
            if (Basic ? At(end) == '\\' && At(end + 1) == '}' : At(end) == '}')
            {
                return Math.Min(Length, end + (Basic ? 2 : 1));
            }

            end++;
        }
        return Length;
    }
    private readonly int RepetitionEnd()
    {
        if (Peek() == '{' || Basic && Peek() == '\\')
        {
            return IntervalEnd();
        }

        return _at + 1;
    }
    private int Decimal(int first)
    {
        if (Peek() is < '0' or > '9')
        { Here(first, IntervalEnd()); _storage.Syntax(_at == Length ? PosixErrorCode.UnbalancedBrace : PosixErrorCode.BadInterval, capability: "PX-009"); }
        var value = 0;
        while (Peek() is >= '0' and <= '9')
        { var digit = Peek() - '0'; Advance(); value = value > 255 ? 256 : value * 10 + digit; }
        return value;
    }
    private readonly int Literal(int value, int start, int end)
    {
        if (!IgnoreCase || value is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z'))
        {
            return Node(PosixNodeKind.Literal, start, end, value: value);
        }

        var set = new ByteSetBuilder();
        set.Add(value);
        set.Add(value is >= 'A' and <= 'Z' ? value + 32 : value - 32);
        return Class(set.Build(), start, end);
    }
    private readonly int Class(PosixByteSet set, int start, int end)
    {
        Here(start, end);
        if (_sets.Count == _storage.Budget.Limits.MaxClassRanges)
        {
            _storage.Fail(RegexDiagnosticCode.ClassLimitExceeded);
        }

        var id = _sets.Count;
        _sets.Add(set);
        return Node(PosixNodeKind.Class, start, end, value: id);
    }
}
