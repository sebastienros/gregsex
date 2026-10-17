#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.XPath.Parsing;

// Compiles only the ratified XPath31 language. No subject, ambient Unicode data
// or executor participates in grammar admission. The caller binds verified data.
internal sealed class XPathCompiler : IXmlCompiler
{
    public bool TryCompile(ReadOnlySpan<char> pattern, RegexProfile profile, RegexInputKind inputKind,
        IXmlUnicodeData unicode, IXmlClassAlgebra algebra, ref CompileBudget budget,
        out XmlProgram? program, out XmlRegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(unicode);
        ArgumentNullException.ThrowIfNull(algebra);
        program = null;
        diagnostic = default;
        var whole = new SourceRange(0, pattern.Length, RegexOffsetUnit.Utf16CodeUnits);
        if (profile is not XPathProfile xpath || inputKind is not (RegexInputKind.Utf16 or RegexInputKind.Utf8))
        {
            diagnostic = Wrap(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, pattern.Length, whole.Unit), "XP-017");
            return false;
        }
        if (unicode.DataId != profile.DataId)
        {
            throw new ArgumentException("XML data identity must match the profile.", nameof(unicode));
        }

        if (pattern.Length > budget.Limits.MaxPatternUnits)
        {
            diagnostic = Wrap(new(RegexDiagnosticCode.PatternLimitExceeded, 0, pattern.Length, whole.Unit), "XP-017");
            return false;
        }
        // Admit the storage owner itself before constructing it.
        if (!budget.TryReserveBytes(192, whole, out var failure))
        { diagnostic = Wrap(failure, "XP-017"); return false; }
        var retainedAtEntry = budget.ReservedBytes - 192;
        var storage = new ParserStorage(budget) { Source = whole };
        try
        {
            var parser = new XPathParser(pattern, xpath, unicode, algebra, storage);
            parser.Parse();
            var bytes = 384L + 48L * parser.Nodes.Length + 8L * parser.Sets.Length +
                8L * parser.Intervals.Length + 4L * parser.Parents.Length;
            if (bytes > storage.Budget.Limits.MaxPlanBytes - retainedAtEntry)
            {
                storage.Fail(RegexDiagnosticCode.PlanLimitExceeded);
            }

            if (!XmlProgram.TryCreate(profile, inputKind, pattern.Length, parser.Nodes, parser.Sets,
                parser.Intervals, parser.Parents, ref storage.Budget, out program, out failure))
            {
                diagnostic = Wrap(failure, storage.Feature);
                return false;
            }
            return true;
        }
        catch (ParseFailure ex) { diagnostic = Wrap(ex.Diagnostic, ex.Feature); return false; }
        finally
        {
            storage.ReleaseAll();
            storage.Budget.ReleaseBytes(192);
            budget = storage.Budget;
        }
    }

    private static XmlRegexDiagnostic Wrap(RegexDiagnostic diagnostic, string feature) => new(diagnostic,
        XmlRegexDiagnosticLocation.Pattern,
        diagnostic.Code == RegexDiagnosticCode.InvalidSyntax ? XPathErrorCode.FORX0002 : XPathErrorCode.None, feature);
}

internal struct GroupFrame
{
    internal int Start, Capture, ActiveCapture, PieceBase, BranchBase;
}
internal struct ClassFrame
{
    internal int Start, LiteralBase, EscapeBase, Parts;
    internal bool Negative, Waiting;
    internal XmlInterval[]? Base;
}

// All grammar/lowering stacks are heap buffers admitted through one budget.
// Balanced sequence/alternative reduction avoids input-length tree depth.
internal ref partial struct XPathParser
{
    private readonly ReadOnlySpan<char> _original;
    private ReadOnlySpan<char> _text;
    private ReadOnlySpan<int> _map;
    private bool _mapped;
    private readonly XPathProfile _profile;
    private readonly IXmlUnicodeData _unicode;
    private readonly IXmlClassAlgebra _algebra;
    private readonly ParserStorage _storage;
    private readonly Buffer<XmlNode> _nodes;
    private readonly Buffer<int> _depths, _parents, _pieces, _branches;
    private readonly Buffer<bool> _closed;
    private readonly Buffer<GroupFrame> _groups;
    private readonly Buffer<ClassFrame> _classes;
    private readonly Buffer<XmlSetSlice> _sets;
    private readonly Buffer<XmlInterval> _intervals, _literalParts, _escapeParts;
    private int _at;
    internal readonly ReadOnlySpan<XmlNode> Nodes => _nodes.Items;
    internal readonly ReadOnlySpan<XmlSetSlice> Sets => _sets.Items;
    internal readonly ReadOnlySpan<XmlInterval> Intervals => _intervals.Items;
    internal readonly ReadOnlySpan<int> Parents => _parents.Items;
    private readonly bool IgnoreCase => (_profile.Flags & XPathFlags.IgnoreCase) != 0;

    internal XPathParser(ReadOnlySpan<char> text, XPathProfile profile, IXmlUnicodeData unicode,
        IXmlClassAlgebra algebra, ParserStorage storage)
    {
        _original = _text = text;
        _map = [];
        _mapped = false;
        _profile = profile;
        _unicode = unicode;
        _algebra = algebra;
        _storage = storage;
        _at = 0;
        _nodes = storage.Buffer<XmlNode>();
        _depths = storage.Buffer<int>();
        _parents = storage.Buffer<int>();
        _pieces = storage.Buffer<int>();
        _branches = storage.Buffer<int>();
        _closed = storage.Buffer<bool>();
        _groups = storage.Buffer<GroupFrame>();
        _classes = storage.Buffer<ClassFrame>();
        _sets = storage.Buffer<XmlSetSlice>();
        _intervals = storage.Buffer<XmlInterval>();
        _literalParts = storage.Buffer<XmlInterval>();
        _escapeParts = storage.Buffer<XmlInterval>();
    }

    private readonly SourceRange Range(int start, int end)
    {
        var first = !_mapped ? start : start == _text.Length ? _original.Length : _map[start];
        var last = end == start ? first : !_mapped ? end : _map[end - 1] + 1;
        return new(first, last - first, RegexOffsetUnit.Utf16CodeUnits);
    }
    private readonly void Here(int start, int end, string feature = "XP-001")
    { _storage.Source = Range(start, end); _storage.Feature = feature; }
    private readonly int Next => _at < _text.Length ? _at + 1 : _at;
    private readonly char Peek(int delta = 0) => delta < _text.Length - _at ? _text[_at + delta] : '\0';
    private void Expect(char value)
    {
        Here(_at, Next);
        _storage.Work();
        if (Peek() != value)
        {
            _storage.Fail();
        }

        _at++;
    }
    private int Scalar()
    {
        Here(_at, Next);
        _storage.Work();
        if (_at == _text.Length)
        {
            _storage.Fail();
        }

        int scalar = _text[_at++];
        if (scalar is >= 0xD800 and <= 0xDBFF)
        {
            scalar = 0x10000 + ((scalar - 0xD800) << 10) + _text[_at++] - 0xDC00;
        }

        return scalar;
    }
    private readonly int Node(XmlNodeKind kind, int start, int end, int x = -1, int y = -1,
        int value = 0, int min = 0, int max = 0, bool lazy = false)
    {
        Here(start, end);
        _storage.Work();
        if (_nodes.Count == _storage.Budget.Limits.MaxInstructions)
        {
            _storage.Fail(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var depth = 1;
        if (x >= 0)
        {
            depth = _depths[x] + 1;
        }

        if (y >= 0)
        {
            depth = Math.Max(depth, _depths[y] + 1);
        }

        if (depth > _storage.Budget.Limits.MaxNestingDepth)
        {
            _storage.Fail(RegexDiagnosticCode.NestingLimitExceeded);
        }

        var id = _nodes.Count;
        _nodes.Add(new(kind, x, y, value, min, max, lazy, Range(start, end)));
        _depths.Add(depth);
        return id;
    }
    private readonly int Pair(XmlNodeKind kind, int x, int y)
    {
        var a = _nodes[x].Source;
        var b = _nodes[y].Source;
        _storage.Source = new(a.Offset, b.Offset + b.Length - a.Offset, a.Unit);
        _storage.Work();
        if (_nodes.Count == _storage.Budget.Limits.MaxInstructions)
        {
            _storage.Fail(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var depth = Math.Max(_depths[x], _depths[y]) + 1;
        if (depth > _storage.Budget.Limits.MaxNestingDepth)
        {
            _storage.Fail(RegexDiagnosticCode.NestingLimitExceeded);
        }

        var id = _nodes.Count;
        _nodes.Add(new(kind, x, y, 0, 0, 0, false, _storage.Source));
        _depths.Add(depth);
        return id;
    }
    private readonly int Reduce(Buffer<int> buffer, int start, XmlNodeKind kind)
    {
        var count = buffer.Count - start;
        if (count == 0)
        {
            return Node(XmlNodeKind.Empty, _at, _at);
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
        var root = Reduce(_pieces, _groups[_groups.Count - 1].PieceBase, XmlNodeKind.Sequence);
        _branches.Add(root);
    }
    private readonly void Nest(int depth, int start)
    {
        Here(start, _at, "XP-017");
        if (depth > _storage.Budget.Limits.MaxNestingDepth)
        {
            _storage.Fail(RegexDiagnosticCode.NestingLimitExceeded);
        }
    }
    internal void Parse()
    {
        ValidateDomain();
        if ((_profile.Flags & XPathFlags.Quote) == 0 && (_profile.Flags & XPathFlags.IgnoreWhitespace) != 0)
        {
            StripWhitespace();
        }

        _parents.Add(-1);
        _closed.Add(true);
        _groups.Add(new() { Start = 0, Capture = 0, PieceBase = 0, BranchBase = 0 });
        if ((_profile.Flags & XPathFlags.Quote) != 0)
        {
            while (_at < _text.Length)
            { var start = _at; var scalar = Scalar(); _pieces.Add(Literal(scalar, start, _at)); }
            Reduce(_pieces, 0, XmlNodeKind.Sequence);
            return;
        }
        while (true)
        {
            Here(_at, Next);
            _storage.Work();
            var ch = Peek();
            if (_at == _text.Length || ch == ')')
            {
                if (_at < _text.Length && _groups.Count == 1)
                {
                    _storage.Fail();
                }

                if (_at == _text.Length && _groups.Count != 1)
                {
                    _storage.Fail();
                }

                FinishBranch();
                var frame = _groups.Pop();
                var root = Reduce(_branches, frame.BranchBase, XmlNodeKind.Alternative);
                if (_at == _text.Length)
                {
                    return;
                }

                _at++;
                if (frame.Capture > 0)
                { root = Node(XmlNodeKind.Capture, frame.Start, _at, x: root, value: frame.Capture); _closed[frame.Capture] = true; }
                else
                { var empty = Node(XmlNodeKind.Empty, frame.Start, frame.Start); root = Node(XmlNodeKind.Sequence, frame.Start, _at, x: empty, y: root); }
                _pieces.Add(Quantify(root, frame.Start));
                continue;
            }
            if (ch == '|')
            { FinishBranch(); _at++; continue; }
            if (ch == '(')
            {
                var start = _at++;
                var capture = true;
                if (Peek() == '?')
                { _at++; Expect(':'); capture = false; }
                int parent = _groups[_groups.Count - 1].ActiveCapture, group = 0;
                if (capture)
                {
                    Here(start, _at, "XP-004");
                    if (_parents.Count == _storage.Budget.Limits.MaxCaptureGroups)
                    {
                        _storage.Fail(RegexDiagnosticCode.CaptureLimitExceeded);
                    }

                    group = _parents.Count;
                    _parents.Add(parent);
                    _closed.Add(false);
                }
                Nest(_groups.Count, start);
                _groups.Add(new()
                {
                    Start = start,
                    Capture = group,
                    ActiveCapture = capture ? group : parent,
                    PieceBase = _pieces.Count,
                    BranchBase = _branches.Count
                });
                continue;
            }
            var atomStart = _at;
            int atom;
            if (ch == '[')
            {
                atom = ClassNode(ParseClass(), atomStart, _at);
            }
            else if (ch == '.')
            {
                _at++;
                var set = (_profile.Flags & XPathFlags.DotAll) != 0 ? Apply(XmlSetOperation.Union, _unicode.Alphabet, [], atomStart, _at)
                    : Apply(XmlSetOperation.Subtract, _unicode.Alphabet, [new(10, 10), new(13, 13)], atomStart, _at);
                atom = ClassNode(set, atomStart, _at);
            }
            else if (ch is '^' or '$')
            { _at++; atom = Node(ch == '^' ? XmlNodeKind.Start : XmlNodeKind.End, atomStart, _at); }
            else if (ch == '\\' && Peek(1) is >= '1' and <= '9')
            {
                atom = Backreference();
            }
            else if (ch == '\\')
            {
                ReadEscape(out var scalar, out var set);
                atom = scalar >= 0 ? Literal(scalar, atomStart, _at) : ClassNode(set!, atomStart, _at);
            }
            else
            {
                if (ch is '?' or '*' or '+' or '{' or '}' or ']')
                {
                    _storage.Fail();
                }

                var scalar = Scalar();
                atom = Literal(scalar, atomStart, _at);
            }
            _pieces.Add(Quantify(atom, atomStart));
        }
    }
    private int Backreference()
    {
        var start = _at++;
        var number = _text[_at++] - '0';
        while (Peek() is >= '0' and <= '9')
        {
            Here(start, _at + 1, "XP-005");
            _storage.Work();
            var next = (long)number * 10 + Peek() - '0';
            if (next >= _parents.Count)
            {
                break;
            }

            number = (int)next;
            _at++;
        }
        Here(start, _at, "XP-005");
        if (number >= _closed.Count || !_closed[number])
        {
            _storage.Fail();
        }

        return Node(XmlNodeKind.Backreference, start, _at, value: number);
    }
    private int Quantify(int atom, int start)
    {
        int min, max;
        var ch = Peek();
        if (ch == '?')
        { _at++; min = 0; max = 1; }
        else if (ch == '*')
        { _at++; min = 0; max = -1; }
        else if (ch == '+')
        { _at++; min = 1; max = -1; }
        else if (ch == '{')
        {
            _at++;
            min = Decimal();
            max = min;
            if (Peek() == ',')
            { _at++; max = Peek() == '}' ? -1 : Decimal(); }
            Expect('}');
            Here(start, _at, "XP-003");
            if (max != -1 && min > max)
            {
                _storage.Fail();
            }
        }
        else
        {
            return atom;
        }

        var lazy = Peek() == '?';
        if (lazy)
        {
            _at++;
        }

        return Node(XmlNodeKind.Repeat, start, _at, x: atom, min: min, max: max, lazy: lazy);
    }
    private int Decimal()
    {
        Here(_at, Next, "XP-003");
        if (Peek() is < '0' or > '9')
        {
            _storage.Fail();
        }

        var value = 0;
        while (Peek() is >= '0' and <= '9')
        {
            Here(_at, _at + 1, "XP-003");
            _storage.Work();
            var digit = _text[_at++] - '0';
            if (value > (int.MaxValue - digit) / 10)
            {
                _storage.Fail(RegexDiagnosticCode.ProgramLimitExceeded);
            }

            value = value * 10 + digit;
        }
        return value;
    }
}
