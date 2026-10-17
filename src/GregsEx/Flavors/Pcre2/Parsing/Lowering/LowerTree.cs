#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal readonly record struct CopyFrame(int Old, int Cursor, int PartsBegin, int ParentDefinition, int Definition, bool StripAtomic);
internal readonly record struct EnumerationFrame(int Node, int Cursor, int Remaining);
internal ref partial struct LowerReader
{
    private void TransformTree()
    {
        var n = _syntax.Nodes.Length;
        LowerFailure.Reserve(ref _budget, checked(160L + 12L * n + 4L * _syntax.Groups.Length), _source);
        _beforeSites = new int[n];
        _afterSites = new int[n];
        Array.Fill(_beforeSites, -1);
        Array.Fill(_afterSites, -1);
        _definitionMap = new int[_syntax.Groups.Length];
        Array.Fill(_definitionMap, -1);
        foreach (var site in _syntax.Callouts)
        {
            _sourceSites.Add(site, ref _budget, _source);
        }
        // Assertion condition headers have an extra source compiler callout;
        // the syntax representation intentionally preserves the raw token site.
        for (var id = 0; id < n; id++)
        {
            var node = _syntax.Nodes[id];
            if (node.Kind != Pcre2NodeKind.Conditional || _syntax.Conditions[node.Operand].Kind != Pcre2ConditionKind.Assertion)
            {
                continue;
            }

            var at = NativeAt(node.Source.Offset);
            for (var site = 0; site < _syntax.Callouts.Length; site++)
            {
                Charge();
                var value = _sourceSites[site];
                if (!value.Automatic || value.PatternPosition != at)
                {
                    continue;
                }

                _sourceSites[site] = value with { NextItemLength = 2 };
                if (value.NextItemLength > 2)
                {
                    _sourceSites.Add(value with { PatternPosition = at + 2, NextItemLength = value.NextItemLength - 2 }, ref _budget, _source);
                }

                break;
            }
        }
        LowerFailure.Reserve(ref _budget, checked(32L + 4L * _sourceSites.Count), _source);
        _nextSites = new int[_sourceSites.Count];
        Array.Fill(_nextSites, -1);
        AssignAutomaticSites();
        PushCopy(_syntax.Root, -1, false);
        while (_copyStack.Count > 0)
        {
            Charge();
            var frame = _copyStack.Pop();
            var old = _syntax.Nodes[frame.Old];
            var children = OldChildren(frame.Old);
            var expanded = old.Kind == Pcre2NodeKind.Repeat && IsGroup(children[0]);
            var count = expanded ? Math.Max(1, old.Max < 0 ? old.Min : old.Max) : children.Length;
            if (frame.Cursor < count)
            {
                _copyStack.Add(frame with { Cursor = frame.Cursor + 1 }, ref _budget, _source);
                var child = expanded ? children[0] : children[frame.Cursor];
                PushCopy(child, frame.Definition >= 0 ? frame.Definition : frame.ParentDefinition,
                    expanded && old.Max < 0 && frame.Cursor == count - 1 && old.Quantifier == Pcre2Quantifier.Possessive && _syntax.Nodes[child].Kind == Pcre2NodeKind.Atomic);
                continue;
            }
            int built;
            if (expanded)
            {
                built = ExpandedRepeat(frame, old, count);
            }
            else
            {
                var begin = _children.Count;
                for (var i = frame.PartsBegin; i < _copyParts.Count; i++)
                {
                    Add(_children, _copyParts[i], 4);
                }

                var node = old with { Children = new(begin, children.Length) };
                if (frame.StripAtomic)
                {
                    node = node with { Kind = Pcre2NodeKind.Sequence, Operand = 0 };
                }

                if (node.Kind == Pcre2NodeKind.Capture)
                {
                    node = node with { Operand = frame.Definition };
                }

                if (node.Kind == Pcre2NodeKind.Conditional)
                {
                    var condition = _syntax.Conditions[node.Operand];
                    if (condition.AssertionNode >= 0)
                    {
                        condition = condition with { AssertionNode = _copyParts[frame.PartsBegin] };
                    }

                    var index = _conditions.Count;
                    Add(_conditions, condition, 32);
                    node = node with { Operand = index };
                }
                if (node.Kind == Pcre2NodeKind.Class)
                {
                    node = node with { Operand = _nodeClass[frame.Old], Flags = node.Flags & ~Pcre2CompileFlags.Caseless };
                    if (TryClassLiteral(frame.Old, out var character, out var caseless))
                    {
                        node = node with { Kind = Pcre2NodeKind.Literal, Operand = character, Flags = caseless ? node.Flags | Pcre2CompileFlags.Caseless : node.Flags };
                    }

                    if (node.Kind == Pcre2NodeKind.Class && IsAllAnyClass(node.Operand))
                    {
                        node = node with { Kind = Pcre2NodeKind.Dot, Operand = 0, Flags = node.Flags | Pcre2CompileFlags.Dotall };
                    }
                }
                else if (node.Kind == Pcre2NodeKind.CodeUnit && !_syntax.Profile.Utf)
                {
                    node = node with { Kind = Pcre2NodeKind.Dot, Flags = node.Flags | Pcre2CompileFlags.Dotall };
                }

                built = AddNode(node, frame.Old);
                if (frame.Definition >= 0)
                {
                    _groups[frame.Definition] = _groups[frame.Definition] with { EntryNode = built };
                }
            }
            while (_copyParts.Count > frame.PartsBegin)
            {
                _copyParts.Pop();
            }

            built = WrapSites(frame.Old, built);
            _copyParts.Add(built, ref _budget, _source);
        }
        for (var i = 0; i < _references.Count; i++)
        {
            var reference = _references[i];
            if (reference.TargetDefinition >= 0)
            {
                _references[i] = reference with { TargetDefinition = _definitionMap[reference.TargetDefinition] };
            }
        }
        RebuildLookbehinds();
        EnumerateSites();
        AutoPossess();
    }
    private readonly bool IsGroup(int id) => _syntax.Nodes[id].Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Sequence or
        Pcre2NodeKind.Atomic or Pcre2NodeKind.Assertion or Pcre2NodeKind.Conditional or Pcre2NodeKind.ScanSubstring or
        Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Define;
    private void PushCopy(int old, int parent, bool stripAtomic)
    {
        var definition = -1;
        var node = _syntax.Nodes[old];
        if (node.Kind == Pcre2NodeKind.Capture)
        {
            definition = _groups.Count;
            Add(_groups, _syntax.Groups[node.Operand] with { ParentDefinition = parent, EntryNode = -1 }, 16);
            if (_definitionMap[node.Operand] < 0)
            {
                _definitionMap[node.Operand] = definition;
            }
        }
        if (_copyStack.Count >= _budget.Limits.MaxNestingDepth)
        {
            Limit(RegexDiagnosticCode.NestingLimitExceeded);
        }

        _copyStack.Add(new(old, 0, _copyParts.Count, parent, definition, stripAtomic), ref _budget, _source);
    }
    private int ExpandedRepeat(CopyFrame frame, Pcre2Node old, int count)
    {
        // Required copies are followed by a nested optional suffix, matching
        // BRAZERO's skip of all remaining copies. Unlimited groups have one
        // residual loop after their required physical copies.
        int optional = -1, required = old.Min;
        var quantifier = old.Quantifier == Pcre2Quantifier.Possessive ? Pcre2Quantifier.Greedy : old.Quantifier;
        if (old.Max == 0)
        {
            return Unary(old with { Quantifier = quantifier }, _copyParts[frame.PartsBegin], frame.Old);
        }

        if (old.Max < 0)
        {
            required = Math.Max(0, count - 1);
            optional = Unary(old with { Min = old.Min == 0 ? 0 : 1, Max = -1, Quantifier = old.Quantifier }, _copyParts[frame.PartsBegin + count - 1], frame.Old);
        }
        else
        {
            for (var i = count - 1; i >= old.Min; i--)
            {
                var child = _copyParts[frame.PartsBegin + i];
                if (optional >= 0)
                {
                    child = Pair(child, optional, old, frame.Old);
                }

                optional = Unary(old with { Min = 0, Max = 1, Quantifier = quantifier }, child, frame.Old);
            }
        }

        var begin = _children.Count;
        for (var i = 0; i < required; i++)
        {
            Add(_children, _copyParts[frame.PartsBegin + i], 4);
        }

        if (optional >= 0)
        {
            Add(_children, optional, 4);
        }

        var result = AddNode(old with
        {
            Kind = Pcre2NodeKind.Sequence,
            Operand = 0,
            Min = 0,
            Max = 0,
            Quantifier = Pcre2Quantifier.Greedy,
            Children = new(begin, _children.Count - begin)
        }, frame.Old);
        if (old.Quantifier == Pcre2Quantifier.Possessive)
        {
            result = Unary(old with { Kind = Pcre2NodeKind.Atomic, Operand = 0, Min = 0, Max = 0, Quantifier = Pcre2Quantifier.Greedy }, result, frame.Old);
        }

        return result;
    }
    private int Unary(Pcre2Node node, int child, int origin)
    { var begin = _children.Count; Add(_children, child, 4); return AddNode(node with { Children = new(begin, 1) }, origin); }
    private int Pair(int first, int second, Pcre2Node owner, int origin)
    {
        var begin = _children.Count;
        Add(_children, first, 4);
        Add(_children, second, 4);
        return AddNode(owner with
        {
            Kind = Pcre2NodeKind.Sequence,
            Operand = 0,
            Min = 0,
            Max = 0,
            Quantifier = Pcre2Quantifier.Greedy,
            Children = new(begin, 2)
        }, origin);
    }
    private int WrapSites(int oldId, int copied)
    {
        if (_beforeSites[oldId] < 0 && _afterSites[oldId] < 0)
        {
            return copied;
        }

        var old = _syntax.Nodes[oldId];
        int start = _children.Count, lo = old.Source.Offset, hi = lo + old.Source.Length;
        for (var site = _beforeSites[oldId]; site >= 0; site = _nextSites[site])
        { var call = SiteNode(site, old); Add(_children, call, 4); lo = Math.Min(lo, _nodes[call].Source.Offset); hi = Math.Max(hi, _nodes[call].Source.Offset); }
        Add(_children, copied, 4);
        for (var site = _afterSites[oldId]; site >= 0; site = _nextSites[site])
        { var call = SiteNode(site, old); Add(_children, call, 4); lo = Math.Min(lo, _nodes[call].Source.Offset); hi = Math.Max(hi, _nodes[call].Source.Offset); }
        return AddNode(new(Pcre2NodeKind.Sequence, new(start, _children.Count - start), 0, 0, 0, Pcre2Quantifier.Greedy,
            old.Flags, old.ExtraFlags, new(lo, hi - lo, old.Source.Unit)), oldId);
    }
    private void RebuildLookbehinds()
    {
        foreach (var branch in _behind.Span)
        {
            _sourceBehind.Add(branch, ref _budget, _source);
        }

        _planned -= 16L * _behind.Count;
        while (_behind.Count > 0)
        {
            _behind.Pop();
        }

        for (var id = 0; id < _nodes.Count; id++)
        {
            var node = _nodes[id];
            if (node.Kind != Pcre2NodeKind.Assertion || !IsBehind(_assertions[node.Operand].Kind))
            {
                continue;
            }

            var child = _children[node.Children.Offset];
            var body = _nodes[child];
            var ordinal = 0;
            foreach (var source in _sourceBehind.Span)
            {
                Charge();
                if (source.AssertionNode != _origins[id])
                {
                    continue;
                }

                var branch = body.Kind == Pcre2NodeKind.Alternative ? _children[body.Children.Offset + ordinal++] : child;
                Add(_behind, new(id, branch, source.MaximumLength), 16);
            }
        }
    }
    private int AddNode(Pcre2Node node, int origin = -1)
    {
        if ((long)_nodes.Count + _classes.Count >= _budget.Limits.MaxInstructions)
        {
            Limit(RegexDiagnosticCode.ProgramLimitExceeded);
        }

        var id = _nodes.Count;
        Add(_nodes, node, 64);
        _origins.Add(origin, ref _budget, _source);
        return id;
    }
    private int SiteNode(int site, Pcre2Node owner)
    {
        var offset = _syntax.SourceMap[_sourceSites[site].PatternPosition];
        return AddNode(new(Pcre2NodeKind.Callout, default, site, 0, 0, Pcre2Quantifier.Greedy,
            owner.Flags, owner.ExtraFlags, new(offset, 0, _syntax.SourceUnit)));
    }
    private void AssignAutomaticSites()
    {
        for (var site = 0; site < _sourceSites.Count; site++)
        {
            Charge();
            var metadata = _sourceSites[site];
            if (!metadata.Automatic)
            {
                continue;
            }

            int pos = metadata.PatternPosition, owner = -1;
            var after = false;
            if (pos == _syntax.NativePatternLength)
            {
                // OP_END follows only the final top-level branch; earlier
                // alternatives finish at their own OP_ALT callout instead.
                owner = _syntax.Root;
                if (_syntax.Nodes[owner].Kind == Pcre2NodeKind.Alternative)
                {
                    owner = OldChildren(owner)[^1];
                }

                after = true;
            }
            else if (_syntax.Units[pos] == '|')
            {
                for (var id = 0; id < _syntax.Nodes.Length && owner < 0; id++)
                {
                    Charge();
                    if (_syntax.Nodes[id].Kind is not (Pcre2NodeKind.Alternative or Pcre2NodeKind.Conditional))
                    {
                        continue;
                    }

                    var branches = OldChildren(id);
                    for (var ordinal = 1; ordinal < branches.Length; ordinal++)
                    {
                        if (NativeAt(_syntax.Nodes[branches[ordinal]].Source.Offset) == pos + 1)
                        { owner = branches[ordinal - 1]; after = true; break; }
                    }
                }
            }
            else if (_syntax.Units[pos] == ')')
            {
                for (var id = 0; id < _syntax.Nodes.Length && owner < 0; id++)
                {
                    Charge();
                    var node = _syntax.Nodes[id];
                    if (node.Kind is not (Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.Assertion or Pcre2NodeKind.Sequence or
                        Pcre2NodeKind.ScanSubstring or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Define or Pcre2NodeKind.Conditional))
                    {
                        continue;
                    }

                    int start = NativeAt(node.Source.Offset), end = NativeAt(node.Source.Offset + node.Source.Length);
                    if (start >= _syntax.NativePatternLength || _syntax.Units[start] != '(' || end != pos + 1 || node.Children.Count == 0)
                    {
                        continue;
                    }

                    var children = OldChildren(id);
                    owner = children[^1];
                    if (_syntax.Nodes[owner].Kind == Pcre2NodeKind.Alternative)
                    {
                        owner = OldChildren(owner)[^1];
                    }

                    after = true;
                }
            }
            if (owner < 0)
            {
                for (var id = 0; id < _syntax.Nodes.Length; id++)
                {
                    Charge();
                    var node = _syntax.Nodes[id];
                    if (NativeAt(node.Source.Offset) != pos || node.Kind is Pcre2NodeKind.Alternative or Pcre2NodeKind.Empty or Pcre2NodeKind.Callout)
                    {
                        continue;
                    }

                    if (node.Kind == Pcre2NodeKind.Sequence && (pos >= _syntax.NativePatternLength || _syntax.Units[pos] != '('))
                    {
                        continue;
                    }

                    if (owner < 0 || id > owner)
                    {
                        owner = id;
                    }
                }
            }
            if (owner >= 0)
            {
                // A conditional predicate must stay an Assertion structural
                // child. Its header callout belongs to the conditional entry.
                var parent = _parents[owner];
                if (!after && _syntax.Nodes[owner].Kind == Pcre2NodeKind.Assertion && parent >= 0 &&
                    _syntax.Nodes[parent].Kind == Pcre2NodeKind.Conditional)
                {
                    owner = parent;
                }

                LinkSite(after ? _afterSites : _beforeSites, owner, site);
            }
            else
            {
                Invalid();
            }
        }
    }
    private void LinkSite(int[] heads, int owner, int site)
    {
        if (heads[owner] < 0)
        { heads[owner] = site; return; }
        var tail = heads[owner];
        while (_nextSites[tail] >= 0)
        { Charge(); tail = _nextSites[tail]; }
        _nextSites[tail] = site;
    }
    private void EnumerateSites()
    {
        if (_sourceSites.Count == 0)
        {
            return;
        }

        _enumerationStack.Add(new(_nodes.Count - 1, 0, 0), ref _budget, _source);
        while (_enumerationStack.Count > 0)
        {
            Charge();
            var frame = _enumerationStack.Pop();
            var node = _nodes[frame.Node];
            if (node.Kind == Pcre2NodeKind.Callout)
            {
                var site = _callouts.Count;
                Add(_callouts, _sourceSites[node.Operand], 40);
                _nodes[frame.Node] = node with { Operand = site };
                continue;
            }
            if (frame.Cursor < node.Children.Count)
            {
                _enumerationStack.Add(frame with { Cursor = frame.Cursor + 1 }, ref _budget, _source);
                _enumerationStack.Add(new(_children[node.Children.Offset + frame.Cursor], 0, 0), ref _budget, _source);
            }
        }
    }
}
