using GregsEx.Contracts.Xml;

namespace GregsEx.Flavors.XPath.Parsing;

internal ref partial struct XPathParser
{
    private readonly void ValidateDomain()
    {
        for (var i = 0; i < _original.Length; i++)
        {
            _storage.Source = new(i, 1, RegexOffsetUnit.Utf16CodeUnits);
            _storage.Feature = "XP-017";
            _storage.Work();
            int cp = _original[i];
            if (cp is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 1 == _original.Length || _original[i + 1] is < '\uDC00' or > '\uDFFF')
                {
                    _storage.Fail(RegexDiagnosticCode.InvalidPatternEncoding);
                }

                _storage.Work();
                cp = 0x10000 + ((cp - 0xD800) << 10) + _original[++i] - 0xDC00;
            }
            if (!XmlAlphabet.Contains(cp))
            {
                _storage.Fail(RegexDiagnosticCode.InvalidPatternEncoding);
            }
        }
    }
    private void StripWhitespace()
    {
        // The escape state follows retained characters, so backslash + removed
        // whitespace + '[' remains an escaped bracket rather than a class opener.
        _storage.Source = new(0, _original.Length, RegexOffsetUnit.Utf16CodeUnits);
        _storage.Reserve(64L + 6L * _original.Length);
        var text = new char[_original.Length];
        var map = new int[_original.Length];
        int count = 0, classDepth = 0;
        var escaped = false;
        for (var i = 0; i < _original.Length; i++)
        {
            _storage.Work();
            var ch = _original[i];
            if (classDepth == 0 && ch is '\t' or '\n' or '\r' or ' ')
            {
                continue;
            }

            text[count] = ch;
            map[count++] = i;
            if (!escaped)
            {
                if (ch == '[')
                {
                    classDepth++;
                }
                else if (ch == ']' && classDepth > 0)
                {
                    classDepth--;
                }
            }
            escaped = !escaped && ch == '\\';
        }
        _text = text.AsSpan(0, count);
        _map = map.AsSpan(0, count);
        _mapped = true;
    }
    private readonly XmlInterval[] Apply(XmlSetOperation operation, scoped ReadOnlySpan<XmlInterval> left,
        scoped ReadOnlySpan<XmlInterval> right, int start, int end)
    {
        Here(start, end, operation == XmlSetOperation.CaseVariants ? "XP-009" : "XP-001");
        if (!_algebra.TryApply(operation, left, right, _unicode, ref _storage.Budget,
            _storage.Source, out var result, out var diagnostic))
        {
            throw new ParseFailure(diagnostic, _storage.Feature);
        }

        _storage.Adopt(result!);
        return result!;
    }
    private readonly int Literal(int scalar, int start, int end)
    {
        if (!IgnoreCase)
        {
            return Node(XmlNodeKind.Literal, start, end, value: scalar);
        }

        return ClassNode(Apply(XmlSetOperation.CaseVariants, [new(scalar, scalar)], [], start, end), start, end);
    }
    private readonly int ClassNode(XmlInterval[] intervals, int start, int end)
    {
        Here(start, end);
        _storage.Work(intervals.Length);
        if (intervals.Length > _storage.Budget.Limits.MaxClassRanges - _intervals.Count)
        {
            _storage.Fail(RegexDiagnosticCode.ClassLimitExceeded);
        }

        var offset = _intervals.Count;
        foreach (var interval in intervals)
        {
            _intervals.Add(interval);
        }

        var id = _sets.Count;
        _sets.Add(new(offset, intervals.Length));
        _storage.Release(intervals);
        return Node(XmlNodeKind.Class, start, end, value: id);
    }
    private void ReadEscape(out int scalar, out XmlInterval[]? intervals)
    {
        var start = _at;
        scalar = -1;
        intervals = null;
        Expect('\\');
        Here(start, Next);
        var ch = Peek();
        if (_at == _text.Length)
        {
            _storage.Fail();
        }

        _at++;
        _storage.Work();
        scalar = ch switch
        {
            'n' => 10,
            'r' => 13,
            't' => 9,
            '\\' or '|' or '.' or '?' or '*' or '+' or '(' or ')' or '{' or '}' or '-' or '[' or ']' or '^' or '$' => ch,
            _ => -1
        };
        if (scalar >= 0)
        {
            return;
        }

        var complement = ch is 'P' or 'S' or 'D' or 'W' or 'I' or 'C';

        ReadOnlySpan<XmlInterval> set;
        if (ch is 'p' or 'P')
        {
            Expect('{');
            var nameStart = _at;
            while (_at < _text.Length && Peek() != '}')
            { _storage.Work(); _at++; }
            var nameEnd = _at;
            Expect('}');
            var name = _text.Slice(nameStart, nameEnd - nameStart);
            Here(start, _at, "XP-006");
            _storage.Work(name.Length + 1L);
            if (name.StartsWith("Is", StringComparison.Ordinal))
            {
                if (name.Length == 2)
                {
                    _storage.Fail();
                }

                for (var i = 2; i < name.Length; i++)
                {
                    _storage.Work();
                    if (name[i] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                    {
                        _storage.Fail();
                    }
                }
                if (!_unicode.TryGetBlock(name[2..], out set))
                {
                    _storage.Fail();
                }
            }
            else if (!_unicode.TryGetCategory(name, out set))
            {
                _storage.Fail();
            }
        }
        else
        {
            var kind = ch switch
            {
                's' or 'S' => XmlPredefinedClass.Whitespace,
                'd' or 'D' => XmlPredefinedClass.Digit,
                'w' or 'W' => XmlPredefinedClass.Word,
                'i' or 'I' => XmlPredefinedClass.NameStart,
                'c' or 'C' => XmlPredefinedClass.NameChar,
                _ => (XmlPredefinedClass)(-1)
            };
            if ((int)kind < 0)
            { Here(start, _at); _storage.Fail(); }
            set = _unicode.GetPredefined(kind);
        }
        intervals = Apply(complement ? XmlSetOperation.Complement : XmlSetOperation.Union, set, [], start, _at);
    }
    private void PushClass()
    {
        var start = _at;
        Expect('[');
        var negative = Peek() == '^';
        if (negative)
        {
            _at++;
        }

        Nest(_groups.Count - 1 + _classes.Count + 1, start);
        _classes.Add(new()
        {
            Start = start,
            Negative = negative,
            LiteralBase = _literalParts.Count,
            EscapeBase = _escapeParts.Count
        });
    }
    private readonly XmlInterval[] FinishClassBase(ClassFrame frame)
    {
        if (frame.Parts == 0)
        { Here(frame.Start, _at); _storage.Fail(); }
        var literal = Apply(IgnoreCase ? XmlSetOperation.CaseVariants : XmlSetOperation.Union,
            _literalParts.Items[frame.LiteralBase..], [], frame.Start, _at);
        var result = Apply(XmlSetOperation.Union, literal, _escapeParts.Items[frame.EscapeBase..], frame.Start, _at);
        _storage.Release(literal);
        _literalParts.Truncate(frame.LiteralBase);
        _escapeParts.Truncate(frame.EscapeBase);
        if (frame.Negative)
        {
            var inverse = Apply(XmlSetOperation.Complement, result, [], frame.Start, _at);
            _storage.Release(result);
            result = inverse;
        }
        return result;
    }
    private XmlInterval[] ParseClass()
    {
        PushClass();
        while (true)
        {
            var frame = _classes[_classes.Count - 1];
            Here(_at, Next);
            _storage.Work();
            if (_at == _text.Length)
            {
                _storage.Fail();
            }

            if (Peek() == ']')
            {
                var result = frame.Waiting ? frame.Base! : FinishClassBase(frame);
                _at++;
                _classes.Pop();
                if (_classes.Count == 0)
                {
                    return result;
                }

                var parent = _classes[_classes.Count - 1];
                var difference = Apply(XmlSetOperation.Subtract, parent.Base!, result, parent.Start, _at);
                _storage.Release(parent.Base!);
                _storage.Release(result);
                parent.Base = difference;
                _classes[_classes.Count - 1] = parent;
                if (Peek() != ']')
                { Here(_at, Next); _storage.Fail(); }
                continue;
            }
            if (frame.Waiting)
            {
                _storage.Fail();
            }

            if (Peek() == '-' && Peek(1) == '[')
            {
                frame.Base = FinishClassBase(frame);
                frame.Waiting = true;
                _classes[_classes.Count - 1] = frame;
                _at++;
                PushClass();
                continue;
            }
            var start = _at;
            var escaped = Peek() == '\\';
            int scalar;
            XmlInterval[]? set = null;
            if (escaped)
            {
                ReadEscape(out scalar, out set);
            }
            else
            { if (Peek() == '[') { _storage.Fail(); } scalar = Scalar(); }
            if (scalar < 0)
            {
                Here(start, _at);
                _storage.Work(set!.Length);
                foreach (var interval in set)
                {
                    _escapeParts.Add(interval);
                }

                _storage.Release(set);
            }
            else
            {
                var end = scalar;
                if (Peek() == '-' && Peek(1) != '[' && Peek(1) != ']' && !(Peek(1) == '-' && Peek(2) == '['))
                {
                    _at++;
                    var endEscaped = Peek() == '\\';
                    if (endEscaped)
                    {
                        ReadEscape(out end, out var invalidSet);
                        if (invalidSet is not null)
                        { _storage.Release(invalidSet); Here(start, _at); _storage.Fail(); }
                    }
                    else
                    { if (Peek() is '[' or ']') { _storage.Fail(); } end = Scalar(); }
                    Here(start, _at);
                    if (scalar > end || scalar == '-' && !escaped || end == '-' && !endEscaped)
                    {
                        _storage.Fail();
                    }
                }
                _literalParts.Add(new(scalar, end));
            }
            frame.Parts++;
            _classes[_classes.Count - 1] = frame;
        }
    }
}
