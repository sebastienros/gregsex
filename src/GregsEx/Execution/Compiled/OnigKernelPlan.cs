#nullable enable
using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Flavors.Oniguruma;
using GregsEx.IR;

namespace GregsEx.Execution.Compiled;

// Oniguruma front end of the kernel framework. It lowers the analysed parse tree (parsed, renumbered and possessified
// exactly as OnigCompiler.Build sees it) for subjects without surrogates: the executor sends any subject holding one to
// the interpreter, so every character is one unit and sets keep their BMP members only. Ignore-case is already resolved
// by the parser (a character's case class; folds to several code points as alternations). Look-behind is Oniguruma's:
// candidate starts nearest first, the body limited to the look-behind's position and ending exactly there, a positive
// look-behind over alternatives of different lengths divided into one look-behind per alternative. Atomic groups and
// possessive repeats are atomic frames. Backreferences, subexpression calls, conditionals, the absent operator, \K,
// text-segment boundaries, non-BMP literals and loops over a nullable body stay on the interpreter.
internal static class OnigKernelPlan
{
    // Returns false only when the pattern does not parse (diagnostic is set); otherwise plan is null with reason set when
    // the pattern is not lowered.
    internal static bool TryPlan(string pattern, OnigurumaProfile profile, RegexCompileLimits limits, in LoweringLimits lowering,
        out CoreKernelPlan? plan, out string reason, out RegexDiagnostic diagnostic)
    {
        plan = null;
        reason = string.Empty;
        diagnostic = default;
        var flags = profile.Options;
        var inline = OnigInlineOptions.None;
        if ((flags & OnigurumaOptions.IgnoreCase) != 0)
        {
            inline |= OnigInlineOptions.IgnoreCase;
        }

        if ((flags & OnigurumaOptions.Multiline) != 0)
        {
            inline |= OnigInlineOptions.DotAll;
        }

        if ((flags & OnigurumaOptions.Extended) != 0)
        {
            inline |= OnigInlineOptions.Extended;
        }

        OnigNode root;
        int groups;
        bool deep;
        try
        {
            root = OnigCompiler.Analyze(pattern, inline, (flags & OnigurumaOptions.CaptureGroup) != 0,
                (flags & OnigurumaOptions.DontCaptureGroup) != 0, limits, out groups, out deep);
        }
        catch (OnigSyntaxException e)
        {
            diagnostic = new RegexDiagnostic(e.Code, e.Offset, e.Length, RegexOffsetUnit.Utf16CodeUnits, e.Message);
            return false;
        }

        if (deep)
        {
            reason = "The pattern nests too deeply for the kernel planner.";
            return true;
        }

        var builder = new Builder(groups, lowering);
        if (!builder.TryBuild(root, out var nodes, out reason))
        {
            return true;
        }

        // The interpreter's search-level prefilters: a required literal, else the required ASCII units; a literal prefix;
        // the minimum length.
        string? required = null;
        OnigCompiler.Required(root, ref required);
        char[]? requiredSet = null;
        if (required is null && OnigCompiler.RequiredUnits(root) is { } units && OnigCompiler.Cost(units) <= OnigCompiler.MaxRequiredCost && !units.NonAscii)
        {
            var chars = new List<char>();
            for (var c = 0; c < 128; c++)
            {
                if (((c < 64 ? units.Low : units.High) & (1UL << c)) != 0)
                {
                    chars.Add((char)c);
                }
            }

            requiredSet = chars.ToArray();
        }

        var prefix = new StringBuilder();
        OnigCompiler.Prefix(root, prefix);
        var (minLength, _) = OnigCompiler.Lengths(root);
        CoreKernelPlan.Analyze(nodes!, groups, scalars: false, dotAll: true, sticky: false, lowering, out plan, out reason,
            requiredSet, required, 0, minLength, prefix.Length >= 2 ? prefix.ToString() : null, surrogateScan: true);
        return true;
    }

    private sealed class Builder
    {
        private const int MaxClassRanges = 512;
        private readonly int _maxNodes;
        private readonly List<KernelNode> _nodes = [];
        private readonly Dictionary<OnigCharSet, CharacterClass> _sets = [];
        private readonly ProfileDescriptor _descriptor;
        private CharacterClass? _dotNoNewline;
        private CharacterClass? _word;
        private CharacterClass? _asciiWord;
        private string? _failure;
        // The next character node is a loop body: a literal becomes a one-member class so the loop fuses into a class run.
        private bool _loopAtom;

        internal Builder(int groups, in LoweringLimits limits)
        {
            _maxNodes = limits.MaxNodes / 16;
            ProfileDescriptor.TryCreate(new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits), RegexInputKind.Utf16, out var descriptor, out _);
            _descriptor = descriptor!;
        }

        internal bool TryBuild(OnigNode root, out KernelNode[]? nodes, out string reason)
        {
            nodes = null;
            reason = string.Empty;
            Alloc(default);
            var match = Alloc(new() { Op = KernelOp.Match });
            var end = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = 0, Next = match });
            var body = Gen(root, end);
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

        private int Gen(OnigNode n, int next)
        {
            if (_failure is not null)
            {
                return next;
            }

            switch (n.Kind)
            {
                case OnigNodeKind.Empty:
                    return next;
                case OnigNodeKind.Char:
                    return GenChar(n, next);
                case OnigNodeKind.Set:
                    {
                        var cls = ClassOf(n.Set!);
                        return cls is null ? next : Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls });
                    }
                case OnigNodeKind.AnyChar:
                    if (n.Flag)
                    {
                        return Alloc(new() { Op = KernelOp.Any, Next = next });
                    }

                    _dotNoNewline ??= MakeClass([new(0, '\n' - 1), new('\n' + 1, 0xFFFF)]);
                    return _dotNoNewline is null ? next : Alloc(new() { Op = KernelOp.Class, Next = next, Class = _dotNoNewline });
                case OnigNodeKind.Anchor:
                    return GenAnchor((OnigAnchor)n.Value, next);
                case OnigNodeKind.Sequence:
                    for (var i = n.Children.Count - 1; i >= 0 && _failure is null; i--)
                    {
                        next = Gen(n.Children[i], next);
                    }

                    return next;
                case OnigNodeKind.Alternation:
                    {
                        var entry = Gen(n.Children[^1], next);
                        for (var i = n.Children.Count - 2; i >= 0 && _failure is null; i--)
                        {
                            var branch = Gen(n.Children[i], next);
                            entry = Alloc(new() { Op = KernelOp.Split, Next = branch, Alternate = entry });
                        }

                        return entry;
                    }
                case OnigNodeKind.Repeat:
                    return GenRepeat(n, next);
                case OnigNodeKind.Capture:
                    {
                        var close = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = n.Value, Next = next });
                        var inner = Gen(n.Child, close);
                        return Alloc(new() { Op = KernelOp.CaptureStart, Operand = n.Value, Next = inner });
                    }
                case OnigNodeKind.Group:
                    return Gen(n.Child, next);
                case OnigNodeKind.Atomic:
                    return GenFrame(n.Child, next, LookFlags.Atomic);
                case OnigNodeKind.Look:
                    return GenLook(n, next);
                case OnigNodeKind.Backref:
                    Fail("Backreferences are not lowered.");
                    return next;
                case OnigNodeKind.Call:
                    Fail("Subexpression calls are not lowered.");
                    return next;
                case OnigNodeKind.CondRef:
                case OnigNodeKind.CondExpr:
                    Fail("Conditionals are not lowered.");
                    return next;
                case OnigNodeKind.Absent:
                    Fail("The absent operator is not lowered.");
                    return next;
                case OnigNodeKind.Keep:
                    Fail("\\K is not lowered.");
                    return next;
                default:
                    Fail("Unknown node kind.");
                    return next;
            }
        }

        private int GenChar(OnigNode n, int next)
        {
            var loopAtom = _loopAtom;
            _loopAtom = false;
            if (n.Value > 0xFFFF)
            {
                Fail("A literal outside the BMP is not lowered.");
                return next;
            }

            if (n.IgnoreCase || loopAtom)
            {
                var members = n.IgnoreCase ? OnigUnicode.CaseClass(n.Value) : [n.Value];
                if (members.Length > 1 || loopAtom)
                {
                    var ranges = new List<CharacterRange>();
                    foreach (var m in members)
                    {
                        if (m <= 0xFFFF)
                        {
                            ranges.Add(new(m, m));
                        }
                    }

                    var cls = MakeClass(ranges);
                    return cls is null ? next : Alloc(new() { Op = KernelOp.Class, Next = next, Class = cls });
                }
            }

            return Alloc(new() { Op = KernelOp.Literal, Next = next, Operand = n.Value });
        }

        private int GenAnchor(OnigAnchor anchor, int next)
        {
            switch (anchor)
            {
                case OnigAnchor.LineStart:
                    // The start, or after a newline that is not the last unit: the XPath style.
                    return Alloc(new() { Op = KernelOp.AssertLineStart, Next = next, Operand = 2 });
                case OnigAnchor.LineEnd:
                    return Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 1 });
                case OnigAnchor.BufferStart:
                    return Alloc(new() { Op = KernelOp.AssertStart, Next = next });
                case OnigAnchor.BufferEnd:
                    return Alloc(new() { Op = KernelOp.AssertEnd, Next = next });
                case OnigAnchor.SemiEnd:
                    return Alloc(new() { Op = KernelOp.AssertLineEnd, Next = next, Operand = 3 });
                case OnigAnchor.SearchStart:
                    return Alloc(new() { Op = KernelOp.AssertSearchStart, Next = next });
                case OnigAnchor.WordBoundary:
                case OnigAnchor.NotWordBoundary:
                case OnigAnchor.AsciiWordBoundary:
                case OnigAnchor.AsciiNotWordBoundary:
                    {
                        var ascii = anchor is OnigAnchor.AsciiWordBoundary or OnigAnchor.AsciiNotWordBoundary;
                        var cls = ascii ? _asciiWord ??= MakeClass(Clip(OnigUnicode.StandaloneWord, 0x7F)) : _word ??= MakeClass(Clip(OnigUnicode.StandaloneWord, 0xFFFF));
                        if (cls is null)
                        {
                            return next;
                        }

                        var boundary = anchor is OnigAnchor.WordBoundary or OnigAnchor.AsciiWordBoundary;
                        return Alloc(new() { Op = boundary ? KernelOp.WordBoundary : KernelOp.NotWordBoundary, Next = next, Class = cls });
                    }
                default:
                    Fail("Text-segment boundaries are not lowered.");
                    return next;
            }
        }

        // A frame around child: an atomic group (flags Atomic), or a look-around whose continuation is next.
        private int GenFrame(OnigNode child, int next, int flags, int min = 0, int max = 0)
        {
            var atomic = (flags & LookFlags.Atomic) != 0;
            var look = Alloc(default);
            var end = Alloc(new() { Op = KernelOp.LookEnd, Operand = look, Next = next, Count = atomic ? 1 : 0 });
            var inner = Gen(child, end);
            if ((flags & LookFlags.Behind) != 0 && _failure is null)
            {
                inner = Alloc(new() { Op = KernelOp.BehindScan, Next = inner, Count = min, Operand = max });
            }

            if (_failure is null)
            {
                _nodes[look] = new() { Op = KernelOp.LookStart, Next = inner, Alternate = atomic ? -1 : next, Operand = flags, Count = atomic ? 1 : 0 };
            }

            return look;
        }

        private int GenLook(OnigNode n, int next)
        {
            var negative = n.Value != 0;
            var body = n.Child;
            while (body.Kind == OnigNodeKind.Group)
            {
                body = body.Child;
            }

            if (!n.Flag)
            {
                return GenFrame(n.Child, next, negative ? LookFlags.Negative : 0);
            }

            // A positive look-behind over alternatives of different lengths is divided as Oniguruma divides it: each
            // alternative is its own look-behind, tried in order, so a nearer start of a later alternative does not win.
            if (!negative && body.Kind == OnigNodeKind.Alternation && !SameFixedLength(body.Children))
            {
                var entry = GenBehind(body.Children[^1], next, negative);
                for (var i = body.Children.Count - 2; i >= 0 && _failure is null; i--)
                {
                    var branch = GenBehind(body.Children[i], next, negative);
                    entry = Alloc(new() { Op = KernelOp.Split, Next = branch, Alternate = entry });
                }

                return entry;
            }

            return GenBehind(n.Child, next, negative);
        }

        private int GenBehind(OnigNode body, int next, bool negative)
        {
            var (min, max) = OnigCompiler.Lengths(body);
            return GenFrame(body, next, (negative ? LookFlags.Negative : 0) | LookFlags.Behind | LookFlags.Limit, min, max);
        }

        private static bool SameFixedLength(List<OnigNode> branches)
        {
            var (min, max) = OnigCompiler.Lengths(branches[0]);
            foreach (var branch in branches)
            {
                var (bmin, bmax) = OnigCompiler.Lengths(branch);
                if (bmin != bmax || bmin != min || bmax != max)
                {
                    return false;
                }
            }

            return true;
        }

        // Whether a repeat's body is one character, which a possessive unbounded repeat lowers to an atomic class run.
        private static bool IsItem(OnigNode node)
            => node.Kind is OnigNodeKind.Char or OnigNodeKind.Set or OnigNodeKind.AnyChar
                || (node.Kind == OnigNodeKind.Group && IsItem(node.Child));

        private int GenRepeat(OnigNode n, int next)
        {
            var child = n.Child;
            int min = n.Min, max = n.Max;
            if (max == 0)
            {
                // x{0} matches empty; a called group inside would still exist, but calls are not lowered.
                return next;
            }

            if (min == 1 && max == 1)
            {
                return Gen(child, next);
            }

            if (OnigCompiler.Lengths(child).Min == 0 && max != min)
            {
                Fail("A loop whose body can match the empty string is not lowered.");
                return next;
            }

            if (n.Mode == OnigRepeatMode.Possessive && !(max < 0 && IsItem(child)))
            {
                // A possessive repeat is the greedy repeat inside an atomic group, as the interpreter runs it.
                var look = Alloc(default);
                var end = Alloc(new() { Op = KernelOp.LookEnd, Operand = look, Next = next, Count = 1 });
                var inner = GenLoop(child, min, max, OnigRepeatMode.Greedy, end);
                if (_failure is null)
                {
                    _nodes[look] = new() { Op = KernelOp.LookStart, Next = inner, Alternate = -1, Operand = LookFlags.Atomic, Count = 1 };
                }

                return look;
            }

            return GenLoop(child, min, max, n.Mode, next);
        }

        private int GenLoop(OnigNode child, int min, int max, OnigRepeatMode mode, int next)
        {
            var lazy = mode == OnigRepeatMode.Lazy;
            // The interpreter keeps the previous iteration's captures, so the body has no Clear.
            int tail;
            if (max < 0)
            {
                var loop = Alloc(default);
                var unwrapped = child;
                while (unwrapped.Kind == OnigNodeKind.Group)
                {
                    unwrapped = unwrapped.Child;
                }

                _loopAtom = unwrapped.Kind == OnigNodeKind.Char;
                var body = Gen(child, loop);
                _loopAtom = false;
                _nodes[loop] = lazy
                    ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                    : new() { Op = KernelOp.Split, Next = body, Alternate = next, Possessive = mode == OnigRepeatMode.Possessive };
                tail = loop;
            }
            else
            {
                tail = next;
                for (var i = (long)max - min; i > 0 && _failure is null; i--)
                {
                    var body = Gen(child, tail);
                    tail = Alloc(lazy
                        ? new() { Op = KernelOp.Split, Next = next, Alternate = body }
                        : new() { Op = KernelOp.Split, Next = body, Alternate = next });
                }
            }

            for (long i = min; i > 0 && _failure is null; i--)
            {
                tail = Gen(child, tail);
            }

            return tail;
        }

        private static List<CharacterRange> Clip(OnigCharSet set, int last)
        {
            var ranges = new List<CharacterRange>();
            var r = set.Ranges;
            for (var i = 0; i < r.Length; i += 2)
            {
                if (r[i] > last)
                {
                    break;
                }

                ranges.Add(new(r[i], Math.Min(r[i + 1], last)));
            }

            return ranges;
        }

        private CharacterClass? ClassOf(OnigCharSet set)
        {
            if (_sets.TryGetValue(set, out var cached))
            {
                return cached;
            }

            var cls = MakeClass(Clip(set, 0xFFFF));
            if (cls is not null)
            {
                _sets[set] = cls;
            }

            return cls;
        }

        private CharacterClass? MakeClass(List<CharacterRange> positive)
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
    }
}
