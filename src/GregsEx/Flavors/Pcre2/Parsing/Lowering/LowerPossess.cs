#nullable enable
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal readonly record struct PossessCursor(int Node, bool EnteredGroup, bool ChargeCall);
internal ref partial struct LowerReader
{
    private void AutoPossess()
    {
        if (!_syntax.Profile.EffectiveOptimization.AutoPossess)
        {
            return;
        }

        LowerFailure.Reserve(ref _budget, checked(32L + 4L * _nodes.Count), _source);
        _executionParents = new int[_nodes.Count];
        Array.Fill(_executionParents, -1);
        for (var parent = 0; parent < _nodes.Count; parent++)
        {
            foreach (var child in ExecutionChildren(parent))
            {
                _executionParents[child] = parent;
            }
        }

        var hasCalls = false;
        foreach (var node in _nodes.Span)
        { Charge(); hasCalls |= node.Kind == Pcre2NodeKind.Subroutine; }
        for (var id = 0; id < _nodes.Span.Length; id++)
        {
            Charge();
            var node = _nodes.Span[id];
            if (node.Kind != Pcre2NodeKind.Repeat || node.Quantifier == Pcre2Quantifier.Possessive || node.Min == node.Max)
            {
                continue;
            }

            var atom = ExecutionChildren(id)[0];
            if (_nodes.Span[atom].Kind is not (Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot or
                Pcre2NodeKind.CodeUnit or Pcre2NodeKind.Newline or Pcre2NodeKind.Grapheme))
            {
                continue;
            }

            if (--_possessRemaining <= 0)
            {
                continue;
            }

            var greedy = node.Quantifier == Pcre2Quantifier.Greedy;
            var next = Following(id, atom, greedy, false, hasCalls);
            if (next == -1)
            {
                continue;
            }

            var allowed = next == -2;
            if (next >= 0)
            {
                allowed = true;
                _possessStack.Add(new(next, false, false), ref _budget, _source);
                while (_possessStack.Count != 0)
                {
                    Charge();
                    var task = _possessStack.Pop();
                    var at = task.Node;
                    var entered = task.EnteredGroup;
                    if (task.ChargeCall && --_possessRemaining <= 0)
                    { allowed = false; while (_possessStack.Count > 0) { _possessStack.Pop(); } break; }
                    var right = _nodes.Span[at];
                    var children = ExecutionChildren(at);
                    if (right.Kind is Pcre2NodeKind.Sequence or Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic)
                    { _possessStack.Add(new(children[0], entered || NativeGroup(at), false), ref _budget, _source); continue; }
                    if (right.Kind == Pcre2NodeKind.Alternative)
                    { for (var i = children.Length - 1; i >= 0; i--) { _possessStack.Add(new(children[i], true, i != children.Length - 1), ref _budget, _source); } continue; }
                    if (right.Kind is Pcre2NodeKind.Callout or Pcre2NodeKind.Empty)
                    {
                        var follow = Following(at, atom, greedy, entered, hasCalls);
                        if (follow >= 0)
                        {
                            _possessStack.Add(new(follow, entered, false), ref _budget, _source);
                        }
                        else if (follow == -1)
                        { allowed = false; while (_possessStack.Count > 0) { _possessStack.Pop(); } break; }
                        continue;
                    }
                    var optional = right.Kind == Pcre2NodeKind.Repeat && right.Min == 0;
                    var rightAtom = right.Kind == Pcre2NodeKind.Repeat ? children[0] : at;
                    var group = right.Kind == Pcre2NodeKind.Repeat && _nodes[rightAtom].Kind is
                        Pcre2NodeKind.Sequence or Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic;
                    if (group && right.Max != 0)
                    {
                        _possessStack.Add(new(rightAtom, entered, false), ref _budget, _source);
                    }
                    else if (!(right.Kind == Pcre2NodeKind.Repeat && right.Max == 0) && !Disjoint(atom, rightAtom))
                    {
                        allowed = false;
                    }

                    if (optional)
                    {
                        var follow = Following(at, atom, greedy, entered, hasCalls);
                        if (follow >= 0)
                        {
                            _possessStack.Add(new(follow, entered, group), ref _budget, _source);
                        }
                        else if (follow == -1)
                        {
                            allowed = false;
                        }
                    }
                    if (!allowed)
                    { while (_possessStack.Count > 0) { _possessStack.Pop(); } break; }
                }
            }
            if (allowed)
            {
                _nodes[id] = node with { Quantifier = Pcre2Quantifier.Possessive };
            }
        }
    }
    private bool NativeGroup(int id)
    {
        var node = _nodes[id];
        if (node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic)
        {
            return true;
        }

        var old = _origins[id];
        if (node.Kind != Pcre2NodeKind.Sequence || old < 0 || _syntax.Nodes[old].Kind != Pcre2NodeKind.Sequence)
        {
            return false;
        }

        int at = NativeAt(node.Source.Offset), end = NativeAt(node.Source.Offset + node.Source.Length);
        return at + 2 < end && _syntax.Units[at] == '(' && _syntax.Units[at + 1] == '?' && _syntax.Units[end - 1] == ')';
    }
    private readonly ReadOnlySpan<int> ExecutionChildren(int id) => _children.Span.Slice(_nodes[id].Children.Offset, _nodes[id].Children.Count);
    // -1 refuses; -2 is a source-permitted terminal; otherwise the next item.
    private int Following(int id, int atom, bool greedy, bool entered, bool hasCalls)
    {
        for (var child = id; ;)
        {
            Charge();
            var parent = _executionParents[child];
            if (parent < 0)
            {
                return greedy ? -2 : -1;
            }

            var node = _nodes.Span[parent];
            var children = ExecutionChildren(parent);
            if (node.Kind == Pcre2NodeKind.Sequence)
            {
                for (var ordinal = 0; ordinal < children.Length - 1; ordinal++)
                {
                    if (children[ordinal] == child)
                    {
                        return children[ordinal + 1];
                    }
                }

                if (NativeGroup(parent) && !greedy)
                {
                    return -1;
                }
            }
            else if (node.Kind == Pcre2NodeKind.Repeat)
            {
                if (node.Max != 1 && node.Quantifier != Pcre2Quantifier.Possessive)
                {
                    return -1;
                }
            }
            else if (node.Kind == Pcre2NodeKind.Capture)
            {
                if (!greedy || hasCalls)
                {
                    return -1;
                }
            }
            else if (node.Kind == Pcre2NodeKind.Atomic)
            {
                return greedy && !entered ? -2 : -1;
            }
            else if (node.Kind == Pcre2NodeKind.ScriptRun)
            {
                if (!greedy || _nodes.Span[atom].Kind != Pcre2NodeKind.Literal)
                {
                    return -1;
                }
            }
            else if (node.Kind == Pcre2NodeKind.Assertion)
            {
                var assertion = _assertions[node.Operand];
                if (!greedy || entered || assertion.Kind is Pcre2AssertionKind.NonAtomicAhead or Pcre2AssertionKind.NonAtomicBehind || assertion.VariableLength)
                {
                    return -1;
                }

                return -2;
            }
            child = parent;
        }
    }
    private bool Disjoint(int left, int right)
    {
        var a = _nodes.Span[left];
        var b = _nodes.Span[right];
        if (b.Kind == Pcre2NodeKind.AbsoluteEnd && TryProperty(left, out _, out _))
        {
            return true;
        }

        if (a.Kind == Pcre2NodeKind.Literal)
        {
            return LiteralDisjoint(a, b, right);
        }

        if (b.Kind == Pcre2NodeKind.Literal)
        {
            return LiteralDisjoint(b, a, left);
        }

        if (TryProperty(left, out var lp, out var ln) && TryProperty(right, out var rp, out var rn))
        {
            return PropertyDisjoint(lp, ln, rp, rn);
        }

        int lt = NativeType(left), rt = NativeType(right);
        if (lt >= 0 && lt < 17 && rt >= 0 && rt < 21)
        {
            return PossessTables.Types[lt * 21 + rt] != 0;
        }

        if (a.Kind != Pcre2NodeKind.Class || b.Kind != Pcre2NodeKind.Class)
        {
            return false;
        }
        // OP_NOT/OP_NOTI (including negated caseless lists) is not an
        // ordinary NCLASS bitmap. Only a small positive literal list can
        // compare against it in the source pass.
        if (NegativeLiteral(left) || NegativeLiteral(right))
        {
            return false;
        }

        PrepareClassSummaries();
        var leftBitmap = LowClass(_nodes[left].Operand) && (lt < 0 || lt <= 5);
        var rightBitmap = LowClass(_nodes[right].Operand) && (rt < 0 || rt <= 5);
        var leftPositive = leftBitmap && ((_classSummary[_nodes[left].Operand] & 8) == 0 || !_syntax.Profile.Utf && _syntax.Profile.Width == Pcre2CodeUnitWidth.Eight);
        var rightPositive = rightBitmap && ((_classSummary[_nodes[right].Operand] & 8) == 0 || !_syntax.Profile.Utf && _syntax.Profile.Width == Pcre2CodeUnitWidth.Eight);
        if (!leftPositive && !rightPositive)
        {
            return false;
        }

        var extended = leftPositive ? right : left;
        if (!leftBitmap || !rightBitmap)
        {
            var cls = _nodes[extended].Operand;
            if ((_classSummary[cls] & 1) != 0)
            {
                return false;
            }
        }
        for (var cp = 0; cp < 256; cp++)
        {
            Charge();
            if (BitmapContains(left, cp) && BitmapContains(right, cp))
            {
                return false;
            }
        }
        return true;
    }
    private bool NegativeLiteral(int id)
    {
        var original = _origins[id];
        if (original < 0)
        {
            return false;
        }

        var node = _syntax.Nodes[original];
        if (node.Kind != Pcre2NodeKind.Class)
        {
            return false;
        }

        int at = NativeAt(node.Source.Offset), end = NativeAt(node.Source.Offset + node.Source.Length);
        if (at >= end || _syntax.Units[at] != '[')
        {
            return false;
        }

        var cls = _syntax.Classes[node.Operand];
        if (cls.Kind != Pcre2ClassKind.Complement)
        {
            return false;
        }

        var atom = _syntax.Classes[cls.Left];
        if (atom.Kind != Pcre2ClassKind.Intervals || atom.Intervals.Count != 1)
        {
            return false;
        }

        var range = _syntax.Intervals[atom.Intervals.Offset];
        return range.First == range.Last && LiteralClassTokens(at, end);
    }
    private bool BitmapContains(int id, int cp)
    {
        var type = NativeType(id);
        if (type is >= 0 and <= 5)
        {
            var table = type < 2 ? 2 : type < 4 ? 0 : 5;
            var present = (_syntax.Profile.CharacterTables.Bytes[512 + table * 32 + cp / 8] & (1 << (cp % 8))) != 0;
            return (type & 1) == 1 ? present : !present;
        }
        return ClassContains(_nodes[id].Operand, cp);
    }
    private int NativeType(int id)
    {
        var node = _nodes.Span[id];
        if (node.Kind == Pcre2NodeKind.Dot)
        {
            return (node.Flags & Pcre2CompileFlags.Dotall) != 0 ? 7 : 6;
        }

        if (node.Kind == Pcre2NodeKind.CodeUnit)
        {
            return _syntax.Profile.Utf ? 8 : 7;
        }

        if (node.Kind == Pcre2NodeKind.Newline)
        {
            return 11;
        }

        if (node.Kind == Pcre2NodeKind.Grapheme)
        {
            return 16;
        }

        if (node.Kind == Pcre2NodeKind.BeforeFinalNewline)
        {
            return 17;
        }

        if (node.Kind == Pcre2NodeKind.AbsoluteEnd)
        {
            return 18;
        }

        if (node.Kind == Pcre2NodeKind.End)
        {
            return (node.Flags & Pcre2CompileFlags.Multiline) != 0 ? 20 : 19;
        }

        if (node.Kind != Pcre2NodeKind.Class || _origins[id] < 0)
        {
            return -1;
        }

        var at = NativeAt(node.Source.Offset);
        if (at + 1 >= _syntax.NativePatternLength || _syntax.Units[at] != '\\')
        {
            return -1;
        }

        var c = _syntax.Units[at + 1];
        if (c is 'd' or 'D' or 's' or 'S' or 'w' or 'W' && !StandaloneTableEscape(_origins[id], out _, out _))
        {
            return -1;
        }

        return c switch { 'D' => 0, 'd' => 1, 'S' => 2, 's' => 3, 'W' => 4, 'w' => 5, 'H' => 12, 'h' => 13, 'V' => 14, 'v' => 15, _ => -1 };
    }
    private bool TryProperty(int id, out Pcre2PropertyKey property, out bool negative)
    {
        property = default;
        negative = false;
        if (_nodes.Span[id].Kind != Pcre2NodeKind.Class)
        {
            return false;
        }

        var original = _origins[id];
        if (original < 0)
        {
            return false;
        }

        var at = NativeAt(_syntax.Nodes[original].Source.Offset);
        if (at >= _syntax.NativePatternLength || _syntax.Units[at] != '\\')
        {
            return false;
        }

        var cls = _classes[_nodes[id].Operand];
        if (cls.Kind == Pcre2ClassKind.Complement)
        { negative = true; cls = _classes[cls.Left]; }
        if (cls.Kind != Pcre2ClassKind.Property)
        {
            return false;
        }

        property = cls.Property;
        return true;
    }
    private static int PropertyType(Pcre2PropertyKey key) => key.Kind switch
    {
        Pcre2PropertyKind.MajorCategory => 1,
        Pcre2PropertyKind.ParticularCategory => 2,
        Pcre2PropertyKind.Script => 3,
        Pcre2PropertyKind.ScriptExtensions => 4,
        Pcre2PropertyKind.BidiClass => 11,
        Pcre2PropertyKind.Binary => 12,
        Pcre2PropertyKind.Special => key.Id,
        _ => -1
    };
    private static bool PropertyDisjoint(Pcre2PropertyKey left, bool leftNegative, Pcre2PropertyKey right, bool rightNegative)
    {
        int lt = PropertyType(left), rt = PropertyType(right);
        if (lt is < 0 or >= 13 || rt is < 0 or >= 13)
        {
            return false;
        }

        int rule = PossessTables.Properties[lt * 13 + rt];
        bool lp = !leftNegative, rp = !rightNegative, same = lp == rp;
        if (rule == 1)
        {
            return lp && rp;
        }

        if (rule == 2)
        {
            return (left.Id == right.Id) != same;
        }

        if (rule == 3)
        {
            return !same;
        }

        if (rule == 4)
        {
            return rp && (left.Id != MajorCategory(right.Id)) == same;
        }

        if (rule == 5)
        {
            return lp && (right.Id != MajorCategory(left.Id)) == same;
        }

        if (rule is >= 6 and <= 17)
        {
            var swapped = rule is >= 9 and <= 11 or >= 15 and <= 17;
            var particular = rule >= 12;
            var row = rule >= 12 ? (rule - 12) % 3 : (rule - 6) % 3;
            int a = row == 0 ? 1 : row == 1 ? 6 : 1, b = row == 0 ? 3 : row == 1 ? 6 : 3, c = row == 0 ? 3 : row == 1 ? 0 : 4, d = row == 0 ? 14 : row == 1 ? 0 : 21;
            var category = swapped ? left.Id : right.Id;
            bool positive = swapped ? rp : lp, other = swapped ? lp : rp;
            var different = particular ? MajorCategory(category) != a && MajorCategory(category) != b && (category != d || !positive) : category != a && category != b && (category != c || !positive);
            return other && positive == different;
        }
        return false;
    }
    private static int MajorCategory(int category) => category < 5 ? 0 : category < 10 ? 1 : category < 13 ? 2 : category < 16 ? 3 : category < 23 ? 4 : category < 27 ? 5 : 6;
    private static bool IsAnyNewline(int cp) => cp is 10 or 13 or 11 or 12 or 0x85 or 0x2028 or 0x2029;
    private bool LowClass(int id)
    {
        PrepareClassSummaries();
        return (_classSummary[id] & 3) == 0;
    }
    private void PrepareClassSummaries()
    {
        if (_classSummary.Length != 0)
        {
            return;
        }

        LowerFailure.Reserve(ref _budget, checked(64L + 2L * _classes.Count), _source);
        _classSummary = new byte[_classes.Count];
        _classReachable = new byte[_classes.Count];
        for (var id = 0; id < _classes.Count; id++)
        {
            Charge();
            var c = _classes[id];
            byte flags = 0;
            if (c.Kind == Pcre2ClassKind.Property)
            {
                flags = 1;
            }
            else if (c.Kind == Pcre2ClassKind.Intervals)
            {
                foreach (var range in _intervals.Span.Slice(c.Intervals.Offset, c.Intervals.Count))
                {
                    Charge();
                    if (range.Last > 255 && (_syntax.Profile.Utf || _syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen))
                    {
                        flags |= 2;
                    }
                }
            }
            else if (c.Kind == Pcre2ClassKind.Complement)
            {
                flags = (byte)((_classSummary[c.Left] & 7) | ((_classSummary[c.Left] & 8) == 0 ? 8 : 0));
            }
            else if (c.Kind == Pcre2ClassKind.Union)
            {
                flags = (byte)(_classSummary[c.Left] | _classSummary[c.Right]);
            }
            else if (c.Kind is Pcre2ClassKind.Intersection or Pcre2ClassKind.Subtraction or Pcre2ClassKind.SymmetricDifference)
            {
                bool left = (_classSummary[c.Left] & 8) != 0, right = (_classSummary[c.Right] & 8) != 0;
                var high = c.Kind == Pcre2ClassKind.Intersection ? left && right : c.Kind == Pcre2ClassKind.Subtraction ? left && !right : left != right;
                flags = (byte)(((_classSummary[c.Left] | _classSummary[c.Right]) & 7) | 4 | (high ? 8 : 0));
            }
            _classSummary[id] = flags;
        }
    }
    private void MarkClass(int root)
    {
        PrepareClassSummaries();
        Array.Clear(_classReachable);
        Charge(_classReachable.Length);
        _classReachable[root] = 1;
        for (var id = root; id >= 0; id--)
        {
            Charge();
            if (_classReachable[id] == 0)
            {
                continue;
            }

            var c = _classes[id];
            if (c.Left >= 0)
            {
                _classReachable[c.Left] = 1;
            }

            if (c.Right >= 0)
            {
                _classReachable[c.Right] = 1;
            }
        }
    }
    private bool LiteralDisjoint(Pcre2Node literal, Pcre2Node other, int otherId)
    {
        Span<int> cases = stackalloc int[8];
        var count = LiteralCases(literal, cases);
        if (other.Kind == Pcre2NodeKind.Literal)
        {
            Span<int> rhs = stackalloc int[8];
            var rc = LiteralCases(other, rhs);
            for (var i = 0; i < count; i++)
            {
                for (var j = 0; j < rc; j++)
                {
                    Charge();
                    if (cases[i] == rhs[j])
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        if (other.Kind == Pcre2NodeKind.Class)
        { for (var i = 0; i < count; i++) { Charge(); if (ClassContains(_nodes[otherId].Operand, cases[i])) { return false; } } return true; }
        if (other.Kind == Pcre2NodeKind.Newline)
        { for (var i = 0; i < count; i++) { if (IsAnyNewline(cases[i])) { return false; } } return true; }
        if (other.Kind == Pcre2NodeKind.AbsoluteEnd)
        {
            return true;
        }

        if (other.Kind == Pcre2NodeKind.BeforeFinalNewline || other.Kind == Pcre2NodeKind.End && (other.Flags & Pcre2CompileFlags.Multiline) == 0)
        { for (var i = 0; i < count; i++) { if (IsAnyNewline(cases[i])) { return false; } } return true; }
        return false;
    }
    private int LiteralCases(Pcre2Node literal, scoped Span<int> output)
    {
        var cp = literal.Operand;
        output[0] = cp;
        if ((literal.Flags & Pcre2CompileFlags.Caseless) == 0)
        {
            return 1;
        }

        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        if ((literal.ExtraFlags & (Pcre2ExtraFlags.TurkishCasing | Pcre2ExtraFlags.CaselessRestrict)) == Pcre2ExtraFlags.TurkishCasing && cp is 0x49 or 0x69 or 0x130 or 0x131)
        { output[1] = cp switch { 0x49 => 0x131, 0x131 => 0x49, 0x69 => 0x130, _ => 0x69 }; return 2; }
        if (!_syntax.Profile.Utf && !_syntax.Profile.Ucp || cp < 128 && (literal.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) != 0)
        { output[1] = cp <= 255 ? data.LowByteTables[256 + cp] : cp; return 2; }
        var record = data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]];
        var count = 1;
        if (record.CaseSetCount > 0)
        {
            foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
            {
                Charge();
                if ((literal.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (equivalent < 128))
                {
                    output[count++] = equivalent;
                }
            }
        }
        else
        {
            output[count++] = cp < 128 ? data.LowByteTables[256 + cp] : cp + record.OtherCaseDelta;
        }

        return count;
    }
    private bool ClassContains(int root, int cp)
    {
        // Reuse admitted fixed bitmap storage as topological booleans only
        // when class count fits; larger classes get a budgeted scratch array.
        if (_classValues is null)
        { LowerFailure.Reserve(ref _budget, checked(32L + _classes.Count), _source); _classValues = new byte[_classes.Count]; }
        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        for (var id = 0; id <= root; id++)
        {
            Charge();
            var item = _classes[id];
            var value = false;
            switch (item.Kind)
            {
                case Pcre2ClassKind.Intervals:
                    foreach (var range in _intervals.Span.Slice(item.Intervals.Offset, item.Intervals.Count))
                    { Charge(); if (cp < range.First) { break; } if (cp <= range.Last) { value = true; break; } }
                    break;
                case Pcre2ClassKind.Property:
                    value = Pcre2CharacterOps.Property(cp, item.Property, data);
                    break;
                case Pcre2ClassKind.TableClass:
                    value = cp <= 255 && (data.LowByteTables[512 + item.TableClass * 32 + cp / 8] & (1 << (cp % 8))) != 0;
                    break;
                case Pcre2ClassKind.Complement:
                    value = _classValues[item.Left] == 0;
                    break;
                case Pcre2ClassKind.Union:
                    value = _classValues[item.Left] != 0 || _classValues[item.Right] != 0;
                    break;
                case Pcre2ClassKind.Intersection:
                    value = _classValues[item.Left] != 0 && _classValues[item.Right] != 0;
                    break;
                case Pcre2ClassKind.Subtraction:
                    value = _classValues[item.Left] != 0 && _classValues[item.Right] == 0;
                    break;
                case Pcre2ClassKind.SymmetricDifference:
                    value = (_classValues[item.Left] != 0) != (_classValues[item.Right] != 0);
                    break;
            }
            _classValues[id] = value ? (byte)1 : (byte)0;
        }
        return _classValues[root] != 0;
    }
}
