#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.IR;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal struct SyntaxFrame
{
    internal int Start, Parts, Branches, BranchStart, Definition, Operand, ResetBase, ResetMaximum, PrefixCallout;
    internal Pcre2NodeKind Kind;
    internal Pcre2CompileFlags SavedFlags;
    internal Pcre2ExtraFlags SavedExtra;
    internal bool ConditionHead;
}

internal ref partial struct SyntaxReader
{
    private readonly ReadOnlySpan<int> _input, _map;
    private readonly int _sourceLength;
    private readonly RegexOffsetUnit _unit;
    private readonly IPcre2Data _data;
#if NETSTANDARD2_0
    // The netstandard2.0 build (generator host) has no ref fields; the caller copies the budget back.
    private CompileBudget _budget;
    internal readonly CompileBudget Budget => _budget;
#else
    private ref CompileBudget _budget;
#endif
    private Pcre2Profile _profile;
    private readonly Pcre2CompileFlags _arguments;
    private Pcre2CompileFlags _flags;
    private Pcre2ExtraFlags _extra;
    private Pcre2PatternLimits _patternLimits;
    private int _p, _nextGroup, _groupCount;
    private bool _quote, _literalOpenBrace, _hasExplicitCrOrLf, _hasDuplicateNameOptionChange, _hasBackslashC;
    private int _modifiedRepeat = -1, _previousCallout = -1;
    private bool _skipCallout, _itemSinceCallout;
    private long _planned;
    private readonly SyntaxBuffer<Pcre2Node> _nodes = new(64);
    private readonly SyntaxBuffer<int> _children = new(4), _parts = new(4), _branches = new(4), _numbers = new(4), _units = new(4), _nodeDepths = new(4);
    private readonly SyntaxBuffer<SyntaxFrame> _frames = new(80);
    private readonly SyntaxBuffer<Pcre2ClassNode> _classes = new(48);
    private readonly SyntaxBuffer<Pcre2Interval> _intervals = new(8);
    private readonly SyntaxBuffer<Pcre2GroupDefinition> _groups = new(16);
    private readonly SyntaxBuffer<Pcre2Name> _names = new(48);
    private readonly SyntaxBuffer<Pcre2Reference> _references = new(32);
    private readonly SyntaxBuffer<Pcre2Assertion> _assertions = new(16);
    private readonly SyntaxBuffer<Pcre2Condition> _conditions = new(32);
    private readonly SyntaxBuffer<Pcre2CalloutSite> _callouts = new(40);
    private readonly SyntaxBuffer<Pcre2Control> _controls = new(8);
    private readonly SyntaxBuffer<Pcre2Slice> _marks = new(8), _scans = new(8);
    private readonly SyntaxBuffer<int> _namePositions = new(4), _ambiguousConditions = new(4);
    private Pcre2MatchFlags _patternMatchFlags;
    private readonly SyntaxBuffer<int> _definitionNames = new(4), _referenceNative = new(4);
    private readonly SyntaxBuffer<SyntaxClassFrame> _classFrames = new(64);

    internal SyntaxReader(ReadOnlySpan<int> input, ReadOnlySpan<int> map, int sourceLength, RegexOffsetUnit unit,
        Pcre2Profile profile, IPcre2Data data, ref CompileBudget budget)
    {
        _input = input;
        _map = map;
        _sourceLength = sourceLength;
        _unit = unit;
        _profile = profile;
        _data = data;
#if NETSTANDARD2_0
        _budget = budget;
#else
        _budget = ref budget;
#endif
        _arguments = profile.Flags | ((profile.Flags & Pcre2CompileFlags.MatchInvalidUtf) != 0 ? Pcre2CompileFlags.Utf : 0);
        _flags = profile.Flags;
        _extra = profile.ExtraFlags;
        _patternLimits = default;
        _p = 0;
        _nextGroup = 1;
        _groupCount = 1;
        _quote = false;
        _planned = checked(1600L + (profile.Width == Pcre2CodeUnitWidth.Eight ? 5L : 6L) * input.Length + 4L * map.Length);
        SyntaxFailure.Reserve(ref _budget, 2048, Range(0, input.Length));
        Plan(0);
    }
    private readonly SourceRange Range(int start, int end) => new(_map[Math.Clamp(start, 0, _input.Length)],
        _map[Math.Clamp(end, 0, _input.Length)] - _map[Math.Clamp(start, 0, _input.Length)], _unit);
    private readonly int Peek(int ahead = 0) => _p + ahead < _input.Length ? _input[_p + ahead] : -1;
    private bool Take(int ch) { if (Peek() != ch) { return false; } _p++; return true; }
    private void Charge(long count = 1) => SyntaxFailure.Charge(ref _budget, count, Range(_p, _p));
    private void Plan(long bytes)
    {
        if (!Pcre2Program.TryAddPlanBytes(_planned, bytes, _budget.Limits.MaxPlanBytes, out _planned))
        {
            Limit(RegexDiagnosticCode.PlanLimitExceeded);
        }
    }
    private void Add<T>(SyntaxBuffer<T> buffer, T item, int bytes = 0)
    { Plan(bytes); buffer.Add(item, ref _budget, Range(_p, _p)); }
    private void Limit(RegexDiagnosticCode code) => throw new SyntaxFailure(new(new(code, _map[_p], 0, _unit), Pcre2DiagnosticKind.CompileLimit));
    private readonly void Error(int err, int offset = -1, Pcre2DiagnosticKind kind = Pcre2DiagnosticKind.Syntax)
    {
        offset = offset < 0 ? _p : Math.Clamp(offset, 0, _input.Length);
        throw new SyntaxFailure(new(new(RegexDiagnosticCode.InvalidSyntax,
            _map[offset], 0, _unit), kind, (Pcre2ErrorCode)(100 + err), NativeOffset: offset));
    }
    private int Node(Pcre2NodeKind kind, int start, int end, int operand = 0, scoped ReadOnlySpan<int> child = default,
        int min = 0, int max = 0, Pcre2Quantifier quantifier = Pcre2Quantifier.Greedy)
    {
        if ((long)_nodes.Count + _classes.Count >= _budget.Limits.MaxInstructions)
        {
            Limit(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var depth = 1;
        foreach (var id in child)
        { Charge(); depth = Math.Max(depth, _nodeDepths[id] + 1); }
        if (depth > _budget.Limits.MaxNestingDepth)
        {
            Limit(RegexDiagnosticCode.NestingLimitExceeded);
        }

        var begin = _children.Count;
        foreach (var id in child)
        {
            Add(_children, id, 4);
        }
        // Literal emission precedes repeats/DEFINE removal in the native compiler.
        if (kind == Pcre2NodeKind.Literal && operand is 10 or 13)
        {
            _hasExplicitCrOrLf = true;
        }
        // Native ESC_C records HASBKC before raw conversion and repeat removal.
        if (kind == Pcre2NodeKind.CodeUnit)
        {
            _hasBackslashC = true;
        }

        var result = _nodes.Count;
        Add(_nodeDepths, depth);
        Add(_nodes, new(kind, new(begin, child.Length), operand, min, max, quantifier, _flags, _extra, Range(start, end)), 64);
        return result;
    }
    private int Single(Pcre2NodeKind kind, int start, int end, int child, int operand = 0, int min = 0, int max = 0, Pcre2Quantifier quantifier = Pcre2Quantifier.Greedy)
    { Span<int> one = stackalloc int[1]; one[0] = child; return Node(kind, start, end, operand, one, min, max, quantifier); }
    private int Sequence(scoped ReadOnlySpan<int> parts, int start, int end)
        => parts.Length == 0 ? Node(Pcre2NodeKind.Empty, start, end) : parts.Length == 1 ? parts[0] : Node(Pcre2NodeKind.Sequence, start, end, child: parts);
    private void Append(int node) => Add(_parts, node);
    private void Push(Pcre2NodeKind kind, int start, int operand = 0, int definition = -1, bool reset = false, bool conditionHead = false)
    {
        if (_frames.Count >= _budget.Limits.MaxNestingDepth)
        {
            Limit(RegexDiagnosticCode.NestingLimitExceeded);
        }

        Add(_frames, new SyntaxFrame
        {
            Start = start,
            Parts = _parts.Count,
            Branches = _branches.Count,
            BranchStart = _p,
            Definition = definition,
            Operand = operand,
            Kind = kind,
            PrefixCallout = -1,
            SavedFlags = _flags,
            SavedExtra = _extra,
            ResetBase = reset ? _nextGroup : 0,
            ResetMaximum = _nextGroup,
            ConditionHead = conditionHead
        });
    }
    private void Branch()
    {
        ref var frame = ref _frames[_frames.Count - 1];
        Add(_branches, Sequence(_parts.Span[frame.Parts..], frame.BranchStart, _p));
        _parts.Truncate(frame.Parts);
        frame.BranchStart = _p;
        if (frame.ResetBase != 0)
        { frame.ResetMaximum = Math.Max(frame.ResetMaximum, _nextGroup); _nextGroup = frame.ResetBase; }
    }
    private int Close(int end)
    {
        var frame = _frames.Pop();
        if (frame.ConditionHead)
        {
            Error(28, end - 1);
        }

        var body = Sequence(_parts.Span[frame.Parts..], frame.BranchStart, end);
        _parts.Truncate(frame.Parts);
        if (_branches.Count > frame.Branches)
        {
            Add(_branches, body);
            if (frame.Kind == Pcre2NodeKind.Conditional)
            {
                if (_branches.Count - frame.Branches > 2)
                {
                    Error(27, frame.Start);
                }

                if (frame.Definition >= 0)
                {
                    Add(_parts, frame.Definition);
                    foreach (var branch in _branches.Span[frame.Branches..])
                    {
                        Add(_parts, branch);
                    }

                    body = Node(Pcre2NodeKind.Conditional, frame.Start, end, frame.Operand, _parts.Span[frame.Parts..]);
                    _parts.Truncate(frame.Parts);
                }
                else
                {
                    body = Node(Pcre2NodeKind.Conditional, frame.Start, end, frame.Operand, _branches.Span[frame.Branches..]);
                }
            }
            else
            {
                if (frame.Kind == Pcre2NodeKind.Define)
                {
                    Error(54, end);
                }

                body = Node(Pcre2NodeKind.Alternative, frame.Start, end, child: _branches.Span[frame.Branches..]);
            }
            _branches.Truncate(frame.Branches);
        }
        else if (frame.Kind == Pcre2NodeKind.Conditional)
        {
            if (frame.Definition >= 0)
            { Span<int> pair = stackalloc int[2]; pair[0] = frame.Definition; pair[1] = body; body = Node(Pcre2NodeKind.Conditional, frame.Start, end, frame.Operand, pair); }
            else
            {
                body = Single(Pcre2NodeKind.Conditional, frame.Start, end, body, frame.Operand);
            }
        }
        if (frame.ResetBase != 0)
        {
            _nextGroup = Math.Max(_nextGroup, frame.ResetMaximum);
        }

        if (frame.Kind == Pcre2NodeKind.Capture)
        {
            body = Single(frame.Kind, frame.Start, end, body, frame.Definition);
            _groups[frame.Definition] = _groups[frame.Definition] with { EntryNode = body };
        }
        else if (frame.Kind is not (Pcre2NodeKind.Alternative or Pcre2NodeKind.Conditional))
        {
            body = Single(frame.Kind, frame.Start, end, body, frame.Operand);
        }

        if (frame.PrefixCallout >= 0)
        { Span<int> prefix = stackalloc int[2]; prefix[0] = frame.PrefixCallout; prefix[1] = body; body = Node(Pcre2NodeKind.Sequence, frame.Start, end, child: prefix); }
        if (_frames.Count > 0)
        { _flags = frame.SavedFlags; _extra = frame.SavedExtra; }
        if (_frames.Count > 0 && _frames[_frames.Count - 1].ConditionHead)
        {
            ref var parent = ref _frames[_frames.Count - 1];
            if (frame.Kind != Pcre2NodeKind.Assertion || _assertions[frame.Operand].Kind is Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind)
            {
                Error(28, end);
            }

            _conditions[parent.Operand] = new(Pcre2ConditionKind.Assertion, -1, body, 0, 0, false);
            parent.ConditionHead = false;
            // Keep the assertion as a structural child of the conditional. It
            // is prepended at close; body branches remain independent.
            parent.Definition = body;
            parent.BranchStart = _p;
            if (_parts.Count > parent.Parts)
            {
                if (_parts.Count - parent.Parts != 1 || _nodes[_parts[parent.Parts]].Kind != Pcre2NodeKind.Callout)
                {
                    Error(28, end);
                }

                parent.PrefixCallout = _parts[parent.Parts];
                _parts.Truncate(parent.Parts);
            }
        }
        else
        {
            Append(body);
        }

        return body;
    }
    internal bool Parse(out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic)
    {
        syntax = null;
        diagnostic = default;
        Directives();
        ValidateOptions();
        ValidateUtf();
        ValidatePostUtfOptions();
        foreach (var unit in _input)
        {
            Add(_units, unit);
        }

        Push(Pcre2NodeKind.Alternative, 0);
        while (_p < _input.Length)
        {
            Charge();
            var start = _p;
            if (_quote)
            {
                if (Peek() == '\\' && Peek(1) == 'E')
                { _p += 2; _quote = false; continue; }
                ManageCallouts(start);
                var cp = Read();
                Emit(Node(Pcre2NodeKind.Literal, start, _p, cp), start);
                continue;
            }
            if ((_flags & Pcre2CompileFlags.Literal) != 0)
            { ManageCallouts(start); var cp = Read(); Emit(Node(Pcre2NodeKind.Literal, start, _p, cp), start); continue; }
            if (SkipExtended())
            {
                continue;
            }

            var token = Peek();
            var comment = token == '(' && Peek(1) == '?' && Peek(2) == '#';
            if (!comment && token is not ('*' or '+' or '?') && !(token == '{' && ProbeRepeat(_p + 1)))
            {
                ManageCallouts(start);
            }

            var c = Read();
            switch (c)
            {
                case '|':
                    Branch();
                    _itemSinceCallout = true;
                    break;
                case ')':
                    if (_frames.Count == 1)
                    {
                        Error(22, _p - 1);
                    }

                    Close(_p);
                    _itemSinceCallout = true;
                    break;
                case '(':
                    Group(start);
                    if (!comment)
                    {
                        _itemSinceCallout = true;
                    }

                    break;
                case '[':
                    ClassAtom(start);
                    break;
                case '\\':
                    EscapeAtom(start);
                    break;
                case '.':
                    Emit(Node(Pcre2NodeKind.Dot, start, _p), start);
                    break;
                case '^':
                    Emit(Node(Pcre2NodeKind.Start, start, _p), start);
                    break;
                case '$':
                    Emit(Node(Pcre2NodeKind.End, start, _p), start);
                    break;
                case '*':
                case '+':
                case '?':
                    Repeat(start, c);
                    break;
                case '{':
                    if (TryRepeat(out var min, out var max))
                    { Repeat(start, c, min, max); }
                    else
                    {
                        Emit(Node(Pcre2NodeKind.Literal, start, _p, c), start);
                    }

                    break;
                default:
                    Emit(Node(Pcre2NodeKind.Literal, start, _p, c), start);
                    break;
            }
        }
        ManageCallouts(_p, final: true);
        if (_frames.Count != 1)
        {
            Error(14, _p);
        }

        var root = Close(_p);
        if ((_extra & (Pcre2ExtraFlags.MatchLine | Pcre2ExtraFlags.MatchWord)) != 0)
        {
            var anchor = (_extra & Pcre2ExtraFlags.MatchLine) != 0 ? Pcre2NodeKind.Start : Pcre2NodeKind.WordBoundary;
            var prefix = Node(anchor, 0, 0);
            _nodes[prefix] = _nodes[prefix] with { Flags = _profile.Flags, ExtraFlags = _profile.ExtraFlags };
            var suffix = Node(anchor == Pcre2NodeKind.Start ? Pcre2NodeKind.End : anchor, _p, _p);
            Span<int> wrapped = stackalloc int[3];
            wrapped[0] = prefix;
            wrapped[1] = root;
            wrapped[2] = suffix;
            Node(Pcre2NodeKind.Sequence, 0, _p, child: wrapped);
        }
        FinishNames();
        var plan = new Pcre2PlanData(_nodes.Span, _children.Span, _classes.Span, _intervals.Span, _groups.Span, _names.Span, _numbers.Span,
            _references.Span, _assertions.Span, _conditions.Span, _callouts.Span, _controls.Span, _marks.Span, _scans.Span, _units.Span, _map);
        return Pcre2Program.TryCreate(_profile, Pcre2PlanStage.Syntax, _sourceLength, _input.Length, _unit, _groupCount, plan, _patternLimits,
            ref _budget, out syntax, out diagnostic, _arguments, _patternMatchFlags, hasExplicitCrOrLf: _hasExplicitCrOrLf, hasDuplicateNameOptionChange: _hasDuplicateNameOptionChange, hasBackslashC: _hasBackslashC);
    }
    private int Read()
    {
        var cp = _input[_p++];
        if (!_profile.Utf)
        {
            return cp;
        }

        if (_profile.Width == Pcre2CodeUnitWidth.Sixteen)
        {
            if (cp is >= 0xD800 and <= 0xDBFF)
            {
                cp = 0x10000 + ((cp - 0xD800) << 10) + _input[_p++] - 0xDC00;
            }
        }
        else if (cp >= 0xC0)
        {
            var width = cp < 0xE0 ? 2 : cp < 0xF0 ? 3 : 4;
            cp &= (1 << (7 - width)) - 1;
            for (var i = 1; i < width; i++)
            {
                cp = (cp << 6) | (_input[_p++] & 63);
            }
        }
        return cp;
    }
    private bool SkipExtended()
    {
        if ((_flags & (Pcre2CompileFlags.Extended | Pcre2CompileFlags.ExtendedMore)) == 0)
        {
            return false;
        }

        var c = ScalarAt(_p, out var width);
        if (c < 256 && c >= 0 && (_profile.CharacterTables.Bytes[832 + c] & 1) != 0 || c is 0x85 or 0x200E or 0x200F or 0x2028 or 0x2029)
        { _p += width; return true; }
        if (c == '#')
        { while (_p < _input.Length && !IsNewline(_p)) { Charge(); Read(); } if (_p < _input.Length) { var newline = Peek(); Read(); if (newline == 13 && Peek() == 10 && _profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Anycrlf or Pcre2Newline.Any) { Read(); } } return true; }
        return false;
    }
    private readonly bool IsNewline(int at)
    {
        var c = ScalarAt(at, out _);
        return _profile.Newline switch
        {
            Pcre2Newline.Cr => c == 13,
            Pcre2Newline.Lf => c == 10,
            Pcre2Newline.Nul => c == 0,
            Pcre2Newline.Crlf => c == 13 && at + 1 < _input.Length && _input[at + 1] == 10,
            Pcre2Newline.Anycrlf => c is 10 or 13,
            _ => c is 10 or 13 or 11 or 12 or 0x85 or 0x2028 or 0x2029
        };
    }
    private readonly int ScalarAt(int at, out int width)
    {
        width = 1;
        if (at >= _input.Length)
        {
            return -1;
        }

        var c = _input[at];
        if (!_profile.Utf)
        {
            return c;
        }

        if (_profile.Width == Pcre2CodeUnitWidth.Sixteen)
        { if (c is >= 0xD800 and <= 0xDBFF) { width = 2; return 0x10000 + ((c - 0xD800) << 10) + _input[at + 1] - 0xDC00; } return c; }
        if (c < 128)
        {
            return c;
        }

        width = c < 0xE0 ? 2 : c < 0xF0 ? 3 : 4;
        c &= (1 << (7 - width)) - 1;
        for (var i = 1; i < width; i++)
        {
            c = (c << 6) | (_input[at + i] & 63);
        }

        return c;
    }
    private void Emit(int node, int start)
    {
        if (_frames[_frames.Count - 1].ConditionHead && _nodes[node].Kind != Pcre2NodeKind.Callout)
        {
            Error(28, start);
        }

        Append(node);
        _itemSinceCallout = true;
    }
    // Raw source sites are independent metadata at Syntax stage. Lowering owns
    // insertion and suppression caused by observable optimizer transformations.
    private void ManageCallouts(int position, bool final = false)
    {
        if (_skipCallout && !final)
        { _skipCallout = false; return; }
        if (_previousCallout >= 0)
        { var previous = _callouts[_previousCallout]; _callouts[_previousCallout] = previous with { NextItemLength = position - previous.PatternPosition }; }
        if ((_flags & Pcre2CompileFlags.AutoCallout) == 0)
        { _previousCallout = -1; return; }
        if (_previousCallout >= 0 && _callouts[_previousCallout].Automatic && !_itemSinceCallout)
        {
            _callouts[_previousCallout] = _callouts[_previousCallout] with { PatternPosition = position, NextItemLength = 0 };
        }
        else
        { _previousCallout = _callouts.Count; Add(_callouts, new(255, true, false, default, position, 0, 0), 40); }
        _itemSinceCallout = false;
    }
    private bool ProbeRepeat(int position)
    {
        SkipSpace(ref position);
        var first = position < _input.Length && Digit(_input[position]);
        while (position < _input.Length && Digit(_input[position]))
        { Charge(); position++; }
        SkipSpace(ref position);
        if (position >= _input.Length)
        {
            return false;
        }

        if (_input[position] == '}')
        {
            return first;
        }

        if (_input[position++] != ',')
        {
            return false;
        }

        SkipSpace(ref position);
        var second = position < _input.Length && Digit(_input[position]);
        while (position < _input.Length && Digit(_input[position]))
        { Charge(); position++; }
        SkipSpace(ref position);
        return (first || second) && position < _input.Length && _input[position] == '}';
    }
    private void Repeat(int start, int token, int min = 0, int max = 0)
    {
        if (token != '{')
        { min = token == '+' ? 1 : 0; max = token == '?' ? 1 : -1; }
        var frame = _frames[_frames.Count - 1];
        if (_parts.Count == frame.Parts)
        {
            Error(9, _p - 1);
        }

        var child = _parts[_parts.Count - 1];
        var n = _nodes[child];
        if (n.Kind == Pcre2NodeKind.Repeat && child != _modifiedRepeat && token is '?' or '+')
        {
            _nodes[child] = n with
            {
                Quantifier = token == '+' ? Pcre2Quantifier.Possessive : n.Quantifier == Pcre2Quantifier.Greedy ? Pcre2Quantifier.Lazy : Pcre2Quantifier.Greedy,
                Source = Range(NativeStart(n.Source), _p)
            };
            _modifiedRepeat = child;
            return;
        }
        if (n.Kind is Pcre2NodeKind.Repeat or Pcre2NodeKind.Start or Pcre2NodeKind.End or Pcre2NodeKind.AbsoluteStart or
            Pcre2NodeKind.AbsoluteEnd or Pcre2NodeKind.BeforeFinalNewline or Pcre2NodeKind.StartOffset or Pcre2NodeKind.WordBoundary or
            Pcre2NodeKind.NotWordBoundary or Pcre2NodeKind.ResetStart or Pcre2NodeKind.Callout)
        {
            Error(9, _p - 1);
        }

        if (n.Kind == Pcre2NodeKind.Verb && _controls[n.Operand].Kind != Pcre2VerbKind.Accept)
        {
            Error(9, _p - 1);
        }

        var q = (_flags & Pcre2CompileFlags.Ungreedy) != 0 ? Pcre2Quantifier.Lazy : Pcre2Quantifier.Greedy;
        var modified = false;
        if (Take('?'))
        { q = q == Pcre2Quantifier.Greedy ? Pcre2Quantifier.Lazy : Pcre2Quantifier.Greedy; modified = true; }
        else if (Take('+'))
        { q = Pcre2Quantifier.Possessive; modified = true; }
        if (_previousCallout >= 0 && _callouts[_previousCallout].Automatic && !_itemSinceCallout &&
            _callouts[_previousCallout].PatternPosition > NativeStart(n.Source))
        {
            if (_previousCallout > 0 && _callouts[_previousCallout - 1].Automatic &&
                _callouts[_previousCallout - 1].PatternPosition + 1 == _callouts[_previousCallout].PatternPosition)
            {
                _callouts[_previousCallout - 1] = _callouts[_previousCallout - 1] with { NextItemLength = _p - _callouts[_previousCallout - 1].PatternPosition };
            }

            _callouts[_previousCallout] = _callouts[_previousCallout] with { NextItemLength = _p - _callouts[_previousCallout].PatternPosition };
        }
        _parts[_parts.Count - 1] = Single(Pcre2NodeKind.Repeat, NativeStart(n.Source), _p, child, min: min, max: max, quantifier: q);
        if (modified)
        {
            _modifiedRepeat = _parts[_parts.Count - 1];
        }
    }
    private int NativeStart(SourceRange range)
    { int left = 0, right = _map.Length; while (left < right) { Charge(); var mid = left + (right - left) / 2; if (_map[mid] < range.Offset) { left = mid + 1; } else { right = mid; } } return left; }
    private bool TryRepeat(out int min, out int max)
    {
        min = 0;
        max = -1;

        var q = _p;
        SkipSpace(ref q);
        var first = q;
        while (q < _input.Length && Digit(_input[q]))
        { Charge(); q++; }
        var got = q > first;
        SkipSpace(ref q);
        if (q >= _input.Length)
        {
            return false;
        }

        if (_input[q] == '}')
        {
            if (!got)
            {
                return false;
            }
        }
        else
        {
            if (_input[q++] != ',')
            {
                return false;
            }

            SkipSpace(ref q);
            var second = q;
            while (q < _input.Length && Digit(_input[q]))
            { Charge(); q++; }
            if (!got && q == second)
            {
                return false;
            }

            SkipSpace(ref q);
            if (q >= _input.Length || _input[q] != '}')
            {
                return false;
            }
        }
        SkipSpace(ref _p);
        min = Number(65535, 5, false);
        SkipSpace(ref _p);
        if (Take(','))
        { SkipSpace(ref _p); max = Digit(Peek()) ? Number(65535, 5) : -1; SkipSpace(ref _p); }
        else
        {
            max = min;
        }

        _p = q + 1;
        if (max >= 0 && min > max)
        {
            Error(4, q);
        }

        return true;
    }
    private void SkipSpace(ref int position) { while (position < _input.Length && _input[position] is 32 or 9) { Charge(); position++; } }
    private static bool Digit(int c) => c is >= '0' and <= '9';
    private int Number(int limit, int error, bool required = true)
    {
        var n = 0;
        if (required && !Digit(Peek()))
        {
            Error(error);
        }

        while (Digit(Peek()))
        { Charge(); var d = _input[_p++] - '0'; if (n > (limit - d) / 10) { while (Digit(Peek())) { Charge(); _p++; } Error(error, _p); } n = n * 10 + d; }
        return n;
    }
}
