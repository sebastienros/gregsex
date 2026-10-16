using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

// Deliberately direct validated executable plans. No production parser or
// peer's unverified output is an oracle or an execution dependency.
internal sealed class AssertionPlan
{
    private readonly List<Pcre2Node> _nodes = [];
    private readonly List<int> _children = [];
    private readonly List<Pcre2GroupDefinition> _groups = [];
    private readonly List<Pcre2ClassNode> _classes = [];
    private readonly List<Pcre2Interval> _intervals = [];
    private readonly List<Pcre2Assertion> _assertions = [];
    private readonly List<Pcre2Reference> _references = [];
    private readonly List<Pcre2Slice> _scans = [];
    private readonly List<int> _numbers = [];
    private readonly List<Pcre2Name> _names = [];
    private readonly List<Pcre2LookbehindBranch> _behind = [];
    internal Pcre2Profile Profile { get; }
    internal AssertionPlan(Pcre2Profile? profile = null) => Profile = profile ?? new();
    internal int Leaf(Pcre2NodeKind kind, int operand = 0, Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null) =>
        Add(kind, [], operand, flags: flags, extra: extra);
    internal int Literal(int cp, Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null) => Leaf(Pcre2NodeKind.Literal, cp, flags, extra);
    internal int Seq(params int[] children) => Add(Pcre2NodeKind.Sequence, children);
    internal int Alt(params int[] children) => Add(Pcre2NodeKind.Alternative, children);
    internal int Repeat(int child, int min = 0, int max = -1, Pcre2Quantifier quantifier = Pcre2Quantifier.Greedy) => Add(Pcre2NodeKind.Repeat, [child], min: min, max: max, quantifier: quantifier);
    internal int Atomic(int child) => Add(Pcre2NodeKind.Atomic, [child]);
    internal int Scan(int child, int group = 1) => Scan(child, [[group]], false);
    internal int Scan(int child, int[][] lists, bool named)
    {
        int reference = _references.Count, index = _scans.Count;
        foreach (var list in lists)
        {
            int offset = _numbers.Count, name = -1;
            _numbers.AddRange(list);
            if (named)
            { name = _names.Count; _names.Add(new("n", new(offset, list.Length))); }
            _references.Add(new(named ? Pcre2ReferenceKind.Named : Pcre2ReferenceKind.Absolute, list[0], name, new(offset, list.Length), -1));
        }
        _scans.Add(new(reference, lists.Length));
        return Add(Pcre2NodeKind.ScanSubstring, [child], index);
    }
    internal int Assertion(int child, Pcre2AssertionKind kind = Pcre2AssertionKind.PositiveAhead,
        int min = 0, int max = 0, bool variable = false, int[]? branchMaxima = null)
    {
        var index = _assertions.Count;
        _assertions.Add(new(kind, min, max, variable));
        var owner = Add(Pcre2NodeKind.Assertion, [child], index);
        if (kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind)
        {
            var n = _nodes[child];
            var count = n.Kind == Pcre2NodeKind.Alternative ? n.Children.Count : 1;
            for (var i = 0; i < count; i++)
            {
                _behind.Add(new(owner, n.Kind == Pcre2NodeKind.Alternative ? _children[n.Children.Offset + i] : child, branchMaxima?[i] ?? max));
            }
        }
        return owner;
    }
    internal int Backref(int[] candidates, Pcre2ReferenceKind kind = Pcre2ReferenceKind.Absolute,
        Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null)
    {
        int offset = _numbers.Count, index = _references.Count, name = -1;
        _numbers.AddRange(candidates);
        if (kind == Pcre2ReferenceKind.Named)
        { name = _names.Count; _names.Add(new("n", new(offset, candidates.Length))); }
        _references.Add(new(kind, kind == Pcre2ReferenceKind.Relative ? -1 : candidates[0], name, new(offset, candidates.Length), -1));
        return Add(Pcre2NodeKind.Backreference, [], index, flags: flags, extra: extra);
    }
    internal int Capture(int child, int number = 1)
    {
        int definition = _groups.Count, node = _nodes.Count;
        _groups.Add(new(number, -1, node));
        return Add(Pcre2NodeKind.Capture, [child], definition);
    }
    internal int Class(Pcre2ClassKind kind, int left = -1, int right = -1, Pcre2PropertyKey property = default, int table = 0, params Pcre2Interval[] intervals)
    {
        int id = _classes.Count, offset = _intervals.Count;
        _intervals.AddRange(intervals);
        _classes.Add(new(kind, left, right, new(offset, intervals.Length), property, table));
        return id;
    }
    internal int Add(Pcre2NodeKind kind, int[] children, int operand = 0, int min = 0, int max = 0,
        Pcre2Quantifier quantifier = Pcre2Quantifier.Greedy, Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null)
    {
        int id = _nodes.Count, offset = _children.Count;
        _children.AddRange(children);
        _nodes.Add(new(kind, new(offset, children.Length), operand, min, max, quantifier,
            flags ?? Profile.Flags, extra ?? Profile.ExtraFlags, new(0, 1, RegexOffsetUnit.Utf16CodeUnits)));
        return id;
    }
    internal Pcre2Program Build(Pcre2MatchFlags patternFlags = default, Pcre2PatternLimits limits = default)
    {
        // Group metadata is top-down even though structural nodes are bottom-up.
        var ordered = _groups.Select((g, i) => (Group: g, Old: i)).OrderByDescending(g => g.Group.EntryNode).ToArray();
        var parents = Enumerable.Repeat(-1, _nodes.Count).ToArray();
        for (var parent = 0; parent < _nodes.Count; parent++)
        {
            for (var child = 0; child < _nodes[parent].Children.Count; child++)
            {
                parents[_children[_nodes[parent].Children.Offset + child]] = parent;
            }
        }

        var newGroups = new Pcre2GroupDefinition[ordered.Length];
        for (var i = 0; i < ordered.Length; i++)
        {
            _nodes[ordered[i].Group.EntryNode] = _nodes[ordered[i].Group.EntryNode] with { Operand = i };
        }

        for (var i = 0; i < ordered.Length; i++)
        {
            var parent = parents[ordered[i].Group.EntryNode];
            while (parent >= 0 && _nodes[parent].Kind != Pcre2NodeKind.Capture)
            {
                parent = parents[parent];
            }

            newGroups[i] = ordered[i].Group with { ParentDefinition = parent < 0 ? -1 : _nodes[parent].Operand };
        }
        var budget = new CompileBudget(new());
        var data = new Pcre2PlanData(_nodes.ToArray(), _children.ToArray(), _classes.ToArray(), _intervals.ToArray(),
            newGroups, names: _names.ToArray(), numbers: _numbers.ToArray(), references: _references.ToArray(), scanReferences: _scans.ToArray(), assertions: _assertions.ToArray(), units: [120], sourceMap: [0, 1], lookbehindBranches: _behind.OrderBy(b => b.AssertionNode).ToArray(), hasLookbehindMetadata: true);
        Assert.True(Pcre2Program.TryCreate(Profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            _groups.Count == 0 ? 1 : _groups.Max(g => g.Number) + 1, data, limits, ref budget, out var program, out var diagnostic,
            patternMatchFlags: patternFlags), diagnostic.ToString());
        return program!;
    }
}

