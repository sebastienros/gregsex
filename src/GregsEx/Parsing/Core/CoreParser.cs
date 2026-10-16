using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Parsing.Core;

// No pattern-controlled recursion, strings, token objects or unaccounted collection growth.
internal ref struct CoreParser
{
    private readonly ReadOnlySpan<char> _chars;
    private readonly ReadOnlySpan<byte> _bytes;
    private readonly bool _bytePattern;
    internal readonly ProfileDescriptor Profile;
    internal CompileBudget Budget;
    internal RegexDiagnostic Diagnostic;
    internal CoreBuffer<CoreNode> Nodes = new(80);
    internal CoreBuffer<CharacterClass> Classes = new(8);
    private CoreBuffer<ParseFrame> _frames = new(24);
    private CoreBuffer<CharacterRange> _ranges = new(8);
    private int _position;
    internal int Groups = 1;
    private long _totalRanges;
    internal readonly int Length => _bytePattern ? _bytes.Length : _chars.Length;
    internal readonly RegexOffsetUnit Unit => _bytePattern ? RegexOffsetUnit.Bytes : RegexOffsetUnit.Utf16CodeUnits;
    internal readonly bool Okay => Diagnostic.Code == RegexDiagnosticCode.None;

    internal CoreParser(ReadOnlySpan<char> chars, ReadOnlySpan<byte> bytes, bool bytePattern,
        ProfileDescriptor profile, CompileBudget budget)
    {
        _chars = chars;
        _bytes = bytes;
        _bytePattern = bytePattern;
        Profile = profile;
        Budget = budget;
    }

    internal readonly SourceRange At(int offset, int length = 1) => new(offset, length, Unit);
    internal bool Fail(RegexDiagnosticCode code, int offset, int length = 1)
    {
        if (Okay)
        {
            Diagnostic = new(code, offset, length, Unit);
        }

        return false;
    }
    private bool Charge(long count, SourceRange source) => Okay && Budget.TryCharge(count, source, out Diagnostic);
    private readonly int Raw(int offset) => _bytePattern ? _bytes[offset] : _chars[offset];
    private int Peek(int ahead = 0)
    {
        var offset = _position + ahead;
        if (!Okay || offset >= Length)
        {
            return -1;
        }

        if (!Charge(1, At(offset)))
        {
            return -1;
        }

        return Raw(offset);
    }
    private int Read()
    {
        var value = Peek();
        if (value >= 0)
        {
            _position++;
        }

        return value;
    }

    internal bool ValidateDomain()
    {
        if (_bytePattern && Profile.TextModel != TextModel.RawBytes)
        {
            return Fail(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0);
        }
        // Whole pattern-domain validation precedes length/syntax admission.
        for (var i = 0; i < Length; i++)
        {
            if (!Charge(1, At(i)))
            {
                return false;
            }

            var value = Raw(i);
            if (Profile.TextModel == TextModel.RawBytes && value > 255)
            {
                return Fail(RegexDiagnosticCode.InvalidPatternEncoding, i);
            }

            if (Profile.TextModel != TextModel.UnicodeScalars || value < 0xD800 || value > 0xDFFF)
            {
                continue;
            }

            if (value > 0xDBFF || i + 1 == Length)
            {
                return Fail(RegexDiagnosticCode.InvalidPatternEncoding, i);
            }

            if (!Charge(1, At(i + 1)))
            {
                return false;
            }

            var low = Raw(i + 1);
            if (low < 0xDC00 || low > 0xDFFF)
            {
                return Fail(RegexDiagnosticCode.InvalidPatternEncoding, i);
            }

            i++;
        }
        return Length <= Budget.Limits.MaxPatternUnits || Fail(RegexDiagnosticCode.PatternLimitExceeded, 0, Length);
    }

    private int Add(CoreNode node)
    {
        if (!Okay)
        {
            return -1;
        }

        if (!Charge(1, node.Source) || !Nodes.Ensure(Nodes.Count + 1, ref Budget, node.Source, out Diagnostic))
        {
            return -1;
        }
        // Preflight exact expanded instruction size with saturating finite arithmetic.
        long limit = Budget.Limits.MaxInstructions;
        var a = node.Left >= 0 ? Nodes.Items[node.Left].Instructions : 0;
        var b = node.Right >= 0 ? Nodes.Items[node.Right].Instructions : 0;
        var size = node.Kind switch
        {
            NodeKind.Concat => a + b,
            NodeKind.Alternate => a + b + 1,
            NodeKind.Group => a + (node.Value == 0 ? 0 : 2),
            NodeKind.Repeat => RepeatSize(a, node, limit),
            _ => 1
        };
        node.Instructions = Math.Min(limit + 1, size);
        Nodes.Items[Nodes.Count] = node;
        return Nodes.Count++;
    }

    private static long RepeatSize(long body, CoreNode node, long limit)
    {
        body += node.GroupCount > 0 ? 1 : 0;
        // Counts are Int32, body saturates at Int32+1: the product fits Int64.
        var size = body * node.Minimum;
        size += node.Maximum < 0 ? body + 1 : (body + 1) * ((long)node.Maximum - node.Minimum);
        // {0} still lowers to one jump, preserving a charged, explicit empty path.
        return Math.Min(limit + 1, Math.Max(1, size));
    }

    private int Leaf(NodeKind kind, int value, int offset, int length) => Add(new()
    { Kind = kind, Value = value, Left = -1, Right = -1, Source = At(offset, length) });

    private int Pair(NodeKind kind, int left, int right, int offset, int length)
    {
        if (left < 0 || right < 0)
        {
            return -1;
        }

        var l = Nodes.Items[left];
        var r = Nodes.Items[right];
        var first = l.GroupCount == 0 ? r.FirstGroup : r.GroupCount == 0 ? l.FirstGroup : Math.Min(l.FirstGroup, r.FirstGroup);
        var end = Math.Max(l.FirstGroup + l.GroupCount, r.FirstGroup + r.GroupCount);
        return Add(new()
        {
            Kind = kind,
            Left = left,
            Right = right,
            FirstGroup = first,
            GroupCount = first == 0 ? 0 : end - first,
            Source = At(offset, length)
        });
    }

    private bool Push(int group, int offset)
    {
        if (_frames.Count > Budget.Limits.MaxNestingDepth)
        {
            return Fail(RegexDiagnosticCode.NestingLimitExceeded, offset);
        }

        if (!_frames.Ensure(_frames.Count + 1, ref Budget, At(offset, 0), out Diagnostic))
        {
            return false;
        }

        _frames.Items[_frames.Count++] = new() { Concat = -1, Alternate = -1, Group = group, Offset = offset, FirstGroup = Groups };
        return true;
    }
    private int FinishConcat(ref ParseFrame frame) => frame.Concat >= 0 ? frame.Concat : Leaf(NodeKind.Empty, 0, _position, 0);
    private int FinishExpression(ref ParseFrame frame)
    {
        var tail = FinishConcat(ref frame);
        return frame.Alternate < 0 ? tail : Pair(NodeKind.Alternate, frame.Alternate, tail, frame.Offset, _position - frame.Offset);
    }
    private void Append(int node)
    {
        if (node < 0)
        {
            return;
        }

        ref var frame = ref _frames.Items[_frames.Count - 1];
        frame.Concat = frame.Concat < 0 ? node : Pair(NodeKind.Concat, frame.Concat, node,
            Nodes.Items[frame.Concat].Source.Offset, _position - Nodes.Items[frame.Concat].Source.Offset);
    }

    internal bool Parse(out int root)
    {
        root = -1;
        if (!ValidateDomain() || !Push(0, 0))
        {
            return false;
        }

        while (Okay)
        {
            var token = Peek();
            if (!Okay)
            {
                break;
            }

            if (token < 0)
            {
                if (_frames.Count != 1)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, _frames.Items[_frames.Count - 1].Offset, Length - _frames.Items[_frames.Count - 1].Offset);
                }

                root = FinishExpression(ref _frames.Items[0]);
                return Okay;
            }
            var start = _position;
            if (token == '|')
            {
                Read();
                ref var frame = ref _frames.Items[_frames.Count - 1];
                var tail = FinishConcat(ref frame);
                frame.Alternate = frame.Alternate < 0 ? tail : Pair(NodeKind.Alternate, frame.Alternate, tail, frame.Offset, _position - frame.Offset);
                frame.Concat = -1;
                continue;
            }
            if (token == '(')
            {
                if (_frames.Count > Budget.Limits.MaxNestingDepth)
                {
                    return Fail(RegexDiagnosticCode.NestingLimitExceeded, start);
                }

                Read();
                int group;
                if (Peek() == '?')
                {
                    Read();
                    if (Peek() != ':')
                    {
                        return RejectExcludedGroup(start);
                    }

                    Read();
                    group = 0;
                }
                else
                {
                    if (Peek() == '*')
                    {
                        return RejectExcludedGroup(start, verb: true);
                    }

                    if (Groups >= Budget.Limits.MaxCaptureGroups)
                    {
                        return Fail(RegexDiagnosticCode.CaptureLimitExceeded, start);
                    }

                    group = Groups++;
                }
                if (!Push(group, start))
                {
                    break;
                }

                continue;
            }
            int node;
            if (token == ')')
            {
                if (_frames.Count == 1)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start);
                }

                Read();
                var frame = _frames.Items[--_frames.Count];
                var child = FinishExpression(ref frame);
                var first = frame.Group > 0 ? frame.Group : frame.FirstGroup;
                var count = Groups - first;
                node = Add(new()
                {
                    Kind = NodeKind.Group,
                    Left = child,
                    Right = -1,
                    Value = frame.Group,
                    FirstGroup = count > 0 ? first : 0,
                    GroupCount = count,
                    Source = At(frame.Offset, _position - frame.Offset)
                });
            }
            else if (token is '*' or '+' or '?' or '{' or '}' or ']')
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, start);
            }
            else if (token == '[')
            {
                if (_frames.Count > Budget.Limits.MaxNestingDepth)
                {
                    return Fail(RegexDiagnosticCode.NestingLimitExceeded, start);
                }

                if (!ParseClass(out var index))
                {
                    break;
                }

                node = Leaf(NodeKind.Class, index, start, _position - start);
            }
            else if (token == '\\')
            {
                if (!Escape(out var value, out var predefined))
                {
                    break;
                }

                if (predefined != 0)
                {
                    _ranges.Count = 0;
                    if (!AddPredefined(predefined) || !PublishClass(false, start, out var index))
                    {
                        break;
                    }

                    node = Leaf(NodeKind.Class, index, start, _position - start);
                }
                else
                {
                    node = Leaf(NodeKind.Literal, value, start, _position - start);
                }
            }
            else if (token is '.' or '^' or '$')
            {
                Read();
                node = Leaf(token == '.' ? NodeKind.Any : token == '^' ? NodeKind.Start : NodeKind.End, 0, start, 1);
            }
            else
            {
                if (!Literal(out var value))
                {
                    break;
                }

                node = Leaf(NodeKind.Literal, value, start, _position - start);
            }
            if (node < 0 || !Quantify(ref node))
            {
                break;
            }

            Append(node);
        }
        return false;
    }


    private bool RejectExcludedGroup(int start, bool verb = false)
    {
        // Recognize excluded group prefixes, then ensure balanced, closed syntax. Unknown
        // prefixes/truncation stay syntax errors, rather than acquiring another flavor's grammar.
        var token = Peek();
        if (!verb)
        {
            if (token is '=' or '!' or '>' or '|' or '(' or '#' or '&' or 'R' or >= '0' and <= '9')
            {
                Read();
            }
            else if (token == 'P')
            {
                Read();
                if (Peek() is not ('<' or '='))
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
            }
            else if (token == '<' || token == '\'')
            {
                int terminator = token == '<' ? '>' : '\'';
                Read();
                if (token == '<' && Peek() is '=' or '!')
                {
                    Read();
                }
                else
                {
                    var nameStart = _position;
                    while (Okay && Peek() >= 0 && Peek() != terminator)
                    {
                        var ch = Read();
                        if (ch is not (>= 'a' and <= 'z') and not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not '_')
                        {
                            return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                        }
                    }
                    if (!Okay)
                    {
                        return false;
                    }

                    if (Peek() != terminator || _position == nameStart)
                    {
                        return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                    }

                    Read();
                }
            }
            else if (token is 'i' or 'm' or 's' or 'x' or 'n' or 'U' or '-')
            {
                while (Peek() is 'i' or 'm' or 's' or 'x' or 'n' or 'U' or '-')
                {
                    Read();
                }

                if (!Okay)
                {
                    return false;
                }

                if (Peek() == ')')
                { Read(); return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start); }
                if (Peek() != ':')
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
            }
            else
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start + (token < 0 ? 0 : 1));
            }
        }
        var depth = token == '(' && !verb ? 2 : 1;
        var inClass = false;
        while (Okay && Peek() >= 0)
        {
            var ch = Read();
            if (ch == '\\')
            {
                if (Peek() < 0)
                {
                    break;
                }

                Read();
                continue;
            }
            if (ch == '[')
            { inClass = true; continue; }
            if (ch == ']' && inClass)
            { inClass = false; continue; }
            if (inClass)
            {
                continue;
            }

            if (ch == '(')
            {
                depth++;
                if ((long)depth + _frames.Count - 1 > Budget.Limits.MaxNestingDepth)
                {
                    return Fail(RegexDiagnosticCode.NestingLimitExceeded, _position - 1);
                }
            }
            if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
                }
            }
        }
        return Okay && Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
    }

    private bool Literal(out int value)
    {
        value = Read();
        if (!Okay)
        {
            return false;
        }

        if (Profile.TextModel == TextModel.UnicodeScalars && value is >= 0xD800 and <= 0xDBFF)
        {
            var low = Read();
            value = 0x10000 + ((value - 0xD800) << 10) + low - 0xDC00;
        }
        return Okay;
    }

    private bool Escape(out int value, out int predefined)
    {
        var start = _position;
        value = 0;
        predefined = 0;
        Read();
        var token = Read();
        if (!Okay)
        {
            return false;
        }

        switch (token)
        {
            case 'd':
            case 'D':
            case 'w':
            case 'W':
            case 's':
            case 'S':
                predefined = token;
                return true;
            case 'n':
                value = 10;
                break;
            case 'r':
                value = 13;
                break;
            case 't':
                value = 9;
                break;
            case 'f':
                value = 12;
                break;
            case 'v':
                value = 11;
                break;
            case '0':
                if (Peek() is >= '0' and <= '9')
                {
                    return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start + 1);
                }

                break;
            case 'x':
                if (!Hex(2, false, start, out value))
                {
                    return false;
                }

                break;
            case 'u':
                if (Peek() == '{')
                {
                    if (Profile.TextModel != TextModel.UnicodeScalars)
                    {
                        return Fail(RegexDiagnosticCode.UnsupportedFeature, start, 3);
                    }

                    Read();
                    if (!Hex(6, true, start, out value))
                    {
                        return false;
                    }
                }
                else if (!Hex(4, false, start, out value))
                {
                    return false;
                }

                break;
            case 'p':
            case 'P':
                if (Peek() != '{')
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
                var nameStart = _position;
                while (Okay && Peek() >= 0 && Peek() != '}')
                {
                    Read();
                }

                if (!Okay)
                {
                    return false;
                }

                if (Peek() != '}' || _position == nameStart)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
                return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
            case 'k':
            case 'g':
                var delimiter = Peek();
                if (delimiter is not ('<' or '{' or '\''))
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
                var referenceStart = _position;
                int close = delimiter == '<' ? '>' : delimiter == '{' ? '}' : '\'';
                while (Okay && Peek() >= 0 && Peek() != close)
                {
                    Read();
                }

                if (!Okay)
                {
                    return false;
                }

                if (Peek() != close || _position == referenceStart)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
                }

                Read();
                return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
            case >= '1' and <= '9':
                while (Peek() is >= '0' and <= '9')
                {
                    Read();
                }

                return Okay && Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
            case 'b':
            case 'B':
            case 'A':
            case 'Z':
            case 'z':
            case 'G':
            case 'K':
            case 'R':
            case 'X':
            case 'C':
            case 'Q':
            case 'E':
                return Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
            case '\\':
            case '.':
            case '^':
            case '$':
            case '|':
            case '?':
            case '*':
            case '+':
            case '(':
            case ')':
            case '[':
            case ']':
            case '{':
            case '}':
            case '-':
                value = token;
                break;
            default:
                return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
        }
        return Profile.Contains(value) || Fail(RegexDiagnosticCode.InvalidPatternEncoding, start, _position - start);
    }

    private bool Hex(int digits, bool braced, int start, out int value)
    {
        value = 0;
        var count = 0;
        while (count < digits)
        {
            var token = Peek();
            if (!Okay)
            {
                return false;
            }

            var digit = token is >= '0' and <= '9' ? token - '0' : token is >= 'a' and <= 'f' ? token - 'a' + 10 : token is >= 'A' and <= 'F' ? token - 'A' + 10 : -1;
            if (digit < 0)
            {
                if (braced && token == '}' && count > 0)
                { Read(); return Okay; }
                return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start + (token >= 0 ? 1 : 0));
            }
            Read();
            value = (value << 4) | digit;
            count++;
        }
        if (!braced)
        {
            return Okay;
        }

        if (Peek() != '}')
        {
            return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start);
        }

        Read();
        return Okay;
    }

    private bool Decimal(int start, out int value)
    {
        value = 0;
        var count = 0;
        while (Peek() is >= '0' and <= '9')
        {
            var digit = Read() - '0';
            if (!Okay)
            {
                return false;
            }

            if (value > (int.MaxValue - digit) / 10)
            {
                return Fail(RegexDiagnosticCode.ProgramLimitExceeded, start, _position - start);
            }

            value = value * 10 + digit;
            count++;
        }
        return Okay && (count > 0 || Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start));
    }

    private bool Quantify(ref int node)
    {
        if (!Okay)
        {
            return false;
        }

        var token = Peek();
        var start = _position;
        if (token is not ('*' or '+' or '?' or '{'))
        {
            return Okay;
        }

        if (Nodes.Items[node].Kind is NodeKind.Start or NodeKind.End)
        {
            return Fail(RegexDiagnosticCode.InvalidSyntax, start);
        }

        Read();
        int minimum, maximum;
        if (token == '{')
        {
            if (!Decimal(start, out minimum))
            {
                return false;
            }

            maximum = minimum;
            if (Peek() == ',')
            {
                Read();
                maximum = -1;
                if (Peek() != '}' && !Decimal(start, out maximum))
                {
                    return false;
                }
            }
            if (Peek() != '}' || (maximum >= 0 && maximum < minimum))
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, start, Math.Max(1, _position - start));
            }

            Read();
        }
        else
        { minimum = token == '+' ? 1 : 0; maximum = token == '?' ? 1 : -1; }
        var lazy = Peek() == '?';
        if (lazy)
        {
            Read();
        }

        if (Peek() == '+')
        {
            Read();
            if (Peek() is '*' or '+' or '?' or '{')
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, _position);
            }

            return Okay && Fail(RegexDiagnosticCode.UnsupportedFeature, start, _position - start);
        }
        if (Peek() is '*' or '?' or '{')
        {
            return Fail(RegexDiagnosticCode.InvalidSyntax, _position);
        }

        var child = Nodes.Items[node];
        node = Add(new()
        {
            Kind = NodeKind.Repeat,
            Left = node,
            Right = -1,
            Minimum = minimum,
            Maximum = maximum,
            Lazy = lazy,
            FirstGroup = child.FirstGroup,
            GroupCount = child.GroupCount,
            Source = At(child.Source.Offset, _position - child.Source.Offset)
        });
        return Okay;
    }

    private bool ClassElement(out int value, out int predefined)
    {
        value = 0;
        predefined = 0;
        var token = Peek();
        if (token == '\\')
        {
            return Escape(out value, out predefined);
        }

        if (token == '[')
        {
            return Fail(RegexDiagnosticCode.UnsupportedFeature, _position);
        }

        if (token < 0 || token == ']')
        {
            return Fail(RegexDiagnosticCode.InvalidSyntax, _position, token < 0 ? 0 : 1);
        }

        return Literal(out value);
    }
    private bool ParseClass(out int index)
    {
        index = -1;
        var start = _position;
        Read();
        var negated = Peek() == '^';
        if (negated)
        {
            Read();
        }

        _ranges.Count = 0;
        var first = true;
        while (Okay && Peek() != ']')
        {
            var offset = _position;
            if (Peek() is '-' && !first && Peek(1) == '[')
            {
                return Fail(RegexDiagnosticCode.UnsupportedFeature, offset, 2);
            }

            if (Peek() is '-' && !first && Peek(1) != ']')
            {
                return Fail(RegexDiagnosticCode.InvalidSyntax, offset);
            }

            var hyphen = Peek() == '-';
            if (!ClassElement(out var value, out var predefined))
            {
                return false;
            }

            if (predefined != 0)
            {
                if (Peek() == '-' && Peek(1) != ']')
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, offset, _position - offset + 1);
                }

                if (!AddPredefined(predefined))
                {
                    return false;
                }
            }
            else if (!hyphen && Peek() == '-' && Peek(1) != ']')
            {
                Read();
                if (Peek() == '[')
                {
                    return Fail(RegexDiagnosticCode.UnsupportedFeature, _position - 1, 2);
                }

                if (!ClassElement(out var end, out var endPredefined))
                {
                    return false;
                }

                if (endPredefined != 0 || end < value)
                {
                    return Fail(RegexDiagnosticCode.InvalidSyntax, offset, _position - offset);
                }

                if (!AddRange(value, end))
                {
                    return false;
                }
            }
            else if (!AddRange(value, value))
            {
                return false;
            }

            if (Peek() == '&' && Peek(1) == '&')
            {
                return Fail(RegexDiagnosticCode.UnsupportedFeature, _position, 2);
            }

            if (Peek() == '-' && Peek(1) == '-')
            {
                return Fail(RegexDiagnosticCode.UnsupportedFeature, _position, 2);
            }

            first = false;
        }
        if (!Okay)
        {
            return false;
        }

        if (Peek() != ']' || first)
        {
            return Fail(RegexDiagnosticCode.InvalidSyntax, start, _position - start + (Peek() == ']' ? 1 : 0));
        }

        Read();
        return PublishClass(negated, start, out index);
    }

    // Sorted merged union with each comparison/output/move charged; no ambient Unicode tables.
    private bool AddRange(int first, int last)
    {
        var i = 0;
        while (i < _ranges.Count)
        {
            if (!Charge(1, At(_position, 0)))
            {
                return false;
            }

            var r = _ranges.Items[i];
            if ((long)r.Last + 1 >= first)
            {
                break;
            }

            i++;
        }
        var end = i;
        while (end < _ranges.Count)
        {
            if (!Charge(1, At(_position, 0)))
            {
                return false;
            }

            var r = _ranges.Items[end];
            if ((long)last + 1 < r.First)
            {
                break;
            }

            first = Math.Min(first, r.First);
            last = Math.Max(last, r.Last);
            end++;
        }
        var moved = _ranges.Count - end;
        if (!Charge(1L + moved, At(_position, 0)))
        {
            return false;
        }

        var count = _ranges.Count - (end - i) + 1;
        if (!_ranges.Ensure(count, ref Budget, At(_position, 0), out Diagnostic))
        {
            return false;
        }

        _ranges.Items.AsSpan(end, moved).CopyTo(_ranges.Items.AsSpan(i + 1));
        _ranges.Items[i] = new(first, last);
        _ranges.Count = count;
        return true;
    }

    private bool AddPredefined(int token)
    {
        // Constant span-backed tables; complements are materialized as bounded intervals.
        ReadOnlySpan<int> ranges = token is 'd' or 'D' ? [48, 57] :
            token is 's' or 'S' ? [9, 13, 32, 32] :
            [48, 57, 65, 90, 95, 95, 97, 122];
        var complement = token is 'D' or 'S' or 'W';
        var next = 0;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            var range = new CharacterRange(ranges[i], ranges[i + 1]);
            if (!Charge(1, At(_position, 0)))
            {
                return false;
            }

            if (!complement)
            {
                if (!AddRange(range.First, range.Last))
                {
                    return false;
                }
            }
            else
            {
                if (next < range.First && !AddRange(next, range.First - 1))
                {
                    return false;
                }

                next = range.Last + 1;
            }
        }
        var maximum = Profile.TextModel == TextModel.RawBytes ? 255 : Profile.TextModel == TextModel.Utf16CodeUnits ? 65535 : 0x10FFFF;
        return !complement || AddRange(next, maximum);
    }

    private bool PublishClass(bool negated, int start, out int index)
    {
        index = -1;
        if (_totalRanges + _ranges.Count > Budget.Limits.MaxClassRanges)
        {
            return Fail(RegexDiagnosticCode.ClassLimitExceeded, start, _position - start);
        }

        if (!Classes.Ensure(Classes.Count + 1, ref Budget, At(start, _position - start), out Diagnostic))
        {
            return false;
        }

        if (!CharacterClass.TryCreate(_ranges.Items.AsSpan(0, _ranges.Count), negated, Profile, ref Budget,
            At(start, _position - start), out var result, out Diagnostic))
        {
            return false;
        }

        _totalRanges += _ranges.Count;
        index = Classes.Count;
        Classes.Items[Classes.Count++] = result!;
        return true;
    }

    internal void ReleaseTemporaries()
    {
        Nodes.Release(ref Budget);
        Classes.Release(ref Budget);
        _frames.Release(ref Budget);
        _ranges.Release(ref Budget);
    }
}
