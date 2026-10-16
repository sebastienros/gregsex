using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal ref partial struct SyntaxReader
{
    private void Group(int start)
    {
        if (Take('*'))
        { if (Peek() is -1 or ')') { Error(9, _p - 1); } AlphaOrVerb(start); return; }
        if (!Take('?'))
        { Capture(start, -1, (_flags & Pcre2CompileFlags.NoAutoCapture) == 0); return; }
        if (Take('['))
        { var cls = ReadClass(true); if (!Take(')')) { Error(115); } Emit(Node(Pcre2NodeKind.Class, start, _p, cls), start); return; }
        if (Take('#'))
        { while (Peek() is not (-1 or ')')) { Charge(); _p++; } if (!Take(')')) { Error(18); } return; }
        if (Take(':'))
        { Push(Pcre2NodeKind.Sequence, start); return; }
        if (Take('|'))
        { Push(Pcre2NodeKind.Sequence, start, reset: true); return; }
        if (Take('>'))
        { Push(Pcre2NodeKind.Atomic, start); return; }
        if (Take('='))
        { Assertion(start, Pcre2AssertionKind.PositiveAhead); return; }
        if (Take('!'))
        { Assertion(start, Pcre2AssertionKind.NegativeAhead); return; }
        if (Take('*'))
        { Assertion(start, Pcre2AssertionKind.NonAtomicAhead); return; }
        if (Take('<'))
        {
            if (Take('='))
            { Assertion(start, Pcre2AssertionKind.PositiveBehind); return; }
            if (Take('!'))
            { Assertion(start, Pcre2AssertionKind.NegativeBehind); return; }
            if (Take('*'))
            { Assertion(start, Pcre2AssertionKind.NonAtomicBehind); return; }
            Capture(start, Name('>'), true);
            return;
        }
        if (Take('\''))
        { Capture(start, Name('\''), true); return; }
        if (Take('P'))
        {
            if (Take('<'))
            { Capture(start, Name('>'), true); return; }
            if (Take('>'))
            { ReferenceAtom(start, Name(')'), true); return; }
            if (Take('='))
            { ReferenceAtom(start, Name(')'), false); return; }
            Error(41);
        }
        if (Take('&'))
        { ReferenceAtom(start, Name(')'), true); return; }
        if (Take('R'))
        { if (!Take(')')) { Error(58); } Emit(Node(Pcre2NodeKind.Subroutine, start, _p, Reference(Pcre2ReferenceKind.WholePattern, 0, -1, start)), start); return; }
        if (Peek() == '+' && !Digit(Peek(1)))
        {
            Error(29, _p);
        }

        if ((Peek() is '+' or '-') && Digit(Peek(1)) || Digit(Peek()))
        {
            var reference = NumericReference(true);
            if (!Take(')'))
            {
                Error(29);
            }

            Emit(Node(Pcre2NodeKind.Subroutine, start, _p, reference), start);
            return;
        }
        if (Take('C'))
        { Callout(start); return; }
        if (Take('('))
        { Condition(start); return; }
        Options(start);
    }
    private void Capture(int start, int name, bool capturing)
    {
        if (!capturing)
        { Push(Pcre2NodeKind.Sequence, start); return; }
        var number = _nextGroup++;
        if (number > 65535)
        {
            Error(97);
        }

        _groupCount = Math.Max(_groupCount, _nextGroup);
        if (_groupCount > _budget.Limits.MaxCaptureGroups)
        {
            Limit(RegexDiagnosticCode.CaptureLimitExceeded);
        }

        var parent = -1;
        for (var i = _frames.Count - 1; i >= 0; i--)
        { Charge(); if (_frames[i].Kind == Pcre2NodeKind.Capture) { parent = _frames[i].Definition; break; } }
        var definition = _groups.Count;
        Add(_groups, new(number, parent, -1), 16);
        Add(_definitionNames, name);
        if (name >= 0)
        {
            for (var i = 0; i < definition; i++)
            {
                Charge();
                if (_groups[i].Number == number && _definitionNames[i] >= 0 && _definitionNames[i] != name)
                {
                    Error(65);
                }

                if (_definitionNames[i] == name && _groups[i].Number != number && (_flags & Pcre2CompileFlags.Dupnames) == 0)
                {
                    Error(43);
                }
            }
        }
        Push(Pcre2NodeKind.Capture, start, definition: definition);
    }
    private void Assertion(int start, Pcre2AssertionKind kind)
    { var operand = _assertions.Count; Add(_assertions, new(kind, 0, -1), 16); Push(Pcre2NodeKind.Assertion, start, operand); }
    private int Name(int terminator, bool braced = false)
    {
        if (braced)
        {
            SkipSpace(ref _p);
        }

        var start = _p;
        Span<char> chars = stackalloc char[128];
        var count = 0;
        if (Peek() < 0)
        {
            Error(62);
        }

        var view = _data.Bind(_profile.CharacterTables);
        var first = true;
        while (_p < _input.Length)
        {
            Charge();
            int before = _p, cp = Read();
            bool valid;
            if (_profile.Utf)
            {
                var category = view.Records[view.Stage2[view.Stage1[cp / 128] * 128 + cp % 128]].ParticularCategory;
                if (first && category == 13)
                {
                    Error(44, before);
                }

                valid = category == 13 || category is >= 5 and <= 9 || cp == '_';
            }
            else
            {
                if (first && Digit(cp))
                {
                    Error(44, before);
                }

                valid = cp <= 255 && (view.LowByteTables[832 + cp] & 0x10) != 0;
            }
            if (!valid)
            { _p = before; break; }
            if (_p - start > 128)
            {
                Error(48, _p);
            }

            if (cp <= 65535)
            {
                chars[count++] = (char)cp;
            }
            else
            { chars[count++] = (char)(0xD800 + ((cp - 0x10000) >> 10)); chars[count++] = (char)(0xDC00 + ((cp - 0x10000) & 1023)); }
            first = false;
        }
        if (count == 0)
        {
            Error(62);
        }

        if (braced)
        {
            SkipSpace(ref _p);
        }

        if (!Take(terminator))
        {
            Error(42);
        }

        for (var i = 0; i < _names.Count; i++)
        {
            Charge(1 + count);
            if (chars[..count].SequenceEqual(_names[i].Name.AsSpan()))
            {
                return i;
            }
        }
        var result = _names.Count;
        var bytes = checked(32L + 2L * count);
        Plan(bytes);
        SyntaxFailure.Reserve(ref _budget, bytes, Range(start, _p));
        Add(_names, new(chars[..count].ToString(), default), 48);
        Add(_namePositions, start);
        return result;
    }
    private int Reference(Pcre2ReferenceKind kind, int number, int name, int start, int candidate = 0)
    {
        var candidates = default(Pcre2Slice);
        if (candidate > 0)
        { candidates = new(_numbers.Count, 1); Add(_numbers, candidate, 4); }
        var result = _references.Count;
        Add(_references, new(kind, number, name, candidates, -1), 32);
        Add(_referenceNative, start);
        return result;
    }
    private int NumericReference(bool whole = false)
    {
        var sign = Take('-') ? -1 : Take('+') ? 1 : 0;
        var number = Number(65535, 61);
        if (number == 0)
        { if (sign != 0) { Error(26); } if (!whole) { Error(15); } return Reference(Pcre2ReferenceKind.WholePattern, 0, -1, _p); }
        var absolute = sign < 0 ? _nextGroup - number : sign > 0 ? _nextGroup - 1 + number : number;
        if (absolute > 65535)
        {
            Error(61);
        }

        if (absolute <= 0)
        {
            Error(15);
        }
        // Native read_number leaves its pointer immediately after the digits.
        // Parenthesized subroutines retain this closing-delimiter position;
        // callers for other forms replace it with their source-specific offset.
        return Reference(sign == 0 ? Pcre2ReferenceKind.Absolute : Pcre2ReferenceKind.Relative, sign == 0 ? number : sign * number, -1, _p,
            sign != 0 ? absolute : 0);
    }
    private void ReferenceAtom(int start, int name, bool subroutine)
    { Emit(Node(subroutine ? Pcre2NodeKind.Subroutine : Pcre2NodeKind.Backreference, start, _p, Reference(Pcre2ReferenceKind.Named, 0, name, start)), start); }
    private void FinishNames()
    {
        for (var i = 0; i < _names.Count; i++)
        {
            int begin = _numbers.Count, previous = 0;
            // Capture numbering is lexical except branch reset. Emit every alias
            // number once in increasing order without altering definition order.
            while (true)
            {
                var next = int.MaxValue;
                for (var j = 0; j < _groups.Count; j++)
                {
                    Charge();
                    if (_definitionNames[j] == i && _groups[j].Number > previous)
                    {
                        next = Math.Min(next, _groups[j].Number);
                    }
                }
                if (next == int.MaxValue)
                {
                    break;
                }

                Add(_numbers, next, 4);
                previous = next;
            }
            _names[i] = _names[i] with { Numbers = new(begin, _numbers.Count - begin) };
        }
        for (var i = 0; i < _conditions.Count; i++)
        {
            var ambiguous = _ambiguousConditions[i];
            if (ambiguous < 0 || _names[ambiguous].Numbers.Count > 0 && _names[ambiguous].Name != "DEFINE")
            {
                continue;
            }

            var word = _names[ambiguous].Name;
            if (word == "DEFINE")
            {
                for (var node = 0; node < _nodes.Count; node++)
                {
                    Charge();
                    if (_nodes[node].Kind != Pcre2NodeKind.Conditional || _nodes[node].Operand != i)
                    {
                        continue;
                    }

                    if (_nodes[node].Children.Count != 1)
                    {
                        Error(54, _namePositions[ambiguous]);
                    }

                    _nodes[node] = _nodes[node] with { Kind = Pcre2NodeKind.Define, Operand = 0 };
                }
            }
            else if (word.Length > 1)
            {
                var number = 0;
                for (var digit = 1; digit < word.Length; digit++)
                { Charge(); var d = word[digit] - '0'; if (number > (65535 - d) / 10) { Error(61, _namePositions[ambiguous] + digit); } number = number * 10 + d; }
                var condition = _conditions[i];
                _references[condition.ReferenceIndex] = new(number == 0 ? Pcre2ReferenceKind.WholePattern : Pcre2ReferenceKind.Absolute, number, -1, default, -1);
                _referenceNative[condition.ReferenceIndex] = _namePositions[ambiguous];
                _conditions[i] = condition with { Kind = Pcre2ConditionKind.Recursion };
            }
            else
            {
                _conditions[i] = new(Pcre2ConditionKind.AnyRecursion, -1, -1, 0, 0, false);
            }
        }
        for (var i = 0; i < _references.Count; i++)
        {
            Charge();
            var r = _references[i];
            if (r.Kind == Pcre2ReferenceKind.Absolute && r.Number >= _groupCount)
            {
                Error(15, _referenceNative[i]);
            }

            if (r.Kind == Pcre2ReferenceKind.Relative && _numbers[r.Candidates.Offset] >= _groupCount)
            {
                Error(15, _referenceNative[i]);
            }
        }
        CompactMetadata();
        // Names are native-width lexical entries, including imported high bytes.
        // Reindex references after an in-place insertion sort; no auxiliary map.
        for (var i = 1; i < _names.Count; i++)
        {
            var j = i;
            while (j > 0 && CompareNames(_names[j - 1].Name, _names[j].Name) > 0)
            {
                (_names[j - 1], _names[j]) = (_names[j], _names[j - 1]);
                for (var r = 0; r < _references.Count; r++)
                {
                    Charge();
                    var reference = _references[r];
                    if (reference.NameIndex == j)
                    {
                        _references[r] = reference with { NameIndex = j - 1 };
                    }
                    else if (reference.NameIndex == j - 1)
                    {
                        _references[r] = reference with { NameIndex = j };
                    }
                }
                j--;
            }
        }

    }
    private int CompareNames(string a, string b)
    {
        Charge(a.Length + b.Length);
        // UTF8 scalar order differs from UTF16 ordinal order for supplementary
        // versus BMP names. UTF8 preserves scalar order; raw8 is ordinal.
        if (_profile.Width == Pcre2CodeUnitWidth.Sixteen || !_profile.Utf)
        {
            return a.AsSpan().SequenceCompareTo(b.AsSpan());
        }

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            int ac = a[x++], bc = b[y++];
            if (ac is >= 0xD800 and <= 0xDBFF)
            {
                ac = 0x10000 + ((ac - 0xD800) << 10) + a[x++] - 0xDC00;
            }

            if (bc is >= 0xD800 and <= 0xDBFF)
            {
                bc = 0x10000 + ((bc - 0xD800) << 10) + b[y++] - 0xDC00;
            }

            if (ac != bc)
            {
                return ac.CompareTo(bc);
            }
        }
        return (a.Length - x).CompareTo(b.Length - y);
    }
    private void Condition(int start)
    {
        var operand = _conditions.Count;
        Add(_conditions, new(Pcre2ConditionKind.Version, -1, -1, 0, 0, false), 32);
        Add(_ambiguousConditions, -1);
        if (Peek() is '?' or '*')
        {
            Push(Pcre2NodeKind.Conditional, start, operand, conditionHead: true);
            var headStart = _p - 1;
            if (Take('?'))
            {
                if (Take('='))
                {
                    Assertion(headStart, Pcre2AssertionKind.PositiveAhead);
                }
                else if (Take('!'))
                {
                    Assertion(headStart, Pcre2AssertionKind.NegativeAhead);
                }
                else if (Take('<'))
                {
                    if (Take('='))
                    {
                        Assertion(headStart, Pcre2AssertionKind.PositiveBehind);
                    }
                    else if (Take('!'))
                    {
                        Assertion(headStart, Pcre2AssertionKind.NegativeBehind);
                    }
                    else
                    {
                        Error(28, headStart);
                    }
                }
                else if (Take('C'))
                { Callout(headStart); }
                else
                {
                    Error(28, headStart);
                }
            }
            else
            { _p++; AlphaOrVerb(headStart); }
            return;
        }
        Pcre2Condition condition;
        if (Peek() is '+' or '-' || Digit(Peek()))
        {
            var reference = NumericReference();
            // META_COND_NUMBER stores read_number's final pointer minus two.
            _referenceNative[reference] = _p - 2;
            condition = new(Pcre2ConditionKind.CaptureSet, reference, -1, 0, 0, false);
        }
        else if (_p + 7 < _input.Length && Word(_p, 7, "VERSION") && _input[_p + 7] != ')')
        {
            _p += 7;
            var atLeast = Take('>');
            if (!Take('='))
            {
                Error(79);
            }

            int major = Number(1000, 79), minor = 0;
            if (Take('.'))
            {
                if (!Digit(Peek()))
                {
                    Error(79);
                }

                minor = (_input[_p++] - '0') * 10;
                if (Digit(Peek()))
                {
                    minor += _input[_p++] - '0';
                }
            }
            condition = new(Pcre2ConditionKind.Version, -1, -1, major, minor, atLeast);
        }
        else
        {
            var rname = Peek() == 'R' && Peek(1) == '&';
            if (rname)
            {
                _p += 2;
            }

            var quoted = Peek() is '<' or '\'';
            var name = quoted ? Name(Take('<') ? '>' : Read()) : Name(')');
            if (!quoted)
            {
                _p--;
            }

            var word = _names[name].Name;
            var ambiguous = !rname && !quoted && (word == "DEFINE" || word[0] == 'R' && AllDigits(word.AsSpan(1)));
            if (ambiguous)
            {
                _ambiguousConditions[operand] = name;
            }

            var reference = Reference(Pcre2ReferenceKind.Named, 0, name, start);
            condition = new(rname ? Pcre2ConditionKind.Recursion : Pcre2ConditionKind.CaptureSet, reference, -1, 0, 0, false);
        }
        if (!Take(')'))
        {
            Error(condition.Kind == Pcre2ConditionKind.Version ? 79 : 24);
        }

        _conditions[operand] = condition;
        Push(Pcre2NodeKind.Conditional, start, operand);
    }
    private static bool AllDigits(ReadOnlySpan<char> word) { foreach (var c in word) { if (c is < '0' or > '9') { return false; } } return true; }
    private void Callout(int start)
    {
        if ((_extra & Pcre2ExtraFlags.NeverCallout) != 0)
        {
            Error(103, _p - 1);
        }

        int number = 0, offset = 0;
        var isString = false;
        Pcre2Slice text = default;
        if (Peek() is not (-1 or ')') && !Digit(Peek()))
        {
            var open = Peek();
            var close = open == '{' ? '}' : open;
            if (open is not ('`' or '\'' or '"' or '^' or '%' or '#' or '$' or '{'))
            {
                Error(82);
            }

            offset = _p++;
            isString = true;
            var begin = _units.Count;
            while (true)
            {
                Charge();
                if (Peek() < 0)
                {
                    Error(81, offset);
                }

                var c = _input[_p++];
                if (c == close)
                { if (Take(close)) { Add(_units, c, 6); continue; } break; }
                Add(_units, c, 6);
            }
            text = new(begin, _units.Count - begin);
        }
        else if (Digit(Peek()))
        {
            number = Number(255, 38);
        }

        if (!Take(')'))
        {
            Error(39);
        }

        if (_previousCallout >= 0 && _previousCallout == _callouts.Count - 1 && _callouts[_previousCallout].Automatic && _callouts[_previousCallout].PatternPosition == start)
        { _callouts.Truncate(_callouts.Count - 1); }
        if (_previousCallout >= 0 && _previousCallout < _callouts.Count &&
            _callouts[_previousCallout].NextItemLength == 0 && start > _callouts[_previousCallout].PatternPosition)
        {
            _callouts[_previousCallout] = _callouts[_previousCallout] with { NextItemLength = start - _callouts[_previousCallout].PatternPosition };
        }

        var site = _callouts.Count;
        Add(_callouts, new(number, false, isString, text, _p, 0, isString ? offset + 1 : offset), 40);
        Emit(Node(Pcre2NodeKind.Callout, start, _p, site), start);
        _previousCallout = site;
        _skipCallout = true;
        _itemSinceCallout = true;
    }
    private void AlphaOrVerb(int start)
    {
        var nameStart = _p;
        while (Peek() is >= 0 and <= 255 && (_profile.CharacterTables.Bytes[832 + Peek()] & 0x10) != 0)
        { Charge(); _p++; }
        var length = _p - nameStart;
        var lower = nameStart < _input.Length && _input[nameStart] <= 255 && (_profile.CharacterTables.Bytes[832 + _input[nameStart]] & 4) != 0;
        var conditional = _frames[_frames.Count - 1].ConditionHead;
        if (conditional && !lower)
        {
            Error(28, start);
        }

        if (Peek() < 0 || Peek() is not (':' or ')'))
        {
            Error(lower ? 95 : 60);
        }

        var colon = Take(':');
        Pcre2AssertionKind? assertion = Word(nameStart, length, "pla") || Word(nameStart, length, "positive_lookahead") ? Pcre2AssertionKind.PositiveAhead :
            Word(nameStart, length, "nla") || Word(nameStart, length, "negative_lookahead") ? Pcre2AssertionKind.NegativeAhead :
            Word(nameStart, length, "plb") || Word(nameStart, length, "positive_lookbehind") ? Pcre2AssertionKind.PositiveBehind :
            Word(nameStart, length, "nlb") || Word(nameStart, length, "negative_lookbehind") ? Pcre2AssertionKind.NegativeBehind :
            Word(nameStart, length, "napla") || Word(nameStart, length, "non_atomic_positive_lookahead") ? Pcre2AssertionKind.NonAtomicAhead :
            Word(nameStart, length, "naplb") || Word(nameStart, length, "non_atomic_positive_lookbehind") ? Pcre2AssertionKind.NonAtomicBehind : null;
        if (assertion.HasValue)
        { if (!colon) { Error(95); } if (conditional && assertion.Value is Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind) { Error(28, _p - 1); } Assertion(start, assertion.Value); return; }
        if (Word(nameStart, length, "atomic"))
        { if (!colon) { Error(95); } if (conditional) { Error(28, _p - 1); } Push(Pcre2NodeKind.Atomic, start); return; }
        if (Word(nameStart, length, "sr") || Word(nameStart, length, "script_run") || Word(nameStart, length, "asr") || Word(nameStart, length, "atomic_script_run"))
        { if (!colon) { Error(95); } if (conditional) { Error(28, _p - 1); } Push(Pcre2NodeKind.ScriptRun, start, Word(nameStart, length, "asr") || Word(nameStart, length, "atomic_script_run") ? 1 : 0); return; }
        if (Word(nameStart, length, "scs") || Word(nameStart, length, "scan_substring"))
        {
            if (conditional)
            {
                Error(28, _p - 1);
            }

            if (Peek() < 0)
            {
                Error(14);
            }

            if (!colon || !Take('('))
            {
                Error(15);
            }

            if (Peek() < 0)
            {
                Error(14);
            }

            var begin = _references.Count;
            while (true)
            {
                SkipSpace(ref _p);
                if (Peek() is '<' or '\'')
                { var name = Name(Take('<') ? '>' : Read()); Reference(Pcre2ReferenceKind.Named, 0, name, start); }
                else
                {
                    int numberStart = _p, reference = NumericReference();
                    _referenceNative[reference] = numberStart;
                }
                SkipSpace(ref _p);
                if (Peek() < 0)
                {
                    Error(14);
                }

                if (Take(')'))
                {
                    break;
                }

                if (!Take(','))
                {
                    Error(24);
                }
            }
            var scan = _scans.Count;
            Add(_scans, new(begin, _references.Count - begin), 8);
            Push(Pcre2NodeKind.ScanSubstring, start, scan);
            return;
        }
        Pcre2VerbKind? verb = length == 0 || Word(nameStart, length, "MARK") ? Pcre2VerbKind.Mark :
            Word(nameStart, length, "F") || Word(nameStart, length, "FAIL") ? Pcre2VerbKind.Fail : Word(nameStart, length, "ACCEPT") ? Pcre2VerbKind.Accept :
            Word(nameStart, length, "COMMIT") ? Pcre2VerbKind.Commit : Word(nameStart, length, "PRUNE") ? Pcre2VerbKind.Prune :
            Word(nameStart, length, "SKIP") ? Pcre2VerbKind.Skip : Word(nameStart, length, "THEN") ? Pcre2VerbKind.Then : null;
        if (!verb.HasValue)
        {
            Error(lower ? 95 : 60, _p - (colon ? 1 : 0));
        }

        var mark = -1;
        if (colon)
        {
            int begin = _units.Count, payload = _p;
            var quoted = false;
            while (Peek() >= 0 && (Peek() != ')' || quoted))
            {
                Charge();
                if ((_flags & Pcre2CompileFlags.AltVerbnames) != 0)
                {
                    if (!quoted && SkipExtended())
                    {
                        continue;
                    }

                    if (Peek() == '\\' && Peek(1) == 'E')
                    { _p += 2; quoted = false; continue; }
                    if (!quoted && Peek() == '\\' && Peek(1) == 'Q')
                    { _p += 2; quoted = true; continue; }
                    if (!quoted && Peek() == '\\')
                    {
                        _p++;
                        int cp;
                        int cls;
                        try
                        { cp = EscapeValue(true, out cls); }
                        catch (SyntaxFailure e) when (e.Diagnostic.SourceError == Pcre2ErrorCode.EscapeInvalidInClass) { Error(40); throw; }

                        if (cls >= 0)
                        {
                            Error(40);
                        }

                        AppendEncoded(cp);
                        continue;
                    }
                }
                Add(_units, _input[_p++], 6);
            }
            if (_p - payload > (_profile.Width == Pcre2CodeUnitWidth.Eight ? 255 : 65535))
            {
                Error(76);
            }

            if (_units.Count == begin && verb == Pcre2VerbKind.Mark)
            {
                Error(66);
            }

            if (_units.Count > begin)
            { mark = _marks.Count; Add(_marks, new(begin, _units.Count - begin), 8); }
        }
        else if (verb == Pcre2VerbKind.Mark)
        {
            Error(66);
        }

        if (!Take(')'))
        {
            Error(60);
        }

        var control = _controls.Count;
        Add(_controls, new(verb!.Value, mark), 8);
        Emit(Node(Pcre2NodeKind.Verb, start, _p, control), start);
    }
    private void AppendEncoded(int cp)
    {
        if (_profile.Width == Pcre2CodeUnitWidth.Sixteen)
        { if (cp <= 65535) { Add(_units, cp, 6); } else { Add(_units, 0xD800 + ((cp - 0x10000) >> 10), 6); Add(_units, 0xDC00 + ((cp - 0x10000) & 1023), 6); } }
        else if (!_profile.Utf || cp < 128)
        {
            Add(_units, cp, 5);
        }
        else
        {
            var n = cp < 2048 ? 2 : cp < 65536 ? 3 : 4;
            Add(_units, (n == 2 ? 0xC0 : n == 3 ? 0xE0 : 0xF0) | (cp >> (6 * (n - 1))), 5);
            for (var i = n - 2; i >= 0; i--)
            {
                Add(_units, 0x80 | ((cp >> (6 * i)) & 63), 5);
            }
        }
    }
}
