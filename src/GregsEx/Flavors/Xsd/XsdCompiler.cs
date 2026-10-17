#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.Xsd;

internal sealed class XsdCompiler : IXmlCompiler
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
        if (profile is not XsdProfile || inputKind is not (RegexInputKind.Utf16 or RegexInputKind.Utf8) ||
            unicode.DataId != profile.DataId)
        { diagnostic = new(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Utf16CodeUnits), default, default, "XS-009"); return false; }
        var priorBytes = budget.ReservedBytes;
        var parser = new Parser(pattern, unicode, algebra, ref budget);
        try
        {
#if NETSTANDARD2_0
            var ok = parser.Parse() && parser.AdmitPlan(priorBytes);
            budget = parser.Budget;
            ok = ok && XmlProgram.TryCreate(profile, inputKind, pattern.Length,
                parser.Nodes.Array.AsSpan(0, parser.Nodes.Count), parser.Sets.Array.AsSpan(0, parser.Sets.Count),
                parser.Intervals.Array.AsSpan(0, parser.Intervals.Count), [-1], ref budget, out program, out parser.Diagnostic);
            parser.Budget = budget;
            if (!ok)
#else
            if (!parser.Parse() || !parser.AdmitPlan(priorBytes) || !XmlProgram.TryCreate(profile, inputKind, pattern.Length,
                parser.Nodes.Array.AsSpan(0, parser.Nodes.Count), parser.Sets.Array.AsSpan(0, parser.Sets.Count),
                parser.Intervals.Array.AsSpan(0, parser.Intervals.Count), [-1], ref budget, out program, out parser.Diagnostic))
#endif
            { diagnostic = new(parser.Diagnostic, default, default, "XS-010"); return false; }
            return true;
        }
#if NETSTANDARD2_0
        finally { parser.Dispose(); budget = parser.Budget; }
#else
        finally { parser.Dispose(); }
#endif
    }

    private struct Group(int start, int pieces, int branches)
    { internal int Start = start, PieceStart = pieces, BranchStart = branches; }
    private struct ClassFrame(int start, bool negative)
    { internal int Start = start, Parts; internal bool Negative = negative, Subtract; internal XmlInterval[] Set = []; }

    private ref struct Parser(ReadOnlySpan<char> text, IXmlUnicodeData unicode,
        IXmlClassAlgebra algebra, ref CompileBudget budget)
    {
        private readonly ReadOnlySpan<char> _text = text;
        private readonly IXmlUnicodeData _unicode = unicode;
        private readonly IXmlClassAlgebra _algebra = algebra;
#if NETSTANDARD2_0
        // The netstandard2.0 build (generator host) has no ref fields; the caller copies the budget back.
        private CompileBudget _budget = budget;
        internal CompileBudget Budget { readonly get => _budget; set => _budget = value; }
#else
        private ref CompileBudget _budget = ref budget;
#endif
        private int _at;
        internal RegexDiagnostic Diagnostic;
        internal XsdBuffer<XmlNode> Nodes = new(48);
        internal XsdBuffer<XmlSetSlice> Sets = new(8);
        internal XsdBuffer<XmlInterval> Intervals = new(8);
        private XsdBuffer<Group> _groups = new(16);
        private XsdBuffer<ClassFrame> _classes = new(48);
        private XsdBuffer<int> _pieces = new(4), _branches = new(4);
        private readonly SourceRange Source(int start, int end) => new(start, end - start, RegexOffsetUnit.Utf16CodeUnits);
        private bool Fail(RegexDiagnosticCode code, int at, int length = 1)
        { Diagnostic = new(code, at, Math.Min(length, _text.Length - at), RegexOffsetUnit.Utf16CodeUnits); return false; }
        private bool Charge(long n, int start = 0) => _budget.TryCharge(n, Source(start, _at < start ? start : _at), out Diagnostic);
        private bool AddNode(XmlNode node, out int id)
        { id = Nodes.Count; return Charge(1, node.Source.Offset) && Nodes.Add(node, _budget.Limits.MaxInstructions, ref _budget, node.Source, out Diagnostic); }
        private bool Leaf(XmlNodeKind kind, int value, int start, out int id)
            => AddNode(new(kind, -1, -1, value, 0, 0, false, Source(start, _at)), out id);
        private bool Pair(XmlNodeKind kind, int left, int right, out int id)
        {
            var a = Nodes.Array[left].Source;
            var b = Nodes.Array[right].Source;
            return AddNode(new(kind, left, right, 0, 0, 0, false, Source(a.Offset, b.Offset + b.Length)), out id);
        }
        private bool PushGroup(int start)
        {
            if (_groups.Count - 1 >= _budget.Limits.MaxNestingDepth)
            {
                return Fail(RegexDiagnosticCode.NestingLimitExceeded, start);
            }

            return _groups.Add(new(start, _pieces.Count, _branches.Count),
                (int)Math.Min((long)_text.Length + 1, (long)_budget.Limits.MaxNestingDepth + 1),
                ref _budget, Source(start, _at), out Diagnostic);
        }
        // Pairwise reduction retains order while keeping AST depth logarithmic in
        // the number of pieces/branches; syntax nesting uses a separate stack.
        private bool Reduce(ref XsdBuffer<int> values, int start, XmlNodeKind kind, out int root)
        {
            root = -1;
            var count = values.Count - start;
            if (count == 0)
            {
                return Leaf(XmlNodeKind.Empty, 0, _at, out root);
            }

            while (count > 1)
            {
                var written = 0;
                for (var i = 0; i < count; i += 2)
                {
                    var id = values.Array[start + i];
                    if (i + 1 < count && !Pair(kind, id, values.Array[start + i + 1], out id))
                    {
                        return false;
                    }

                    values.Array[start + written++] = id;
                }
                count = written;
            }
            root = values.Array[start];
            values.Count = start;
            return true;
        }
        private bool EndBranch()
        {
            var group = _groups.Array[_groups.Count - 1];
            if (!Reduce(ref _pieces, group.PieceStart, XmlNodeKind.Sequence, out var root))
            {
                return false;
            }

            return _branches.Add(root, _budget.Limits.MaxInstructions, ref _budget, Source(group.Start, _at), out Diagnostic);
        }
        internal bool AdmitPlan(long priorBytes)
        {
            var bytes = 384L + 48L * Nodes.Count + 8L * Sets.Count + 8L * Intervals.Count + 4;
            return bytes <= _budget.Limits.MaxPlanBytes - priorBytes || Fail(RegexDiagnosticCode.PlanLimitExceeded, 0, _text.Length);
        }
        internal bool Parse()
        {
            if (_text.Length > _budget.Limits.MaxPatternUnits)
            {
                return Fail(RegexDiagnosticCode.PatternLimitExceeded, 0, _text.Length);
            }
            // Validate the original entire pattern before interpreting metacharacters.
            for (var i = 0; i < _text.Length;)
            {
                int start = i, scalar = _text[i++];
                if (!_budget.TryCharge(1, Source(start, i), out Diagnostic))
                {
                    return false;
                }

                if (scalar is >= 0xD800 and <= 0xDFFF)
                {
                    if (scalar > 0xDBFF || i == _text.Length || _text[i] is < '\uDC00' or > '\uDFFF')
                    {
                        return Fail(RegexDiagnosticCode.InvalidPatternEncoding, start);
                    }

                    if (!_budget.TryCharge(1, Source(i, i + 1), out Diagnostic))
                    {
                        return false;
                    }

                    scalar = 0x10000 + ((scalar - 0xD800) << 10) + _text[i++] - 0xDC00;
                }
                if (!XmlAlphabet.Contains(scalar))
                {
                    return Fail(RegexDiagnosticCode.InvalidPatternEncoding, start, i - start);
                }
            }
            if (!PushGroup(0))
            {
                return false;
            }

            while (true)
            {
                if (!Charge(1))
                {
                    return false;
                }

                if (_at == _text.Length || _text[_at] == ')')
                {
                    if (!EndBranch())
                    {
                        return false;
                    }

                    var group = _groups.Array[--_groups.Count];
                    if (!Reduce(ref _branches, group.BranchStart, XmlNodeKind.Alternative, out var root))
                    {
                        return false;
                    }

                    if (_groups.Count == 0)
                    {
                        if (_at != _text.Length)
                        {
                            return Fail(RegexDiagnosticCode.InvalidSyntax, _at);
                        }

                        Nodes.Array[root] = Nodes.Array[root] with { Source = Source(0, _at) };
                        return true;
                    }
                    if (_at == _text.Length)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, group.Start);
                    }

                    _at++;
                    // Noncapturing grouping changes source bounds, not the language.
                    Nodes.Array[root] = Nodes.Array[root] with { Source = Source(group.Start, _at) };
                    if (!Quantify(ref root) || !Piece(root))
                    {
                        return false;
                    }
                }
                else if (_text[_at] == '|')
                { if (!EndBranch()) { return false; } _at++; }
                else if (_text[_at] == '(')
                {
                    var start = _at++;
                    if (!PushGroup(start))
                    {
                        return false;
                    }
                }
                else
                {
                    int start = _at, root;
                    var c = _text[_at];
                    if (c is '?' or '*' or '+' or '{' or '}' or ']')
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, _at);
                    }

                    if (c == '[')
                    {
                        if (!Class(out var set))
                        {
                            return false;
                        }

                        if (!ClassNode(set!, start, out root))
                        {
                            return false;
                        }
                    }
                    else if (c == '\\')
                    {
                        if (!Escape(out var scalar, out var set))
                        {
                            return false;
                        }

                        if (set is null)
                        {
                            if (!Leaf(XmlNodeKind.Literal, scalar, start, out root))
                            {
                                return false;
                            }
                        }
                        else if (!ClassNode(set, start, out root))
                        {
                            return false;
                        }
                    }
                    else if (c == '.')
                    {
                        _at++;
                        if (!Apply(XmlSetOperation.Subtract, _unicode.Alphabet, [new(10, 10), new(13, 13)], start, out var set) ||
                            !ClassNode(set!, start, out root))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        var scalar = Scalar();
                        if (!Leaf(XmlNodeKind.Literal, scalar, start, out root))
                        {
                            return false;
                        }
                    }
                    if (!Quantify(ref root) || !Piece(root))
                    {
                        return false;
                    }
                }
            }
        }
        private bool Piece(int root) => _pieces.Add(root, _budget.Limits.MaxInstructions, ref _budget, Nodes.Array[root].Source, out Diagnostic);
        private int Scalar()
        {
            int c = _text[_at++];
            return c is >= 0xD800 and <= 0xDBFF ? 0x10000 + ((c - 0xD800) << 10) + _text[_at++] - 0xDC00 : c;
        }
        private bool Number(out int value)
        {
            value = 0;
            var start = _at;
            while (_at < _text.Length && _text[_at] is >= '0' and <= '9')
            {
                if (!Charge(1, start))
                {
                    return false;
                }

                var digit = _text[_at++] - '0';
                if (value > (int.MaxValue - digit) / 10)
                {
                    return Fail(RegexDiagnosticCode.ProgramLimitExceeded, start, _at - start);
                }

                value = value * 10 + digit;
            }
            return _at > start || Fail(RegexDiagnosticCode.InvalidSyntax, _at, 0);
        }
        private bool Quantify(ref int root)
        {
            if (_at == _text.Length)
            {
                return true;
            }

            int min, max, start = Nodes.Array[root].Source.Offset;
            switch (_text[_at])
            {
                case '?':
                    min = 0;
                    max = 1;
                    _at++;
                    break;
                case '*':
                    min = 0;
                    max = -1;
                    _at++;
                    break;
                case '+':
                    min = 1;
                    max = -1;
                    _at++;
                    break;
                case '{':
                    _at++;
                    if (!Number(out min))
                    {
                        return false;
                    }

                    max = min;
                    if (_at < _text.Length && _text[_at] == ',')
                    {
                        _at++;
                        if (_at < _text.Length && _text[_at] == '}')
                        {
                            max = -1;
                        }
                        else if (!Number(out max))
                        {
                            return false;
                        }
                    }
                    if (_at == _text.Length || _text[_at] != '}' || max != -1 && min > max)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, _at, 0);
                    }

                    _at++;
                    break;
                default:
                    return true;
            }
            return AddNode(new(XmlNodeKind.Repeat, root, -1, 0, min, max, false, Source(start, _at)), out root);
        }
        private static long SetBytes(XmlInterval[] set) => set.Length == 0 ? 0 : 32L + 8L * set.Length;
        private bool Apply(XmlSetOperation op, scoped ReadOnlySpan<XmlInterval> left, scoped ReadOnlySpan<XmlInterval> right, int start, out XmlInterval[]? set)
            => _algebra.TryApply(op, left, right, _unicode, ref _budget, Source(start, _at), out set, out Diagnostic);
        private bool ClassNode(XmlInterval[] set, int start, out int root)
        {
            root = -1;
            try
            {
                if (set.Length > _budget.Limits.MaxClassRanges - Intervals.Count)
                {
                    return Fail(RegexDiagnosticCode.ClassLimitExceeded, start, _at - start);
                }

                int index = Sets.Count, offset = Intervals.Count;
                foreach (var interval in set)
                {
                    if (!Charge(1, start) || !Intervals.Add(interval, _budget.Limits.MaxClassRanges, ref _budget, Source(start, _at), out Diagnostic))
                    {
                        return false;
                    }
                }

                if (!Sets.Add(new(offset, set.Length), _budget.Limits.MaxInstructions, ref _budget, Source(start, _at), out Diagnostic))
                {
                    return false;
                }

                return Leaf(XmlNodeKind.Class, index, start, out root);
            }
            finally { _budget.ReleaseBytes(SetBytes(set)); }
        }
        private bool Escape(out int scalar, out XmlInterval[]? set)
        {
            scalar = 0;
            set = null;
            var start = _at++;
            if (_at == _text.Length)
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, start);
            }

            var c = _text[_at++];
            scalar = c switch { 'n' => 10, 'r' => 13, 't' => 9, _ => c };
            if (c is 'n' or 'r' or 't' or '\\' or '|' or '.' or '?' or '*' or '+' or '(' or ')' or '{' or '}' or '-' or '[' or ']' or '^')
            {
                return true;
            }

            ReadOnlySpan<XmlInterval> ranges;
            var inverse = c is 'S' or 'D' or 'W' or 'I' or 'C' or 'P';
            var kind = c switch
            {
                's' or 'S' => XmlPredefinedClass.Whitespace,
                'd' or 'D' => XmlPredefinedClass.Digit,
                'w' or 'W' => XmlPredefinedClass.Word,
                'i' or 'I' => XmlPredefinedClass.NameStart,
                _ => XmlPredefinedClass.NameChar
            };
            if (c is 's' or 'S' or 'd' or 'D' or 'w' or 'W' or 'i' or 'I' or 'c' or 'C')
            {
                ranges = _unicode.GetPredefined(kind);
            }
            else if (c is 'p' or 'P')
            {
                if (_at == _text.Length || _text[_at++] != '{')
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                }

                var nameStart = _at;
                while (_at < _text.Length && _text[_at] != '}')
                { if (!Charge(1, start)) { return false; } _at++; }
                if (_at == _text.Length)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                }

                var name = _text.Slice(nameStart, _at++ - nameStart);
                if (name.StartsWith("Is"))
                {
                    var block = name[2..];
                    if (block.IsEmpty)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                    }

                    foreach (var b in block)
                    {
                        if (!(b is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                        {
                            return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                        }
                    }

                    if (!_unicode.TryGetBlock(block, out ranges))
                    { ranges = _unicode.Alphabet; inverse = false; }
                }
                else if (!_unicode.TryGetCategory(name, out ranges))
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                }
            }
            else
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
            }

            return Apply(inverse ? XmlSetOperation.Complement : XmlSetOperation.Union, ranges, [], start, out set);
        }
        private bool OpenClass()
        {
            var start = _at++;
            if ((long)_classes.Count + _groups.Count - 1 >= _budget.Limits.MaxNestingDepth)
            {
                return Fail(RegexDiagnosticCode.NestingLimitExceeded, start);
            }

            var negative = _at < _text.Length && _text[_at] == '^';
            if (negative)
            {
                _at++;
            }

            return _classes.Add(new(start, negative), _budget.Limits.MaxNestingDepth, ref _budget, Source(start, _at), out Diagnostic);
        }
        private bool Class(out XmlInterval[]? result)
        {
            result = null;
            if (!OpenClass())
            {
                return false;
            }

            while (_classes.Count > 0)
            {
                if (!Charge(1))
                {
                    return false;
                }

                ref var frame = ref _classes.Array[_classes.Count - 1];
                if (_at == _text.Length)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, frame.Start, _at - frame.Start);
                }

                if (_text[_at] == ']')
                {
                    if (frame.Parts == 0)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, frame.Start, _at - frame.Start + 1);
                    }

                    _at++;
                    if (frame.Negative)
                    {
                        if (!Apply(XmlSetOperation.Complement, frame.Set, [], frame.Start, out var complement))
                        {
                            return false;
                        }

                        _budget.ReleaseBytes(SetBytes(frame.Set));
                        frame.Set = complement!;
                        frame.Negative = false;
                    }
                    var completed = frame.Set;
                    frame.Set = [];
                    _classes.Count--;
                    if (_classes.Count == 0)
                    { result = completed; return true; }
                    ref var parent = ref _classes.Array[_classes.Count - 1];
                    var ok = Apply(XmlSetOperation.Subtract, parent.Set, completed, parent.Start, out var difference);
                    _budget.ReleaseBytes(SetBytes(completed));
                    if (!ok)
                    {
                        return false;
                    }

                    _budget.ReleaseBytes(SetBytes(parent.Set));
                    parent.Set = difference!;
                    if (_at == _text.Length || _text[_at] != ']')
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, _at, 0);
                    }

                    continue;
                }
                if (frame.Subtract)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, _at);
                }

                if (_text[_at] == '-' && _at + 1 < _text.Length && _text[_at + 1] == '[')
                {
                    if (frame.Parts == 0)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, _at);
                    }

                    if (frame.Negative)
                    {
                        if (!Apply(XmlSetOperation.Complement, frame.Set, [], frame.Start, out var complement))
                        {
                            return false;
                        }

                        _budget.ReleaseBytes(SetBytes(frame.Set));
                        frame.Set = complement!;
                        frame.Negative = false;
                    }
                    frame.Subtract = true;
                    _at++;
                    if (!OpenClass())
                    {
                        return false;
                    }

                    continue;
                }
                var start = _at;
                if (!ClassPart(out var scalar, out var unescapedHyphen, out var set))
                {
                    return false;
                }

                if (set is null && _at < _text.Length && _text[_at] == '-' &&
                    _at + 1 < _text.Length && _text[_at + 1] is not ('[' or ']') &&
                    !(_text[_at + 1] == '-' && _at + 2 < _text.Length && _text[_at + 2] == '['))
                {
                    _at++;
                    if (!ClassPart(out var end, out var endHyphen, out var endSet))
                    {
                        return false;
                    }

                    if (endSet is not null)
                    {
                        _budget.ReleaseBytes(SetBytes(endSet));
                    }

                    if (unescapedHyphen || endHyphen || end == '-' || endSet is not null || scalar > end)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, start, _at - start);
                    }

                    if (!Apply(XmlSetOperation.Union, [new(scalar, end)], [], start, out set))
                    {
                        return false;
                    }
                }
                else if (set is null && !Apply(XmlSetOperation.Union, [new(scalar, scalar)], [], start, out set))
                {
                    return false;
                }

                var unionOk = Apply(XmlSetOperation.Union, frame.Set, set, frame.Start, out var union);
                _budget.ReleaseBytes(SetBytes(set!));
                if (!unionOk)
                {
                    return false;
                }

                _budget.ReleaseBytes(SetBytes(frame.Set));
                frame.Set = union!;
                frame.Parts++;
            }
            return false;
        }
        private bool ClassPart(out int scalar, out bool hyphen, out XmlInterval[]? set)
        {
            scalar = 0;
            hyphen = false;
            set = null;
            if (_at == _text.Length || _text[_at] is '[' or ']')
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, _at, 0);
            }

            if (_text[_at] == '\\')
            {
                return Escape(out scalar, out set);
            }

            hyphen = _text[_at] == '-';
            scalar = Scalar();
            return true;
        }
        internal void Dispose()
        {
            for (var i = 0; i < _classes.Count; i++)
            {
                _budget.ReleaseBytes(SetBytes(_classes.Array[i].Set));
            }

            Nodes.Release(ref _budget);
            Sets.Release(ref _budget);
            Intervals.Release(ref _budget);
            _groups.Release(ref _budget);
            _classes.Release(ref _budget);
            _pieces.Release(ref _budget);
            _branches.Release(ref _budget);
        }
    }
}
