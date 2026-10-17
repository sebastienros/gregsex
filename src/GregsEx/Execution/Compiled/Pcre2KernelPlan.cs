#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using GregsEx.IR;

namespace GregsEx.Execution.Compiled;

// PCRE2 front end of the kernel framework, for a deliberately restricted subset: 16-bit subjects, LF newlines, and a tree made of
// literals, classes, dot, sequences, alternatives, greedy/lazy repeats, capture groups and the simple anchors. Everything
// else (possessive/atomic groups, assertions, backreferences, subroutines, conditionals, verbs, callouts, \K, \G, \C, \R,
// graphemes, script runs) stays on the interpreter, which also remains the owner of resource-limit diagnostics.
// Atoms are resolved here, once, into exact BMP membership tables by evaluating the interpreter's own character predicates
// for every code unit, so case folding, UCP properties and character tables cannot drift from the interpreter.
internal static class Pcre2KernelPlan
{
    internal static bool TryCreate(Pcre2Program program, in LoweringLimits limits, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        if (!IsEligible(program, out reason))
        {
            return false;
        }

        var builder = new Builder(program, limits);
        // The program's profile also carries the anchoring PCRE2 infers for patterns that begin with ^, which is invisible
        // from an explicit PCRE2_ANCHORED. Both agree with an unanchored kernel only when every path starts with ^.
        if ((program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0 && !builder.StartsAnchored(program.Root))
        {
            reason = "An anchored search that does not begin with ^ is not lowered.";
            return false;
        }
        if (!builder.TryBuild(out var nodes, out reason))
        {
            return false;
        }

        return CoreKernelPlan.Analyze(nodes!, program.GroupCount, scalars: false, dotAll: false, sticky: false, limits, out plan, out reason);
    }

    private static bool IsEligible(Pcre2Program program, out string reason)
    {
        reason = string.Empty;
        const Pcre2CompileFlags Unsupported = Pcre2CompileFlags.Endanchored | Pcre2CompileFlags.Firstline |
            Pcre2CompileFlags.MatchInvalidUtf | Pcre2CompileFlags.NoUtfCheck;
        if (!program.IsExecutable
            || program.Profile.Width != Pcre2CodeUnitWidth.Sixteen
            || program.Profile.Newline != Pcre2Newline.Lf
            || (program.Profile.Flags & Unsupported) != 0
            || program.PatternMatchFlags != 0
            || program.HasBackslashC
            || !program.Callouts.IsEmpty || !program.Controls.IsEmpty || !program.Marks.IsEmpty || !program.Conditions.IsEmpty
            || !program.References.IsEmpty || !program.ScanReferences.IsEmpty || !program.Assertions.IsEmpty)
        {
            reason = "The PCRE2 profile or program uses features outside the compiled subset.";
            return false;
        }
        return true;
    }

    private sealed class Builder
    {
        private const int MaxClassRanges = 512;
        private readonly Pcre2Program _program;
        private readonly Pcre2Node[] _tree;
        private readonly int[] _children;
        private readonly bool[] _nullable;
        private readonly int _maxNodes;
        private readonly List<KernelNode> _nodes = [];
        private readonly Dictionary<(int, Pcre2CompileFlags, Pcre2ExtraFlags, int), CharacterClass> _sets = [];
        private readonly ProfileDescriptor _descriptor;
        private string? _failure;
        private bool _loopAtom;

        internal Builder(Pcre2Program program, in LoweringLimits limits)
        {
            _program = program;
            _tree = program.Nodes.ToArray();
            _children = program.Children.ToArray();
            _maxNodes = limits.MaxNodes / 16;
            ProfileDescriptor.TryCreate(new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16, out var descriptor, out _);
            _descriptor = descriptor!;
            _nullable = new bool[_tree.Length];
            for (var i = 0; i < _tree.Length; i++)
            {
                var n = _tree[i];
                var kids = Kids(n);
                bool all = true, any = false;
                foreach (var k in kids)
                { all &= _nullable[k]; any |= _nullable[k]; }
                _nullable[i] = n.Kind switch
                {
                    Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot => false,
                    Pcre2NodeKind.Sequence => all,
                    Pcre2NodeKind.Alternative => any,
                    Pcre2NodeKind.Repeat => n.Min == 0 || _nullable[kids[0]],
                    Pcre2NodeKind.Capture => _nullable[kids[0]],
                    _ => true
                };
            }
        }

        internal bool StartsAnchored(int index)
        {
            var n = _tree[index];
            var kids = Kids(n);
            switch (n.Kind)
            {
                case Pcre2NodeKind.AbsoluteStart:
                    return true;
                case Pcre2NodeKind.Start:
                    return (n.Flags & (Pcre2CompileFlags.Multiline | Pcre2CompileFlags.AltCircumflex)) == 0;
                case Pcre2NodeKind.Sequence:
                    return kids.Length > 0 && StartsAnchored(kids[0]);
                case Pcre2NodeKind.Alternative:
                    foreach (var k in kids)
                    {
                        if (!StartsAnchored(k))
                        {
                            return false;
                        }
                    }

                    return kids.Length > 0;
                case Pcre2NodeKind.Capture:
                    return StartsAnchored(kids[0]);
                case Pcre2NodeKind.Repeat:
                    return n.Min >= 1 && StartsAnchored(kids[0]);
                default:
                    return false;
            }
        }

        private ReadOnlySpan<int> Kids(Pcre2Node n) => _children.AsSpan(n.Children.Offset, n.Children.Count);

        internal bool TryBuild(out KernelNode[]? nodes, out string reason)
        {
            nodes = null;
            reason = string.Empty;
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

            var n = _tree[index];
            switch (n.Kind)
            {
                case Pcre2NodeKind.Empty:
                    return next;
                case Pcre2NodeKind.Literal:
                    return GenLiteral(n, next);
                case Pcre2NodeKind.Class:
                    return GenSet(n, n.Operand, next);
                case Pcre2NodeKind.Dot:
                    return GenSet(n, -1, next);
                case Pcre2NodeKind.Sequence:
                    {
                        var kids = Kids(n);
                        for (var i = kids.Length - 1; i >= 0 && _failure is null; i--)
                        {
                            next = Gen(kids[i], next);
                        }

                        return next;
                    }
                case Pcre2NodeKind.Alternative:
                    {
                        var kids = Kids(n);
                        var entry = Gen(kids[^1], next);
                        for (var i = kids.Length - 2; i >= 0 && _failure is null; i--)
                        {
                            var branch = Gen(kids[i], next);
                            entry = Alloc(new() { Op = KernelOp.Split, Next = branch, Alternate = entry });
                        }
                        return entry;
                    }
                case Pcre2NodeKind.Capture:
                    {
                        var number = _program.Groups[n.Operand].Number;
                        if (number == 0)
                        {
                            return Gen(Kids(n)[0], next);
                        }

                        var close = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = number, Next = next });
                        var inner = Gen(Kids(n)[0], close);
                        return Alloc(new() { Op = KernelOp.CaptureStart, Operand = number, Next = inner });
                    }
                case Pcre2NodeKind.Repeat:
                    return GenRepeat(n, next);
                case Pcre2NodeKind.AbsoluteStart:
                    return Alloc(new() { Op = KernelOp.AssertStart, Next = next });
                case Pcre2NodeKind.Start:
                    if ((n.Flags & Pcre2CompileFlags.Multiline) == 0)
                    {
                        return Alloc(new() { Op = KernelOp.AssertStart, Next = next });
                    }

                    if ((n.Flags & Pcre2CompileFlags.AltCircumflex) != 0)
                    {
                        Fail("PCRE2_ALT_CIRCUMFLEX is not lowered.");
                        return next;
                    }
                    return Alloc(new() { Op = KernelOp.AssertLineStart, Next = next, Operand = 2 });
                case Pcre2NodeKind.AbsoluteEnd:
                    return Alloc(new() { Op = KernelOp.AssertEnd, Next = next });
                case Pcre2NodeKind.BeforeFinalNewline:
                    return Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 3 });
                case Pcre2NodeKind.End:
                    if ((n.Flags & Pcre2CompileFlags.Multiline) != 0)
                    {
                        return Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 1 });
                    }

                    return (n.Flags & Pcre2CompileFlags.DollarEndonly) != 0
                        ? Alloc(new() { Op = KernelOp.AssertEnd, Next = next })
                        : Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 3 });
                case Pcre2NodeKind.WordBoundary:
                case Pcre2NodeKind.NotWordBoundary:
                    {
                        var cls = SetOf(n, -2);
                        if (cls is null)
                        {
                            return next;
                        }

                        return Alloc(new()
                        {
                            Op = n.Kind == Pcre2NodeKind.WordBoundary ? KernelOp.WordBoundary : KernelOp.NotWordBoundary,
                            Next = next,
                            Class = cls
                        });
                    }
                default:
                    Fail($"The PCRE2 node kind {n.Kind} is not lowered.");
                    return next;
            }
        }

        private int GenLiteral(Pcre2Node n, int next)
        {
            if (n.Operand > 0xFFFF)
            {
                Fail("A literal outside the BMP is not lowered.");
                return next;
            }
            var loopAtom = _loopAtom;
            _loopAtom = false;
            if ((n.Flags & Pcre2CompileFlags.Caseless) == 0 && !loopAtom)
            {
                return Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = n.Operand });
            }

            var data = Pcre2Data.Instance.Bind(_program.Profile.CharacterTables);
            var member = new bool[0x10000];
            var count = 0;
            for (var c = 0; c <= 0xFFFF; c++)
            {
                if (member[c] = EqualUncharged(n.Operand, c, n.Flags, n.ExtraFlags, data))
                {
                    count++;
                }
            }

            if (count == 1 && member[n.Operand] && !loopAtom)
            {
                return Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = n.Operand });
            }

            var cls = MakeClass(member);
            if (cls is null)
            {
                return next;
            }

            return Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls });
        }

        private int GenSet(Pcre2Node n, int id, int next)
        {
            var cls = SetOf(n, id);
            return cls is null ? next : Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls });
        }

        // id >= 0: a PCRE2 class table entry; -1: dot; -2: the word-character set.
        private CharacterClass? SetOf(Pcre2Node n, int id)
        {
            if (_failure is not null)
            {
                return null;
            }

            var key = (id, n.Flags, n.ExtraFlags, 0);
            if (_sets.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var data = Pcre2Data.Instance.Bind(_program.Profile.CharacterTables);
            var member = new bool[0x10000];
            for (var c = 0; c <= 0xFFFF; c++)
            {
                member[c] = id switch
                {
                    -1 => (n.Flags & Pcre2CompileFlags.Dotall) != 0 || c != '\n',
                    -2 => Pcre2CharacterOps.Word(c, n.Flags, n.ExtraFlags, data),
                    _ => ClassUncharged(n, id, c, data)
                };
            }

            var cls = MakeClass(member);
            if (cls is not null)
            {
                _sets[key] = cls;
            }

            return cls;
        }

        private CharacterClass? MakeClass(bool[] member)
        {
            var positive = new List<CharacterRange>();
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

                positive.Add(new(first, c));
            }
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
            if (chosen.Count > MaxClassRanges)
            {
                Fail("A character class exceeds the class bound.");
                return null;
            }
            var budget = new CompileBudget(new RegexCompileLimits());
            if (!CharacterClass.TryCreate(chosen.ToArray(), negate, _descriptor, ref budget, default, out var cls, out _))
            {
                Fail("A character class could not be built.");
                return null;
            }
            return cls;
        }

        private int GenRepeat(Pcre2Node n, int next)
        {
            var child = Kids(n)[0];
            var possessive = n.Quantifier == Pcre2Quantifier.Possessive;
            // Possessive loops over one unit (including the interpreter's automatic possessification) are lowered as greedy
            // loops; the plan analysis rejects them unless the continuation is disjoint, which is exactly when they agree.
            if (possessive && (n.Max >= 0 || _tree[child].Kind is not (Pcre2NodeKind.Literal or Pcre2NodeKind.Class or Pcre2NodeKind.Dot)))
            {
                Fail("Possessive quantifiers over groups or bounded counts are not lowered.");
                return next;
            }
            _loopAtom = _tree[child].Kind == Pcre2NodeKind.Literal;
            if (_nullable[child] && n.Max != n.Min)
            {
                Fail("A loop whose body can match the empty string is not lowered.");
                return next;
            }
            var lazy = n.Quantifier == Pcre2Quantifier.Lazy;
            // The interpreter keeps the previous iteration's captures, so the body has no Clear.
            int tail;
            if (n.Max < 0)
            {
                var loop = Alloc(default);
                var body = Gen(child, loop);
                _nodes[loop] = lazy
                    ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                    : new() { Op = KernelOp.Split, Next = body, Alternate = next, Possessive = possessive };
                tail = loop;
            }
            else
            {
                tail = next;
                for (var i = (long)n.Max - n.Min; i > 0 && _failure is null; i--)
                {
                    var body = Gen(child, tail);
                    tail = Alloc(lazy
                        ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                        : new() { Op = KernelOp.Split, Next = body, Alternate = next });
                }
            }
            for (long i = n.Min; i > 0 && _failure is null; i--)
            {
                tail = Gen(child, tail);
            }

            return tail;
        }

        // The interpreter's character predicates without the work charging.
        private static bool EqualUncharged(int expected, int actual, Pcre2CompileFlags flags, Pcre2ExtraFlags extra, Pcre2DataView data)
        {
            if (expected == actual)
            {
                return true;
            }

            if ((flags & Pcre2CompileFlags.Caseless) == 0)
            {
                return false;
            }

            if ((extra & Pcre2ExtraFlags.CaselessRestrict) != 0 && (expected < 128) != (actual < 128))
            {
                return false;
            }

            if ((extra & Pcre2ExtraFlags.TurkishCasing) != 0)
            {
                if (expected is 0x49 or 0x131)
                {
                    return actual is 0x49 or 0x131;
                }

                if (expected is 0x69 or 0x130)
                {
                    return actual is 0x69 or 0x130;
                }
            }
            if ((flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0)
            {
                return expected <= 255 && actual <= 255 && data.LowByteTables[Pcre2DataAbi.LowerOffset + expected] ==
                    data.LowByteTables[Pcre2DataAbi.LowerOffset + actual];
            }

            var record = Pcre2CharacterOps.Record(expected, data);
            if (expected < 128 && (record.CaseSetCount == 0 || (extra & Pcre2ExtraFlags.CaselessRestrict) != 0))
            {
                return actual <= 255 && data.LowByteTables[Pcre2DataAbi.LowerOffset + expected] ==
                    data.LowByteTables[Pcre2DataAbi.LowerOffset + actual];
            }

            if (expected + record.OtherCaseDelta == actual)
            {
                return true;
            }

            foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
            {
                if (equivalent == actual)
                {
                    return true;
                }
            }

            return false;
        }

        private bool ClassUncharged(Pcre2Node node, int id, int cp, Pcre2DataView data)
        {
            var scratch = id < 128 ? stackalloc byte[id + 1] : new byte[id + 1];
            for (var i = 0; i <= id; i++)
            {
                var item = _program.Classes[i];
                var value = item.Kind switch
                {
                    Pcre2ClassKind.Intervals => IntervalsUncharged(_program.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count),
                        cp, node.Flags, node.ExtraFlags, data),
                    Pcre2ClassKind.Property => Pcre2CharacterOps.Property(cp, item.Property, data),
                    Pcre2ClassKind.TableClass => TableUncharged(cp, item.TableClass, node.Flags, data),
                    Pcre2ClassKind.Union => scratch[item.Left] != 0 || scratch[item.Right] != 0,
                    Pcre2ClassKind.Intersection => scratch[item.Left] != 0 && scratch[item.Right] != 0,
                    Pcre2ClassKind.Subtraction => scratch[item.Left] != 0 && scratch[item.Right] == 0,
                    Pcre2ClassKind.SymmetricDifference => (scratch[item.Left] != 0) != (scratch[item.Right] != 0),
                    Pcre2ClassKind.Complement => scratch[item.Left] == 0,
                    _ => throw new InvalidOperationException("Unknown class operation.")
                };
                scratch[i] = value ? (byte)1 : (byte)0;
            }
            return scratch[id] != 0;
        }

        private static bool TableUncharged(int cp, int table, Pcre2CompileFlags flags, Pcre2DataView data)
        {
            if (cp > 255)
            {
                return false;
            }

            var original = (data.LowByteTables[Pcre2DataAbi.ClassOffset + table * 32 + cp / 8] & (1 << (cp % 8))) != 0;
            if (original || (flags & Pcre2CompileFlags.Caseless) == 0 || table is not (3 or 4))
            {
                return original;
            }

            var other = table == 3 ? 4 : 3;
            return (data.LowByteTables[Pcre2DataAbi.ClassOffset + other * 32 + cp / 8] & (1 << (cp % 8))) != 0;
        }

        private static bool IntervalsUncharged(ReadOnlySpan<Pcre2Interval> intervals, int cp, Pcre2CompileFlags flags,
            Pcre2ExtraFlags extra, Pcre2DataView data)
        {
            if (Contains(intervals, cp))
            {
                return true;
            }

            if ((flags & Pcre2CompileFlags.Caseless) == 0)
            {
                return false;
            }

            if ((extra & Pcre2ExtraFlags.TurkishCasing) != 0 && cp is 0x49 or 0x69 or 0x130 or 0x131)
            {
                var partner = cp switch { 0x49 => 0x131, 0x131 => 0x49, 0x69 => 0x130, _ => 0x69 };
                return Contains(intervals, partner);
            }
            if ((flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0)
            {
                return cp <= 255 && Contains(intervals, data.LowByteTables[Pcre2DataAbi.FlipOffset + cp]);
            }

            var record = Pcre2CharacterOps.Record(cp, data);
            var other = cp + record.OtherCaseDelta;
            if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (other < 128)) && Contains(intervals, other))
            {
                return true;
            }

            foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
            {
                if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (equivalent < 128)) && Contains(intervals, equivalent))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Contains(ReadOnlySpan<Pcre2Interval> intervals, int cp)
        {
            int low = 0, high = intervals.Length - 1;
            while (low <= high)
            {
                var mid = low + (high - low) / 2;
                var item = intervals[mid];
                if (cp < item.First)
                {
                    high = mid - 1;
                }
                else if (cp > item.Last)
                {
                    low = mid + 1;
                }
                else
                {
                    return true;
                }
            }
            return false;
        }
    }
}
