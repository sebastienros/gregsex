using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal ref partial struct SyntaxReader
{
    private bool Word(int start, int length, ReadOnlySpan<char> text)
    {
        Charge(length);
        if (length != text.Length)
        {
            return false;
        }

        for (var i = 0; i < length; i++)
        {
            if (_input[start + i] != text[i])
            {
                return false;
            }
        }

        return true;
    }
    private void Directives()
    {
        if ((_flags & Pcre2CompileFlags.Literal) != 0)
        {
            return;
        }

        var newline = _profile.Newline;
        var bsr = _profile.Bsr;
        var opt = _profile.Optimization;
        while (Peek() == '(' && Peek(1) == '*')
        {
            int start = _p + 2, end = start;
            while (end < _input.Length && _input[end] != ')')
            { Charge(); end++; }
            var len = end - start;
            if (Word(start, len, "UTF") || Word(start, len, _profile.Width == Pcre2CodeUnitWidth.Eight ? "UTF8" : "UTF16"))
            {
                _flags |= Pcre2CompileFlags.Utf;
            }
            else if (Word(start, len, "UCP"))
            {
                _flags |= Pcre2CompileFlags.Ucp;
            }
            else if (Word(start, len, "CR"))
            {
                newline = Pcre2Newline.Cr;
            }
            else if (Word(start, len, "LF"))
            {
                newline = Pcre2Newline.Lf;
            }
            else if (Word(start, len, "CRLF"))
            {
                newline = Pcre2Newline.Crlf;
            }
            else if (Word(start, len, "ANY"))
            {
                newline = Pcre2Newline.Any;
            }
            else if (Word(start, len, "ANYCRLF"))
            {
                newline = Pcre2Newline.Anycrlf;
            }
            else if (Word(start, len, "NUL"))
            {
                newline = Pcre2Newline.Nul;
            }
            else if (Word(start, len, "BSR_ANYCRLF"))
            {
                bsr = Pcre2Bsr.Anycrlf;
            }
            else if (Word(start, len, "BSR_UNICODE"))
            {
                bsr = Pcre2Bsr.Unicode;
            }
            else if (Word(start, len, "CASELESS_RESTRICT"))
            {
                _extra |= Pcre2ExtraFlags.CaselessRestrict;
            }
            else if (Word(start, len, "TURKISH_CASING"))
            {
                _extra |= Pcre2ExtraFlags.TurkishCasing;
            }
            else if (Word(start, len, "NO_AUTO_POSSESS"))
            { _flags |= Pcre2CompileFlags.NoAutoPossess; opt = opt with { AutoPossess = false }; }
            else if (Word(start, len, "NO_DOTSTAR_ANCHOR"))
            { _flags |= Pcre2CompileFlags.NoDotstarAnchor; opt = opt with { DotStarAnchor = false }; }
            else if (Word(start, len, "NO_START_OPT"))
            { _flags |= Pcre2CompileFlags.NoStartOptimize; opt = opt with { StartOptimize = false }; }
            else if (Word(start, len, "NOTEMPTY"))
            {
                _patternMatchFlags |= Pcre2MatchFlags.Notempty;
            }
            else if (Word(start, len, "NOTEMPTY_ATSTART"))
            {
                _patternMatchFlags |= Pcre2MatchFlags.NotemptyAtstart;
            }
            else if (Word(start, len, "NO_JIT"))
            { }
            else
            {
                var equal = start;
                while (equal < end && _input[equal] != '=')
                { Charge(); equal++; }
                var which = Word(start, equal - start, "LIMIT_MATCH") ? 1 : Word(start, equal - start, "LIMIT_DEPTH") || Word(start, equal - start, "LIMIT_RECURSION") ? 2 :
                    Word(start, equal - start, "LIMIT_HEAP") ? 3 : 0;
                if (which == 0 || equal == end)
                {
                    break;
                }

                _p = equal + 1;
                if (equal == end || !Digit(Peek()))
                {
                    Error(60, _p);
                }

                long n = 0;
                while (Digit(Peek()))
                { Charge(); if (n > uint.MaxValue / 10 - 1) { Error(60, _p); } var d = _input[_p++] - '0'; n = n * 10 + d; }
                if (_p != end || end == _input.Length)
                {
                    Error(60, _p);
                }

                if (which == 1)
                {
                    _patternLimits = _patternLimits with { MatchWork = n, NativeMatchLimit = (uint)n };
                }
                else if (which == 2)
                {
                    _patternLimits = _patternLimits with { Depth = (int)Math.Min(int.MaxValue, n), NativeDepthLimit = (uint)n };
                }
                else
                {
                    _patternLimits = _patternLimits with { HeapBytes = checked(n * 1024), NativeHeapLimit = (uint)n };
                }
            }
            _p = end + 1;
        }
        if ((_flags & Pcre2CompileFlags.MatchInvalidUtf) != 0)
        {
            _flags |= Pcre2CompileFlags.Utf;
        }

        _profile = new(_profile.Width, _flags, _extra, newline, bsr, _profile.CharacterTables, opt, _profile.MaxVariableLookbehind);
        if ((_flags & Pcre2CompileFlags.ExtendedMore) != 0)
        {
            _flags |= Pcre2CompileFlags.Extended;
        }
    }
    private readonly void ValidateOptions()
    {
        if ((_flags & Pcre2CompileFlags.Literal) != 0)
        {
            const Pcre2CompileFlags allowed = Pcre2CompileFlags.Anchored | Pcre2CompileFlags.AutoCallout | Pcre2CompileFlags.Caseless |
                Pcre2CompileFlags.Endanchored | Pcre2CompileFlags.Firstline | Pcre2CompileFlags.Literal | Pcre2CompileFlags.MatchInvalidUtf |
                Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoUtfCheck | Pcre2CompileFlags.UseOffsetLimit | Pcre2CompileFlags.Utf;
            const Pcre2ExtraFlags extra = Pcre2ExtraFlags.MatchLine | Pcre2ExtraFlags.MatchWord | Pcre2ExtraFlags.CaselessRestrict | Pcre2ExtraFlags.TurkishCasing;
            if ((_flags & ~allowed) != 0 || (_extra & ~extra) != 0)
            {
                Error(92, 0, Pcre2DiagnosticKind.Options);
            }
        }
        if (_profile.Utf && (_flags & Pcre2CompileFlags.NeverUtf) != 0)
        {
            Error(74, _p, Pcre2DiagnosticKind.Options);
        }
    }
    private readonly void ValidatePostUtfOptions()
    {
        if (_profile.Utf && _profile.Width == Pcre2CodeUnitWidth.Sixteen && (_extra & Pcre2ExtraFlags.AllowSurrogateEscapes) != 0)
        {
            Error(91, 0, Pcre2DiagnosticKind.Options);
        }

        if (_profile.Ucp && (_flags & Pcre2CompileFlags.NeverUcp) != 0)
        {
            Error(75, _p, Pcre2DiagnosticKind.Options);
        }

        if ((_extra & Pcre2ExtraFlags.TurkishCasing) != 0)
        {
            if (!_profile.Utf && !_profile.Ucp)
            {
                Error(104, 0, Pcre2DiagnosticKind.Options);
            }

            if (!_profile.Utf && _profile.Width == Pcre2CodeUnitWidth.Eight)
            {
                Error(105, 0, Pcre2DiagnosticKind.Options);
            }

            if ((_extra & Pcre2ExtraFlags.CaselessRestrict) != 0)
            {
                Error(106, 0, Pcre2DiagnosticKind.Options);
            }
        }
    }
    private void ValidateUtf()
    {
        if (!_profile.Utf)
        {
            return;
        }

        for (var i = 0; i < _input.Length; i++)
        {
            Charge();
            var c = _input[i];
            if (_profile.Width == Pcre2CodeUnitWidth.Sixteen)
            {
                if (c is >= 0xDC00 and <= 0xDFFF)
                {
                    UtfError(i, -26);
                }

                if (c is >= 0xD800 and <= 0xDBFF)
                { if (i + 1 == _input.Length) { UtfError(i, -24); } if (_input[i + 1] is < 0xDC00 or > 0xDFFF) { UtfError(i, -25); } i++; }
                continue;
            }
            if (c < 128)
            {
                continue;
            }

            if (c < 0xC0)
            {
                UtfError(i, -22);
            }

            if (c >= 0xFE)
            {
                UtfError(i, -23);
            }

            var extra = c < 0xE0 ? 1 : c < 0xF0 ? 2 : c < 0xF8 ? 3 : c < 0xFC ? 4 : 5;
            var remaining = _input.Length - i - 1;
            if (remaining < extra)
            {
                UtfError(i, -2 - (extra - remaining));
            }

            for (var k = 1; k <= extra; k++)
            {
                Charge();
                if ((_input[i + k] & 0xC0) != 0x80)
                {
                    UtfError(i, -7 - k);
                }
            }
            var d = _input[i + 1];
            if (extra == 1 && (c & 0x3E) == 0)
            {
                UtfError(i, -17);
            }

            if (extra == 2 && c == 0xE0 && (d & 0x20) == 0)
            {
                UtfError(i, -18);
            }

            if (extra == 2 && c == 0xED && d >= 0xA0)
            {
                UtfError(i, -16);
            }

            if (extra == 3 && c == 0xF0 && (d & 0x30) == 0)
            {
                UtfError(i, -19);
            }

            if (extra == 3 && (c > 0xF4 || c == 0xF4 && d > 0x8F))
            {
                UtfError(i, -15);
            }

            if (extra == 4 && c == 0xF8 && (d & 0x38) == 0)
            {
                UtfError(i, -20);
            }

            if (extra == 5 && c == 0xFC && (d & 0x3C) == 0)
            {
                UtfError(i, -21);
            }

            if (extra >= 4)
            {
                UtfError(i, extra == 4 ? -13 : -14);
            }

            i += extra;
        }
    }
    private readonly void UtfError(int offset, int code)
    {
        var undefined = (_flags & Pcre2CompileFlags.NoUtfCheck) != 0;
        throw new SyntaxFailure(new(new(undefined ? RegexDiagnosticCode.UnsupportedFeature : RegexDiagnosticCode.InvalidPatternEncoding, _map[offset], 1, _unit),
            undefined ? Pcre2DiagnosticKind.Capability : Pcre2DiagnosticKind.PatternEncoding, (Pcre2ErrorCode)code,
            undefined ? "PC-002/PH-012" : null, offset));
    }
    private void Options(int start)
    {
        var savedFlags = _flags;
        var savedExtra = _extra;
        bool minus = false, hyphen = true;
        Pcre2CompileFlags set = 0, unset = 0;
        Pcre2ExtraFlags xset = 0, xunset = 0;
        if (Take('^'))
        {
            _flags &= ~(Pcre2CompileFlags.Caseless | Pcre2CompileFlags.Multiline | Pcre2CompileFlags.NoAutoCapture |
            Pcre2CompileFlags.Dotall | Pcre2CompileFlags.Extended | Pcre2CompileFlags.ExtendedMore);
            _extra &= ~Pcre2ExtraFlags.CaselessRestrict;
            hyphen = false;
        }
        while (Peek() is not (-1 or ')' or ':'))
        {
            Charge();
            var c = _input[_p++];
            if (c == '-')
            { if (!hyphen) { Error(94, _p - 1); } minus = true; hyphen = false; continue; }
            Pcre2CompileFlags flag = 0;
            Pcre2ExtraFlags extra = 0;
            switch (c)
            {
                case 'i':
                    flag = Pcre2CompileFlags.Caseless;
                    break;
                case 'm':
                    flag = Pcre2CompileFlags.Multiline;
                    break;
                case 'n':
                    flag = Pcre2CompileFlags.NoAutoCapture;
                    break;
                case 's':
                    flag = Pcre2CompileFlags.Dotall;
                    break;
                case 'U':
                    flag = Pcre2CompileFlags.Ungreedy;
                    break;
                case 'J':
                    flag = Pcre2CompileFlags.Dupnames;
                    _hasDuplicateNameOptionChange = true;
                    break;
                case 'r':
                    extra = Pcre2ExtraFlags.CaselessRestrict;
                    break;
                case 'x':
                    flag = Pcre2CompileFlags.Extended;
                    if (Take('x'))
                    {
                        flag |= Pcre2CompileFlags.ExtendedMore;
                    }

                    break;
                case 'a':
                    extra = Peek() switch
                    {
                        'D' => Pcre2ExtraFlags.AsciiBsd,
                        'S' => Pcre2ExtraFlags.AsciiBss,
                        'W' => Pcre2ExtraFlags.AsciiBsw,
                        'P' => Pcre2ExtraFlags.AsciiPosix | Pcre2ExtraFlags.AsciiDigit,
                        'T' => Pcre2ExtraFlags.AsciiDigit,
                        _ => Pcre2ExtraFlags.AsciiBsd | Pcre2ExtraFlags.AsciiBss | Pcre2ExtraFlags.AsciiBsw | Pcre2ExtraFlags.AsciiPosix | Pcre2ExtraFlags.AsciiDigit
                    };
                    if (Peek() is 'D' or 'S' or 'W' or 'P' or 'T')
                    {
                        _p++;
                    }

                    break;
                default:
                    Error(11, _p - 1);
                    break;
            }
            if (minus)
            { unset |= flag; xunset |= extra; }
            else
            { set |= flag; xset |= extra; }
        }
        if ((set & (Pcre2CompileFlags.Extended | Pcre2CompileFlags.ExtendedMore)) == Pcre2CompileFlags.Extended ||
            (unset & Pcre2CompileFlags.Extended) != 0)
        {
            unset |= Pcre2CompileFlags.ExtendedMore;
        }

        _flags = (_flags | set) & ~unset;
        _extra = (_extra | xset) & ~xunset;
        if (Take(')'))
        {
            return;
        }

        if (!Take(':'))
        {
            Error(14);
        }

        Push(Pcre2NodeKind.Sequence, start);
        _frames[_frames.Count - 1].SavedFlags = savedFlags;
        _frames[_frames.Count - 1].SavedExtra = savedExtra;
    }
}
