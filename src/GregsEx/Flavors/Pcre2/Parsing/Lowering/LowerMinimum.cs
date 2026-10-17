#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal ref partial struct LowerReader
{
    private int StudyMinimum(int root)
    {
        LowerFailure.Reserve(ref _budget, checked(128L + 7L * _syntax.Nodes.Length), _source);
        _minimum = new int[_syntax.Nodes.Length];
        _minimumState = new byte[_syntax.Nodes.Length];
        _minimumRecursive = new byte[_syntax.Nodes.Length];
        _minimumRefActive = new byte[_syntax.Nodes.Length];
        _minimumStack.Add(new() { Node = root }, ref _budget, _source);
        var calls = 1;
        while (_minimumStack.Count > 0)
        {
            Charge();
            var top = _minimumStack.Count - 1;
            var frame = _minimumStack[top];
            var id = frame.Node;
            var node = _syntax.Nodes[id];
            var children = OldChildren(id);
            _minimumState[id] = 1;
            var immediate = node.Kind switch
            {
                Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot or Pcre2NodeKind.Newline or Pcre2NodeKind.Grapheme => 1,
                Pcre2NodeKind.CodeUnit => _syntax.Profile.Utf ? -1 : 1,
                _ => 0
            };
            var reference = node.Kind is Pcre2NodeKind.Backreference or Pcre2NodeKind.Subroutine;
            var container = node.Kind is Pcre2NodeKind.Sequence or Pcre2NodeKind.Alternative or Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or
                Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Repeat or Pcre2NodeKind.Conditional;
            var r = reference ? _references[node.Operand] : default;
            if (reference)
            {
                if (node.Kind == Pcre2NodeKind.Backreference && ((_syntax.Profile.Flags & Pcre2CompileFlags.MatchUnsetBackref) != 0 ||
                    _duplicateCaptures && r.Candidates.Count > 1 || r.Candidates.Count == 1 && _definitionCounts[_numbers[r.Candidates.Offset]] > 1))
                {
                    reference = false;
                }
                else
                {
                    container = true;
                }
            }
            if (node.Kind == Pcre2NodeKind.Repeat && (node.Min == 0 || node.Max < 0 && !_facts[children[0]].Matched && _syntax.Nodes[children[0]].Kind != Pcre2NodeKind.Backreference))
            {
                container = false;
            }

            var begin = node.Kind == Pcre2NodeKind.Conditional && _syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? 1 : 0;
            if (node.Kind == Pcre2NodeKind.Conditional && children.Length - begin == 1)
            {
                container = false;
            }

            if (node.Kind == Pcre2NodeKind.Repeat && node.Min > 0 && _syntax.Nodes[children[0]].Kind == Pcre2NodeKind.CodeUnit)
            { container = false; immediate = node.Min; }
            if (!container)
            { FinishMinimum(id, immediate); continue; }
            var count = reference ? r.Kind == Pcre2ReferenceKind.WholePattern ? 1 : r.Candidates.Count : children.Length;
            if (frame.Cursor < begin && !reference)
            { frame.Cursor = begin; _minimumStack[top] = frame; }
            if (frame.Cursor < count)
            {
                var child = reference ? r.Kind == Pcre2ReferenceKind.WholePattern ? _syntax.Root :
                    _syntax.Groups[_firstDefinitions[_numbers[r.Candidates.Offset + frame.Cursor]]].EntryNode : children[frame.Cursor];
                int length;
                var cycle = reference && (_minimumRefActive[child] != 0 || node.Source.Offset >= _syntax.Nodes[child].Source.Offset &&
                    node.Source.Offset < _syntax.Nodes[child].Source.Offset + _syntax.Nodes[child].Source.Length);
                if (cycle)
                { length = 0; _minimumRecursive[id] = 1; }
                else if (_minimumState[child] != 2)
                {
                    var cn = _syntax.Nodes[child];
                    var call = child == _syntax.Root || cn.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Conditional ||
                        cn.Kind == Pcre2NodeKind.Sequence && cn.Children.Count == 1 && NativeAt(cn.Source.Offset) < _syntax.NativePatternLength && _syntax.Units[NativeAt(cn.Source.Offset)] == '(';
                    if (call && calls++ > 1000)
                    { FinishMinimum(id, -1); continue; }
                    if (reference)
                    {
                        _minimumRefActive[child] = 1;
                    }

                    _minimumStack.Add(new() { Node = child, Mode = reference ? 1 : 0 }, ref _budget, _source);
                    continue;
                }
                else
                {
                    length = _minimum[child];
                }

                if (length < 0)
                { FinishMinimum(id, -1); continue; }
                var alternative = node.Kind is Pcre2NodeKind.Alternative or Pcre2NodeKind.Conditional || reference;
                var recursive = _minimumRecursive[child] != 0 || cycle;
                if (alternative)
                {
                    if (frame.Cursor == (reference ? 0 : begin) || !recursive)
                    {
                        frame.Minimum = frame.Cursor == (reference ? 0 : begin) ? length : Math.Min(frame.Minimum, length);
                    }
                }
                else
                {
                    frame.Minimum = Math.Min(65535, frame.Minimum + length);
                }

                if (recursive)
                {
                    _minimumRecursive[id] = 1;
                }

                frame.Cursor++;
                _minimumStack[top] = frame;
                continue;
            }
            var result = frame.Minimum;
            if (node.Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Alternative)
            {
                _minimumRecursive[id] = 0;
            }

            if (node.Kind == Pcre2NodeKind.Repeat)
            {
                result = (int)Math.Min(65535, node.Min * (long)result);
            }

            FinishMinimum(id, result);
        }
        return _minimum[root];
    }
    private readonly void FinishMinimum(int id, int minimum)
    {
        _minimum[id] = minimum;
        _minimumState[id] = 2;
        var frame = _minimumStack.Pop();
        if (frame.Mode == 1)
        {
            _minimumRefActive[id] = 0;
        }
    }
}
