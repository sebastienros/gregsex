#nullable enable
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

namespace GregsEx.Flavors.Oniguruma;

// Lowers a parse tree to OnigProgram instructions. Layouts (operands after the opcode):
//   Char c | CharIC2 a b | Str offset length | Set s | SetUnit s | AnyNL | Any | Anchor kind | Keep | Fail | Match
//   Jmp target | Split first second | SplitLazy body exit | Alt first second firstBits secondBits (-1: any)
//   Run item arg min max mode follow (follow: the unit that must come next, or -1)
//   SaveOpen n | SaveClose n | RetIfCalled n | Call target n
//   LoopInit r | LoopHead r min max lazy exit | LoopEnter r | LoopTail r head exit
//   AtomicStart id | AtomicEnd id | LookStart negative id continuation | LookEnd negative id
//   BehindStart negative id continuation min max | BehindEnd negative id
//   AssertSet negative behind s | AssertStr negative behind offset length
//   Backref ignoreCase count groups... | CondRef else count groups... | CondStart id else | CondEnd id
//   Absent offset length
internal sealed class OnigCompiler
{
    internal const int LoopEnter = 36;

    private static readonly ConcurrentDictionary<(ulong, ulong, bool), SearchValues<char>> s_firstUnits = new();

    private readonly List<int> _code = [];
    private readonly List<OnigCharSet> _sets = [];
    private readonly Dictionary<OnigCharSet, int> _setIndex = new(ReferenceEqualityComparer.Instance);
    private readonly StringBuilder _literals = new();
    private readonly List<OnigStartBits> _bits = [];
    private readonly HashSet<int> _called = [];
    private readonly int[] _groupPc;
    private readonly List<(int At, int Group)> _callFixups = [];
    private readonly RegexCompileLimits _limits;
    private int _loops;
    private int _ids;

    private OnigCompiler(int groupCount, RegexCompileLimits limits)
    {
        _groupPc = new int[groupCount];
        _limits = limits;
    }

    internal static OnigProgram Compile(string pattern, OnigInlineOptions options, bool captureGroup, bool dontCaptureGroup, RegexCompileLimits limits)
    {
        var parser = OnigParser.Parse(pattern, options, limits, out var root);
        var groupCount = parser.GroupCount;
        var names = new string?[groupCount];
        if (dontCaptureGroup || (!captureGroup && parser.HasNamedGroups))
        {
            groupCount = Renumber(parser, names, unnamedCapture: false);
            names = names[..groupCount];
        }
        else
        {
            for (var g = 1; g < groupCount; g++)
            {
                names[g] = parser.Captures[g].Name;
            }
        }

        var compiler = new OnigCompiler(groupCount, limits);
        foreach (var reference in parser.References)
        {
            if (reference.Kind == OnigNodeKind.Call)
            {
                compiler._called.Add(reference.Value);
            }
        }

        compiler.Emit(root);
        compiler.Add(Op.Match);
        foreach (var (at, group) in compiler._callFixups)
        {
            compiler._code[at] = group == 0 ? 0 : compiler._groupPc[group];
        }

        if (compiler._code.Count > limits.MaxInstructions)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.ProgramLimitExceeded, 0, pattern.Length, "program too large");
        }

        var startBits = Bits(root);
        SearchValues<char>? firstUnits = null, notFirstAscii = null;
        if (!startBits.Empty)
        {
            if (!startBits.NonAscii)
            {
                firstUnits = s_firstUnits.GetOrAdd((startBits.Low, startBits.High, false), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, true)));
            }
            else if (startBits.Low != ulong.MaxValue || startBits.High != ulong.MaxValue)
            {
                notFirstAscii = s_firstUnits.GetOrAdd((startBits.Low, startBits.High, true), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, false)));
            }
        }

        var prefix = new StringBuilder();
        Prefix(root, prefix);
        string? required = null;
        Required(root, ref required);
        return new OnigProgram
        {
            Code = compiler._code.ToArray(),
            Sets = compiler._sets.ToArray(),
            Literals = compiler._literals.ToString(),
            Bits = compiler._bits.ToArray(),
            GroupCount = groupCount,
            LoopCount = compiler._loops,
            UsesCalls = compiler._called.Count > 0,
            NeedsUndo = true,
            GroupNames = names,
            StartAnchor = Anchor(root),
            StartBits = startBits,
            FirstUnits = firstUnits,
            NotFirstAscii = notFirstAscii,
            Prefix = prefix.Length >= 2 ? prefix.ToString() : null,
            Required = required,
            MinLength = MinLength(root),
        };
    }

    private static char[] AsciiMembers(ulong low, ulong high, bool members)
    {
        var list = new List<char>();
        for (var c = 0; c < 128; c++)
        {
            var bit = ((c < 64 ? low : high) & (1UL << c)) != 0;
            if (bit == members)
            {
                list.Add((char)c);
            }
        }
        return list.ToArray();
    }

    // Unnamed groups stop capturing (named groups only, or ONIG_OPTION_DONT_CAPTURE_GROUP); named groups renumber.
    private static int Renumber(OnigParser parser, string?[] names, bool unnamedCapture)
    {
        var map = new int[parser.GroupCount];
        var next = 1;
        for (var g = 1; g < parser.GroupCount; g++)
        {
            var node = parser.Captures[g];
            if (node.Name is null && !unnamedCapture)
            {
                node.Kind = OnigNodeKind.Group;
                map[g] = -1;
            }
            else
            {
                map[g] = next;
                node.Value = next;
                names[next] = node.Name;
                next++;
            }
        }

        foreach (var reference in parser.References)
        {
            if (reference.Name is null && !(reference.Kind == OnigNodeKind.Call && reference.Value == 0))
            {
                throw new OnigSyntaxException(RegexDiagnosticCode.InvalidSyntax, reference.Offset, reference.Length,
                    "numbered backref/call is not allowed. (use name)");
            }

            if (reference.Groups is not null)
            {
                for (var i = 0; i < reference.Groups.Length; i++)
                {
                    reference.Groups[i] = map[reference.Groups[i]];
                }

                reference.Value = reference.Groups[^1];
            }
        }
        return next;
    }

    private int Here => _code.Count;

    private void Add(int value) => _code.Add(value);

    private void Add(int a, int b)
    {
        _code.Add(a);
        _code.Add(b);
    }

    private int SetIndex(OnigCharSet set)
    {
        if (!_setIndex.TryGetValue(set, out var index))
        {
            index = _sets.Count;
            _sets.Add(set);
            _setIndex[set] = index;
        }
        return index;
    }

    private int BitsIndex(OnigStartBits bits)
    {
        if (bits.IsAny)
        {
            return -1;
        }

        _bits.Add(bits);
        return _bits.Count - 1;
    }

    private int LiteralOffset(string s)
    {
        var offset = _literals.Length;
        _literals.Append(s);
        return offset;
    }

    private void Emit(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Empty:
                return;
            case OnigNodeKind.Char:
                EmitItem(node);
                return;
            case OnigNodeKind.Set:
                EmitItem(node);
                return;
            case OnigNodeKind.AnyChar:
                Add(node.Flag ? Op.Any : Op.AnyNL);
                return;
            case OnigNodeKind.Anchor:
                Add(Op.Anchor, node.Value);
                return;
            case OnigNodeKind.Keep:
                Add(Op.Keep);
                return;
            case OnigNodeKind.Sequence:
                EmitSequence(node.Children);
                return;
            case OnigNodeKind.Alternation:
                EmitAlternation(node.Children);
                return;
            case OnigNodeKind.Repeat:
                EmitRepeat(node);
                return;
            case OnigNodeKind.Capture:
                _groupPc[node.Value] = Here;
                Add(Op.SaveOpen, node.Value);
                Emit(node.Child);
                Add(Op.SaveClose, node.Value);
                if (_called.Contains(node.Value))
                {
                    Add(Op.RetIfCalled, node.Value);
                }
                return;
            case OnigNodeKind.Group:
                Emit(node.Child);
                return;
            case OnigNodeKind.Atomic:
                {
                    var id = _ids++;
                    Add(Op.AtomicStart, id);
                    Emit(node.Child);
                    Add(Op.AtomicEnd, id);
                    return;
                }
            case OnigNodeKind.Look:
                EmitLook(node);
                return;
            case OnigNodeKind.Backref:
                Add(Op.Backref);
                Add(node.IgnoreCase ? 1 : 0);
                Add(node.Groups!.Length);
                // Oniguruma checks the last group of a name first.
                for (var i = node.Groups.Length - 1; i >= 0; i--)
                {
                    Add(node.Groups[i]);
                }
                return;
            case OnigNodeKind.Call:
                Add(Op.Call);
                _callFixups.Add((Here, node.Value));
                Add(0);
                Add(node.Value);
                return;
            case OnigNodeKind.CondRef:
                {
                    Add(Op.CondRef);
                    var elseAt = Here;
                    Add(0);
                    Add(node.Groups!.Length);
                    foreach (var g in node.Groups)
                    {
                        Add(g);
                    }

                    Emit(node.Children[0]);
                    Add(Op.Jmp);
                    var endAt = Here;
                    Add(0);
                    _code[elseAt] = Here;
                    if (node.Children.Count > 1)
                    {
                        Emit(node.Children[1]);
                    }

                    _code[endAt] = Here;
                    return;
                }
            case OnigNodeKind.CondExpr:
                {
                    var id = _ids++;
                    Add(Op.CondStart, id);
                    var elseAt = Here;
                    Add(0);
                    Emit(node.Children[0]);
                    Add(Op.CondEnd, id);
                    Emit(node.Children[1]);
                    Add(Op.Jmp);
                    var endAt = Here;
                    Add(0);
                    _code[elseAt] = Here;
                    if (node.Children.Count > 2)
                    {
                        Emit(node.Children[2]);
                    }

                    _code[endAt] = Here;
                    return;
                }
            case OnigNodeKind.Absent:
                Add(Op.Absent);
                Add(LiteralOffset(node.Literal!));
                Add(node.Literal!.Length);
                return;
            default:
                throw new InvalidOperationException(node.Kind.ToString());
        }
    }

    // A Char or Set node as one instruction; under ignore-case a char becomes its case class.
    private void EmitItem(OnigNode node)
    {
        var (kind, arg) = Item(node);
        switch (kind)
        {
            case RunItem.Char:
                if (arg > 0xFFFF)
                {
                    Add(Op.Str);
                    Add(LiteralOffset(char.ConvertFromUtf32(arg)));
                    Add(2);
                }
                else
                {
                    Add(Op.Char, arg);
                }
                return;
            case RunItem.Set:
                Add(Op.Set, arg);
                return;
            case RunItem.SetUnit:
                {
                    var set = _sets[arg];
                    if (set.Ranges.Length == 4 && set.Ranges[0] == set.Ranges[1] && set.Ranges[2] == set.Ranges[3])
                    {
                        Add(Op.CharIC2);
                        Add(set.Ranges[0], set.Ranges[2]);
                        return;
                    }

                    Add(Op.SetUnit, arg);
                    return;
                }
            case RunItem.AnyNL:
                Add(Op.AnyNL);
                return;
            default:
                Add(Op.Any);
                return;
        }
    }

    // The run item a single-character node lowers to, or (-1, 0).
    private (int Kind, int Arg) Item(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char:
                {
                    if (node.IgnoreCase)
                    {
                        var members = OnigUnicode.CaseClass(node.Value);
                        if (members.Length > 1)
                        {
                            var builder = new OnigCharSet.Builder();
                            foreach (var m in members)
                            {
                                builder.Add(m);
                            }

                            return SetItem(builder.Build());
                        }
                    }
                    return (RunItem.Char, node.Value);
                }
            case OnigNodeKind.Set:
                {
                    var set = node.Set!;
                    if (set.Single >= 0)
                    {
                        return (RunItem.Char, set.Single);
                    }

                    return SetItem(set);
                }
            case OnigNodeKind.AnyChar:
                return (node.Flag ? RunItem.Any : RunItem.AnyNL, 0);
            case OnigNodeKind.Group when node.Child.Kind is OnigNodeKind.Char or OnigNodeKind.Set or OnigNodeKind.AnyChar:
                return Item(node.Child);
            default:
                return (-1, 0);
        }
    }

    private (int, int) SetItem(OnigCharSet set)
        => (set.IsUnitRun ? RunItem.SetUnit : RunItem.Set, SetIndex(set));

    // Whether a char node is a plain unit sequence: case-sensitive, or with no case variants.
    private static bool IsPlain(OnigNode node, out int cp)
    {
        cp = node.Value;
        return node.Kind == OnigNodeKind.Char && (!node.IgnoreCase || OnigUnicode.CaseClass(node.Value).Length == 1);
    }

    private void EmitSequence(List<OnigNode> children)
    {
        var run = new StringBuilder();
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (IsPlain(child, out var cp))
            {
                run.Append(char.ConvertFromUtf32(cp));
                continue;
            }

            FlushLiteral(run);
            Emit(child);
        }
        FlushLiteral(run);
    }

    private void FlushLiteral(StringBuilder run)
    {
        if (run.Length == 0)
        {
            return;
        }

        if (run.Length == 1)
        {
            Add(Op.Char, run[0]);
        }
        else
        {
            Add(Op.Str);
            Add(LiteralOffset(run.ToString()));
            Add(run.Length);
        }
        run.Clear();
    }

    private void EmitAlternation(List<OnigNode> branches)
    {
        var jumps = new List<int>();
        var rest = new OnigStartBits[branches.Count];
        rest[^1] = Bits(branches[^1]);
        for (var i = branches.Count - 2; i >= 0; i--)
        {
            rest[i] = Bits(branches[i]).Union(rest[i + 1]);
        }

        for (var i = 0; i < branches.Count - 1; i++)
        {
            var at = Here;
            Add(Op.Alt);
            Add(at + 5, 0);
            Add(BitsIndex(Bits(branches[i])), BitsIndex(rest[i + 1]));
            Emit(branches[i]);
            Add(Op.Jmp);
            jumps.Add(Here);
            Add(0);
            _code[at + 2] = Here;
        }
        Emit(branches[^1]);
        foreach (var j in jumps)
        {
            _code[j] = Here;
        }
    }

    private void EmitRepeat(OnigNode node)
    {
        var child = node.Child;
        int min = node.Min, max = node.Max;
        if (max == 0)
        {
            // x{0} matches empty, but a called group inside must still be emitted.
            if (ContainsCalled(child))
            {
                Add(Op.Jmp);
                var over = Here;
                Add(0);
                Emit(child);
                _code[over] = Here;
            }
            return;
        }

        if (min == 1 && max == 1)
        {
            Emit(child);
            return;
        }

        var (kind, arg) = Item(child);
        if (kind >= 0)
        {
            Add(Op.Run);
            Add(kind, arg);
            Add(min, max);
            Add((int)node.Mode);
            Add(-1);
            return;
        }

        if (node.Mode == OnigRepeatMode.Possessive)
        {
            var id = _ids++;
            Add(Op.AtomicStart, id);
            EmitLoop(child, min, max, lazy: false);
            Add(Op.AtomicEnd, id);
            return;
        }

        EmitLoop(child, min, max, node.Mode == OnigRepeatMode.Lazy);
    }

    private void EmitLoop(OnigNode child, int min, int max, bool lazy)
    {
        var childBits = Bits(child);
        if (!childBits.Empty && min <= 2 && (max == -1 || max == min + 1 || (max == 1 && min == 0)))
        {
            for (var i = 0; i < min; i++)
            {
                Emit(child);
            }

            if (max == -1)
            {
                // L: Split(body, exit); body; Jmp L
                var head = Here;
                if (lazy)
                {
                    Add(Op.SplitLazy);
                    Add(head + 3, 0);
                }
                else
                {
                    Add(Op.Alt);
                    Add(head + 5, 0);
                    Add(BitsIndex(childBits), -1);
                }
                Emit(child);
                Add(Op.Jmp, head);
                _code[head + 2] = Here;
                return;
            }

            // One optional iteration.
            var at = Here;
            if (lazy)
            {
                Add(Op.SplitLazy);
                Add(at + 3, 0);
            }
            else
            {
                Add(Op.Alt);
                Add(at + 5, 0);
                Add(BitsIndex(childBits), -1);
            }
            Emit(child);
            _code[at + 2] = Here;
            return;
        }

        var r = _loops++;
        Add(Op.LoopInit, r);
        var loopHead = Here;
        Add(Op.LoopHead);
        Add(r, min);
        Add(max, lazy ? 1 : 0);
        var exitAt = Here;
        Add(0);
        Add(LoopEnter, r);
        Emit(child);
        Add(Op.LoopTail);
        Add(r, loopHead);
        var tailExit = Here;
        Add(0);
        _code[exitAt] = Here;
        _code[tailExit] = Here;
    }

    private void EmitLook(OnigNode node)
    {
        var negative = node.Value;
        var behind = node.Flag;
        var body = Unwrap(node.Child);
        var (kind, arg) = Item(body);
        if (kind is RunItem.Set or RunItem.SetUnit or RunItem.Char)
        {
            var set = kind == RunItem.Char ? Single(arg) : _sets[arg];
            Add(Op.AssertSet);
            Add(negative, behind ? 1 : 0);
            Add(SetIndex(set));
            return;
        }

        var literal = new StringBuilder();
        if (PlainLiteral(body, literal) && literal.Length > 0)
        {
            Add(Op.AssertStr);
            Add(negative, behind ? 1 : 0);
            Add(LiteralOffset(literal.ToString()), literal.Length);
            return;
        }

        var id = _ids++;
        if (behind)
        {
            var (minLen, maxLen) = Lengths(node.Child);
            Add(Op.BehindStart);
            Add(negative, id);
            var cont = Here;
            Add(0);
            Add(minLen, maxLen);
            Emit(node.Child);
            Add(Op.BehindEnd);
            Add(negative, id);
            _code[cont] = Here;
        }
        else
        {
            Add(Op.LookStart);
            Add(negative, id);
            var cont = Here;
            Add(0);
            Emit(node.Child);
            Add(Op.LookEnd);
            Add(negative, id);
            _code[cont] = Here;
        }
    }

    private static OnigCharSet Single(int cp)
    {
        var builder = new OnigCharSet.Builder();
        builder.Add(cp);
        return builder.Build();
    }

    private static OnigNode Unwrap(OnigNode node)
        => node.Kind == OnigNodeKind.Group ? Unwrap(node.Child) : node;

    private static bool PlainLiteral(OnigNode node, StringBuilder literal)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char when IsPlain(node, out var cp):
                literal.Append(char.ConvertFromUtf32(cp));
                return true;
            case OnigNodeKind.Set when node.Set!.Single >= 0:
                literal.Append(char.ConvertFromUtf32(node.Set.Single));
                return true;
            case OnigNodeKind.Sequence:
                foreach (var child in node.Children)
                {
                    if (!PlainLiteral(child, literal))
                    {
                        return false;
                    }
                }
                return true;
            case OnigNodeKind.Group:
                return PlainLiteral(node.Child, literal);
            default:
                return false;
        }
    }

    private bool ContainsCalled(OnigNode node)
    {
        if (node.Kind == OnigNodeKind.Capture && _called.Contains(node.Value))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (ContainsCalled(child))
            {
                return true;
            }
        }
        return false;
    }

    internal static OnigStartBits Bits(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char:
                {
                    if (node.IgnoreCase)
                    {
                        var bits = OnigStartBits.None;
                        foreach (var m in OnigUnicode.CaseClass(node.Value))
                        {
                            bits = bits.Union(CodePointBits(m));
                        }
                        return bits;
                    }
                    return CodePointBits(node.Value);
                }
            case OnigNodeKind.Set:
                {
                    var set = node.Set!;
                    return new OnigStartBits(set.Low, set.High, set.HasNonAscii, false);
                }
            case OnigNodeKind.AnyChar:
                return node.Flag ? new OnigStartBits(ulong.MaxValue, ulong.MaxValue, true, false)
                    : new OnigStartBits(ulong.MaxValue & ~(1UL << '\n'), ulong.MaxValue, true, false);
            case OnigNodeKind.Anchor or OnigNodeKind.Keep or OnigNodeKind.Look or OnigNodeKind.Empty:
                return OnigStartBits.EmptyOnly;
            case OnigNodeKind.Sequence:
                {
                    var result = OnigStartBits.EmptyOnly;
                    foreach (var child in node.Children)
                    {
                        var b = Bits(child);
                        result = new OnigStartBits(result.Low | b.Low, result.High | b.High, result.NonAscii || b.NonAscii, b.Empty);
                        if (!b.Empty)
                        {
                            return result;
                        }
                    }
                    return result with { Empty = true };
                }
            case OnigNodeKind.Alternation:
                {
                    var result = OnigStartBits.None;
                    foreach (var child in node.Children)
                    {
                        result = result.Union(Bits(child));
                    }
                    return result;
                }
            case OnigNodeKind.Repeat:
                {
                    var b = Bits(node.Child);
                    return node.Min == 0 || node.Max == 0 ? b with { Empty = true } : b;
                }
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return Bits(node.Child);
            default:
                return OnigStartBits.Any;
        }
    }

    private static OnigStartBits CodePointBits(int cp)
        => cp < 64 ? new(1UL << cp, 0, false, false) : cp < 128 ? new(0, 1UL << (cp - 64), false, false) : new(0, 0, true, false);

    // Minimum and maximum lengths in UTF-16 units; -1 is unbounded.
    internal static (int Min, int Max) Lengths(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char:
                {
                    var min = int.MaxValue;
                    var max = 0;
                    var members = node.IgnoreCase ? OnigUnicode.CaseClass(node.Value) : [node.Value];
                    foreach (var m in members)
                    {
                        var w = m > 0xFFFF ? 2 : 1;
                        min = Math.Min(min, w);
                        max = Math.Max(max, w);
                    }
                    return (min, max);
                }
            case OnigNodeKind.Set:
                {
                    var set = node.Set!;
                    var hasBmp = set.Ranges.Length > 0 && set.Ranges[0] <= 0xFFFF;
                    return (hasBmp ? 1 : 2, set.HasSupplementary ? 2 : 1);
                }
            case OnigNodeKind.AnyChar:
                return (1, 2);
            case OnigNodeKind.Anchor or OnigNodeKind.Keep or OnigNodeKind.Look or OnigNodeKind.Empty:
                return (0, 0);
            case OnigNodeKind.Sequence:
                {
                    int min = 0, max = 0;
                    foreach (var child in node.Children)
                    {
                        var (cmin, cmax) = Lengths(child);
                        min = Saturate((long)min + cmin);
                        max = max < 0 || cmax < 0 ? -1 : Saturate((long)max + cmax);
                    }
                    return (min, max);
                }
            case OnigNodeKind.Alternation:
                {
                    int min = int.MaxValue, max = 0;
                    foreach (var child in node.Children)
                    {
                        var (cmin, cmax) = Lengths(child);
                        min = Math.Min(min, cmin);
                        max = max < 0 || cmax < 0 ? -1 : Math.Max(max, cmax);
                    }
                    return (min, max);
                }
            case OnigNodeKind.Repeat:
                {
                    var (cmin, cmax) = Lengths(node.Child);
                    var min = Saturate((long)cmin * node.Min);
                    var max = node.Max < 0 || cmax < 0 ? -1 : Saturate((long)cmax * node.Max);
                    return (min, max == int.MaxValue ? -1 : max);
                }
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return Lengths(node.Child);
            case OnigNodeKind.CondRef:
                {
                    var (ymin, ymax) = Lengths(node.Children[0]);
                    var (nmin, nmax) = node.Children.Count > 1 ? Lengths(node.Children[1]) : (0, 0);
                    return (Math.Min(ymin, nmin), ymax < 0 || nmax < 0 ? -1 : Math.Max(ymax, nmax));
                }
            default:
                return (0, -1);
        }
    }

    private static int Saturate(long value) => value > int.MaxValue / 2 ? int.MaxValue / 2 : (int)value;

    private static int MinLength(OnigNode node) => Lengths(node).Min;

    private static OnigStartAnchor Anchor(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Anchor:
                return (OnigAnchor)node.Value switch
                {
                    OnigAnchor.BufferStart => OnigStartAnchor.BufferStart,
                    OnigAnchor.SearchStart => OnigStartAnchor.SearchStart,
                    OnigAnchor.LineStart => OnigStartAnchor.LineStart,
                    _ => OnigStartAnchor.None,
                };
            case OnigNodeKind.Sequence:
                return node.Children.Count > 0 ? Anchor(node.Children[0]) : OnigStartAnchor.None;
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return Anchor(node.Child);
            case OnigNodeKind.Alternation:
                {
                    var first = Anchor(node.Children[0]);
                    foreach (var child in node.Children)
                    {
                        if (Anchor(child) != first)
                        {
                            return OnigStartAnchor.None;
                        }
                    }
                    return first;
                }
            default:
                return OnigStartAnchor.None;
        }
    }

    // The case-sensitive literal every match starts with; returns whether the node was entirely that literal.
    private static bool Prefix(OnigNode node, StringBuilder prefix)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char when IsPlain(node, out var cp):
                prefix.Append(char.ConvertFromUtf32(cp));
                return true;
            case OnigNodeKind.Set when node.Set!.Single >= 0:
                prefix.Append(char.ConvertFromUtf32(node.Set.Single));
                return true;
            case OnigNodeKind.Anchor or OnigNodeKind.Look or OnigNodeKind.Empty:
                return true;
            case OnigNodeKind.Sequence:
                foreach (var child in node.Children)
                {
                    if (!Prefix(child, prefix))
                    {
                        return false;
                    }
                }
                return true;
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return Prefix(node.Child, prefix);
            case OnigNodeKind.Repeat when node.Min >= 1:
                Prefix(node.Child, prefix);
                return false;
            default:
                return false;
        }
    }

    // The longest case-sensitive literal run every match consumes.
    private static void Required(OnigNode node, ref string? best)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char when IsPlain(node, out var cp):
                Keep(char.ConvertFromUtf32(cp), ref best);
                return;
            case OnigNodeKind.Sequence:
                {
                    var run = new StringBuilder();
                    foreach (var child in node.Children)
                    {
                        if (IsPlain(child, out var c))
                        {
                            run.Append(char.ConvertFromUtf32(c));
                            continue;
                        }

                        if (child.Kind is OnigNodeKind.Anchor or OnigNodeKind.Look)
                        {
                            continue;
                        }

                        Keep(run.ToString(), ref best);
                        run.Clear();
                        Required(child, ref best);
                    }
                    Keep(run.ToString(), ref best);
                    return;
                }
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                Required(node.Child, ref best);
                return;
            case OnigNodeKind.Repeat when node.Min >= 1:
                Required(node.Child, ref best);
                return;
            default:
                return;
        }
    }

    private static void Keep(string candidate, ref string? best)
    {
        if (candidate.Length > 0 && (best is null || candidate.Length > best.Length))
        {
            best = candidate;
        }
    }
}
