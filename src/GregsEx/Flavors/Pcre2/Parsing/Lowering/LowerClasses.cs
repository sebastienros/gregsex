#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal ref partial struct LowerReader
{
    private void NormalizeClasses()
    {
        var bytes = checked(128L + 4L * _syntax.Nodes.Length + 32L * _syntax.Classes.Length + 139264);
        LowerFailure.Reserve(ref _budget, bytes, _source);
        _nodeClass = new int[_syntax.Nodes.Length];
        _classMap = new int[checked(_syntax.Classes.Length * 8)];
        Array.Fill(_classMap, -1);
        _classBits = new byte[139264];
        foreach (var interval in _syntax.Intervals)
        {
            Add(_intervals, interval, 8);
        }

        foreach (var item in _syntax.Classes)
        {
            Add(_classes, item, 48);
        }

        for (var id = 0; id < _syntax.Nodes.Length; id++)
        {
            Charge();
            var node = _syntax.Nodes[id];
            if (node.Kind != Pcre2NodeKind.Class)
            {
                continue;
            }

            var context = (node.Flags & Pcre2CompileFlags.Caseless) != 0 ? 1 : 0;
            if ((node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) != 0)
            {
                context |= 2;
            }

            if ((node.ExtraFlags & Pcre2ExtraFlags.TurkishCasing) != 0)
            {
                context |= 4;
            }

            if (StandaloneTableEscape(id, out var mask, out var negative))
            {
                Array.Clear(_classBits);
                Charge(_classBits.Length);
                for (var cp = 0; cp < 256; cp++)
                {
                    Charge();
                    if ((_syntax.Profile.CharacterTables.Bytes[832 + cp] & mask) != 0)
                    {
                        SetBit(cp);
                    }
                }
                var cls = BitsClass();
                if (negative)
                {
                    cls = AddClass(new(Pcre2ClassKind.Complement, cls, -1, default, default, 0));
                }

                _nodeClass[id] = cls;
                continue;
            }
            if ((context & 1) == 0)
            { _nodeClass[id] = node.Operand; continue; }
            // Rebuild in bottom-up order once for each scoped casing context.
            // Interval operands are expanded forward BEFORE set complements.
            for (var c = 0; c <= node.Operand; c++)
            {
                Charge();
                var slot = c * 8 + context;
                if (_classMap[slot] >= 0)
                {
                    continue;
                }

                var item = _syntax.Classes[c];
                if (item.Left >= 0)
                {
                    item = item with { Left = _classMap[item.Left * 8 + context] };
                }

                if (item.Right >= 0)
                {
                    item = item with { Right = _classMap[item.Right * 8 + context] };
                }

                if (item.Kind == Pcre2ClassKind.Property && item.Property.Kind == Pcre2PropertyKind.ParticularCategory &&
                    item.Property.Id is 5 or 8 or 9)
                {
                    item = item with { Property = new(Pcre2PropertyKind.Special, 0) };
                }

                if (item.Kind == Pcre2ClassKind.Intervals)
                {
                    Array.Clear(_classBits);
                    Charge(_classBits.Length);
                    var data = _provider.Bind(_syntax.Profile.CharacterTables);
                    foreach (var range in _syntax.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count))
                    {
                        for (var cp = range.First; cp <= range.Last; cp++)
                        {
                            Charge();
                            SetBit(cp);
                            if ((node.ExtraFlags & (Pcre2ExtraFlags.TurkishCasing | Pcre2ExtraFlags.CaselessRestrict)) == Pcre2ExtraFlags.TurkishCasing && cp is 0x49 or 0x69 or 0x130 or 0x131)
                            { SetBit(cp switch { 0x49 => 0x131, 0x131 => 0x49, 0x69 => 0x130, _ => 0x69 }); continue; }
                            if (!_syntax.Profile.Utf && !_syntax.Profile.Ucp)
                            { if (cp <= 255) { SetBit(data.LowByteTables[256 + cp]); } continue; }
                            var record = data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]];
                            var other = cp + record.OtherCaseDelta;
                            if ((node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (other < 128))
                            {
                                SetBit(other);
                            }

                            foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
                            {
                                Charge();
                                if ((node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (equivalent < 128))
                                {
                                    SetBit(equivalent);
                                }
                            }
                        }
                    }

                    _classMap[slot] = BitsClass();
                }
                else if (item.Kind == Pcre2ClassKind.TableClass && item.TableClass is 3 or 4)
                {
                    var lower = AddClass(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 4));
                    var upper = AddClass(new(Pcre2ClassKind.TableClass, -1, -1, default, default, 3));
                    _classMap[slot] = AddClass(new(Pcre2ClassKind.Union, lower, upper, default, default, 0));
                }
                else
                {
                    _classMap[slot] = AddClass(item);
                }
            }
            _nodeClass[id] = _classMap[node.Operand * 8 + context];
        }
    }
    private bool StandaloneTableEscape(int id, out int mask, out bool negative)
    {
        mask = 0;
        negative = false;
        var node = _syntax.Nodes[id];
        var at = NativeAt(node.Source.Offset);
        if (at + 1 >= _syntax.NativePatternLength || _syntax.Units[at] != '\\')
        {
            return false;
        }

        var c = _syntax.Units[at + 1];
        negative = c is 'D' or 'S' or 'W';
        var lower = c is >= 'A' and <= 'Z' ? c + 32 : c;
        mask = lower switch { 'd' => 8, 's' => 1, 'w' => 16, _ => 0 };
        if (mask == 0)
        {
            return false;
        }

        var ascii = lower == 'd' ? Pcre2ExtraFlags.AsciiBsd : lower == 's' ? Pcre2ExtraFlags.AsciiBss : Pcre2ExtraFlags.AsciiBsw;
        return !_syntax.Profile.Ucp || (node.ExtraFlags & ascii) != 0;
    }
    private readonly void SetBit(int cp)
    {
        if ((uint)cp < 0x110000)
        {
            _classBits[cp / 8] |= (byte)(1 << (cp % 8));
        }
    }
    private int BitsClass()
    {
        int start = _intervals.Count, first = -1;
        // Scan bytes, avoiding a million charged iterations for sparse maps.
        for (var b = 0; b < _classBits.Length; b++)
        {
            Charge();
            var bits = _classBits[b];
            if (bits == 0 && first < 0)
            {
                continue;
            }

            for (var bit = 0; bit < 8; bit++)
            {
                var cp = b * 8 + bit;
                var present = (bits & (1 << bit)) != 0;
                if (present && first < 0)
                {
                    first = cp;
                }
                else if (!present && first >= 0)
                { AddInterval(new(first, cp - 1)); first = -1; }
            }
        }
        if (first >= 0)
        {
            AddInterval(new(first, 0x10FFFF));
        }

        return AddClass(new(Pcre2ClassKind.Intervals, -1, -1, new(start, _intervals.Count - start), default, 0));
    }
    private void AddInterval(Pcre2Interval interval)
    { if (_intervals.Count >= _budget.Limits.MaxClassRanges) { Limit(RegexDiagnosticCode.ClassLimitExceeded); } Add(_intervals, interval, 8); }
    private int AddClass(Pcre2ClassNode item)
    {
        if ((long)_classes.Count + _nodes.Count >= _budget.Limits.MaxInstructions)
        {
            Limit(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var id = _classes.Count;
        Add(_classes, item, 48);
        return id;
    }
}

internal ref partial struct LowerReader
{
    private bool IsNeverClass(int root)
    {
        var node = _classes[root];
        return node.Kind == Pcre2ClassKind.Complement && ContainsAnyProperty(node.Left);
    }
    private bool ContainsAnyProperty(int root)
    {
        // Ordinary source class union containing p{Any} emits ALLANY (or
        // FAIL when negated), before repeat and first-unit bookkeeping.
        var node = _classes[root];
        if (node.Kind == Pcre2ClassKind.Property)
        {
            return node.Property == new Pcre2PropertyKey(Pcre2PropertyKind.Special, 13);
        }

        if (node.Kind != Pcre2ClassKind.Union)
        {
            return false;
        }
        // A bounded explicit stack avoids pattern-sized CLR recursion.
        _possessStack.Add(new(root, false, false), ref _budget, _source);
        var any = false;
        while (_possessStack.Count > 0)
        {
            Charge();
            var id = _possessStack.Pop().Node;
            var part = _classes[id];
            if (part.Kind == Pcre2ClassKind.Property && part.Property == new Pcre2PropertyKey(Pcre2PropertyKind.Special, 13))
            {
                any = true;
            }
            else if (part.Kind == Pcre2ClassKind.Union)
            { _possessStack.Add(new(part.Left, false, false), ref _budget, _source); _possessStack.Add(new(part.Right, false, false), ref _budget, _source); }
        }
        return any;
    }
}
