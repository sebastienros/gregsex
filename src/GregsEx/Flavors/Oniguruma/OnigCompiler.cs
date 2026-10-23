#nullable enable
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;

namespace GregsEx.Flavors.Oniguruma;

// Lowers a parse tree to OnigProgram instructions. Layouts (operands after the opcode):
//   Char c | CharIC2 a b | Str offset length | Set s | SetUnit s | AnyNL | Any | Anchor kind | Keep | Fail | Match
//   Jmp target | Split first second | SplitLazy body exit | Alt first second firstBits secondBits (-1: any)
//   Run item arg min max mode follow (follow: the bits of what may come next, or -1)
//   SaveOpen n | SaveClose n | RetIfCalled n | Call target n
//   LoopInit r | LoopHead r min max lazy exit | LoopEnter r snapshot groups | LoopTail r head exit snapshot groups
//   (groups: index into LoopGroups, or -1; snapshot: offset of the loop's capture snapshot)
//   AtomicStart id | AtomicEnd id | LookStart negative id continuation | LookEnd negative id
//   BehindStart negative id continuation min max | BehindEnd negative id
//   AssertSet negative behind s anchors (1: the input edge also holds, 2: a line start also holds, behind)
//   AssertStr negative behind offset length
//   Backref ignoreCase count groups... | CondRef else count groups... | CondStart id else | CondEnd id
//   Absent offset length | AssertLiterals negative behind table
internal sealed class OnigCompiler
{
    internal const int LoopEnter = 36;

    private static readonly ConcurrentDictionary<(ulong, ulong, bool), SearchValues<char>> s_firstUnits = new();

    private readonly List<int> _code = [];
    private readonly List<OnigCharSet> _sets = [];
    private readonly Dictionary<(OnigCharSet, bool), int> _setIndex = [];
    private readonly List<bool> _setIncomplete = [];
    private readonly StringBuilder _literals = new();
    private readonly List<OnigStartBits> _bits = [];
    private readonly List<OnigLiteralTable> _tables = [];
    private readonly HashSet<int> _called = [];
    // What may follow each called group: assumed for this pass, and seen at its definition and call sites.
    private Dictionary<int, OnigStartBits>? _groupFollow;
    private readonly Dictionary<int, OnigStartBits> _observedFollow = [];
    private readonly int[] _groupPc;
    private readonly List<(int At, int Group)> _callFixups = [];
    private readonly RegexCompileLimits _limits;
    private int _loops;
    private int _ids;
    private int _snapshots;
    private readonly List<int[]> _loopGroups = [];
    private readonly List<OnigNode> _backrefs = [];
    private readonly Dictionary<int, OnigNode> _captureNodes = [];

    private OnigCompiler(int groupCount, RegexCompileLimits limits)
    {
        _groupPc = new int[groupCount];
        _limits = limits;
    }

    internal static OnigProgram Compile(string pattern, OnigInlineOptions options, bool captureGroup, bool dontCaptureGroup, RegexCompileLimits limits)
    {
        var parser = OnigParser.Parse(pattern, options, limits, out var root);
        if (parser.TreeDepth <= ShallowTreeDepth)
        {
            return Build(pattern, parser, root, captureGroup, dontCaptureGroup, limits);
        }

        // The compiler's passes recurse over the tree: a nesting limit raised past what a thread pool stack holds
        // compiles on a thread whose stack is sized for the tree.
        var stack = (1L << 20) + (long)parser.TreeDepth * StackPerLevel;
        if (stack > MaxCompileStack)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.NestingLimitExceeded, 0, pattern.Length, "nesting too deep");
        }

        OnigProgram? program = null;
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                program = Build(pattern, parser, root, captureGroup, dontCaptureGroup, limits);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }, (int)stack);
        thread.Start();
        thread.Join();
        error?.Throw();
        return program!;
    }

    private const int ShallowTreeDepth = 512;
    private const int MaxFollowPasses = 4;

    // Oniguruma's tune_next makes a greedy x* or x+ possessive when the node after it cannot start with x's head
    // literal, looking through the capture groups around the quantifier. For a called group that follow is only the one
    // at its definition, so calls from elsewhere run the quantifier possessive too: (?<n>\d+)\.\g<n>\d does not
    // match 12.345. Elsewhere the rewrite cannot change a result, and the follow sets cover it.
    private static void NativePossessive(OnigNode node, HashSet<int> called)
    {
        if (node.Kind == OnigNodeKind.Sequence)
        {
            for (var i = 0; i + 1 < node.Children.Count; i++)
            {
                var capture = node.Children[i];
                if (capture.Kind != OnigNodeKind.Capture || !called.Contains(capture.Value))
                {
                    continue;
                }

                var body = capture.Child;
                while (body.Kind is OnigNodeKind.Capture or OnigNodeKind.Group)
                {
                    body = body.Child;
                }

                if (body.Kind == OnigNodeKind.Repeat && body.Mode == OnigRepeatMode.Greedy && body.Max < 0 && body.Min <= 1
                    && StrictHead(body.Child) is { } x && HeadLiteral(node.Children, i + 1) is { } y && IsExclusive(x, y))
                {
                    body.Mode = OnigRepeatMode.Possessive;
                }
            }
        }

        foreach (var child in node.Children)
        {
            NativePossessive(child, called);
        }
    }

    // A head literal as Oniguruma's get_tree_head_literal finds it: a string (its units), a class, or the \w ctype.
    private sealed record HeadNode(string? Text, OnigCharSet? Set = null, bool Word = false, bool Negated = false)
    {
        internal static HeadNode Of(OnigNode set) => new(null, set.Set, set.WordType, set.Negated);
    }

    // The quantified body's head when it is a string without real case folding, a class or a ctype
    // (is_strict_real_node); plain groups are not nodes in Oniguruma's tree.
    private static HeadNode? StrictHead(OnigNode body)
    {
        while (body.Kind == OnigNodeKind.Group)
        {
            body = body.Child;
        }

        if (body.Kind == OnigNodeKind.Set)
        {
            return HeadNode.Of(body);
        }

        var siblings = body.Kind == OnigNodeKind.Sequence ? body.Children : [body];
        return siblings.Count > 0 && siblings.TrueForAll(c => IsPlain(c, out _)) ? HeadLiteral(siblings, 0) : null;
    }

    private static HeadNode? HeadLiteral(List<OnigNode> siblings, int from)
    {
        if (from >= siblings.Count)
        {
            return null;
        }

        var node = siblings[from];
        if (node.Kind == OnigNodeKind.Char && !IsPlain(node, out _))
        {
            // A character with case variants is folded into a class of them.
            var variants = new OnigCharSet.Builder();
            foreach (var v in OnigUnicode.CaseClass(node.Value))
            {
                variants.Add(v);
            }

            return new HeadNode(null, variants.Build());
        }

        if (node.Kind == OnigNodeKind.Char)
        {
            // Adjacent characters under the same case option are one string node.
            var text = new StringBuilder();
            for (var i = from; i < siblings.Count && IsPlain(siblings[i], out _) && siblings[i].IgnoreCase == node.IgnoreCase; i++)
            {
                text.Append(OnigUnicode.ToUtf16(siblings[i].Value));
            }

            return new HeadNode(text.ToString(), null);
        }

        switch (node.Kind)
        {
            case OnigNodeKind.Set:
                return HeadNode.Of(node);
            case OnigNodeKind.Sequence:
                return HeadLiteral(node.Children, 0);
            case OnigNodeKind.Repeat when node.Min > 0:
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
            case OnigNodeKind.Look when node.Value == 0 && !node.Flag:
                return HeadLiteral(node.Children, 0);
            default:
                return null;
        }
    }

    // Oniguruma's is_exclusive under UTF-16, where every class with members keeps them in its multibyte buffer.
    private static bool IsExclusive(HeadNode x, HeadNode y)
    {
        if (x.Text is null && y.Text is not null)
        {
            (x, y) = (y, x);
        }

        if (x.Text is { } xs)
        {
            if (y.Text is { } ys)
            {
                var n = Math.Min(xs.Length, ys.Length);
                return !xs.AsSpan(0, n).SequenceEqual(ys.AsSpan(0, n));
            }

            return !y.Set!.Contains(xs.Length > 1 && char.IsSurrogatePair(xs[0], xs[1]) ? char.ConvertToUtf32(xs[0], xs[1]) : xs[0]);
        }

        if (x.Word && y.Word)
        {
            return x.Negated != y.Negated && x.Set == y.Set!.Negate();
        }

        // A class without members has no multibyte buffer.
        return (x.Set!.IsEmpty && !x.Negated && !x.Word) || (y.Set!.IsEmpty && !y.Negated && !y.Word);
    }

    // Emits the program once; groupFollow, when given, is what may follow each called group.
    private static OnigCompiler EmitPass(OnigParser parser, OnigNode root, int groupCount, RegexCompileLimits limits,
        Dictionary<int, OnigStartBits>? groupFollow)
    {
        var compiler = new OnigCompiler(groupCount, limits) { _groupFollow = groupFollow };
        CollectCaptures(root, compiler._captureNodes);
        foreach (var reference in parser.References)
        {
            if (reference.Kind == OnigNodeKind.Call)
            {
                compiler._called.Add(reference.Value);
            }
            else if (reference.Kind == OnigNodeKind.Backref)
            {
                compiler._backrefs.Add(reference);
            }
        }

        compiler.CheckRecursion(root);
        compiler.Emit(root, OnigStartBits.Any);
        compiler.Add(Op.Match);
        return compiler;
    }
    private const int StackPerLevel = 16 * 1024;
    private const long MaxCompileStack = 1L << 30;

    private static OnigProgram Build(string pattern, OnigParser parser, OnigNode root, bool captureGroup, bool dontCaptureGroup, RegexCompileLimits limits)
    {
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

        var calledGroups = parser.References.Where(r => r.Kind == OnigNodeKind.Call).Select(r => r.Value).ToHashSet();
        if (calledGroups.Count > 0)
        {
            NativePossessive(root, calledGroups);
        }

        var compiler = EmitPass(parser, root, groupCount, limits, null);
        if (compiler._called.Count > 0)
        {
            // A called group's body continues at its definition and after each call: its follow is the union of
            // theirs, which calls inside the group make circular. Iterate from nothing-follows to a fixpoint; without one
            // within a few passes, anything may follow.
            var assumed = new Dictionary<int, OnigStartBits>();
            foreach (var g in compiler._called)
            {
                assumed[g] = OnigStartBits.None;
            }

            for (var i = 0; i < MaxFollowPasses; i++)
            {
                var pass = EmitPass(parser, root, groupCount, limits, assumed);
                var observed = pass._observedFollow;
                var same = true;
                foreach (var (g, bits) in assumed)
                {
                    var seen = observed.TryGetValue(g, out var o) ? o : OnigStartBits.None;
                    same &= seen == bits;
                    assumed[g] = bits.Union(seen);
                }

                if (same)
                {
                    compiler = pass;
                    break;
                }
            }
        }
        var callSaves = new int[groupCount][];
        var callSaveMax = 0;
        for (var g = 0; g < groupCount; g++)
        {
            callSaves[g] = compiler._called.Contains(g) && compiler._captureNodes.TryGetValue(g, out var called) ? GroupsIn(called) : [];
            callSaveMax = Math.Max(callSaveMax, callSaves[g].Length);
        }
        foreach (var (at, group) in compiler._callFixups)
        {
            compiler._code[at] = group == 0 ? 0 : compiler._groupPc[group];
        }

        if (compiler._code.Count > limits.MaxInstructions)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.ProgramLimitExceeded, 0, pattern.Length, "program too large");
        }

        var leadingRun = -1;
        // The skip is sound only when no later backreference or condition can depend on where the run started.
        if (compiler._called.Count == 0 && !parser.References.Any(r => r.Kind is OnigNodeKind.Backref or OnigNodeKind.CondRef))
        {
            var pc = 0;
            var code = compiler._code;
            while (code[pc] == Op.SaveOpen)
            {
                pc += 2;
            }

            if (code[pc] == Op.Run && code[pc + 4] < 0 && code[pc + 5] != (int)OnigRepeatMode.Lazy)
            {
                leadingRun = pc;
            }
        }

        var startBits = FollowBits(root);
        var (precededBy, precededAtStart) = Preceded(root);
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
        SearchValues<char>? requiredUnits = null, notRequiredAscii = null;
        if (required is null && RequiredUnits(root) is { } units && Cost(units) <= MaxRequiredCost)
        {
            if (!units.NonAscii)
            {
                requiredUnits = s_firstUnits.GetOrAdd((units.Low, units.High, false), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, true)));
            }
            else
            {
                notRequiredAscii = s_firstUnits.GetOrAdd((units.Low, units.High, true), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, false)));
            }
        }
        return new OnigProgram
        {
            Code = compiler._code.ToArray(),
            Sets = compiler._sets.ToArray(),
            IncompleteSets = compiler._setIncomplete.ToArray(),
            Literals = compiler._literals.ToString(),
            Bits = compiler._bits.ToArray(),
            BitsBack = compiler._bits.Select(static b => b.Empty ? null : b.NonAscii
                ? s_firstUnits.GetOrAdd((b.Low, b.High, true), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, false)))
                : s_firstUnits.GetOrAdd((b.Low, b.High, false), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, true)))).ToArray(),
            BitsBackExcept = compiler._bits.Select(static b => b.NonAscii).ToArray(),
            GroupCount = groupCount,
            LoopCount = compiler._loops,
            UsesCalls = compiler._called.Count > 0,
            GroupNames = names,
            StartAnchor = Anchor(root),
            StartBits = startBits,
            FirstUnits = firstUnits,
            NotFirstAscii = notFirstAscii,
            Prefix = prefix.Length >= 2 ? prefix.ToString() : null,
            Required = required,
            RequiredUnits = requiredUnits,
            NotRequiredAscii = notRequiredAscii,
            MinLength = MinLength(root),
            LeadingRun = leadingRun,
            UsesSearchStart = compiler._code.Count > 0 && UsesAnchor(root, OnigAnchor.SearchStart),
            CallSaves = callSaves,
            CallSaveMax = callSaveMax,
            LoopGroups = compiler._loopGroups.ToArray(),
            SnapshotCount = compiler._snapshots,
            Tables = compiler._tables.ToArray(),
            PrecededBy = precededBy,
            PrecededAtStart = precededAtStart,
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

    private void Add(int value)
    {
        if (_code.Count >= _limits.MaxInstructions)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.ProgramLimitExceeded, 0, 0, "program too large");
        }

        _code.Add(value);
    }

    private void Add(int a, int b)
    {
        Add(a);
        Add(b);
    }

    // A negated set (incomplete: it also takes a truncated high surrogate) gets an index of its own.
    private int SetIndex(OnigCharSet set, bool incomplete = false)
    {
        if (!_setIndex.TryGetValue((set, incomplete), out var index))
        {
            index = _sets.Count;
            _sets.Add(set);
            _setIncomplete.Add(incomplete);
            _setIndex[(set, incomplete)] = index;
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

    private void Observe(int group, OnigStartBits follow)
        => _observedFollow[group] = _observedFollow.TryGetValue(group, out var seen) ? seen.Union(follow) : follow;

    // follow: what may come after node (FollowBits), for auto-possessive runs and give-back skipping.
    private void Emit(OnigNode node, OnigStartBits follow)
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
                EmitSequence(node.Children, follow);
                return;
            case OnigNodeKind.Alternation:
                EmitAlternation(node.Children, follow);
                return;
            case OnigNodeKind.Repeat:
                EmitRepeat(node, follow);
                return;
            case OnigNodeKind.Capture:
                _groupPc[node.Value] = Here;
                Add(Op.SaveOpen, node.Value);
                // A called group's code also runs from each call site: its follow covers them all.
                if (_called.Contains(node.Value))
                {
                    Observe(node.Value, follow);
                    Emit(node.Child, _groupFollow is not null ? _groupFollow[node.Value] : OnigStartBits.Any);
                }
                else
                {
                    Emit(node.Child, follow);
                }

                Add(Op.SaveClose, node.Value);
                if (_called.Contains(node.Value))
                {
                    Add(Op.RetIfCalled, node.Value);
                }
                return;
            case OnigNodeKind.Group:
                Emit(node.Child, follow);
                return;
            case OnigNodeKind.Atomic:
                {
                    // The body succeeds as it would alone: what follows the group must not steer it.
                    var id = _ids++;
                    Add(Op.AtomicStart, id);
                    Emit(node.Child, OnigStartBits.Any);
                    Add(Op.AtomicEnd, id);
                    return;
                }
            case OnigNodeKind.Look:
                EmitLook(node, follow);
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
                Observe(node.Value, follow);
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

                    Emit(node.Children[0], follow);
                    Add(Op.Jmp);
                    var endAt = Here;
                    Add(0);
                    _code[elseAt] = Here;
                    if (node.Children.Count > 1)
                    {
                        Emit(node.Children[1], follow);
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
                    Emit(node.Children[0], OnigStartBits.Any);
                    Add(Op.CondEnd, id);
                    Emit(node.Children[1], follow);
                    Add(Op.Jmp);
                    var endAt = Here;
                    Add(0);
                    _code[elseAt] = Here;
                    if (node.Children.Count > 2)
                    {
                        Emit(node.Children[2], follow);
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
                    Add(LiteralOffset(OnigUnicode.ToUtf16(arg)));
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

                    return SetItem(set, node.Negated);
                }
            case OnigNodeKind.AnyChar:
                return (node.Flag ? RunItem.Any : RunItem.AnyNL, 0);
            case OnigNodeKind.Group when node.Child.Kind is OnigNodeKind.Char or OnigNodeKind.Set or OnigNodeKind.AnyChar:
                return Item(node.Child);
            default:
                return (-1, 0);
        }
    }

    private (int, int) SetItem(OnigCharSet set, bool negated = false)
        => (set.IsUnitRun ? RunItem.SetUnit : RunItem.Set, SetIndex(set, negated));

    // Whether a char node is a plain unit sequence: case-sensitive, or with no case variants.
    private static bool IsPlain(OnigNode node, out int cp)
    {
        cp = node.Value;
        return node.Kind == OnigNodeKind.Char && (!node.IgnoreCase || OnigUnicode.CaseClass(node.Value).Length == 1);
    }

    private void EmitSequence(List<OnigNode> children, OnigStartBits follow)
    {
        // What may follow each child: the rest of the sequence, then the sequence's own follow.
        var after = new OnigStartBits[children.Count];
        var next = follow;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            after[i] = next;
            next = Then(FollowBits(children[i]), next);
        }

        var run = new StringBuilder();
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (IsPlain(child, out var cp))
            {
                run.Append(OnigUnicode.ToUtf16(cp));
                continue;
            }

            FlushLiteral(run);
            Emit(child, after[i]);
        }
        FlushLiteral(run);
    }

    // The bits of first followed by next: next only adds when first can match empty.
    internal static OnigStartBits Then(OnigStartBits first, OnigStartBits next)
        => first.Empty ? first.Union(next) with { Empty = next.Empty, End = first.End || next.End } : first;

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

    private void EmitAlternation(List<OnigNode> branches, OnigStartBits follow)
    {
        var jumps = new List<int>();
        var rest = new OnigStartBits[branches.Count];
        rest[^1] = FollowBits(branches[^1]);
        for (var i = branches.Count - 2; i >= 0; i--)
        {
            rest[i] = FollowBits(branches[i]).Union(rest[i + 1]);
        }

        for (var i = 0; i < branches.Count - 1; i++)
        {
            var at = Here;
            Add(Op.Alt);
            Add(at + 5, 0);
            Add(BitsIndex(FollowBits(branches[i])), BitsIndex(rest[i + 1]));
            Emit(branches[i], follow);
            Add(Op.Jmp);
            jumps.Add(Here);
            Add(0);
            _code[at + 2] = Here;
        }
        Emit(branches[^1], follow);
        foreach (var j in jumps)
        {
            _code[j] = Here;
        }
    }

    private void EmitRepeat(OnigNode node, OnigStartBits follow)
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
                Emit(child, OnigStartBits.Any);
                _code[over] = Here;
            }
            return;
        }

        if (min == 1 && max == 1)
        {
            Emit(child, follow);
            return;
        }

        var (kind, arg) = Item(child);
        if (kind >= 0)
        {
            var mode = node.Mode;
            // A greedy run followed by something that cannot start with one of its units never gives units back
            // usefully: Oniguruma makes such repeats possessive too.
            if (mode == OnigRepeatMode.Greedy && !follow.Empty && FollowBits(child).DisjointUnits(follow))
            {
                mode = OnigRepeatMode.Possessive;
            }

            Add(Op.Run);
            Add(kind, arg);
            Add(min, max);
            Add((int)mode);
            Add(mode == OnigRepeatMode.Greedy ? BitsIndex(follow) : -1);
            return;
        }

        var bodyFollow = Bits(child).Union(follow) with { Empty = follow.Empty, End = follow.End };
        if (node.Mode == OnigRepeatMode.Possessive)
        {
            var id = _ids++;
            Add(Op.AtomicStart, id);
            EmitLoop(child, min, max, lazy: false, Bits(child).Union(OnigStartBits.Any));
            Add(Op.AtomicEnd, id);
            return;
        }

        EmitLoop(child, min, max, node.Mode == OnigRepeatMode.Lazy, bodyFollow);
    }

    private void EmitLoop(OnigNode child, int min, int max, bool lazy, OnigStartBits follow)
    {
        var childBits = Bits(child);
        // Copies of the body unroll a small count; nested repeats and alternatives use counters instead, so code stays
        // linear in the pattern ((?:a{1,2}){1,2}... would otherwise double per level).
        if (!Nullable(child) && min <= 2 && (max == -1 || max == min + 1 || (max == 1 && min == 0)) && IsSimpleBody(child, 32))
        {
            for (var i = 0; i < min; i++)
            {
                Emit(child, follow);
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
                Emit(child, follow);
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
            Emit(child, follow);
            _code[at + 2] = Here;
            return;
        }

        var r = _loops++;
        // Only groups a backreference outside the body refers to make an empty iteration count as progress.
        var groups = Nullable(child) ? GroupsIn(child).Where(g => _backrefs.Any(b => b.Groups!.Contains(g) && !Contains(child, b))).ToArray() : [];
        var groupsIndex = -1;
        var snapshot = 0;
        if (groups.Length > 0)
        {
            groupsIndex = _loopGroups.Count;
            _loopGroups.Add(groups);
            snapshot = _snapshots;
            _snapshots += 2 * groups.Length;
        }

        Add(Op.LoopInit, r);
        var loopHead = Here;
        Add(Op.LoopHead);
        Add(r, min);
        Add(max, lazy ? 1 : 0);
        var exitAt = Here;
        Add(0);
        Add(LoopEnter, r);
        Add(snapshot, groupsIndex);
        Emit(child, follow);
        Add(Op.LoopTail);
        Add(r, loopHead);
        var tailExit = Here;
        Add(0);
        Add(snapshot, groupsIndex);
        _code[exitAt] = Here;
        _code[tailExit] = Here;
    }

    private void EmitLook(OnigNode node, OnigStartBits follow)
    {
        var negative = node.Value;
        var behind = node.Flag;
        var body = Unwrap(node.Child);
        var branches = body.Kind == OnigNodeKind.Alternation ? body.Children : [body];
        if (branches.Count > 1 && TryUnitsAndEdges(branches, behind, out var unitSet, out var edges))
        {
            // (?<=^|[\t &;]) or (?=[\t ;]|$): one unit test with the anchors as flags.
            Add(Op.AssertSet);
            Add(negative, behind ? 1 : 0);
            Add(SetIndex(unitSet), edges);
            return;
        }

        if (!ContainsCapture(body))
        {
            // Without captures only the outcome matters, so literal branches (with ^ before or $ after) share one
            // bucketed table and the other branches are tested on their own.
            var entries = new List<(string, byte)>();
            var others = new List<OnigNode>();
            foreach (var branch in branches)
            {
                if (TryLiteralEntry(branch, out var entry))
                {
                    entries.Add(entry);
                }
                else
                {
                    others.Add(branch);
                }
            }

            if (entries.Count >= 2 || (entries.Count == 1 && entries[0].Item2 != 0))
            {
                var table = _tables.Count;
                _tables.Add(new OnigLiteralTable(entries, behind));
                if (others.Count == 0)
                {
                    Add(Op.AssertLiterals);
                    Add(negative, behind ? 1 : 0);
                    Add(table);
                    return;
                }

                if (negative != 0)
                {
                    Add(Op.AssertLiterals);
                    Add(negative, behind ? 1 : 0);
                    Add(table);
                    foreach (var other in others)
                    {
                        Emit(LookOf(node, other), follow);
                    }
                    return;
                }

                // Positive: the table first, then each other branch, as alternatives.
                var at = Here;
                Add(Op.Alt);
                Add(at + 5, 0);
                Add(-1, -1);
                Add(Op.AssertLiterals);
                Add(0, behind ? 1 : 0);
                Add(table);
                Add(Op.Jmp);
                var jump = Here;
                Add(0);
                _code[at + 2] = Here;
                var rest = new OnigNode(OnigNodeKind.Alternation, node.Offset, node.Length);
                foreach (var other in others)
                {
                    rest.Children.Add(LookOf(node, other));
                }

                Emit(rest.Children.Count == 1 ? rest.Children[0] : rest, follow);
                _code[jump] = Here;
                return;
            }
        }

        if (body.Kind == OnigNodeKind.Alternation && (negative != 0 || (behind && !SameFixedLength(body.Children))))
        {
            // (?!A|B) is (?!A)(?!B). A positive look-behind over alternatives of different lengths is divided as
            // Oniguruma divides it, each then stepping back by its own length; otherwise a positive look-around keeps
            // its alternatives together, atomic as a whole.
            var split = new OnigNode(negative != 0 ? OnigNodeKind.Sequence : OnigNodeKind.Alternation, node.Offset, node.Length);
            foreach (var branch in body.Children)
            {
                var look = new OnigNode(OnigNodeKind.Look, node.Offset, node.Length) { Value = negative, Flag = behind };
                look.Children.Add(branch);
                split.Children.Add(look);
            }

            Emit(split, follow);
            return;
        }

        var (kind, arg) = Item(body);
        if (kind is RunItem.Set or RunItem.SetUnit or RunItem.Char)
        {
            var set = kind == RunItem.Char ? Single(arg) : _sets[arg];
            Add(Op.AssertSet);
            Add(negative, behind ? 1 : 0);
            Add(SetIndex(set), 0);
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
            Emit(node.Child, OnigStartBits.Any);
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
            Emit(node.Child, OnigStartBits.Any);
            Add(Op.LookEnd);
            Add(negative, id);
            _code[cont] = Here;
        }
    }

    // The units that must precede a match from a leading positive look-behind of single units, ^ or \A.
    private static (SearchValues<char>?, bool) Preceded(OnigNode root)
    {
        var first = root;
        while (true)
        {
            if (first.Kind == OnigNodeKind.Sequence && first.Children.Count > 0)
            {
                first = first.Children[0];
            }
            else if (first.Kind is OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic)
            {
                first = first.Child;
            }
            else
            {
                break;
            }
        }

        if (first.Kind != OnigNodeKind.Look || first.Value != 0 || !first.Flag)
        {
            return (null, false);
        }

        var body = Unwrap(first.Child);
        var branches = body.Kind == OnigNodeKind.Alternation ? body.Children : [body];
        ulong low = 0, high = 0;
        var atStart = false;
        foreach (var branch in branches)
        {
            var b = Unwrap(branch);
            if (b.Kind == OnigNodeKind.Anchor && (OnigAnchor)b.Value is OnigAnchor.LineStart or OnigAnchor.BufferStart)
            {
                atStart = true;
                if ((OnigAnchor)b.Value == OnigAnchor.LineStart)
                {
                    low |= 1UL << '\n';
                }
                continue;
            }

            if (b.Kind is not (OnigNodeKind.Char or OnigNodeKind.Set))
            {
                return (null, false);
            }

            var bits = Bits(b);
            if (bits.NonAscii)
            {
                return (null, false);
            }

            low |= bits.Low;
            high |= bits.High;
        }

        if (System.Numerics.BitOperations.PopCount(low) + System.Numerics.BitOperations.PopCount(high) > 16)
        {
            return (null, false);
        }

        return (s_firstUnits.GetOrAdd((low, high, false), static k => SearchValues.Create(AsciiMembers(k.Item1, k.Item2, true))), atStart);
    }

    // Branches that are single units, or ^ and \A behind, or $ and \z ahead ($ adds the newline to the set).
    private static bool TryUnitsAndEdges(List<OnigNode> branches, bool behind, out OnigCharSet set, out int edges)
    {
        set = OnigCharSet.Empty;
        edges = 0;
        var builder = new OnigCharSet.Builder();
        foreach (var branch in branches)
        {
            var b = Unwrap(branch);
            switch (b.Kind)
            {
                case OnigNodeKind.Char:
                    foreach (var m in b.IgnoreCase ? OnigUnicode.CaseClass(b.Value) : [b.Value])
                    {
                        builder.Add(m);
                    }
                    break;
                case OnigNodeKind.Set:
                    builder.Add(b.Set!);
                    break;
                case OnigNodeKind.Anchor when behind && (OnigAnchor)b.Value == OnigAnchor.BufferStart:
                    edges |= 1;
                    break;
                case OnigNodeKind.Anchor when behind && (OnigAnchor)b.Value == OnigAnchor.LineStart:
                    edges |= 3;
                    break;
                case OnigNodeKind.Anchor when !behind && (OnigAnchor)b.Value == OnigAnchor.BufferEnd:
                    edges |= 1;
                    break;
                case OnigNodeKind.Anchor when !behind && (OnigAnchor)b.Value == OnigAnchor.LineEnd:
                    edges |= 1;
                    builder.Add('\n');
                    break;
                default:
                    return false;
            }
        }
        set = builder.Build();
        return true;
    }

    private static bool Contains(OnigNode node, OnigNode target)
    {
        if (ReferenceEquals(node, target))
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (Contains(child, target))
            {
                return true;
            }
        }
        return false;
    }

    private static void CollectCaptures(OnigNode node, Dictionary<int, OnigNode> captures)
    {
        if (node.Kind == OnigNodeKind.Capture)
        {
            captures[node.Value] = node;
        }

        foreach (var child in node.Children)
        {
            CollectCaptures(child, captures);
        }
    }

    // The capture groups node contains, itself included.
    private static int[] GroupsIn(OnigNode node)
    {
        var groups = new List<int>();
        Collect(node);
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

    // Oniguruma rejects a call that can reach itself before consuming input ("never ending recursion").
    private void CheckRecursion(OnigNode root)
    {
        foreach (var g in _called)
        {
            var target = g == 0 ? root : _captureNodes[g];
            if (ReachesAtStart(target, g, [g]))
            {
                throw new OnigSyntaxException(RegexDiagnosticCode.InvalidSyntax, target.Offset, target.Length, "never ending recursion");
            }
        }

        // Whether node can call group before it consumes anything.
        bool ReachesAtStart(OnigNode node, int group, HashSet<int> visiting)
        {
            switch (node.Kind)
            {
                case OnigNodeKind.Call:
                    {
                        if (node.Value == group)
                        {
                            return true;
                        }

                        var callee = node.Value == 0 ? root : _captureNodes[node.Value];
                        return visiting.Add(node.Value) && ReachesAtStart(callee, group, visiting);
                    }
                case OnigNodeKind.Sequence:
                    foreach (var child in node.Children)
                    {
                        if (ReachesAtStart(child, group, visiting))
                        {
                            return true;
                        }

                        if (MinWithCalls(child, []) > 0)
                        {
                            return false;
                        }
                    }
                    return false;
                case OnigNodeKind.Repeat when node.Max == 0:
                    return false;
                default:
                    foreach (var child in node.Children)
                    {
                        if (ReachesAtStart(child, group, visiting))
                        {
                            return true;
                        }
                    }
                    return false;
            }
        }
    }

    // The minimum length of node, a call measuring its group (a recursive call counts as empty).
    private int MinWithCalls(OnigNode node, HashSet<int> visiting)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Call:
                return node.Value != 0 && visiting.Add(node.Value) && _captureNodes.TryGetValue(node.Value, out var callee)
                    ? MinWithCalls(callee, visiting) : 0;
            case OnigNodeKind.Sequence:
                {
                    var sum = 0;
                    foreach (var child in node.Children)
                    {
                        sum = Math.Min(int.MaxValue / 2, sum + MinWithCalls(child, visiting));
                    }
                    return sum;
                }
            case OnigNodeKind.Alternation:
                {
                    var min = int.MaxValue;
                    foreach (var child in node.Children)
                    {
                        min = Math.Min(min, MinWithCalls(child, [.. visiting]));
                    }
                    return min;
                }
            case OnigNodeKind.Repeat:
                return node.Min == 0 ? 0 : (int)Math.Min(int.MaxValue / 2, (long)node.Min * MinWithCalls(node.Child, visiting));
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return MinWithCalls(node.Child, visiting);
            default:
                return Lengths(node).Min;
        }
    }

    private static bool UsesAnchor(OnigNode node, OnigAnchor anchor)
        => (node.Kind == OnigNodeKind.Anchor && (OnigAnchor)node.Value == anchor) || node.Children.Any(c => UsesAnchor(c, anchor));

    private static bool SameFixedLength(List<OnigNode> branches)
    {
        var (min, max) = Lengths(branches[0]);
        foreach (var branch in branches)
        {
            var (bmin, bmax) = Lengths(branch);
            if (bmin != bmax || bmin != min || bmax != max)
            {
                return false;
            }
        }
        return true;
    }

    // Whether node has no repeat or alternative and at most budget nodes.
    private static bool IsSimpleBody(OnigNode node, int budget)
    {
        var count = 0;
        return Visit(node);

        bool Visit(OnigNode n)
        {
            if (++count > budget || n.Kind is OnigNodeKind.Repeat or OnigNodeKind.Alternation)
            {
                return false;
            }

            foreach (var child in n.Children)
            {
                if (!Visit(child))
                {
                    return false;
                }
            }
            return true;
        }
    }

    private static OnigNode LookOf(OnigNode look, OnigNode body)
    {
        var node = new OnigNode(OnigNodeKind.Look, look.Offset, look.Length) { Value = look.Value, Flag = look.Flag };
        node.Children.Add(body);
        return node;
    }

    private static bool ContainsCapture(OnigNode node)
    {
        if (node.Kind is OnigNodeKind.Capture or OnigNodeKind.Call or OnigNodeKind.Backref or OnigNodeKind.Keep)
        {
            return true;
        }

        foreach (var child in node.Children)
        {
            if (ContainsCapture(child))
            {
                return true;
            }
        }
        return false;
    }

    // A branch of plain units, optionally after ^ and before $.
    private static bool TryLiteralEntry(OnigNode branch, out (string, byte) entry)
    {
        entry = default;
        var items = Unwrap(branch).Kind == OnigNodeKind.Sequence ? Unwrap(branch).Children : [Unwrap(branch)];
        byte anchors = 0;
        var from = 0;
        var to = items.Count;
        if (to > 0 && items[0].Kind == OnigNodeKind.Anchor && (OnigAnchor)items[0].Value == OnigAnchor.LineStart)
        {
            anchors |= OnigLiteralTable.LineStartBefore;
            from = 1;
        }

        if (to > from && items[to - 1].Kind == OnigNodeKind.Anchor && (OnigAnchor)items[to - 1].Value == OnigAnchor.LineEnd)
        {
            anchors |= OnigLiteralTable.LineEndAfter;
            to--;
        }

        var literal = new StringBuilder();
        for (var i = from; i < to; i++)
        {
            if (!PlainLiteral(items[i], literal))
            {
                return false;
            }
        }

        entry = (literal.ToString(), anchors);
        return true;
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
                literal.Append(OnigUnicode.ToUtf16(cp));
                return true;
            case OnigNodeKind.Set when node.Set!.Single >= 0:
                literal.Append(OnigUnicode.ToUtf16(node.Set.Single));
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

    // What a match of node can start with, for start filters and alternative skipping. A positive look-ahead
    // constrains the unit at its position as its body does.
    internal static OnigStartBits Bits(OnigNode node) => Bits(node, anchors: false);

    // What may come where node starts, for follow sets: end anchors also constrain ($ needs a newline or the end).
    internal static OnigStartBits FollowBits(OnigNode node) => Bits(node, anchors: true);

    private static OnigStartBits Bits(OnigNode node, bool anchors)
    {
        ref var cache = ref anchors ? ref node.FollowCache : ref node.BitsCache;
        return cache ??= ComputeBits(node, anchors);
    }

    private static OnigStartBits ComputeBits(OnigNode node, bool anchors)
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
            case OnigNodeKind.Anchor when anchors:
                return (OnigAnchor)node.Value switch
                {
                    OnigAnchor.LineEnd or OnigAnchor.SemiEnd => new OnigStartBits(1UL << '\n', 0, false, false, true),
                    OnigAnchor.BufferEnd => new OnigStartBits(0, 0, false, false, true),
                    _ => OnigStartBits.EmptyOnly,
                };
            case OnigNodeKind.Look when node.Value == 0 && !node.Flag:
                {
                    var b = Bits(node.Child, anchors);
                    return b.Empty ? OnigStartBits.EmptyOnly : b;
                }
            case OnigNodeKind.Anchor or OnigNodeKind.Keep or OnigNodeKind.Look or OnigNodeKind.Empty:
                return OnigStartBits.EmptyOnly;
            case OnigNodeKind.Sequence:
                return SequenceBits(node.Children, 0, anchors);
            case OnigNodeKind.Alternation:
                {
                    var result = OnigStartBits.None;
                    foreach (var child in node.Children)
                    {
                        result = result.Union(Bits(child, anchors));
                    }
                    return result;
                }
            case OnigNodeKind.Repeat:
                {
                    var b = Bits(node.Child, anchors);
                    return node.Min == 0 || node.Max == 0 ? b with { Empty = true } : b;
                }
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return Bits(node.Child, anchors);
            default:
                return OnigStartBits.Any;
        }
    }

    private static OnigStartBits SequenceBits(List<OnigNode> children, int from, bool anchors)
    {
        var result = OnigStartBits.EmptyOnly;
        for (var i = from; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Kind == OnigNodeKind.Look && child.Value == 0 && !child.Flag)
            {
                // The unit here must start both the look-ahead's body and the rest.
                var look = Bits(child.Child, anchors);
                var rest = SequenceBits(children, i + 1, anchors);
                var here = look.Empty ? rest : rest.Empty ? look : look.Intersect(rest);
                return Then(result, here);
            }

            result = Then(result, Bits(child, anchors));
            if (!result.Empty)
            {
                return result;
            }
        }
        return result;
    }

    private static bool Nullable(OnigNode node) => Lengths(node).Min == 0;

    private static OnigStartBits CodePointBits(int cp)
        => cp < 64 ? new(1UL << cp, 0, false, false) : cp < 128 ? new(0, 1UL << (cp - 64), false, false) : new(0, 0, true, false);

    // Minimum and maximum lengths in UTF-16 units; -1 is unbounded.
    internal static (int Min, int Max) Lengths(OnigNode node) => node.LengthsCache ??= ComputeLengths(node);

    private static (int Min, int Max) ComputeLengths(OnigNode node)
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
                prefix.Append(OnigUnicode.ToUtf16(cp));
                return true;
            case OnigNodeKind.Set when node.Set!.Single >= 0:
                prefix.Append(OnigUnicode.ToUtf16(node.Set.Single));
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
                Keep(OnigUnicode.ToUtf16(cp), ref best);
                return;
            case OnigNodeKind.Sequence:
                {
                    var run = new StringBuilder();
                    foreach (var child in node.Children)
                    {
                        if (IsPlain(child, out var c))
                        {
                            run.Append(OnigUnicode.ToUtf16(c));
                            continue;
                        }

                        if (child.Kind is OnigNodeKind.Anchor || (child.Kind == OnigNodeKind.Look && (child.Value != 0 || child.Flag)))
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
            // A positive look-ahead's text lies at or after the match start, so the subject must contain it too.
            case OnigNodeKind.Look when node.Value == 0 && !node.Flag:
                Required(node.Child, ref best);
                return;
            case OnigNodeKind.Repeat when node.Min >= 1:
                Required(node.Child, ref best);
                return;
            default:
                return;
        }
    }

    // A required-units set larger than this rarely rejects a subject, so it is not worth a scan.
    private const int MaxRequiredCost = 24;

    private static int Cost(OnigStartBits units)
        => System.Numerics.BitOperations.PopCount(units.Low) + System.Numerics.BitOperations.PopCount(units.High) + (units.NonAscii ? 64 : 0);

    // Units of which every match of node contains one, in the text it consumes or looks ahead at; null when none is
    // known. Alternatives unite their branches' sets; a sequence keeps its most selective child; a literal offers its
    // rarest unit.
    internal static OnigStartBits? RequiredUnits(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char:
                return Bits(node);
            case OnigNodeKind.Set:
                return Bits(node);
            case OnigNodeKind.Sequence:
                {
                    OnigStartBits? best = null;
                    var literal = new StringBuilder();
                    foreach (var child in node.Children)
                    {
                        if (IsPlain(child, out var cp))
                        {
                            literal.Append(OnigUnicode.ToUtf16(cp));
                            continue;
                        }

                        best = Better(best, RarestUnit(literal));
                        literal.Clear();
                        best = Better(best, RequiredUnits(child));
                    }
                    return Better(best, RarestUnit(literal));
                }
            case OnigNodeKind.Alternation:
                {
                    var result = OnigStartBits.None;
                    foreach (var child in node.Children)
                    {
                        if (RequiredUnits(child) is not { } units)
                        {
                            return null;
                        }

                        result = result.Union(units);
                    }
                    return result;
                }
            case OnigNodeKind.Repeat when node.Min >= 1:
                return RequiredUnits(node.Child);
            case OnigNodeKind.Capture or OnigNodeKind.Group or OnigNodeKind.Atomic:
                return RequiredUnits(node.Child);
            case OnigNodeKind.Look when node.Value == 0 && !node.Flag:
                return RequiredUnits(node.Child);
            default:
                return null;
        }
    }

    private static OnigStartBits? Better(OnigStartBits? a, OnigStartBits? b)
        => a is null ? b : b is null ? a : Cost(b.Value) < Cost(a.Value) ? b : a;

    // The unit of a literal least likely to occur in text: punctuation first, then digits and capitals, then the
    // letters least frequent in code.
    private static OnigStartBits? RarestUnit(StringBuilder literal)
    {
        if (literal.Length == 0)
        {
            return null;
        }

        const string common = "etaoinsrlcdumhpfgbyw_vkxjqz";
        var best = literal[0];
        var bestRank = int.MaxValue;
        foreach (var c in literal.ToString())
        {
            var rank = c >= 128 ? 0 : char.IsAsciiLetterLower(c) ? 100 - common.IndexOf(c) : char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) ? 20 : c == ' ' ? 200 : 1;
            if (rank < bestRank)
            {
                (best, bestRank) = (c, rank);
            }
        }
        return CodePointBits(best);
    }

    private static void Keep(string candidate, ref string? best)
    {
        if (candidate.Length > 0 && (best is null || candidate.Length > best.Length))
        {
            best = candidate;
        }
    }
}
