#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal struct CompileFacts
{
    internal int First, Required, Minimum;
    internal bool FirstCaseless, RequiredCaseless, RequiredVariable, Variable, Matched, Stopped;
}
internal ref partial struct LowerReader
{
    private Pcre2Profile Study(out Pcre2SearchHints hints, out ReadOnlySpan<byte> bitmap)
    {
        var n = _syntax.Nodes.Length;
        LowerFailure.Reserve(ref _budget, checked(256L + 64L * n), _source);
        _facts = new CompileFacts[n];
        _anchor = new bool[n];
        _line = new bool[n];
        _dotForbidden = new bool[n];
        _startBits = new byte[checked(n * 32)];
        _bitmapState = new byte[n];
        bool accept = false, prune = false;
        uint backrefMap = 0;
        int topBackref = 0, maximumBehind = 0;
        foreach (var node in _syntax.Nodes)
        {
            Charge();
            if (node.Kind == Pcre2NodeKind.Verb)
            { var verb = _syntax.Controls[node.Operand].Kind; accept |= verb == Pcre2VerbKind.Accept; prune |= verb is Pcre2VerbKind.Prune or Pcre2VerbKind.Skip; }
            if (node.Kind == Pcre2NodeKind.Backreference)
            {
                foreach (var number in _numbers.Span.Slice(_references[node.Operand].Candidates.Offset, _references[node.Operand].Candidates.Count))
                { Charge(); backrefMap |= number < 32 ? 1u << number : 1; topBackref = Math.Max(topBackref, number); }
            }

            if (node.Kind == Pcre2NodeKind.Conditional && _syntax.Conditions[node.Operand].ReferenceIndex >= 0)
            {
                var r = _references[_syntax.Conditions[node.Operand].ReferenceIndex];
                foreach (var number in _numbers.Span.Slice(r.Candidates.Offset, r.Candidates.Count))
                {
                    Charge();
                    if (_syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.CaptureSet || r.Kind == Pcre2ReferenceKind.Named)
                    {
                        topBackref = Math.Max(topBackref, number);
                    }

                    if (r.Kind == Pcre2ReferenceKind.Named && r.Candidates.Count > 1)
                    {
                        backrefMap |= number < 32 ? 1u << number : 1;
                    }
                }
            }
            if (node.Kind == Pcre2NodeKind.ScanSubstring)
            {
                var s = _syntax.ScanReferences[node.Operand];
                foreach (var r in _references.Span.Slice(s.Offset, s.Count))
                {
                    foreach (var number in _numbers.Span.Slice(r.Candidates.Offset, r.Candidates.Count))
                    {
                        Charge();
                        topBackref = Math.Max(topBackref, number);
                        if (r.Kind == Pcre2ReferenceKind.Named && r.Candidates.Count > 1)
                        {
                            backrefMap |= number < 32 ? 1u << number : 1;
                        }
                    }
                }
            }
            if (node.Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary or Pcre2NodeKind.AbsoluteStart)
            {
                maximumBehind = Math.Max(maximumBehind, 1);
            }

            if (node.Kind == Pcre2NodeKind.Assertion && IsBehind(_assertions[node.Operand].Kind))
            {
                maximumBehind = Math.Max(maximumBehind, _assertions[node.Operand].MaximumLength);
            }
        }
        for (var id = n - 1; id >= 0; id--)
        {
            Charge();
            var node = _syntax.Nodes[id];
            var forbid = _dotForbidden[id] || prune;
            if (node.Kind is Pcre2NodeKind.Atomic or Pcre2NodeKind.Assertion)
            {
                forbid = true;
            }

            if (node.Kind == Pcre2NodeKind.Capture)
            {
                var number = _syntax.Groups[node.Operand].Number;
                if ((backrefMap & (number < 32 ? 1u << number : 1)) != 0)
                {
                    forbid = true;
                }
            }
            foreach (var child in OldChildren(id))
            {
                _dotForbidden[child] = forbid;
            }
        }
        SourceVaryOrder();
        for (var id = 0; id < n; id++)
        {
            Charge();
            var node = _syntax.Nodes[id];
            var children = OldChildren(id);
            var fact = new CompileFacts { First = -2, Required = -1 };
            if (node.Kind == Pcre2NodeKind.Literal)
            {
                fact = LiteralFact(node);
            }
            else if (node.Kind == Pcre2NodeKind.Class)
            {
                if (TryClassLiteral(id, out var cp, out var caseless))
                {
                    fact = LiteralFact(node with
                    {
                        Kind = Pcre2NodeKind.Literal,
                        Operand = cp,
                        Flags = caseless ? node.Flags | Pcre2CompileFlags.Caseless : node.Flags & ~Pcre2CompileFlags.Caseless
                    });
                }
                else
                {
                    fact = new() { First = -1, Required = -1, Minimum = 1, Matched = true };
                }
            }
            else if (node.Kind is Pcre2NodeKind.Dot or Pcre2NodeKind.CodeUnit or Pcre2NodeKind.Newline or Pcre2NodeKind.Grapheme)
            {
                fact = new() { First = -1, Required = -1, Minimum = 1, Matched = true, Variable = false };
            }
            else if (node.Kind is Pcre2NodeKind.Backreference or Pcre2NodeKind.Subroutine)
            {
                fact = new() { First = -1, Required = -1 };
            }
            else if (node.Kind == Pcre2NodeKind.Start && (node.Flags & Pcre2CompileFlags.Multiline) != 0)
            {
                fact.First = -1;
            }
            else if (node.Kind == Pcre2NodeKind.Verb && _syntax.Controls[node.Operand].Kind == Pcre2VerbKind.Accept)
            { fact.First = -1; fact.Stopped = true; }
            else if (node.Kind == Pcre2NodeKind.Repeat)
            {
                fact = _facts[children[0]];
                fact.Matched &= node.Min > 0;
                fact.Minimum = Math.Min(65535, (int)Math.Min(65535, node.Min * (long)fact.Minimum));
                if (node.Min == 0)
                { fact.First = fact.First == -2 ? -2 : _syntax.Nodes[children[0]].Kind == Pcre2NodeKind.Backreference && _references[_syntax.Nodes[children[0]].Operand].Candidates.Count > 1 ? -2 : -1; fact.Required = -1; }
                else if (node.Min > 1 && fact.First >= 0 && fact.Required < 0)
                { fact.Required = fact.First; fact.RequiredCaseless = fact.FirstCaseless; }
                fact.Variable |= node.Min != node.Max;

                if (node.Max == 0)
                {
                    fact = new() { First = -2, Required = -1 };
                }
            }
            else if (node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun)
            { fact = _facts[children[0]]; fact.Stopped = false; }
            else if (node.Kind == Pcre2NodeKind.Sequence)
            {
                fact = SequenceFact(children);
            }
            else if (node.Kind == Pcre2NodeKind.Alternative || node.Kind == Pcre2NodeKind.Conditional)
            {
                var begin = node.Kind == Pcre2NodeKind.Conditional && _syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? 1 : 0;
                fact = _facts[children[begin]];
                for (var ordinal = begin + 1; ordinal < children.Length; ordinal++)
                {
                    fact = AlternativeFact(fact, _facts[children[ordinal]]);
                }

                if (node.Kind == Pcre2NodeKind.Conditional && children.Length - begin == 1)
                { fact.First = -1; fact.Required = -1; fact.Matched = false; fact.Minimum = 0; }
            }
            else if (node.Kind == Pcre2NodeKind.Assertion)
            {
                var assertion = _assertions[node.Operand];
                var sub = _facts[children[0]];
                if (assertion.Kind is Pcre2AssertionKind.PositiveAhead or Pcre2AssertionKind.NonAtomicAhead && sub.First >= 0 && sub.Required >= 0)
                { fact.Required = sub.Required; fact.RequiredCaseless = sub.RequiredCaseless; fact.RequiredVariable = sub.RequiredVariable; }
            }
            if (node.Kind == Pcre2NodeKind.Literal || node.Kind == Pcre2NodeKind.Class && fact.First >= 0)
            {
                fact.RequiredVariable = _varyBefore[id];
            }

            _facts[id] = fact;
            AnalyzeAnchors(id);
            AnalyzeBitmap(id);
        }
        var root = _facts[_syntax.Root];
        var profile = _syntax.Profile;
        var anchored = (profile.Flags & Pcre2CompileFlags.Anchored) != 0 || _anchor[_syntax.Root];
        if (anchored && (profile.Flags & Pcre2CompileFlags.Anchored) == 0)
        {
            LowerFailure.Reserve(ref _budget, 256, _source);
            Charge();
            profile = new(profile.Width, profile.Flags | Pcre2CompileFlags.Anchored, profile.ExtraFlags, profile.Newline, profile.Bsr,
                profile.CharacterTables, profile.Optimization, profile.MaxVariableLookbehind);
        }
        int first = -1, required = -1, minimum = 0;
        bool firstCase = false, requiredCase = false, startLine = false;
        bitmap = default;
        if (profile.EffectiveOptimization.StartOptimize)
        {
            first = root.First;
            firstCase = root.FirstCaseless;
            required = accept ? -1 : root.Required;
            requiredCase = root.RequiredCaseless;
            if (first < 0)
            {
                var asserted = AssertedFirst(_syntax.Root);
                if (asserted >= 0 && _facts[asserted].First != required)
                { first = _facts[asserted].First; firstCase = _facts[asserted].FirstCaseless; }
            }
            if (first < 0 && !anchored)
            {
                startLine = _line[_syntax.Root];
            }

            var minUnits = first >= 0 ? 1 : 0;
            if (required >= 0 && !(profile.Utf && (profile.Width == Pcre2CodeUnitWidth.Eight ? first >= 128 && required >= 128 : first is >= 0xD800 and <= 0xDBFF && required is >= 0xDC00 and <= 0xDFFF)))
            {
                minUnits++;
            }

            if (anchored && !root.RequiredVariable)
            {
                required = -1;
            }

            if (first < 0 && !startLine && _bitmapState[_syntax.Root] == 2)
            {
                var bits = _startBits.AsSpan(_syntax.Root * 32, 32);
                int a = -1, b = -1, count = 0;
                for (var cp = 0; cp < 256; cp++)
                {
                    if ((bits[cp / 8] & (1 << (cp % 8))) != 0)
                    {
                        count++;
                        if (a < 0)
                        {
                            a = cp;
                        }
                        else if (b < 0)
                        {
                            b = cp;
                        }
                    }
                }

                if (count == 1 && !(profile.Width == Pcre2CodeUnitWidth.Sixteen && a == 255) && !(profile.Width == Pcre2CodeUnitWidth.Eight && profile.Utf && a > 127))
                { first = a; firstCase = false; }
                else if (count == 2 && !(profile.Width == Pcre2CodeUnitWidth.Sixteen && b == 255) && !(profile.Width == Pcre2CodeUnitWidth.Eight && profile.Utf && b > 127) && OtherCase(b) == a && !HasMultipleCases(b))
                { first = a; firstCase = true; }
                else
                {
                    bitmap = bits;
                }

                if (first >= 0 && (required == a || required == b))
                {
                    required = -1;
                }
            }
            if (root.Matched && !accept && topBackref <= 128)
            {
                minimum = Math.Max(0, StudyMinimum(_syntax.Root));
            }

            if (!bitmap.IsEmpty && minUnits == 0)
            {
                minUnits = 1;
            }

            minimum = Math.Max(minimum, minUnits);
            firstCase &= first >= 0 && OtherCase(first) != first;
            requiredCase &= required >= 0 && OtherCase(required) != required;
        }
        hints = new(minimum, maximumBehind, !root.Matched, startLine, Math.Max(first, -1), firstCase, Math.Max(required, -1), requiredCase, topBackref);
        return profile;
    }
    private CompileFacts LiteralFact(Pcre2Node node)
    {
        int cp = node.Operand, first = NativeLead(cp), last = NativeLast(cp);
        var caseless = (node.Flags & Pcre2CompileFlags.Caseless) != 0;
        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        var record = data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]];
        var many = caseless && (_syntax.Profile.Utf || _syntax.Profile.Ucp) &&
            ((node.ExtraFlags & (Pcre2ExtraFlags.TurkishCasing | Pcre2ExtraFlags.CaselessRestrict)) == Pcre2ExtraFlags.TurkishCasing && cp is 0x49 or 0x69 or 0x130 or 0x131 ||
             record.CaseSetCount > 0 && ((node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) == 0 || data.CaseSets[record.CaseSetOffset] >= 128));
        var wide = _syntax.Profile.Utf && (cp > (_syntax.Profile.Width == Pcre2CodeUnitWidth.Eight ? 127 : 65535));
        return new()
        {
            First = many || caseless && wide ? -1 : first,
            Required = !caseless && wide ? last : -1,
            FirstCaseless = caseless && !many,
            Minimum = 1,
            Matched = true
        };
    }
    private static CompileFacts AlternativeFact(CompileFacts current, CompileFacts next)
    {
        if (current.First != next.First || current.FirstCaseless != next.FirstCaseless)
        {
            if (current.First >= 0 && current.Required < 0)
            { current.Required = current.First; current.RequiredCaseless = current.FirstCaseless; }
            current.First = -1;
        }
        if (current.First < 0 && next.First >= 0 && next.Required < 0)
        { next.Required = next.First; next.RequiredCaseless = next.FirstCaseless; }
        if (current.Required != next.Required || current.RequiredCaseless != next.RequiredCaseless)
        {
            current.Required = -1;
        }

        current.RequiredVariable |= next.RequiredVariable;
        current.Minimum = Math.Min(current.Minimum, next.Minimum);
        current.Matched &= next.Matched;
        current.Variable |= next.Variable;
        return current;
    }
    private readonly int NativeLead(int cp) => !_syntax.Profile.Utf ? cp : _syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen ?
        cp <= 65535 ? cp : 0xD800 + ((cp - 0x10000) >> 10) : cp < 128 ? cp : cp < 2048 ? 0xC0 | (cp >> 6) : cp < 65536 ? 0xE0 | (cp >> 12) : 0xF0 | (cp >> 18);
    private readonly int NativeLast(int cp) => !_syntax.Profile.Utf ? cp : _syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen ?
        cp <= 65535 ? cp : 0xDC00 + ((cp - 0x10000) & 1023) : cp < 128 ? cp : 0x80 | (cp & 63);
    private readonly bool HasMultipleCases(int cp)
    {
        if (!_syntax.Profile.Utf && !_syntax.Profile.Ucp)
        {
            return false;
        }

        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        return data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]].CaseSetCount != 0;
    }
    private readonly int OtherCase(int cp)
    {
        if (!_syntax.Profile.Utf && !_syntax.Profile.Ucp)
        {
            return cp < 256 ? _syntax.Profile.CharacterTables.Bytes[256 + cp] : cp;
        }

        if (cp < 128)
        {
            return _syntax.Profile.CharacterTables.Bytes[256 + cp];
        }

        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        return cp + data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]].OtherCaseDelta;
    }
    private bool TryClassLiteral(int id, out int cp, out bool caseless)
    {
        cp = -1;
        caseless = false;
        var node = _syntax.Nodes[id];
        int at = NativeAt(node.Source.Offset), end = NativeAt(node.Source.Offset + node.Source.Length);
        if (at >= end || _syntax.Units[at] != '[')
        {
            return false;
        }

        var item = _syntax.Classes[node.Operand];
        // Native one/two literal peepholes apply to parsed literal tokens,
        // before source class closure, not an arbitrary equivalent set proof.
        if (!LiteralClassTokens(at, end) && !(item.Kind == Pcre2ClassKind.Intervals && item.Intervals.Count == 1 && _syntax.Intervals[item.Intervals.Offset].First == _syntax.Intervals[item.Intervals.Offset].Last))
        {
            return false;
        }

        if (item.Kind == Pcre2ClassKind.Intervals && item.Intervals.Count == 1)
        {
            var range = _syntax.Intervals[item.Intervals.Offset];
            if (range.First != range.Last)
            {
                return false;
            }

            cp = range.First;
            caseless = (node.Flags & Pcre2CompileFlags.Caseless) != 0;
            return true;
        }
        if (item.Kind != Pcre2ClassKind.Union)
        {
            return false;
        }

        var a = _syntax.Classes[item.Left];
        var b = _syntax.Classes[item.Right];
        if (a.Kind != Pcre2ClassKind.Intervals || b.Kind != Pcre2ClassKind.Intervals || a.Intervals.Count != 1 || b.Intervals.Count != 1)
        {
            return false;
        }

        var ar = _syntax.Intervals[a.Intervals.Offset];
        var br = _syntax.Intervals[b.Intervals.Offset];
        if (ar.First != ar.Last || br.First != br.Last || ar.First == br.First || OtherCase(ar.First) != br.First)
        {
            return false;
        }

        var data = _provider.Bind(_syntax.Profile.CharacterTables);
        var record = data.Records[data.Stage2[data.Stage1[ar.First / 128] * 128 + ar.First % 128]];
        var restricted = (node.ExtraFlags & Pcre2ExtraFlags.CaselessRestrict) != 0;
        if (record.CaseSetCount != 0 && !(restricted && ar.First < 128 && br.First < 128))
        {
            return false;
        }

        if (!restricted && (node.ExtraFlags & Pcre2ExtraFlags.TurkishCasing) != 0 && ar.First is 0x49 or 0x69 or 0x130 or 0x131)
        {
            return false;
        }

        cp = ar.First;
        caseless = true;
        return true;
    }
    private bool LiteralClassTokens(int at, int end)
    {
        var quoted = false;
        for (var pos = at + 1; pos < end - 1; pos++)
        {
            Charge();
            var token = _syntax.Units[pos];
            if (token == '\\' && pos + 1 < end - 1)
            { var escaped = _syntax.Units[++pos]; if (escaped == 'Q') { quoted = true; } else if (escaped == 'E') { quoted = false; } continue; }
            if (!quoted && token == '-' && pos > at + 1 && pos < end - 2)
            {
                return false;
            }

            if (!quoted && token == '[' && pos + 1 < end - 1 && _syntax.Units[pos + 1] is ':' or '.' or '=')
            {
                return false;
            }
        }
        return true;
    }
    private bool IsAllAnyClass(int root)
    {
        var cls = _classes[root];
        if (cls.Kind == Pcre2ClassKind.Property && cls.Property == new Pcre2PropertyKey(Pcre2PropertyKind.Special, 13))
        {
            return true;
        }

        PrepareClassSummaries();
        // Source OP_NCLASS/OP_CLASS peephole: all low bits and its
        // implicit high range, without an XCLASS/ECLASS property payload.
        if ((_classSummary[root] & 7) != 0 || (_classSummary[root] & 8) == 0 && (_syntax.Profile.Utf || _syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen))
        {
            return false;
        }

        for (var cp = 0; cp < 256; cp++)
        {
            Charge();
            if (!ClassContains(root, cp))
            {
                return false;
            }
        }
        return true;
    }
    private void AnalyzeAnchors(int id)
    {
        var node = _syntax.Nodes[id];
        var children = OldChildren(id);
        bool anchor = false, line = false;
        switch (node.Kind)
        {
            case Pcre2NodeKind.AbsoluteStart:
            case Pcre2NodeKind.StartOffset:
                anchor = true;
                break;
            case Pcre2NodeKind.Start:
                anchor = (node.Flags & Pcre2CompileFlags.Multiline) == 0;
                line = true;
                break;
            case Pcre2NodeKind.Repeat:
                var dot = _syntax.Nodes[children[0]].Kind == Pcre2NodeKind.Dot;
                var all = dot && (_syntax.Nodes[children[0]].Flags & Pcre2CompileFlags.Dotall) != 0;
                if (_syntax.Nodes[children[0]].Kind == Pcre2NodeKind.Class)
                {
                    _ = _classes[_nodeClass[children[0]]];
                    dot = all = IsAllAnyClass(_nodeClass[children[0]]);
                }
                if (node.Min == 0 && node.Max == -1 && dot && !_dotForbidden[id] && _syntax.Profile.EffectiveOptimization.DotStarAnchor)
                { anchor = all; line = !all; }
                else if (node.Min > 0)
                { anchor = _anchor[children[0]]; line = _line[children[0]]; }
                break;
            case Pcre2NodeKind.Sequence:
                foreach (var child in children)
                { if (SkipSignificant(child)) { continue; } anchor = _anchor[child]; line = _line[child]; break; }
                break;
            case Pcre2NodeKind.Alternative:
                anchor = line = true;
                foreach (var child in children)
                { anchor &= _anchor[child]; line &= _line[child]; }
                break;
            case Pcre2NodeKind.Capture:
            case Pcre2NodeKind.Atomic:
                anchor = _anchor[children[0]];
                line = _line[children[0]];
                break;
            case Pcre2NodeKind.Assertion:
                if (_assertions[node.Operand].Kind is Pcre2AssertionKind.PositiveAhead or Pcre2AssertionKind.NonAtomicAhead)
                { anchor = _anchor[children[0]]; line = _line[children[0]]; }
                break;
            case Pcre2NodeKind.Conditional:
                var begin = _syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? 1 : 0;
                if (children.Length - begin == 2)
                { anchor = (_anchor[children[begin]] || begin == 1 && _anchor[children[0]]) && _anchor[children[begin + 1]]; line = begin == 1 && _line[children[0]] && _line[children[1]] && _line[children[2]]; }
                break;
        }
        _anchor[id] = anchor;
        _line[id] = line;
    }
    private readonly bool SkipSignificant(int id)
    {
        var node = _syntax.Nodes[id];
        return node.Kind is Pcre2NodeKind.Callout or Pcre2NodeKind.Empty or Pcre2NodeKind.Define ||
            node.Kind == Pcre2NodeKind.Conditional && node.Children.Count == 1 && _syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.Version && !VersionCondition(_syntax.Conditions[node.Operand]) ||
            node.Kind == Pcre2NodeKind.Repeat && node.Max == 0 || node.Kind == Pcre2NodeKind.Verb && _syntax.Controls[node.Operand].MarkIndex >= 0 && _syntax.Controls[node.Operand].Kind != Pcre2VerbKind.Accept;
    }
    private static bool VersionCondition(Pcre2Condition c) => c.AtLeast ? 10 > c.VersionMajor || 10 == c.VersionMajor && 46 >= c.VersionMinor : 10 == c.VersionMajor && 46 == c.VersionMinor;
    private int AssertedFirst(int root)
    {
        // Source find_firstassertedcu only follows leading significant positive
        // forward assertions/groups. A non-asserted first atom stops the scan.
        var id = root;
        for (var depth = 0; depth < _budget.Limits.MaxNestingDepth; depth++)
        {
            Charge();
            var node = _syntax.Nodes[id];
            var children = OldChildren(id);
            if (node.Kind == Pcre2NodeKind.Assertion && _assertions[node.Operand].Kind is Pcre2AssertionKind.PositiveAhead or Pcre2AssertionKind.NonAtomicAhead)
            {
                return _facts[children[0]].First >= 0 ? children[0] : -1;
            }

            if (node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun)
            { id = children[0]; continue; }
            if (node.Kind == Pcre2NodeKind.Sequence)
            { var next = -1; foreach (var child in children) { if (SkipSignificant(child) || _syntax.Nodes[child].Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary) { continue; } next = child; break; } if (next < 0) { return -1; } id = next; continue; }
            return -1;
        }
        return -1;
    }
    private void AnalyzeBitmap(int id)
    {
        var node = _syntax.Nodes[id];
        var bits = _startBits.AsSpan(id * 32, 32);
        var children = OldChildren(id);
        byte status = 0;
        if (node.Kind == Pcre2NodeKind.Literal)
        {
            Span<int> cases = stackalloc int[8];
            var count = LiteralCases(node, cases);
            for (var i = 0; i < count; i++)
            { var cu = NativeLead(cases[i]); cu = Math.Min(cu, 255); bits[cu / 8] |= (byte)(1 << (cu % 8)); }
            status = 2;
        }
        else if (node.Kind == Pcre2NodeKind.Class && BitmapClassAllowed(id))
        {
            var root = _nodeClass[id];
            if (StandaloneTableEscape(id, out var mask, out var negative))
            {
                var table = mask == 8 ? 2 : mask == 1 ? 0 : 5;
                var count = _syntax.Profile.Utf && _syntax.Profile.Width == Pcre2CodeUnitWidth.Eight ? 16 : 32;
                for (var b = 0; b < count; b++)
                { Charge(); var value = _syntax.Profile.CharacterTables.Bytes[512 + table * 32 + b]; bits[b] |= negative ? (byte)~value : value; }
                if (count == 16)
                {
                    if (negative)
                    {
                        bits[24..].Fill(255);
                    }
                    else
                    {
                        for (var cp = 128; cp < 256; cp++)
                        {
                            Charge();
                            // The pinned study implementation intentionally reads
                            // the first cbits block for non-ASCII table bytes.
                            if ((_syntax.Profile.CharacterTables.Bytes[512 + cp / 8] & (1 << (cp % 8))) != 0)
                            { var cu = NativeLead(cp); bits[cu / 8] |= (byte)(1 << (cu % 8)); }
                        }
                    }
                }
                _bitmapState[id] = 2;
                return;
            }
            for (var cp = 0; cp < 256; cp++)
            {
                if (ClassContains(root, cp))
                { var cu = Math.Min(NativeLead(cp), 255); bits[cu / 8] |= (byte)(1 << (cu % 8)); }
            }

            MarkClass(root);
            if ((_classSummary[root] & 8) != 0)
            {
                if (_syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen)
                {
                    bits[31] |= 128;
                }
                else if (_syntax.Profile.Utf)
                { bits[24] |= 240; bits[25..].Fill(255); }
                _bitmapState[id] = 2;
                return;
            }
            var maximum = _syntax.Profile.Utf ? 0x10FFFF : _syntax.Profile.Width == Pcre2CodeUnitWidth.Sixteen ? 65535 : 255;
            if (maximum > 255)
            {
                // Membership is constant between interval boundaries. Test
                // each reachable boundary and each UTF leading-unit boundary,
                // rather than scanning unrelated classes or every scalar.
                SetWideBitmap(root, 256, maximum, bits);
                for (var c = 0; c <= root; c++)
                {
                    Charge();
                    if (_classReachable[c] == 0 || _classes[c].Kind != Pcre2ClassKind.Intervals)
                    {
                        continue;
                    }

                    foreach (var range in _intervals.Span.Slice(_classes[c].Intervals.Offset, _classes[c].Intervals.Count))
                    { SetWideBitmap(root, range.First, maximum, bits); SetWideBitmap(root, range.Last + 1, maximum, bits); }
                }
                if (_syntax.Profile.Utf && _syntax.Profile.Width == Pcre2CodeUnitWidth.Eight)
                {
                    for (var lead = 0xC4; lead <= 0xF4; lead++)
                    { var cp = lead < 0xE0 ? (lead & 31) << 6 : lead < 0xF0 ? (lead & 15) << 12 : (lead & 7) << 18; SetWideBitmap(root, cp, maximum, bits); }
                }
            }
            status = 2;
        }
        else if (node.Kind == Pcre2NodeKind.Sequence)
        {
            status = 1;
            foreach (var child in children)
            {
                var state = _bitmapState[child];
                if (state == 0)
                { status = 0; break; }
                var childBits = _startBits.AsSpan(child * 32, 32);
                for (var b = 0; b < 32; b++)
                {
                    bits[b] |= childBits[b];
                }

                if (state == 2)
                { status = 2; break; }
            }
        }
        else if (node.Kind == Pcre2NodeKind.Alternative)
        {
            status = 2;
            foreach (var child in children)
            {
                var state = _bitmapState[child];
                if (state != 2)
                {
                    status = state == 0 ? (byte)0 : (byte)1;
                }

                var childBits = _startBits.AsSpan(child * 32, 32);
                for (var b = 0; b < 32; b++)
                {
                    bits[b] |= childBits[b];
                }
            }
        }
        else if (node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Repeat)
        { if (node.Kind == Pcre2NodeKind.Repeat && node.Max == 0) { status = 1; } else { _startBits.AsSpan(children[0] * 32, 32).CopyTo(bits); status = _bitmapState[children[0]]; if (node.Kind == Pcre2NodeKind.Repeat && node.Min == 0 && status != 0) { status = 1; } } }
        else if (node.Kind == Pcre2NodeKind.Assertion)
        {
            var assertion = _assertions[node.Operand];
            if (assertion.Kind is Pcre2AssertionKind.PositiveAhead or Pcre2AssertionKind.NonAtomicAhead)
            {
                status = _bitmapState[children[0]];
                if (status == 2)
                {
                    _startBits.AsSpan(children[0] * 32, 32).CopyTo(bits);
                }
            }
            else
            {
                status = 1;
            }
        }
        else if (node.Kind is Pcre2NodeKind.Callout or Pcre2NodeKind.Empty or Pcre2NodeKind.Define or Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary)
        {
            status = 1;
        }
        else if (node.Kind == Pcre2NodeKind.Start && (node.Flags & Pcre2CompileFlags.Multiline) == 0)
        {
            status = 1;
        }

        _bitmapState[id] = status;
    }
    private void SetWideBitmap(int root, int cp, int maximum, Span<byte> bits)
    {
        Charge();
        if (cp < 256 || cp > maximum || !ClassContains(root, cp))
        {
            return;
        }

        var cu = Math.Min(NativeLead(cp), 255);
        bits[cu / 8] |= (byte)(1 << (cu % 8));
    }
    private bool BitmapClassAllowed(int id)
    {
        var node = _syntax.Nodes[id];
        var at = NativeAt(node.Source.Offset);
        if (at + 1 < _syntax.NativePatternLength && _syntax.Units[at] == '\\' && _syntax.Units[at + 1] is 'H' or 'V' or 'P')
        {
            return false;
        }

        var root = _nodeClass[id];
        PrepareClassSummaries();
        if ((_classSummary[root] & 1) != 0 || (_classSummary[root] & 6) == 6)
        {
            return false;
        }
        // One negative literal compiles as OP_NOT/OP_NOTI, for which
        // set_start_bits deliberately refuses a map.
        var original = _syntax.Classes[node.Operand];
        if (original.Kind == Pcre2ClassKind.Complement)
        {
            var atom = _syntax.Classes[original.Left];
            if (atom.Kind == Pcre2ClassKind.Intervals && atom.Intervals.Count == 1)
            {
                var range = _syntax.Intervals[atom.Intervals.Offset];
                if (range.First == range.Last && _syntax.Units[at] == '[' && LiteralClassTokens(at, NativeAt(node.Source.Offset + node.Source.Length)))
                {
                    return false;
                }
            }
            if ((_classSummary[root] & 2) != 0)
            {
                var anyLow = false;
                for (var cp = 0; cp < 256; cp++)
                {
                    if (!ClassContains(root, cp))
                    { anyLow = true; break; }
                }

                if (!anyLow)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
