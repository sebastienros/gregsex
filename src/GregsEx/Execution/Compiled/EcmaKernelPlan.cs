#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Contracts.EcmaScript;
using GregsEx.IR;

namespace GregsEx.Execution.Compiled;

// ECMAScript front end of the kernel framework: translates the immutable semantic tree into the same backtracking
// instruction graph the RegularCore front end produces, so one lowering (FEC) and one emitter (C#) serve both.
// Case-insensitivity and Unicode data are resolved here, once, into plain BMP classes; the kernel never sees them.
// Constructs without a faithful instruction-graph image (backreferences, lookaround, string sets, non-BMP literals,
// loops that may iterate on an empty match) make the pattern ineligible instead of approximating them.
internal static class EcmaKernelPlan
{
    internal static bool TryCreate(EcmaProgram program, EcmaCaseTables tables, in LoweringLimits limits,
        out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        var builder = new Builder(program, tables, limits);
        if (!builder.TryBuild(out var nodes, out reason))
        {
            return false;
        }

        var flags = program.Profile.Flags;
        return CoreKernelPlan.Analyze(nodes!, program.GroupCount, program.Profile.IsUnicodeAware, dotAll: false,
            sticky: (flags & EcmaScriptFlags.Sticky) != 0, limits, out plan, out reason);
    }

    private sealed class Builder
    {
        private const int MaxClassRanges = 512;
        private readonly EcmaProgram _program;
        private readonly EcmaCaseTables _tables;
        private readonly bool _unicode;
        private readonly int _maxNodes;
        private readonly List<KernelNode> _nodes = [];
        private readonly Dictionary<(int Id, bool Fold), (CharacterClass Class, bool Sensitive)> _sets = [];
        private readonly Dictionary<(int Value, bool Fold), (CharacterClass? Class, bool Sensitive)> _literals = [];
        private readonly Dictionary<bool, CharacterClass> _words = [];
        private readonly ProfileDescriptor _descriptor;
        private readonly bool[] _nullable;
        private string? _failure;

        internal Builder(EcmaProgram program, EcmaCaseTables tables, in LoweringLimits limits)
        {
            _program = program;
            _tables = tables;
            _unicode = program.Profile.IsUnicodeAware;
            _maxNodes = limits.MaxNodes / 16;
            ProfileDescriptor.TryCreate(new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16, out var descriptor, out _);
            _descriptor = descriptor!;
            _nullable = new bool[program.Nodes.Length];
            for (var i = 0; i < _nullable.Length; i++)
            {
                var n = program.Nodes[i];
                _nullable[i] = n.Kind switch
                {
                    EcmaNodeKind.Literal or EcmaNodeKind.Set => false,
                    EcmaNodeKind.Sequence => _nullable[n.X] && _nullable[n.Y],
                    EcmaNodeKind.Alternative => _nullable[n.X] || _nullable[n.Y],
                    EcmaNodeKind.Repeat => n.Min == 0 || _nullable[n.X],
                    EcmaNodeKind.Capture => _nullable[n.X],
                    _ => true
                };
            }
        }

        internal bool TryBuild(out KernelNode[]? nodes, out string reason)
        {
            nodes = null;
            reason = string.Empty;
            // Node 0 is the entry: it opens group 0 and falls into the pattern; the pattern ends by closing group 0.
            Alloc(default);
            var match = Alloc(new() { Op = KernelOp.Match });
            var end = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = 0, Next = match });
            var body = Gen(_program.Root, end);
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
            var fold = (n.EffectiveFlags & EcmaScriptFlags.IgnoreCase) != 0;
            switch (n.Kind)
            {
                case EcmaNodeKind.Empty:
                    return next;
                case EcmaNodeKind.Literal:
                    return GenLiteral(n.Value, fold, next);
                case EcmaNodeKind.Set:
                    {
                        var set = _program.Sets[n.Value];
                        if (set.StringCount > 0)
                        { Fail("A set with string alternatives is not lowered."); return next; }
                        var (cls, sensitive) = ClassOf(n.Value, fold);
                        if (cls is null)
                        {
                            return next;
                        }

                        return Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls, SurrogateSensitive = sensitive });
                    }
                case EcmaNodeKind.Sequence:
                    return Gen(n.X, Gen(n.Y, next));
                case EcmaNodeKind.Alternative:
                    {
                        var y = Gen(n.Y, next);
                        var x = Gen(n.X, next);
                        return Alloc(new() { Op = KernelOp.Split, Next = x, Alternate = y });
                    }
                case EcmaNodeKind.Capture:
                    {
                        var close = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = n.Value, Next = next });
                        var inner = Gen(n.X, close);
                        return Alloc(new() { Op = KernelOp.CaptureStart, Operand = n.Value, Next = inner });
                    }
                case EcmaNodeKind.Repeat:
                    return GenRepeat(n, next);
                case EcmaNodeKind.Start:
                    return (n.EffectiveFlags & EcmaScriptFlags.Multiline) != 0
                        ? Alloc(new() { Op = KernelOp.AssertLineStart, Next = next, Operand = 0 })
                        : Alloc(new() { Op = KernelOp.AssertStart, Next = next });
                case EcmaNodeKind.End:
                    return (n.EffectiveFlags & EcmaScriptFlags.Multiline) != 0
                        ? Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 0 })
                        : Alloc(new() { Op = KernelOp.AssertEnd, Next = next });
                case EcmaNodeKind.WordBoundary:
                case EcmaNodeKind.NotWordBoundary:
                    {
                        var word = WordClass(fold && _unicode);
                        if (word is null)
                        {
                            return next;
                        }

                        return Alloc(new()
                        {
                            Op = n.Kind == EcmaNodeKind.WordBoundary ? KernelOp.WordBoundary : KernelOp.NotWordBoundary,
                            Next = next,
                            Class = word
                        });
                    }
                case EcmaNodeKind.Backreference:
                    Fail("Backreferences are not lowered.");
                    return next;
                default:
                    Fail("Lookaround assertions are not lowered.");
                    return next;
            }
        }

        private int GenLiteral(int value, bool fold, int next)
        {
            if (value > 0xFFFF || (_unicode && value is >= 0xD800 and <= 0xDFFF))
            {
                Fail("A literal outside the BMP or a lone surrogate is not lowered.");
                return next;
            }
            if (!fold)
            {
                return Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = value });
            }

            if (!_literals.TryGetValue((value, true), out var cached))
            {
                var members = FoldMembers(value);
                if (members.Count == 1)
                {
                    cached = (null, false);
                }
                else
                {
                    var cls = MakeClass(ToRanges(members), out var ok);
                    cached = ok ? (cls, false) : default;
                    if (!ok)
                    {
                        Fail("A case-insensitive literal class exceeds the class bound.");
                    }
                }
                _literals[(value, true)] = cached;
            }
            if (_failure is not null)
            {
                return next;
            }

            return cached.Class is null
                ? Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = value })
                : Alloc(new() { Op = KernelOp.Class, Next = next, Class = cached.Class });
        }

        private int GenRepeat(EcmaNode n, int next)
        {
            var child = _program.Nodes[n.X];
            var childNullable = _nullable[n.X];
            if (childNullable && n.Max != n.Min)
            {
                Fail("A loop whose body can match the empty string is not lowered.");
                return next;
            }
            int Body(int target)
            {
                var entry = Gen(n.X, target);
                if (_failure is null && child.CaptureCount > 0)
                {
                    entry = Alloc(new() { Op = KernelOp.Clear, Next = entry, Operand = child.FirstCapture, Count = child.CaptureCount });
                }

                return entry;
            }

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
                for (long i = n.Max - n.Min; i > 0 && _failure is null; i--)
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

        // Canonicalize as the interpreter does: Unicode mode folds simple case, legacy mode uppercases.
        private ReadOnlySpan<EcmaCaseMapping> Mappings => _unicode ? _tables.UnicodeFold : _tables.LegacyUppercase;

        private int Canonical(int point)
        {
            var map = Mappings;
            int lo = 0, hi = map.Length - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (point < map[mid].Source)
                {
                    hi = mid - 1;
                }
                else if (point > map[mid].Source)
                {
                    lo = mid + 1;
                }
                else
                {
                    return map[mid].Target;
                }
            }
            return point;
        }

        // Every BMP code unit whose canonical form equals that of the literal.
        private List<int> FoldMembers(int value)
        {
            var target = Canonical(value);
            var members = new List<int>();
            var targetIsSource = false;
            foreach (var m in Mappings)
            {
                if (m.Source == target)
                {
                    targetIsSource = true;
                }

                if (m.Source <= 0xFFFF && m.Target == target)
                {
                    members.Add(m.Source);
                }
            }
            if (!targetIsSource && target <= 0xFFFF)
            {
                members.Add(target);
            }

            if (!members.Contains(value))
            {
                members.Add(value);
            }

            members.Sort();
            return members;
        }

        private static List<CharacterRange> ToRanges(List<int> sorted)
        {
            var ranges = new List<CharacterRange>();
            foreach (var c in sorted)
            {
                if (ranges.Count > 0 && ranges[^1].Last == c - 1)
                {
                    ranges[^1] = new(ranges[^1].First, c);
                }
                else if (ranges.Count == 0 || ranges[^1].Last < c)
                {
                    ranges.Add(new(c, c));
                }
            }
            return ranges;
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
            // Pick the smaller of the set and its BMP complement; a negated class keeps dot-like sets compact.
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

        private (CharacterClass? Class, bool Sensitive) ClassOf(int setId, bool fold)
        {
            if (_sets.TryGetValue((setId, fold), out var cached))
            {
                return cached;
            }

            var set = _program.Sets[setId];
            var intervals = _program.Intervals.Slice(set.IntervalOffset, set.IntervalCount);
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
            if (fold)
            {
                var source = (bool[])member.Clone();
                foreach (var m in Mappings)
                {
                    if (m.Source <= 0xFFFF)
                    {
                        member[m.Source] = m.Target <= 0xFFFF && source[m.Target] || m.Target > 0xFFFF && ContainsPoint(intervals, m.Target);
                    }
                }
            }
            if (_unicode)
            {
                for (var c = 0xD800; c <= 0xDFFF; c++)
                {
                    sensitive |= member[c];
                }

                if (sensitive)
                {
                    Array.Fill(member, true, 0xD800, 0x800);
                }
            }
            var cls = MakeClass(ToRanges(member), out var ok);
            if (!ok)
            { Fail("A character set exceeds the class bound."); return default; }
            return _sets[(setId, fold)] = (cls!, sensitive);
        }

        private static bool ContainsPoint(ReadOnlySpan<EcmaInterval> intervals, int point)
        {
            foreach (var iv in intervals)
            {
                if (point >= iv.First && point <= iv.Last)
                {
                    return true;
                }
            }

            return false;
        }

        // ASCII word characters; under Unicode ignoreCase the input is folded first, which also admits U+017F and U+212A.
        private CharacterClass? WordClass(bool fold)
        {
            if (_words.TryGetValue(fold, out var cached))
            {
                return cached;
            }

            var member = new bool[0x10000];
            for (var c = 0; c < 128; c++)
            {
                member[c] = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
            }

            if (fold)
            {
                var ascii = (bool[])member.Clone();
                foreach (var m in Mappings)
                {
                    if (m.Source <= 0xFFFF)
                    {
                        member[m.Source] = m.Target < 128 && ascii[m.Target];
                    }
                }
            }
            var cls = MakeClass(ToRanges(member), out var ok);
            if (!ok)
            { Fail("The word class exceeds the class bound."); return null; }
            return _words[fold] = cls!;
        }
    }
}
