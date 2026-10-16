using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

internal ref partial struct SyntaxReader
{
    // R/DEFINE are initially lexical names because later definitions may
    // override their keyword meanings. Remove those temporary symbols once
    // resolved, so lowering receives only real definitions or referenced names.
    private void CompactMetadata()
    {
        var bytes = checked(96L + 4L * (_conditions.Count + (long)_references.Count + _names.Count));
        SyntaxFailure.Reserve(ref _budget, bytes, Range(0, _input.Length));
        Charge(_conditions.Count + (long)_references.Count + _names.Count);
        var conditions = new int[_conditions.Count];
        Array.Fill(conditions, -1);
        var references = new int[_references.Count];
        Array.Fill(references, -1);
        var names = new int[_names.Count];
        Array.Fill(names, -1);
        try
        {
            foreach (var node in _nodes.Span)
            {
                Charge();
                if (node.Kind == Pcre2NodeKind.Conditional)
                {
                    conditions[node.Operand] = 1;
                }
            }
            var count = 0;
            for (var i = 0; i < conditions.Length; i++)
            {
                Charge();
                if (conditions[i] < 0)
                {
                    continue;
                }

                conditions[i] = count;
                _conditions[count++] = _conditions[i];
            }
            _conditions.Truncate(count);
            for (var i = 0; i < _nodes.Count; i++)
            {
                Charge();
                var node = _nodes[i];
                if (node.Kind == Pcre2NodeKind.Conditional)
                {
                    _nodes[i] = node with { Operand = conditions[node.Operand] };
                }
                else if (node.Kind is Pcre2NodeKind.Backreference or Pcre2NodeKind.Subroutine)
                {
                    references[node.Operand] = 1;
                }
            }
            foreach (var condition in _conditions.Span)
            {
                Charge();
                if (condition.ReferenceIndex >= 0)
                {
                    references[condition.ReferenceIndex] = 1;
                }
            }
            foreach (var scan in _scans.Span)
            {
                for (var i = scan.Offset; i < scan.Offset + scan.Count; i++)
                { Charge(); references[i] = 1; }
            }

            count = 0;
            for (var i = 0; i < references.Length; i++)
            {
                Charge();
                if (references[i] < 0)
                {
                    continue;
                }

                references[i] = count;
                var reference = _references[i];
                _references[count++] = reference;
                if (reference.NameIndex >= 0)
                {
                    names[reference.NameIndex] = 1;
                }
            }
            _references.Truncate(count);
            for (var i = 0; i < _nodes.Count; i++)
            {
                Charge();
                var node = _nodes[i];
                if (node.Kind is Pcre2NodeKind.Backreference or Pcre2NodeKind.Subroutine)
                {
                    _nodes[i] = node with { Operand = references[node.Operand] };
                }
            }
            for (var i = 0; i < _conditions.Count; i++)
            {
                Charge();
                var c = _conditions[i];
                if (c.ReferenceIndex >= 0)
                {
                    _conditions[i] = c with { ReferenceIndex = references[c.ReferenceIndex] };
                }
            }
            for (var i = 0; i < _scans.Count; i++)
            { Charge(); var s = _scans[i]; _scans[i] = s with { Offset = references[s.Offset] }; }
            count = 0;
            for (var i = 0; i < names.Length; i++)
            {
                Charge();
                if (names[i] < 0 && _names[i].Numbers.Count == 0)
                {
                    continue;
                }

                names[i] = count;
                _names[count++] = _names[i];
            }
            _names.Truncate(count);
            for (var i = 0; i < _references.Count; i++)
            {
                Charge();
                var r = _references[i];
                if (r.NameIndex >= 0)
                {
                    _references[i] = r with { NameIndex = names[r.NameIndex] };
                }
            }
        }
        finally { _budget.ReleaseBytes(bytes); }
    }
}
