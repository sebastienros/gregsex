using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal struct LengthFrame
{
    internal int Node, Cursor, Minimum, Maximum, Mode, Waiting;
}
internal readonly record struct StaticLength(int Minimum, int Maximum, int Error = 0);

internal ref partial struct LowerReader
{

    private void ValidateAssertions()
    {
        LowerFailure.Reserve(ref _budget, checked(64L + 18L * _syntax.Nodes.Length), _source);
        _lengths = new StaticLength[_syntax.Nodes.Length];
        _lengthState = new byte[_syntax.Nodes.Length];
        _lengthMode = new byte[_syntax.Nodes.Length];
        foreach (var count in _definitionCounts)
        { Charge(); _duplicateCaptures |= count > 1; }
        foreach (var node in _syntax.Nodes)
        {
            Charge();
            var at = NativeAt(node.Source.Offset);
            if (node.Kind == Pcre2NodeKind.Sequence && at + 2 < _syntax.NativePatternLength &&
                _syntax.Units[at] == '(' && _syntax.Units[at + 1] == '?' && _syntax.Units[at + 2] == '|')
            {
                _duplicateCaptures = true;
            }
        }
        foreach (var assertion in _syntax.Assertions)
        {
            Add(_assertions, assertion, 24);
        }

        for (var id = 0; id < _syntax.Nodes.Length; id++)
        {
            Charge();
            var node = _syntax.Nodes[id];
            if (node.Kind == Pcre2NodeKind.ResetStart && (node.ExtraFlags & Pcre2ExtraFlags.AllowLookaroundBsk) == 0)
            {
                for (var p = _parents[id]; p >= 0; p = _parents[p])
                {
                    Charge();
                    if (_syntax.Nodes[p].Kind == Pcre2NodeKind.Assertion)
                    {
                        Error(199, _syntax.NativePatternLength);
                    }
                }
            }

            if (node.Kind != Pcre2NodeKind.Assertion)
            {
                continue;
            }

            var assertion = _assertions[node.Operand];
            if (!IsBehind(assertion.Kind))
            {
                continue;
            }

            int child = OldChildren(id)[0], minimum = int.MaxValue, maximum = 0;
            var variable = false;
            var body = _syntax.Nodes[child];
            var count = body.Kind == Pcre2NodeKind.Alternative ? body.Children.Count : 1;
            for (var branch = 0; branch < count; branch++)
            {
                var b = body.Kind == Pcre2NodeKind.Alternative ? OldChildren(child)[branch] : child;
                var length = Length(b);
                if (length.Error != 0)
                {
                    Error(length.Error, NativeAt(node.Source.Offset));
                }

                if (length.Maximum < 0)
                {
                    Error(125, NativeAt(node.Source.Offset));
                }

                minimum = Math.Min(minimum, length.Minimum);
                maximum = Math.Max(maximum, length.Maximum);
                variable |= length.Minimum != length.Maximum;
                Add(_behind, new(id, b, length.Maximum), 16);
            }
            if (variable && maximum > _syntax.Profile.MaxVariableLookbehind)
            {
                Error(200, NativeAt(node.Source.Offset));
            }

            _assertions[node.Operand] = assertion with { MinimumLength = minimum, MaximumLength = maximum, VariableLength = variable };
        }
    }
    private static bool IsBehind(Pcre2AssertionKind kind) => kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind;
    private StaticLength Length(int root)
    {
        if (_lengthState[root] == 2 && _lengthMode[root] == 0 && !_duplicateCaptures)
        {
            return _lengths[root];
        }

        var calls = 1;
        _lengthStack.Add(new() { Node = root }, ref _budget, _source);
        while (_lengthStack.Count != 0)
        {
            Charge();
            var top = _lengthStack.Count - 1;
            var frame = _lengthStack[top];
            var id = frame.Node;
            var node = _syntax.Nodes[id];
            _lengthState[id] = 1;
            var assertion = node.Kind == Pcre2NodeKind.Assertion;
            var behind = assertion && IsBehind(_assertions[node.Operand].Kind);
            var zero = frame.Mode == 1;
            var immediate = zero ? new(0, 0) : node.Kind switch
            {
                Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot => new(1, 1),
                Pcre2NodeKind.CodeUnit => _syntax.Profile.Utf ? new(0, -1, 136) : new(1, 1),
                Pcre2NodeKind.Newline => new(1, 2),
                Pcre2NodeKind.Grapheme => new(0, -1),
                Pcre2NodeKind.Subroutine or Pcre2NodeKind.Backreference => ReferenceLength(id, node),
                _ => new(0, 0)
            };
            var reference = !zero && node.Kind is Pcre2NodeKind.Subroutine or Pcre2NodeKind.Backreference;
            var container = node.Kind is Pcre2NodeKind.Sequence or Pcre2NodeKind.Alternative or Pcre2NodeKind.Capture or
                Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Conditional or Pcre2NodeKind.Repeat or
                Pcre2NodeKind.Assertion or Pcre2NodeKind.ScanSubstring;
            if (reference && immediate.Maximum == -2)
            {
                container = true;
            }

            if (!container || reference && immediate.Maximum != -2)
            { FinishLength(id, immediate, frame.Mode); continue; }
            var children = OldChildren(id);
            var count = reference ? 1 : children.Length;
            var begin = node.Kind == Pcre2NodeKind.Conditional && _syntax.Conditions[node.Operand].Kind == Pcre2ConditionKind.Assertion ? 1 : 0;
            if (frame.Cursor < begin)
            { frame.Cursor = begin; _lengthStack[top] = frame; }
            if (frame.Cursor < count)
            {
                var child = reference ? _syntax.Groups[_references[node.Operand].TargetDefinition].EntryNode : children[frame.Cursor];
                var mode = behind ? 0 : zero || assertion || node.Kind == Pcre2NodeKind.ScanSubstring ? 1 : 0;
                if (_lengthState[child] == 1)
                { FinishLength(id, new(0, -1, behind ? 125 : 0), frame.Mode); continue; }
                var cached = _lengthState[child] == 2 && _lengthMode[child] == mode && mode == 0 && !_duplicateCaptures;
                if (!cached && frame.Waiting == 0)
                {
                    var group = _syntax.Nodes[child].Kind is Pcre2NodeKind.Capture or Pcre2NodeKind.Atomic or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Conditional;
                    if (_syntax.Nodes[child].Kind == Pcre2NodeKind.Sequence)
                    { var at = NativeAt(_syntax.Nodes[child].Source.Offset); group = at < _syntax.NativePatternLength && _syntax.Units[at] == '('; }
                    if (group && ++calls > 2000)
                    { FinishLength(id, new(0, -1, 135), frame.Mode); continue; }
                    frame.Waiting = 1;
                    _lengthStack[top] = frame;
                    _lengthStack.Add(new() { Node = child, Mode = mode }, ref _budget, _source);
                    continue;
                }
                frame.Waiting = 0;
                var length = _lengths[child];
                if (length.Error != 0 || length.Maximum < 0)
                { FinishLength(id, length.Error != 0 ? length : new(0, -1, behind ? 125 : 0), frame.Mode); continue; }
                var alternatives = node.Kind is Pcre2NodeKind.Alternative or Pcre2NodeKind.Conditional;
                if (alternatives)
                { frame.Minimum = frame.Cursor == begin ? length.Minimum : Math.Min(frame.Minimum, length.Minimum); frame.Maximum = Math.Max(frame.Maximum, length.Maximum); }
                else
                {
                    var maximum = frame.Maximum + (long)length.Maximum;
                    if (maximum > 65535)
                    { FinishLength(id, new(0, -1, 187), frame.Mode); continue; }
                    frame.Maximum = (int)maximum;
                    frame.Minimum += length.Minimum;
                }
                frame.Cursor++;
                if (node.Kind == Pcre2NodeKind.Sequence && Terminates(child))
                {
                    frame.Cursor = count;
                }

                _lengthStack[top] = frame;
                continue;
            }
            var result = new StaticLength(frame.Minimum, frame.Maximum);
            if (node.Kind == Pcre2NodeKind.Repeat && !zero)
            {
                var repeated = _syntax.Nodes[children[0]];
                var ignored = repeated.Kind == Pcre2NodeKind.ScanSubstring || repeated.Kind == Pcre2NodeKind.Assertion && !IsBehind(_assertions[repeated.Operand].Kind);
                if (ignored)
                {
                    result = new(0, 0);
                }
                else if (node.Max < 0)
                {
                    result = new(0, -1);
                }
                else if (node.Max * (long)result.Maximum > 65535)
                {
                    result = new(0, -1, 187);
                }
                else
                {
                    result = new(node.Min * result.Minimum, node.Max * result.Maximum);
                }
            }
            if (zero || assertion || node.Kind == Pcre2NodeKind.ScanSubstring)
            {
                result = new(0, 0);
            }

            FinishLength(id, result, frame.Mode);
        }
        return _lengths[root];
    }
    private readonly bool Terminates(int id) => _syntax.Nodes[id].Kind == Pcre2NodeKind.Verb &&
        _syntax.Controls[_syntax.Nodes[id].Operand].Kind is Pcre2VerbKind.Fail or Pcre2VerbKind.Accept;
    private readonly void FinishLength(int id, StaticLength result, int mode)
    { _lengths[id] = result; _lengthState[id] = 2; _lengthMode[id] = (byte)mode; _lengthStack.Pop(); }
    private StaticLength ReferenceLength(int id, Pcre2Node node)
    {
        var reference = _references[node.Operand];
        if (reference.Kind == Pcre2ReferenceKind.WholePattern)
        {
            return new(0, -1);
        }

        if (node.Kind == Pcre2NodeKind.Backreference && (_duplicateCaptures || reference.Candidates.Count != 1 ||
            (_syntax.Profile.Flags & Pcre2CompileFlags.MatchUnsetBackref) != 0))
        {
            return new(0, -1);
        }

        var definition = reference.TargetDefinition;
        for (var p = _parents[id]; p >= 0; p = _parents[p])
        {
            Charge();
            if (_syntax.Nodes[p].Kind == Pcre2NodeKind.Capture && _syntax.Nodes[p].Operand == definition)
            {
                return new(0, -1);
            }
        }
        return new(0, -2);
    }
}
