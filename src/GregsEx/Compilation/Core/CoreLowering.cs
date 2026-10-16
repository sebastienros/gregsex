using GregsEx.IR;
using GregsEx.Parsing.Core;

namespace GregsEx.Compilation.Core;

internal struct LowerFrame
{
    internal int Node, Next, Stage, Temporary, Cursor, Remaining, Phase, Loop;
}

// Continuation-based, backwards construction. Final reversal gives the frozen group-zero ABI.
internal ref struct CoreLowering
{
    private CoreBuffer<CoreInstruction> _code = new(32);
    private CoreBuffer<LowerFrame> _stack = new(40);
    private RegexDiagnostic _diagnostic;

    public CoreLowering() { }

    private int Emit(ref CoreParser parser, CoreOpcode opcode, SourceRange source, int x = 0, int y = 0, int operand = 0, int count = 0)
    {
        if (_code.Count >= parser.Budget.Limits.MaxInstructions)
        {
            _diagnostic = new(RegexDiagnosticCode.ProgramLimitExceeded, source.Offset, source.Length, source.Unit);
            return -1;
        }
        if (!parser.Budget.TryCharge(1, source, out _diagnostic) ||
            !_code.Ensure(_code.Count + 1, ref parser.Budget, source, out _diagnostic))
        {
            return -1;
        }

        _code.Items[_code.Count] = new(opcode, x, y, operand, count, source);
        return _code.Count++;
    }
    private bool Push(ref CoreParser parser, int node, int next)
    {
        var source = parser.Nodes.Items[node].Source;
        if (!parser.Budget.TryCharge(1, source, out _diagnostic) ||
            !_stack.Ensure(_stack.Count + 1, ref parser.Budget, source, out _diagnostic))
        {
            return false;
        }

        _stack.Items[_stack.Count++] = new() { Node = node, Next = next };
        return true;
    }
    private int Clear(ref CoreParser parser, CoreNode node, int next) => node.GroupCount == 0 ? next :
        Emit(ref parser, CoreOpcode.ClearCaptures, node.Source, next, operand: node.FirstGroup, count: node.GroupCount);

    internal static bool TryLower(ref CoreParser parser, int root, out SemanticProgram? program, out RegexDiagnostic diagnostic)
    {
        var lowering = new CoreLowering();
        program = null;
        diagnostic = default;
        try
        {
            if (!lowering.Build(ref parser, root))
            { diagnostic = lowering._diagnostic; return false; }
            return SemanticProgram.TryCreate(lowering._code.Items.AsSpan(0, lowering._code.Count),
                parser.Classes.Items.AsSpan(0, parser.Classes.Count), parser.Profile, parser.Groups,
                parser.Length, parser.Unit, ref parser.Budget, out program, out diagnostic);
        }
        finally { lowering._code.Release(ref parser.Budget); lowering._stack.Release(ref parser.Budget); }
    }

    private bool Build(ref CoreParser parser, int root)
    {
        var synthetic = parser.At(parser.Length, 0);
        var match = Emit(ref parser, CoreOpcode.Match, synthetic);
        var end = Emit(ref parser, CoreOpcode.CaptureEnd, synthetic, match);
        if (end < 0 || !Push(ref parser, root, end))
        {
            return false;
        }

        var result = -1;
        while (_stack.Count > 0)
        {
            var top = _stack.Count - 1;
            var frame = _stack.Items[top];
            var node = parser.Nodes.Items[frame.Node];
            if (!parser.Budget.TryCharge(1, node.Source, out _diagnostic))
            {
                return false;
            }

            switch (frame.Stage)
            {
                case 0:
                    switch (node.Kind)
                    {
                        case NodeKind.Concat:
                            frame.Stage = 1;
                            _stack.Items[top] = frame;
                            if (!Push(ref parser, node.Right, frame.Next))
                            {
                                return false;
                            }

                            continue;
                        case NodeKind.Alternate:
                            frame.Stage = 2;
                            _stack.Items[top] = frame;
                            if (!Push(ref parser, node.Right, frame.Next))
                            {
                                return false;
                            }

                            continue;
                        case NodeKind.Group:
                            frame.Stage = 3;
                            _stack.Items[top] = frame;
                            var continuation = node.Value == 0 ? frame.Next : Emit(ref parser, CoreOpcode.CaptureEnd, node.Source, frame.Next, operand: node.Value);
                            if (continuation < 0 || !Push(ref parser, node.Left, continuation))
                            {
                                return false;
                            }

                            continue;
                        case NodeKind.Repeat:
                            frame.Stage = 4;
                            frame.Phase = 1;
                            frame.Cursor = frame.Next;
                            frame.Remaining = node.Maximum < 0 ? 1 : node.Maximum - node.Minimum;
                            _stack.Items[top] = frame;
                            continue;
                        default:
                            var opcode = node.Kind switch
                            {
                                NodeKind.Literal => CoreOpcode.Literal,
                                NodeKind.Class => CoreOpcode.Class,
                                NodeKind.Any => CoreOpcode.Any,
                                NodeKind.Start => CoreOpcode.AssertStart,
                                NodeKind.End => CoreOpcode.AssertEnd,
                                _ => CoreOpcode.Jump
                            };
                            result = Emit(ref parser, opcode, node.Source, frame.Next,
                                operand: node.Kind is NodeKind.Literal or NodeKind.Class ? node.Value : 0);
                            break;
                    }
                    break;
                case 1: // Concat: right returned, now lower left.
                    frame.Stage = 6;
                    _stack.Items[top] = frame;
                    if (!Push(ref parser, node.Left, result))
                    {
                        return false;
                    }

                    continue;
                case 2: // Alternative: right returned, then left, preserving source order.
                    frame.Temporary = result;
                    frame.Stage = 7;
                    _stack.Items[top] = frame;
                    if (!Push(ref parser, node.Left, frame.Next))
                    {
                        return false;
                    }

                    continue;
                case 3:
                    if (node.Value != 0)
                    {
                        result = Emit(ref parser, CoreOpcode.CaptureStart, node.Source, result, operand: node.Value);
                    }

                    break;
                case 4:
                    if (frame.Remaining == 0)
                    {
                        if (frame.Phase == 1)
                        {
                            frame.Phase = 2;
                            frame.Remaining = node.Minimum;
                            _stack.Items[top] = frame;
                            continue;
                        }
                        result = frame.Cursor;
                        if (node.Maximum == 0)
                        {
                            result = Emit(ref parser, CoreOpcode.Jump, node.Source, frame.Next);
                        }

                        break;
                    }
                    frame.Stage = 5;
                    frame.Loop = frame.Phase == 1 && node.Maximum < 0 ? Emit(ref parser, CoreOpcode.Split, node.Source, frame.Cursor, frame.Cursor) : -1;
                    _stack.Items[top] = frame;
                    if (_diagnostic.Code != RegexDiagnosticCode.None || !Push(ref parser, node.Left, frame.Loop >= 0 ? frame.Loop : frame.Cursor))
                    {
                        return false;
                    }

                    continue;
                case 5:
                    result = Clear(ref parser, node, result);
                    if (result < 0)
                    {
                        return false;
                    }

                    if (frame.Phase == 1)
                    {
                        var x = node.Lazy ? frame.Cursor : result;
                        var y = node.Lazy ? result : frame.Cursor;
                        if (frame.Loop >= 0)
                        {
                            if (!parser.Budget.TryCharge(1, node.Source, out _diagnostic))
                            {
                                return false;
                            }

                            _code.Items[frame.Loop] = new(CoreOpcode.Split, x, y, 0, 0, node.Source);
                            result = frame.Loop;
                        }
                        else
                        {
                            result = Emit(ref parser, CoreOpcode.Split, node.Source, x, y);
                        }
                    }
                    frame.Cursor = result;
                    frame.Remaining--;
                    frame.Stage = 4;
                    _stack.Items[top] = frame;
                    continue;
                case 6:
                    break;
                case 7:
                    result = Emit(ref parser, CoreOpcode.Split, node.Source, result, frame.Temporary);
                    break;
            }
            if (result < 0 || _diagnostic.Code != RegexDiagnosticCode.None)
            {
                return false;
            }

            _stack.Count--;
        }
        if (Emit(ref parser, CoreOpcode.CaptureStart, parser.At(0, 0), result) < 0)
        {
            return false;
        }

        var count = _code.Count;
        // Charge each visited/rewritten instruction before reversal or edge fixup.
        if (!parser.Budget.TryCharge(2L * count, parser.At(0, parser.Length), out _diagnostic))
        {
            return false;
        }

        _code.Items.AsSpan(0, count).Reverse();
        for (var i = 0; i < count; i++)
        {
            var instruction = _code.Items[i];
            _code.Items[i] = instruction with
            {
                X = instruction.Opcode == CoreOpcode.Match ? 0 : count - 1 - instruction.X,
                Y = instruction.Opcode == CoreOpcode.Split ? count - 1 - instruction.Y : 0
            };
        }
        return true;
    }
}
