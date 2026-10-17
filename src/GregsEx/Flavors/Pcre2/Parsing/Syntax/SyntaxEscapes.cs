#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal ref partial struct SyntaxReader
{
    private void EscapeAtom(int start)
    {
        var begin = _p;
        try
        { EscapeAtomCore(start); }
        catch (SyntaxFailure e) when ((_extra & Pcre2ExtraFlags.BadEscapeIsLiteral) != 0 && Literalizable(e.Diagnostic))
        { _p = begin; var cp = Peek() < 0 ? '\\' : Read(); Emit(Node(Pcre2NodeKind.Literal, start, _p, cp), start); }
    }
    private static bool Literalizable(Pcre2Diagnostic d) => d.Kind == Pcre2DiagnosticKind.Syntax && (int)d.SourceError is
        101 or 102 or 103 or 134 or 137 or 151 or 155 or 157 or 158 or 161 or 164 or 167 or 168 or 173 or 177 or 178 or 183 or 193 or 198 or 202 or 142 or 144 or 148 or 162 or 169;
    private void EscapeAtomCore(int start)
    {
        var c = Peek();
        if (c < 0)
        {
            Error(1);
        }

        if (c == 'Q')
        { _p++; _quote = true; return; }
        if (c == 'E')
        { _p++; return; }
        Pcre2NodeKind? kind = c switch
        {
            'A' => Pcre2NodeKind.AbsoluteStart,
            'z' => Pcre2NodeKind.AbsoluteEnd,
            'Z' => Pcre2NodeKind.BeforeFinalNewline,
            'G' => Pcre2NodeKind.StartOffset,
            'b' => Pcre2NodeKind.WordBoundary,
            'B' => Pcre2NodeKind.NotWordBoundary,
            'K' => Pcre2NodeKind.ResetStart,
            'R' => Pcre2NodeKind.Newline,
            'X' => Pcre2NodeKind.Grapheme,
            'C' => Pcre2NodeKind.CodeUnit,
            _ => null
        };
        if (kind.HasValue)
        {
            _p++;
            if (c == 'C' && (_flags & Pcre2CompileFlags.NeverBackslashC) != 0)
            {
                Error(83);
            }

            Emit(Node(kind.Value, start, _p), start);
            return;
        }
        if (c is 'k' or 'g')
        {
            _p++;
            var sub = c == 'g' && Peek() is '<' or '\'';
            var end = Take('{') ? '}' : Take('<') ? '>' : Take('\'') ? '\'' : 0;
            if (end != 0)
            {
                var begin = _p;
                SkipSpace(ref _p);
                if (c == 'g' && (Digit(Peek()) || Peek() is '-' or '+'))
                {
                    int reference;
                    try
                    { reference = NumericReference(sub || end == '}'); }
                    // Native delimited number reading uses a local pointer. On
                    // an invalid negative target the outer pointer stays after g.
                    catch (SyntaxFailure e) when ((int)e.Diagnostic.SourceError == 115)
                    { Error(15, start + 2); throw; }
                    SkipSpace(ref _p);
                    if (!Take(end))
                    {
                        Error(57, start + 2);
                    }

                    if (!sub && _references[reference].Kind == Pcre2ReferenceKind.WholePattern)
                    {
                        Error(15);
                    }

                    _referenceNative[reference] = _p - 1;
                    Emit(Node(sub ? Pcre2NodeKind.Subroutine : Pcre2NodeKind.Backreference, start, _p, reference), start);
                    return;
                }
                _p = begin;
                ReferenceAtom(start, Name(end, end == '}'), sub);
                return;
            }
            if (c == 'g' && (Digit(Peek()) || Peek() is '-' or '+'))
            { Emit(Node(Pcre2NodeKind.Backreference, start, NumericReferenceEnd(out var reference), reference), start); return; }
            Error(c == 'k' ? 69 : 57);
        }
        if (c is >= '1' and <= '9')
        {
            int begin = _p, q = _p;
            long n = 0;
            while (q < _input.Length && Digit(_input[q]))
            { Charge(); n = Math.Min(65536, n * 10 + _input[q++] - '0'); }
            var python = (_extra & Pcre2ExtraFlags.PythonOctal) != 0;
            var pythonOctal = c <= '7' && q - begin >= 3 && _input[begin + 1] is >= '0' and <= '7' && _input[begin + 2] is >= '0' and <= '7';
            if (python ? !pythonOctal : n < 10 || c >= '8' || n < _nextGroup)
            {
                if (n > 65535)
                {
                    Error(61, q);
                }

                _p = q;
                Emit(Node(Pcre2NodeKind.Backreference, start, _p, Reference(Pcre2ReferenceKind.Absolute, (int)n, -1, _p - 1)), start);
                return;
            }
        }
        if (c == 'N' && Peek(1) == '{')
        {
            int save = _p + 2, q = save;
            SkipSpace(ref q);
            if (q + 1 < _input.Length && _input[q] == 'U' && _input[q + 1] == '+')
            { }
            else
            {
                _p = save;
                var repeat = TryRepeat(out _, out _);
                _p = start + 2;
                if (!repeat)
                {
                    Error(37);
                }

                var node = Node(Pcre2NodeKind.Dot, start, _p);
                _nodes[node] = _nodes[node] with { Flags = _flags & ~Pcre2CompileFlags.Dotall };
                Emit(node, start);
                return;
            }
        }
        if (c == 'N' && Peek(1) != '{')
        { _p++; var node = Node(Pcre2NodeKind.Dot, start, _p); _nodes[node] = _nodes[node] with { Flags = _flags & ~Pcre2CompileFlags.Dotall }; Emit(node, start); return; }
        var value = EscapeValue(false, out var cls);
        if (_literalOpenBrace)
        {
            _literalOpenBrace = false;
            Emit(Node(Pcre2NodeKind.Literal, start, _p, 'u'), start);
            var brace = _p++;
            Emit(Node(Pcre2NodeKind.Literal, brace, _p, '{'), brace);
            return;
        }
        Emit(Node(cls >= 0 ? Pcre2NodeKind.Class : Pcre2NodeKind.Literal, start, _p, cls >= 0 ? cls : value), start);
    }
    private int NumericReferenceEnd(out int reference)
    { reference = NumericReference(); _referenceNative[reference] = _p - 1; return _p; }
    private int EscapeValue(bool isClass, out int cls)
    {
        var begin = _p;
        try
        { return EscapeValueCore(isClass, out cls); }
        catch (SyntaxFailure e) when ((_extra & Pcre2ExtraFlags.BadEscapeIsLiteral) != 0 &&
            (!isClass || _classFrames.Count == 0 || _classFrames[_classFrames.Count - 1].Mode < 2) && Literalizable(e.Diagnostic))
        { _p = begin; cls = -1; return Peek() < 0 ? '\\' : Read(); }
    }
    private int EscapeValueCore(bool isClass, out int cls)
    {
        cls = -1;
        if (Peek() < 0)
        {
            Error(1);
        }

        var c = Read();
        switch (c)
        {
            case 'a':
                return 7;
            case 'e':
                return 27;
            case 'f':
                return 12;
            case 'n':
                return 10;
            case 'r':
                return (_extra & Pcre2ExtraFlags.EscapedCrIsLf) != 0 ? 10 : 13;
            case 't':
                return 9;
            case 'b':
                if (isClass)
                {
                    return 8;
                }

                Error(7);
                break;
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
            case 'h':
            case 'H':
            case 'v':
            case 'V':
                cls = EscapeClass(c);
                return 0;
            case 'p':
            case 'P':
                cls = Property(c == 'P');
                return 0;
            case 'c':
                if (Peek() < 0)
                {
                    Error(2);
                }

                c = Peek();
                if (c is < 32 or > 126)
                {
                    Error(68);
                }

                _p++;
                if (c is >= 'a' and <= 'z')
                {
                    c -= 32;
                }

                return c ^ 64;
            case 'o':
                if (!Take('{'))
                {
                    Error(55);
                }

                return BracedValue(8, 64);
            case 'x':
                if ((_flags & Pcre2CompileFlags.AltBsux) != 0 || (_extra & Pcre2ExtraFlags.AltBsux) != 0)
                { if (Hex(Peek()) < 0 || Hex(Peek(1)) < 0) { return 'x'; } var v = (Hex(Peek()) << 4) | Hex(Peek(1)); _p += 2; return v; }
                if (Take('{'))
                {
                    return BracedValue(16, 67);
                }

                if (Hex(Peek()) < 0)
                {
                    Error(78);
                }

                var x = Hex(_input[_p++]);
                if (Hex(Peek()) >= 0)
                {
                    x = (x << 4) | Hex(_input[_p++]);
                }

                return x;
            case 'u':
                if ((_flags & Pcre2CompileFlags.AltBsux) == 0 && (_extra & Pcre2ExtraFlags.AltBsux) == 0)
                {
                    Error(37);
                }

                if (Peek() == '{' && (_extra & Pcre2ExtraFlags.AltBsux) != 0)
                {
                    var begin = _p++;
                    long v = 0;
                    var got = false;
                    while (Hex(Peek()) >= 0)
                    { Charge(); got = true; v = Math.Min(0x110000, v * 16 + Hex(_input[_p++])); }
                    if (!got || !Take('}'))
                    { _p = begin; if (!isClass) { _literalOpenBrace = true; } return 'u'; }
                    CheckValue((int)v, 77, _p);
                    return (int)v;
                }
                var ux = 0;
                for (var i = 0; i < 4; i++)
                {
                    if (Hex(Peek(i)) < 0)
                    {
                        return 'u';
                    }
                }

                for (var i = 0; i < 4; i++)
                {
                    ux = (ux << 4) | Hex(_input[_p++]);
                }

                CheckValue(ux, 77, _p);
                return ux;
            case 'U':
                if ((_flags & Pcre2CompileFlags.AltBsux) != 0 || (_extra & Pcre2ExtraFlags.AltBsux) != 0)
                {
                    return c;
                }

                Error(37);
                break;
            case 'N':
                if (Take('{'))
                {
                    var brace = _p - 1;
                    SkipSpace(ref _p);
                    if (!Take('U') || !Take('+'))
                    {
                        Error(37, brace);
                    }

                    if (!_profile.Utf)
                    {
                        Error(93, _p - 3);
                    }

                    return BracedValue(16, 67);
                }
                Error(71);
                break;
            case '0':
            case '1':
            case '2':
            case '3':
            case '4':
            case '5':
            case '6':
            case '7':
                int octal = c - '0', digits = 1;
                while (digits < 3 && Peek() is >= '0' and <= '7')
                { Charge(); octal = octal * 8 + _input[_p++] - '0'; digits++; }
                if (octal > 255 && (_extra & Pcre2ExtraFlags.PythonOctal) != 0)
                {
                    Error(102);
                }

                if (octal > 255 && !_profile.Utf && _profile.Width == Pcre2CodeUnitWidth.Eight)
                {
                    Error(51);
                }

                if (c == '0' && digits == 1 && (_extra & Pcre2ExtraFlags.NoBs0) != 0)
                {
                    Error(98);
                }

                return octal;
            case '8':
            case '9':
                return c;
            case 'g':
            case 'k':
                if (isClass)
                {
                    return c;
                }

                Error(57);
                break;
            case 'F':
            case 'l':
            case 'L':
                Error(37);
                break;
            case 'Q':
                if (isClass)
                { _quote = true; return -1; }
                break;
            case 'E':
                if (isClass)
                { _quote = false; return -1; }
                break;
            case 'A':
            case 'B':
            case 'C':
            case 'G':
            case 'K':
            case 'R':
            case 'X':
            case 'Z':
            case 'z':
                Error(7, _p - 1);
                break;
            default:
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                { Error(3, _p - 1); }
                return c;
        }
        return c;
    }
    private static int Hex(int c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'A' and <= 'F' ? c - 'A' + 10 : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
    private int BracedValue(int radix, int malformed)
    {
        SkipSpace(ref _p);
        if (Peek() < 0 || Peek() == '}')
        {
            Error(78);
        }

        long value = 0;
        var got = false;
        while (true)
        {
            var digit = radix == 16 ? Hex(Peek()) : Peek() is >= '0' and <= '7' ? Peek() - '0' : -1;
            if (digit < 0)
            {
                break;
            }

            got = true;
            Charge();
            _p++;
            value = Math.Min(0x110000, value * radix + digit);
        }
        SkipSpace(ref _p);
        if (!got || !Take('}'))
        {
            Error(malformed, _p);
        }

        CheckValue((int)value, 34);
        return (int)value;
    }
    private readonly void CheckValue(int value, int tooBig, int surrogateOffset = -1)
    {
        var maximum = _profile.Utf ? 0x10FFFF : _profile.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535;
        if (value > maximum)
        {
            Error(tooBig, tooBig == 34 ? _p - 1 : _p);
        }

        if (_profile.Utf && value is >= 0xD800 and <= 0xDFFF && (_extra & Pcre2ExtraFlags.AllowSurrogateEscapes) == 0)
        {
            Error(73, surrogateOffset < 0 ? _p - 1 : surrogateOffset);
        }
    }
    private int Property(bool negative)
    {
        Span<char> chars = stackalloc char[49];
        var count = 0;
        var negate = negative;
        if (Take('{'))
        {
            var sawNegation = false;
            while (true)
            {
                Charge();
                if (Peek() < 0)
                {
                    Error(46);
                }

                var c = _input[_p++];
                if (c is '_' or '-' or 32 or >= 9 and <= 13)
                {
                    continue;
                }

                if (c == '^' && count == 0 && !sawNegation)
                { negate = !negate; sawNegation = true; continue; }
                if (c == '}')
                {
                    break;
                }

                if (c < '&' || c > 'z' || count == 48)
                {
                    Error(46);
                }

                chars[count++] = (char)c;
            }
        }
        else
        { if (Peek() < 0) { Error(46); } var c = _input[_p++]; if (c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z')) { Error(46); } chars[count++] = (char)c; }
        if (!_data.TryResolveProperty(chars[..count], default, out var key))
        {
            Error(47);
        }

        var cls = ClassNode(new(Pcre2ClassKind.Property, -1, -1, default, key, 0));
        return negate ? Complement(cls) : cls;
    }
    private int EscapeClass(int c)
    {
        var negative = c is 'D' or 'S' or 'W' or 'H' or 'V';
        var lower = c is >= 'A' and <= 'Z' ? c + 32 : c;
        int cls;
        if (lower is 'h' or 'v')
        {
            // Fixed maximum9 intervals; never derive stack size from input.
            Span<Pcre2Interval> ranges = stackalloc Pcre2Interval[9];
            if (lower == 'h')
            {
                ranges[0] = new(9, 9);
                ranges[1] = new(32, 32);
                ranges[2] = new(160, 160);
                ranges[3] = new(0x1680, 0x1680);
                ranges[4] = new(0x180E, 0x180E);
                ranges[5] = new(0x2000, 0x200A);
                ranges[6] = new(0x202F, 0x202F);
                ranges[7] = new(0x205F, 0x205F);
                ranges[8] = new(0x3000, 0x3000);
                cls = Intervals(ranges);
            }
            else
            { ranges[0] = new(10, 13); ranges[1] = new(0x85, 0x85); ranges[2] = new(0x2028, 0x2029); cls = Intervals(ranges[..3]); }
        }
        else
        {
            var ascii = lower == 'd' ? Pcre2ExtraFlags.AsciiBsd : lower == 's' ? Pcre2ExtraFlags.AsciiBss : Pcre2ExtraFlags.AsciiBsw;
            if ((_flags & Pcre2CompileFlags.Ucp) != 0 && (_extra & ascii) == 0)
            {
                cls = ClassNode(new(Pcre2ClassKind.Property, -1, -1, default, lower == 'd' ? new(Pcre2PropertyKind.ParticularCategory, 13) :
                    new(Pcre2PropertyKind.Special, lower == 's' ? 6 : 8), 0));
            }
            else
            {
                cls = ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, lower == 'd' ? 2 : lower == 's' ? 0 : 5));
            }
        }
        return negative ? Complement(cls) : cls;
    }
}
