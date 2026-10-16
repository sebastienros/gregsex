using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal ref partial struct LowerReader
{
    private void ResolveReferences()
    {
        foreach (var number in _syntax.Numbers)
        {
            Add(_numbers, number, 4);
        }

        for (var i = 0; i < _syntax.References.Length; i++)
        {
            Charge();
            var reference = _syntax.References[i];
            if (reference.Kind == Pcre2ReferenceKind.WholePattern)
            { Add(_references, reference, 32); continue; }
            Pcre2Slice candidates;
            if (reference.Kind == Pcre2ReferenceKind.Named)
            {
                candidates = _syntax.Names[reference.NameIndex].Numbers;
                if (candidates.Count == 0)
                {
                    Error(115, ReferenceOffset(i));
                }
            }
            else if (reference.Kind == Pcre2ReferenceKind.Relative)
            {
                // Number remains signed source data. Candidates already holds
                // the lexical absolute anchor, including branch-reset scope.
                candidates = reference.Candidates;
                if (candidates.Count != 1)
                {
                    Invalid();
                }
            }
            else
            {
                candidates = new(_numbers.Count, 1);
                Add(_numbers, reference.Number, 4);
            }
            var number = _numbers[candidates.Offset];
            if ((uint)number >= (uint)_firstDefinitions.Length || _firstDefinitions[number] < 0)
            {
                Error(115, ReferenceOffset(i));
            }

            Add(_references, reference with { Candidates = candidates, TargetDefinition = _firstDefinitions[number] }, 32);
        }
    }
    private void Invalid() => throw new LowerFailure(new(new(RegexDiagnosticCode.InvalidProgram, 0, _syntax.SourceLength, _syntax.SourceUnit), Pcre2DiagnosticKind.InvalidProgram));
    private int ReferenceOffset(int referenceIndex)
    {
        for (var id = 0; id < _syntax.Nodes.Length; id++)
        {
            Charge();
            var node = _syntax.Nodes[id];
            var uses = node.Kind is Pcre2NodeKind.Backreference or Pcre2NodeKind.Subroutine && node.Operand == referenceIndex;
            if (node.Kind == Pcre2NodeKind.Conditional)
            {
                uses = _syntax.Conditions[node.Operand].ReferenceIndex == referenceIndex;
            }

            if (node.Kind == Pcre2NodeKind.ScanSubstring)
            { var slice = _syntax.ScanReferences[node.Operand]; uses = referenceIndex >= slice.Offset && referenceIndex < slice.Offset + slice.Count; }
            if (!uses)
            {
                continue;
            }

            var start = NativeAt(node.Source.Offset);
            var units = _syntax.Units;
            if (_syntax.References[referenceIndex].Kind != Pcre2ReferenceKind.Named)
            {
                return start;
            }

            var p = start;
            if (units[p] == '\\')
            {
                p += 2;
                if (p < units.Length && units[p] is '<' or '{' or '\'')
                {
                    p++;
                }
            }
            else if (node.Kind == Pcre2NodeKind.ScanSubstring)
            { while (p < _syntax.NativePatternLength && units[p] != '<' && units[p] != '\'') { Charge(); p++; } p++; }
            else
            {
                p += 3;
                if (start + 2 < units.Length && units[start + 2] == 'P')
                {
                    return start + 4;
                }

                if (p < units.Length && units[p] is 'P' or 'R')
                {
                    p += 2;
                }
                else if (p < units.Length && units[p] is '<' or '\'')
                {
                    p++;
                }
            }
            return p;
        }
        return _syntax.NativePatternLength;
    }
}
