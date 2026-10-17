#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal struct SyntaxClassFrame
{
    internal int Mode, End, Left, Term, Operator, LastOperator, Start, HighLeft, RangeState, LiteralClass, HyphenOffset;
    // Saturated lexical token shape is independent of interval/algebra normalization.
    internal int MetadataTokens, MetadataFirst, MetadataSecond;
    internal bool Negate, First, MetadataCrOrLf, MetadataComplex;
}
internal ref partial struct SyntaxReader
{
    private int ClassNode(Pcre2ClassNode node)
    {
        if ((long)_classes.Count + _nodes.Count >= _budget.Limits.MaxInstructions)
        {
            Limit(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var result = _classes.Count;
        Add(_classes, node, 48);
        return result;
    }
    private int Intervals(scoped ReadOnlySpan<Pcre2Interval> intervals)
    {
        if ((long)_intervals.Count + intervals.Length > _budget.Limits.MaxClassRanges)
        {
            Limit(RegexDiagnosticCode.ClassLimitExceeded);
        }

        var start = _intervals.Count;
        foreach (var interval in intervals)
        {
            Add(_intervals, interval, 8);
        }

        return ClassNode(new(Pcre2ClassKind.Intervals, -1, -1, new(start, intervals.Length), default, 0));
    }
    private int Interval(int first, int last)
    { Span<Pcre2Interval> one = stackalloc Pcre2Interval[1]; one[0] = new(first, last); return Intervals(one); }
    private int Complement(int child) => ClassNode(new(Pcre2ClassKind.Complement, child, -1, default, default, 0));
    private int Binary(Pcre2ClassKind kind, int left, int right) => ClassNode(new(kind, left, right, default, default, 0));
    private void ClassAtom(int start)
    {
        if (_input.Length - _p >= 6 && (Word(_p, 6, "[:<:]]") || Word(_p, 6, "[:>:]]")))
        {
            var ahead = _input[_p + 2] == '<';
            _p += 6;
            Emit(Node(Pcre2NodeKind.WordBoundary, start, _p), start);
            var cls = (_flags & Pcre2CompileFlags.Ucp) != 0 ? ClassNode(new(Pcre2ClassKind.Property, -1, -1, default, new(Pcre2PropertyKind.Special, 8), 0)) :
                ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 5));
            int item = Node(Pcre2NodeKind.Class, start, _p, cls), assertion = _assertions.Count;
            Add(_assertions, new(ahead ? Pcre2AssertionKind.PositiveAhead : Pcre2AssertionKind.PositiveBehind, 0, -1), 16);
            Append(Single(Pcre2NodeKind.Assertion, start, _p, item, assertion));
            return;
        }
        if (Peek() is ':' or '.' or '=' && RecognizePosix(_p - 1))
        {
            Error(Peek() == ':' ? 12 : 13, _p - 1);
        }

        var result = ReadClass(false);
        Emit(Node(Pcre2NodeKind.Class, start, _p, result), start);
    }
    private bool RecognizePosix(int at)
    {
        var delimiter = _input[at + 1];
        for (var i = at + 2; i + 1 < _input.Length; i++)
        {
            Charge();
            if (_input[i] == '\\' && _input[i + 1] is ']' or '\\')
            { i++; continue; }
            if (_input[i] == '[' && _input[i + 1] == delimiter || _input[i] == ']')
            {
                return false;
            }

            if (_input[i] == delimiter && _input[i + 1] == ']')
            {
                return true;
            }
        }
        return false;
    }
    private bool StartsPosix() => Peek() == '[' && Peek(1) is ':' or '.' or '=' && RecognizePosix(_p);
    private int AfterHyphen(int end, int mode)
    {
        var at = _p + 1;
        if ((_flags & Pcre2CompileFlags.ExtendedMore) != 0)
        {
            SkipSpace(ref at);
        }

        if (at >= _input.Length || _input[at] == end)
        {
            return -1;
        }

        if (mode == 1)
        {
            if (_input[at] == '[')
            {
                return -1;
            }

            if (at + 1 < _input.Length && _input[at] == _input[at + 1] && _input[at] is '&' or '|' or '-' or '~')
            {
                return -1;
            }
        }
        return at;
    }
    private void PushClass(int mode, int end, int start)
    {
        if (_classFrames.Count + _frames.Count - 1 >= _budget.Limits.MaxNestingDepth)
        {
            Limit(RegexDiagnosticCode.NestingLimitExceeded);
        }

        var nativeDepth = 0;
        for (var i = 0; i < _classFrames.Count; i++)
        {
            Charge();
            if (_classFrames[i].End != 0)
            {
                nativeDepth++;
            }
        }
        if (end != 0 && nativeDepth >= 15)
        {
            Error(107);
        }

        var negate = false;
        if (mode != 2)
        {
            while (true)
            {
                if (!negate && Take('^'))
                { negate = true; continue; }
                if (Peek() == '\\' && Peek(1) == 'E')
                { _p += 2; continue; }
                if (Peek() == '\\' && Peek(1) == 'Q' && Peek(2) == '\\' && Peek(3) == 'E')
                { _p += 4; continue; }
                if (((_flags & Pcre2CompileFlags.ExtendedMore) != 0 || mode == 3) && Peek() is 9 or 32)
                { _p++; continue; }
                break;
            }
        }
        Add(_classFrames, new SyntaxClassFrame { Mode = mode, End = end, Left = -1, Term = -1, Negate = negate, First = true, Start = start, HighLeft = -1 });
    }
    // Explicit source state machine: a pending hyphen survives insignificant
    // whitespace and quoting delimiters. It is flushed by a close, nested class,
    // or algebra operator, and cannot join a multi-character set to a literal.
    private int ReadClass(bool perl)
    {
        int initial = _classFrames.Count, maximum = 1;
        PushClass(perl ? 2 : (_flags & Pcre2CompileFlags.AltExtendedClass) != 0 ? 1 : 0, ']', _p - 1);
        while (_classFrames.Count > initial)
        {
            Charge();
            ref var f = ref _classFrames[_classFrames.Count - 1];
            maximum = Math.Max(maximum, _classFrames.Count - initial);
            if (Peek() < 0)
            {
                if (f.End == ')' && _p == f.Start + 1)
                {
                    Error(14);
                }

                if (f.Mode == 1 && _classFrames.Count == initial + 1 && maximum == 2)
                {
                    Error(112);
                }

                Error(6);
            }
            if (_quote)
            {
                if (Peek() == '\\' && Peek(1) == 'E')
                { _p += 2; _quote = false; continue; }
                var quoted = Read();
                if (f.Mode == 2)
                {
                    Error(116);
                }

                ClassLiteral(quoted, false);
                continue;
            }
            // The source's implied /xx skips only SPACE and HT, not all whitespace.
            if (f.Mode == 2 && (Peek() is 9 or 32))
            { _p++; continue; }
            if (f.Mode != 2 && ((_flags & Pcre2CompileFlags.ExtendedMore) != 0 || f.Mode == 3) && Peek() is 9 or 32)
            { _p++; continue; }
            if (Peek() == f.End && (!f.First || f.Mode == 2 || f.Mode < 2 && (_flags & Pcre2CompileFlags.AllowEmptyClass) != 0))
            {
                FlushHyphen();
                _p++;
                var finished = _classFrames.Pop();
                var result = FinishClass(finished);
                if (finished.Negate)
                {
                    result = Complement(result);
                }

                if (_classFrames.Count == initial)
                {
                    _hasExplicitCrOrLf |= ClassCompileMetadata(finished);
                    return result;
                }
                // Extended operands always run compile_class_not_nested, including
                // negative singleton leaves and operands eliminated by set algebra.
                ref var parent = ref _classFrames[_classFrames.Count - 1];
                parent.MetadataComplex = true;
                parent.MetadataCrOrLf |= finished.MetadataCrOrLf;
                ClassSet(result);
                continue;
            }
            if (f.Mode == 2)
            {
                if (Peek() == ']' && f.End == ')')
                {
                    Error(14);
                }

                if (Peek() == ')' && f.End == ']')
                {
                    Error(22, _p + 1);
                }

                if (Peek() == '!')
                {
                    _p++;
                    if (f.Term >= 0)
                    {
                        Error(113);
                    }

                    f.MetadataComplex = true;
                    var negate = true;
                    while (Take('!'))
                    { Charge(); negate = !negate; }
                    PushClass(2, 0, _p - 1);
                    _classFrames[_classFrames.Count - 1].Negate = negate;
                    continue;
                }
                if (Take('('))
                { if (f.Term >= 0) { Error(113); } f.MetadataComplex = true; PushClass(2, ')', _p - 1); continue; }
                if (Peek() is '&' or '|' or '+' or '-' or '^')
                { var op = _input[_p++]; ClassOperator(op, true); continue; }
                if (StartsPosix())
                { ClassSet(PosixClass()); continue; }
                if (Take('['))
                { if (f.Term >= 0) { Error(113); } PushClass(3, ']', _p - 1); continue; }
                if (Peek() != '\\')
                { Read(); Error(116); }
            }
            else if (f.Mode == 1)
            {
                if (Peek() == Peek(1) && Peek() is '&' or '|' or '-' or '~')
                {
                    var op = _input[_p];
                    _p += 2;
                    if (Peek() == op)
                    { while (Take(op)) { Charge(); } Error(108); }
                    FlushHyphen();
                    ClassOperator(op, false);
                    f.RangeState = 0;
                    continue;
                }
                if (Peek() == '[' && !StartsPosix())
                { FlushHyphen(); f.MetadataComplex = true; _p++; PushClass(1, ']', _p - 1); continue; }
            }
            if (StartsPosix())
            {
                if (f.RangeState == 3)
                {
                    var delimiter = Peek(1);
                    _p += 2;
                    while (!(Peek() == delimiter && Peek(1) == ']'))
                    { Charge(); _p++; }
                    _p += 2;
                    Error(50);
                }
                if (f.RangeState == 4)
                {
                    Error(50, f.HyphenOffset);
                }

                ClassSet(PosixClass());
                continue;
            }
            int value, cls = -1;
            var escaped = Take('\\');
            if (escaped)
            {
                value = EscapeValue(true, out cls);
            }
            else
            {
                value = Read();
            }

            if (value == -1 && cls < 0)
            {
                continue;
            }

            if (cls >= 0)
            {
                ClassSet(cls);
            }
            else
            {
                ClassLiteral(value, !escaped);
            }
        }
        throw new InvalidOperationException();
    }
    private void FlushHyphen()
    {
        ref var f = ref _classFrames[_classFrames.Count - 1];
        if (f.RangeState is 3 or 4)
        { f.RangeState = 0; ClassLiteral('-', false); }
    }
    private void ClassSet(int cls)
    {
        ref var f = ref _classFrames[_classFrames.Count - 1];
        if (f.RangeState == 3)
        {
            Error(50);
        }

        if (f.RangeState == 4)
        {
            Error(50, f.HyphenOffset);
        }

        f.MetadataTokens = 3;
        ClassOperand(cls);
        _classFrames[_classFrames.Count - 1].RangeState = 2;
    }
    private void ClassLiteral(int cp, bool unescaped)
    {
        ref var f = ref _classFrames[_classFrames.Count - 1];
        f.First = false;
        if (unescaped && cp == '-' && f.RangeState is 1 or 2)
        { f.HyphenOffset = _p; f.RangeState = f.RangeState == 1 ? 3 : 4; return; }
        f.MetadataCrOrLf |= cp is 10 or 13;
        if (f.RangeState == 3)
        {
            var interval = _classes[f.LiteralClass].Intervals.Offset;
            if (cp < _intervals[interval].First)
            {
                Error(8, _p - 1);
            }
            // Equal endpoints collapse to one literal in the native parsed stream.
            if (cp != _intervals[interval].First)
            {
                f.MetadataTokens = 3;
            }

            _intervals[interval] = _intervals[interval] with { Last = cp };
            f.RangeState = 0;
            return;
        }
        if (f.RangeState == 4)
        {
            Error(50, f.HyphenOffset);
        }

        if (f.MetadataTokens == 0)
        {
            f.MetadataFirst = cp;
        }
        else if (f.MetadataTokens == 1)
        {
            f.MetadataSecond = cp;
        }

        f.MetadataTokens = Math.Min(3, f.MetadataTokens + 1);
        var cls = Interval(cp, cp);
        ClassOperand(cls);
        ref var parent = ref _classFrames[_classFrames.Count - 1];
        parent.RangeState = 1;
        parent.LiteralClass = cls;
    }
    private readonly bool ClassCompileMetadata(SyntaxClassFrame f)
    {
        if (!f.MetadataCrOrLf)
        {
            return false;
        }
        // compile.c: plain negative one-token classes emit OP_NOT and bypass
        // both the literal and compile_class_not_nested flag-producing sites.
        if (!f.MetadataComplex && f.Negate && f.MetadataTokens == 1)
        {
            return false;
        }
        // The positive two-token case-partner peephole emits only its FIRST
        // literal. Imported FCC tables need not be symmetric or involutive.
        if (!f.MetadataComplex && !f.Negate && f.MetadataTokens == 2)
        {
            int c = f.MetadataFirst, d = f.MetadataSecond;
            var data = _data.Bind(_profile.CharacterTables);
            var record = data.Records[data.Stage2[data.Stage1[c / 128] * 128 + c % 128]];
            var restricted = (_extra & Pcre2ExtraFlags.CaselessRestrict) != 0;
            var unicode = (_flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp)) != 0;
            var usableSet = record.CaseSetCount == 0 || restricted && c < 128 && d < 128;
            var turkish = (_extra & (Pcre2ExtraFlags.TurkishCasing | Pcre2ExtraFlags.CaselessRestrict)) == Pcre2ExtraFlags.TurkishCasing &&
                c is 'I' or 'i' or 0x130 or 0x131;
            var other = unicode && c > 127 ? c + record.OtherCaseDelta : c > 255 ? c : _profile.CharacterTables.Bytes[256 + c];
            if (usableSet && !turkish && c != other && d == other)
            {
                return c is 10 or 13;
            }
        }
        return true;
    }
    private void ClassOperand(int operand)
    {
        while (true)
        {
            ref var f = ref _classFrames[_classFrames.Count - 1];
            f.First = false;
            if (f.Term < 0)
            {
                f.Term = operand;
            }
            else if (f.Mode == 2)
            {
                Error(113);
            }
            else
            {
                f.Term = Binary(Pcre2ClassKind.Union, f.Term, operand);
            }

            if (f.End != 0)
            {
                return;
            }

            var unary = _classFrames.Pop();
            operand = unary.Negate ? Complement(FinishClass(unary)) : FinishClass(unary);
            ref var parent = ref _classFrames[_classFrames.Count - 1];
            parent.MetadataComplex = true;
            parent.MetadataCrOrLf |= unary.MetadataCrOrLf;
        }
    }
    private void ClassOperator(int op, bool perl)
    {
        ref var f = ref _classFrames[_classFrames.Count - 1];
        if (f.Term < 0)
        {
            Error(109);
        }

        f.MetadataComplex = true;
        if (perl && op == '&')
        { f.HighLeft = f.HighLeft < 0 ? f.Term : Binary(Pcre2ClassKind.Intersection, f.HighLeft, f.Term); f.Term = -1; return; }
        if (f.HighLeft >= 0)
        { f.Term = Binary(Pcre2ClassKind.Intersection, f.HighLeft, f.Term); f.HighLeft = -1; }
        if (!perl && f.LastOperator != 0 && f.LastOperator != op)
        {
            Error(111);
        }

        if (f.Left >= 0)
        {
            f.Left = Binary(OperatorKind(f.Operator), f.Left, f.Term);
        }
        else
        {
            f.Left = f.Term;
        }

        f.Term = -1;
        f.Operator = op;
        f.LastOperator = op;
    }
    private int FinishClass(SyntaxClassFrame f)
    {
        if (f.Term < 0)
        {
            if (f.Operator != 0 || f.HighLeft >= 0)
            {
                Error(110);
            }

            if (f.Mode == 2)
            {
                Error(114);
            }

            return Intervals(default);
        }
        if (f.HighLeft >= 0)
        {
            f.Term = Binary(Pcre2ClassKind.Intersection, f.HighLeft, f.Term);
        }

        return f.Left < 0 ? f.Term : Binary(OperatorKind(f.Operator), f.Left, f.Term);
    }
    private static Pcre2ClassKind OperatorKind(int op) => op switch
    { '&' => Pcre2ClassKind.Intersection, '-' => Pcre2ClassKind.Subtraction, '~' or '^' => Pcre2ClassKind.SymmetricDifference, _ => Pcre2ClassKind.Union };
    private int PosixClass()
    {
        _p++;
        var delimiter = _input[_p++];
        var negative = Take('^');
        var start = _p;
        while (Peek() >= 0 && !(Peek() == delimiter && Peek(1) == ']'))
        { Charge(); _p++; }
        if (Peek() < 0)
        {
            Error(6);
        }

        var length = _p - start;
        _p += 2;
        if (delimiter != ':')
        {
            Error(13);
        }

        var table = Word(start, length, "space") ? 0 : Word(start, length, "xdigit") ? 1 : Word(start, length, "digit") ? 2 :
            Word(start, length, "upper") ? 3 : Word(start, length, "lower") ? 4 : Word(start, length, "word") ? 5 :
            Word(start, length, "graph") ? 6 : Word(start, length, "print") ? 7 : Word(start, length, "punct") ? 8 :
            Word(start, length, "cntrl") ? 9 : Word(start, length, "alpha") ? 10 : Word(start, length, "alnum") ? 11 : Word(start, length, "ascii") ? 12 :
            Word(start, length, "blank") ? 13 : -1;
        if (table < 0)
        {
            Error(30);
        }

        int cls;
        var unicode = (_flags & Pcre2CompileFlags.Ucp) != 0 && (_extra & Pcre2ExtraFlags.AsciiPosix) == 0;
        if (unicode && table == 13)
        {
            cls = EscapeClass('h');
        }
        else if (unicode && table != 12)
        {
            Pcre2PropertyKey prop = table switch
            {
                0 => new(Pcre2PropertyKind.Special, 7),
                1 => new(Pcre2PropertyKind.Special, 17),
                2 => new(Pcre2PropertyKind.ParticularCategory, 13),
                3 => new(Pcre2PropertyKind.ParticularCategory, 9),
                4 => new(Pcre2PropertyKind.ParticularCategory, 5),
                5 => new(Pcre2PropertyKind.Special, 8),
                6 => new(Pcre2PropertyKind.Special, 14),
                7 => new(Pcre2PropertyKind.Special, 15),
                8 => new(Pcre2PropertyKind.Special, 16),
                9 => new(Pcre2PropertyKind.ParticularCategory, 0),
                10 => new(Pcre2PropertyKind.MajorCategory, 1),
                _ => new(Pcre2PropertyKind.Special, 5)
            };
            if (table is 1 or 2 && (_extra & Pcre2ExtraFlags.AsciiDigit) != 0)
            {
                cls = ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, table));
            }
            else
            {
                cls = ClassNode(new(Pcre2ClassKind.Property, -1, -1, default, prop, 0));
            }
        }
        else
        {
            if (table is 3 or 4 && (_flags & Pcre2CompileFlags.Caseless) != 0)
            {
                table = 10;
            }

            if (table == 12)
            {
                cls = Binary(Pcre2ClassKind.Union, ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 7)),
                ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 9)));
            }
            else if (table == 13)
            {
                cls = Binary(Pcre2ClassKind.Subtraction, ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 0)), Interval(10, 13));
            }
            else if (table is 10 or 11)
            {
                cls = ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 5));
                if (table == 10)
                {
                    cls = Binary(Pcre2ClassKind.Subtraction, cls, ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 2)));
                }

                cls = Binary(Pcre2ClassKind.Subtraction, cls, Interval('_', '_'));
            }
            else
            {
                cls = ClassNode(new(Pcre2ClassKind.TableClass, -1, -1, default, default, table));
            }
        }
        return negative ? Complement(cls) : cls;
    }
}
