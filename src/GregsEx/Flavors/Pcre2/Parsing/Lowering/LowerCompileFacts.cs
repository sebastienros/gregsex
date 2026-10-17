#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal ref partial struct LowerReader
{
    private void SourceVaryOrder()
    {
        LowerFailure.Reserve(ref _budget, 32L + _syntax.Nodes.Length, _source);
        _varyBefore = new bool[_syntax.Nodes.Length];
        var vary = false;
        _enumerationStack.Add(new(_syntax.Root, 0, 0), ref _budget, _source);
        while (_enumerationStack.Count > 0)
        {
            Charge();
            var frame = _enumerationStack.Pop();
            var node = _syntax.Nodes[frame.Node];
            var children = OldChildren(frame.Node);
            if (frame.Cursor == 0)
            {
                _varyBefore[frame.Node] = vary;
            }

            if (frame.Cursor < children.Length)
            {
                _enumerationStack.Add(frame with { Cursor = frame.Cursor + 1 }, ref _budget, _source);
                _enumerationStack.Add(new(children[frame.Cursor], 0, 0), ref _budget, _source);
                continue;
            }
            if (node.Kind == Pcre2NodeKind.Repeat && node.Min != node.Max)
            {
                vary = true;
            }
        }
    }
    private CompileFacts SequenceFact(ReadOnlySpan<int> children)
    {
        var result = new CompileFacts { First = -2, Required = -1 };
        var zero = result;
        foreach (var child in children)
        {
            AppendSource(ref result, ref zero, child);
        }

        return result;
    }
    private void AppendSource(ref CompileFacts current, ref CompileFacts zero, int id)
    {
        var node = _syntax.Nodes[id];
        var fact = _facts[id];
        if (node.Kind == Pcre2NodeKind.Repeat)
        {
            var atom = OldChildren(id)[0];
            var basis = _facts[atom];
            var matched = current.Matched;
            var previousFirst = current.First;
            var previousCase = current.FirstCaseless;
            if (_syntax.Nodes[atom].Kind == Pcre2NodeKind.Verb && _syntax.Controls[_syntax.Nodes[atom].Operand].Kind == Pcre2VerbKind.Accept)
            {
                zero = current;
            }

            AppendSource(ref current, ref zero, atom);
            if (node.Min == 0)
            {
                current.First = zero.First;
                current.FirstCaseless = zero.FirstCaseless;
                if (_syntax.Nodes[atom].Kind == Pcre2NodeKind.Class && IsNeverClass(_nodeClass[atom]))
                { current.First = previousFirst; current.FirstCaseless = previousCase; }
                current.Required = zero.Required;
                current.RequiredCaseless = zero.RequiredCaseless;
                current.RequiredVariable = zero.RequiredVariable;
                current.Matched = matched;
            }
            else if (node.Min > 1 && basis.First >= 0 && basis.Required < 0)
            {
                current.Required = basis.First;
                current.RequiredCaseless = basis.FirstCaseless;
                // A group's replicated first unit retains its first flags.
                current.RequiredVariable = _syntax.Nodes[atom].Kind == Pcre2NodeKind.Literal && _varyBefore[id];
            }
            return;
        }
        var reference = node.Kind == Pcre2NodeKind.Backreference;
        var subroutine = node.Kind == Pcre2NodeKind.Subroutine;
        var group = node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Conditional or Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring or Pcre2NodeKind.Define ||
            node.Kind == Pcre2NodeKind.Sequence && NativeAt(node.Source.Offset) < _syntax.NativePatternLength && _syntax.Units[NativeAt(node.Source.Offset)] == '(';
        if (node.Kind == Pcre2NodeKind.Start && (node.Flags & Pcre2CompileFlags.Multiline) != 0)
        {
            zero.First = -1;
        }

        if (reference)
        {
            if (current.First == -2 && _references[node.Operand].Candidates.Count == 1)
            {
                zero.First = -1;
            }
        }
        else if (subroutine)
        { zero.First = current.First == -2 ? -1 : current.First; zero.FirstCaseless = current.FirstCaseless; }
        else if (node.Kind == Pcre2NodeKind.Literal && fact.First < 0 && !fact.FirstCaseless)
        {
            if (current.First == -2)
            {
                zero.First = -1;
            }
        }
        else if (group || node.Kind is Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot or Pcre2NodeKind.CodeUnit or Pcre2NodeKind.Newline or Pcre2NodeKind.Grapheme)
        {
            zero = current;
            if (current.First == -2 && fact.First != -2 && node.Kind is not (Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring))
            {
                zero.First = -1;
            }
        }
        var hadFirst = current.First != -2;
        if (!current.Stopped && !hadFirst && fact.First != -2)
        { current.First = fact.First; current.FirstCaseless = fact.FirstCaseless; }
        if (fact.Required >= 0)
        { current.Required = fact.Required; current.RequiredCaseless = fact.RequiredCaseless; current.RequiredVariable = fact.RequiredVariable; }
        else if (hadFirst && fact.First >= 0)
        { current.Required = fact.First; current.RequiredCaseless = fact.FirstCaseless; current.RequiredVariable = _varyBefore[id]; }
        current.Matched |= !current.Stopped && fact.Matched;
        current.Stopped |= fact.Stopped && !group;
        current.Minimum = Math.Min(65535, current.Minimum + fact.Minimum);
    }
}
