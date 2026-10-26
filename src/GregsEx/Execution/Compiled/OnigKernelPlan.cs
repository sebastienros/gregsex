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
// possessive repeats are atomic frames. Subexpression calls are Call/CallReturn nodes with the interpreter's rules: a
// call frame holds the group, the return address and the open starts of the groups the callee re-enters, the group's
// CaptureEnd returns when the innermost frame is a call of that group, and a call nested at Oniguruma's
// SubexpCallMaxNestLevel fails. Backreferences, conditionals, the absent operator, \K, text-segment boundaries,
// non-BMP literals and loops over a nullable body stay on the interpreter.
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
        // Subexpression calls: the capture node of each group, the called groups, each called group's entry node (the
        // CaptureStart the compiler's calls jump to: the last emitted in the interpreter, so the first generated here,
        // generation running back to front), its CallReturn node and the Call nodes awaiting their entry.
        private readonly Dictionary<int, OnigNode> _captures = [];
        private readonly HashSet<int> _called = [];
        private readonly Dictionary<int, int> _entries = [];
        private readonly Dictionary<int, int> _returns = [];
        private readonly List<int> _calls = [];
        private OnigNode? _root;

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
            _root = root;
            Survey(root);
            Alloc(default);
            var match = Alloc(new() { Op = KernelOp.Match });
            var end = match;
            if (_called.Contains(0))
            {
                // \g<0>: the whole pattern is the callee, entered at node 0 and returning before Match. The interpreter
                // saves no group start for it; the kernel saves the match start, which its Match reports.
                end = Alloc(new() { Op = KernelOp.CallReturn, Operand = 0, Next = match, Groups = [0] });
                _returns[0] = end;
                _entries[0] = 0;
            }

            end = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = 0, Next = end });
            var body = Gen(root, end);
            if (_failure is not null)
            { reason = _failure; return false; }
            _nodes[0] = new() { Op = KernelOp.CaptureStart, Operand = 0, Next = body };
            foreach (var call in _calls)
            {
                var node = _nodes[call];
                if (!_entries.TryGetValue(node.Operand, out var entry))
                {
                    reason = "A subexpression call has no callee.";
                    return false;
                }

                node.Alternate = entry;
                node.Groups = _nodes[_returns[node.Operand]].Groups;
                _nodes[call] = node;
            }

            foreach (var (group, at) in _returns)
            {
                var node = _nodes[at];
                node.Targets = _calls.Where(c => _nodes[c].Operand == group).ToArray();
                _nodes[at] = node;
            }

            nodes = [.. _nodes];
            return true;
        }

        // The capture nodes by group and the called groups, before generation: a call may precede its callee.
        private void Survey(OnigNode node)
        {
            if (node.Kind == OnigNodeKind.Capture)
            {
                _captures[node.Value] = node;
            }
            else if (node.Kind == OnigNodeKind.Call)
            {
                _called.Add(node.Value);
            }

            foreach (var child in node.Children)
            {
                Survey(child);
            }
        }

        // The groups a call of group saves, as the interpreter's CallSaves: the group and those inside it.
        private int[] SavedGroups(int group)
        {
            var groups = new List<int>();
            Collect(_captures[group]);
            return groups.ToArray();

            void Collect(OnigNode n)
            {
                if (n.Kind == OnigNodeKind.Capture)
                {
                    groups.Add(n.Value);
                }

                foreach (var child in n.Children)
                {
                    Collect(child);
                }
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

        // The minimum length of node with a call measuring its group (a recursive call as empty): a lower bound of the
        // true minimum, so a positive result proves the node consumes. The compiler's Lengths counts a call as empty.
        private int MinLength(OnigNode node, HashSet<int> visiting)
        {
            switch (node.Kind)
            {
                case OnigNodeKind.Call:
                    if (!visiting.Add(node.Value))
                    {
                        return 0;
                    }

                    return node.Value == 0 ? MinLength(_root!, visiting)
                        : _captures.TryGetValue(node.Value, out var callee) ? MinLength(callee, visiting) : 0;
                case OnigNodeKind.Sequence:
                    {
                        var sum = 0;
                        foreach (var child in node.Children)
                        {
                            sum = Math.Min(int.MaxValue / 2, sum + MinLength(child, visiting));
                        }

                        return sum;
                    }
                case OnigNodeKind.Alternation:
                    {
                        var min = int.MaxValue;
                        foreach (var child in node.Children)
                        {
                            min = Math.Min(min, MinLength(child, [.. visiting]));
                        }

                        return min;
                    }
                case OnigNodeKind.Repeat:
                    return node.Min == 0 ? 0 : (int)Math.Min(int.MaxValue / 2, (long)node.Min * MinLength(node.Child, visiting));
                case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                    return MinLength(node.Child, visiting);
                default:
                    return OnigCompiler.Lengths(node).Min;
            }
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
                        if (_called.Contains(n.Value))
                        {
                            // The interpreter's RetIfCalled after the group's SaveClose.
                            next = Alloc(new() { Op = KernelOp.CallReturn, Operand = n.Value, Next = next, Groups = SavedGroups(n.Value) });
                            if (!_returns.ContainsKey(n.Value))
                            {
                                _returns[n.Value] = next;
                            }
                        }

                        var close = Alloc(new() { Op = KernelOp.CaptureEnd, Operand = n.Value, Next = next });
                        var inner = Gen(n.Child, close);
                        var open = Alloc(new() { Op = KernelOp.CaptureStart, Operand = n.Value, Next = inner });
                        if (_called.Contains(n.Value) && !_entries.ContainsKey(n.Value))
                        {
                            _entries[n.Value] = open;
                        }

                        return open;
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
                    {
                        // The entry is patched once the callee is generated (it may follow the call). A call whose group can
                        // match empty is an epsilon step for the plan's cycle check.
                        var callee = n.Value == 0 ? _root! : _captures[n.Value];
                        var nullable = MinLength(callee, [n.Value]) == 0;
                        var call = Alloc(new() { Op = KernelOp.Call, Operand = n.Value, Next = next, Alternate = -1, Count = nullable ? 1 : 0 });
                        _calls.Add(call);
                        return call;
                    }
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
                // x{0} matches empty, but a called group inside still exists: its code is reached only through calls.
                if (ContainsCalled(child))
                {
                    Gen(child, next);
                }

                return next;
            }

            if (min == 1 && max == 1)
            {
                return Gen(child, next);
            }

            if (max != min && (_called.Count == 0 ? OnigCompiler.Lengths(child).Min : MinLength(child, [])) == 0)
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
