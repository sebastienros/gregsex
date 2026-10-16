using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Execution.Compiled;

// XSD / XPath front end of the kernel framework. XSD is whole-string membership, so it becomes an anchored plan that must end
// at the end of the input; XPath is the ordinary leftmost-first search. Case variants are resolved here, once, into plain
// BMP classes. Backreferences and loops whose body may match the empty string make the pattern ineligible.
internal static class XmlKernelPlan
{
    internal static bool TryCreate(XmlProgram program, ReadOnlySpan<XmlCaseMapping> cases, in LoweringLimits limits,
        out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        var builder = new Builder(program, cases, limits);
        if (!builder.TryBuild(out var nodes, out reason))
        {
            return false;
        }

        var xsd = program.Dialect == XmlDialect.Xsd;
        return CoreKernelPlan.Analyze(nodes!, program.GroupCount, scalars: true, dotAll: false, sticky: xsd, limits, out plan, out reason);
    }

    private sealed class Builder
    {
        private const int MaxClassRanges = 512;
        private readonly XmlProgram _program;
        private readonly XmlCaseMapping[] _cases;
        private readonly bool _xsd;
        private readonly bool _fold;
        private readonly bool _multiline;
        private readonly int _maxNodes;
        private readonly List<KernelNode> _nodes = [];
        private readonly Dictionary<int, (CharacterClass Class, bool Sensitive)> _sets = [];
        private readonly Dictionary<int, (CharacterClass? Class, bool Sensitive)> _literals = [];
        private readonly ProfileDescriptor _descriptor;
        private readonly bool[] _nullable;
        private string? _failure;

        internal Builder(XmlProgram program, ReadOnlySpan<XmlCaseMapping> cases, in LoweringLimits limits)
        {
            _program = program;
            _cases = cases.ToArray();
            _xsd = program.Dialect == XmlDialect.Xsd;
            if (program.Profile is XPathProfile xpath)
            {
                _fold = (xpath.Flags & XPathFlags.IgnoreCase) != 0;
                _multiline = (xpath.Flags & XPathFlags.Multiline) != 0;
            }
            _maxNodes = limits.MaxNodes / 16;
            ProfileDescriptor.TryCreate(new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16, out var descriptor, out _);
            _descriptor = descriptor!;
            var nodes = program.Nodes;
            _nullable = new bool[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                _nullable[i] = n.Kind switch
                {
                    XmlNodeKind.Literal or XmlNodeKind.Class => false,
                    XmlNodeKind.Sequence => _nullable[n.X] && _nullable[n.Y],
                    XmlNodeKind.Alternative => _nullable[n.X] || _nullable[n.Y],
                    XmlNodeKind.Repeat => n.Min == 0 || _nullable[n.X],
                    XmlNodeKind.Capture => _nullable[n.X],
                    _ => true
                };
            }
        }

        internal bool TryBuild(out KernelNode[]? nodes, out string reason)
        {
            nodes = null;
            reason = string.Empty;
            Alloc(default);
            var match = Alloc(new() { Op = KernelOp.Match });
            var end = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = 0, Next = match });
            // XSD membership: the language must consume the whole subject.
            var tail = _xsd ? Alloc(new() { Op = KernelOp.AssertEnd, Next = end }) : end;
            var body = Gen(_program.Root, tail);
            if (_failure is not null)
            { reason = _failure; return false; }
            _nodes[0] = new() { Op = KernelOp.CaptureStart, Operand = 0, Next = body };
            nodes = [.. _nodes];
            return true;
        }

        private int Alloc(KernelNode node)
        {
            if (_nodes.Count >= _maxNodes)
            {
                Fail("The program exceeds the lowering node bound.");
            }
            else
            {
                _nodes.Add(node);
            }

            return _nodes.Count - 1;
        }

        private void Fail(string reason) => _failure ??= reason;

        private int Gen(int index, int next)
        {
            if (_failure is not null)
            {
                return next;
            }

            var n = _program.Nodes[index];
            switch (n.Kind)
            {
                case XmlNodeKind.Empty:
                    return next;
                case XmlNodeKind.Literal:
                    return GenLiteral(n.Value, next);
                case XmlNodeKind.Class:
                    {
                        var (cls, sensitive) = ClassOf(n.Value);
                        if (cls is null)
                        {
                            return next;
                        }

                        return Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls, SurrogateSensitive = sensitive });
                    }
                case XmlNodeKind.Sequence:
                    return Gen(n.X, Gen(n.Y, next));
                case XmlNodeKind.Alternative:
                    {
                        var y = Gen(n.Y, next);
                        var x = Gen(n.X, next);
                        return Alloc(new() { Op = KernelOp.Split, Next = x, Alternate = y });
                    }
                case XmlNodeKind.Capture:
                    {
                        var close = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = n.Value, Next = next });
                        var inner = Gen(n.X, close);
                        return Alloc(new() { Op = KernelOp.CaptureStart, Operand = n.Value, Next = inner });
                    }
                case XmlNodeKind.Repeat:
                    return GenRepeat(n, next);
                case XmlNodeKind.Start:
                    return _multiline
                        ? Alloc(new() { Op = KernelOp.AssertLineStart, Next = next, Operand = 2 })
                        : Alloc(new() { Op = KernelOp.AssertStart, Next = next });
                case XmlNodeKind.End:
                    return _multiline
                        ? Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 2 })
                        : Alloc(new() { Op = KernelOp.AssertEnd, Next = next });
                default:
                    Fail("Backreferences are not lowered.");
                    return next;
            }
        }

        private int GenLiteral(int value, int next)
        {
            if (value > 0xFFFF)
            {
                Fail("A literal outside the BMP is not lowered.");
                return next;
            }
            if (!_fold)
            {
                return Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = value });
            }

            if (!_literals.TryGetValue(value, out var cached))
            {
                var members = FoldMembers(value, out var astral);
                if (members.Count == 1 && !astral)
                {
                    cached = (null, false);
                }
                else
                {
                    var member = new bool[0x10000];
                    foreach (var c in members)
                    {
                        member[c] = true;
                    }

                    if (astral)
                    {
                        Array.Fill(member, true, 0xD800, 0x800);
                    }

                    var cls = MakeClass(ToRanges(member), out var ok);
                    if (!ok)
                    {
                        Fail("A case-insensitive literal class exceeds the class bound.");
                    }

                    cached = ok ? (cls, astral) : default;
                }
                _literals[value] = cached;
            }
            if (_failure is not null)
            {
                return next;
            }

            return cached.Class is null
                ? Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = value })
                : Alloc(new() { Op = KernelOp.Class, Next = next, Class = cached.Class, SurrogateSensitive = cached.Sensitive });
        }

        private int GenRepeat(XmlNode n, int next)
        {
            if (_nullable[n.X] && n.Max != n.Min)
            {
                Fail("A loop whose body can match the empty string is not lowered.");
                return next;
            }
            // The XPath interpreter keeps the previous iteration's captures, so the body has no Clear.
            int Body(int target) => Gen(n.X, target);

            int tail;
            if (n.Max == -1)
            {
                var loop = Alloc(default);
                var body = Body(loop);
                _nodes[loop] = n.Lazy
                    ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                    : new() { Op = KernelOp.Split, Next = body, Alternate = next };
                tail = loop;
            }
            else
            {
                tail = next;
                for (var i = (long)n.Max - n.Min; i > 0 && _failure is null; i--)
                {
                    var body = Body(tail);
                    tail = Alloc(n.Lazy
                        ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                        : new() { Op = KernelOp.Split, Next = body, Alternate = next });
                }
            }
            for (long i = n.Min; i > 0 && _failure is null; i--)
            {
                tail = Body(tail);
            }

            return tail;
        }

        private XmlCaseMapping Mapping(int cp)
        {
            int lo = 0, hi = _cases.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                var item = _cases[mid];
                if (cp < item.Scalar)
                {
                    hi = mid - 1;
                }
                else if (cp > item.Scalar)
                {
                    lo = mid + 1;
                }
                else
                {
                    return item;
                }
            }
            return new(cp, cp, cp);
        }

        private bool Equal(int a, int b)
        {
            if (a == b)
            {
                return true;
            }

            var x = Mapping(a);
            var y = Mapping(b);
            return x.LowerKey == y.LowerKey || x.UpperKey == y.UpperKey;
        }

        // Every scalar the interpreter's case-variant test equates with the literal; astral members make the class surrogate sensitive.
        private List<int> FoldMembers(int value, out bool astral)
        {
            var mv = Mapping(value);
            var members = new SortedSet<int> { value };
            void Consider(int c)
            {
                if (c is >= 0 and <= 0x10FFFF && Equal(c, value))
                {
                    members.Add(c);
                }
            }
            foreach (var m in _cases)
            {
                Consider(m.Scalar);
            }

            Consider(mv.LowerKey);
            Consider(mv.UpperKey);
            astral = members.Any(c => c > 0xFFFF);
            return [.. members.Where(c => c <= 0xFFFF)];
        }

        private static List<CharacterRange> ToRanges(bool[] member)
        {
            var ranges = new List<CharacterRange>();
            for (var c = 0; c < member.Length; c++)
            {
                if (!member[c])
                {
                    continue;
                }

                var first = c;
                while (c + 1 < member.Length && member[c + 1])
                {
                    c++;
                }

                ranges.Add(new(first, c));
            }
            return ranges;
        }

        private CharacterClass? MakeClass(List<CharacterRange> positive, out bool ok)
        {
            var complement = new List<CharacterRange>();
            var cursor = 0;
            foreach (var r in positive)
            {
                if (r.First > cursor)
                {
                    complement.Add(new(cursor, r.First - 1));
                }

                cursor = r.Last + 1;
            }
            if (cursor <= 0xFFFF)
            {
                complement.Add(new(cursor, 0xFFFF));
            }

            var negate = complement.Count < positive.Count;
            var chosen = negate ? complement : positive;
            ok = false;
            if (chosen.Count > MaxClassRanges)
            {
                return null;
            }

            var budget = new CompileBudget(new RegexCompileLimits());
            ok = CharacterClass.TryCreate(chosen.ToArray(), negate, _descriptor, ref budget, default, out var cls, out _);
            return cls;
        }

        private (CharacterClass? Class, bool Sensitive) ClassOf(int setId)
        {
            if (_sets.TryGetValue(setId, out var cached))
            {
                return cached;
            }

            var set = _program.Sets[setId];
            var intervals = _program.Intervals.Slice(set.Offset, set.Count);
            var member = new bool[0x10000];
            var sensitive = false;
            foreach (var iv in intervals)
            {
                if (iv.Last > 0xFFFF)
                {
                    sensitive = true;
                }

                for (var c = iv.First; c <= Math.Min(iv.Last, 0xFFFF); c++)
                {
                    member[c] = true;
                }
            }
            if (sensitive)
            {
                Array.Fill(member, true, 0xD800, 0x800);
            }

            var cls = MakeClass(ToRanges(member), out var ok);
            if (!ok)
            { Fail("A character class exceeds the class bound."); return default; }
            return _sets[setId] = (cls!, sensitive);
        }
    }
}
