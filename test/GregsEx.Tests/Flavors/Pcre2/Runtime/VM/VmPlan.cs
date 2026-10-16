using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.VM;

// Source-derived direct executable trees. No unverified parser/lowerer is used.
internal sealed class VmPlan(Pcre2Profile? profile = null)
{
    internal Pcre2Profile Profile { get; } = profile ?? new();
    private readonly List<Pcre2Node> nodes = [];
    private readonly List<int> children = [], numbers = [];
    private readonly List<Pcre2GroupDefinition> groups = [];
    private readonly List<Pcre2Reference> references = [];
    private readonly List<Pcre2Assertion> assertions = [];
    private readonly List<Pcre2Condition> conditions = [];
    private readonly List<Pcre2LookbehindBranch> behind = [];
    private readonly List<Pcre2Slice> scans = [];
    internal int Leaf(Pcre2NodeKind kind, int operand = 0) => Add(kind, [], operand);
    internal int Lit(int cp) => Leaf(Pcre2NodeKind.Literal, cp);
    internal int Seq(params int[] xs) => Add(Pcre2NodeKind.Sequence, xs);
    internal int Alt(params int[] xs) => Add(Pcre2NodeKind.Alternative, xs);
    internal int Repeat(int child, int min = 0, int max = -1) => Add(Pcre2NodeKind.Repeat, [child], min: min, max: max);
    internal int Script(int child, bool atomic = false) => Add(Pcre2NodeKind.ScriptRun, [child], atomic ? 1 : 0);
    internal int Define(int child) => Add(Pcre2NodeKind.Define, [child]);
    internal int Capture(int child, int number)
    {
        var index = groups.Count;
        groups.Add(new(number, -1, nodes.Count));
        return Add(Pcre2NodeKind.Capture, [child], index);
    }
    private int Reference(int number)
    {
        int index = references.Count, offset = numbers.Count;
        if (number != 0)
        {
            numbers.Add(number);
        }

        references.Add(new(number == 0 ? Pcre2ReferenceKind.WholePattern : Pcre2ReferenceKind.Absolute,
            number, -1, new(offset, number == 0 ? 0 : 1), -1));
        return index;
    }
    internal int Call(int number = 0) => Leaf(Pcre2NodeKind.Subroutine, Reference(number));
    internal int Backref(int number) => Leaf(Pcre2NodeKind.Backreference, Reference(number));
    internal int Assertion(int child, Pcre2AssertionKind kind = Pcre2AssertionKind.PositiveAhead,
        int min = 0, int max = 0, bool variable = false, int[]? branchMaxima = null)
    {
        var index = assertions.Count;
        assertions.Add(new(kind, min, max, variable));
        var owner = Add(Pcre2NodeKind.Assertion, [child], index);
        if (kind is Pcre2AssertionKind.PositiveBehind or Pcre2AssertionKind.NegativeBehind or Pcre2AssertionKind.NonAtomicBehind)
        {
            var node = nodes[child];
            var count = node.Kind == Pcre2NodeKind.Alternative ? node.Children.Count : 1;
            for (var i = 0; i < count; i++)
            {
                behind.Add(new(owner,
                node.Kind == Pcre2NodeKind.Alternative ? children[node.Children.Offset + i] : child, branchMaxima?[i] ?? max));
            }
        }
        return owner;
    }
    internal int Predicate(int assertion, int yes, int no)
    {
        var index = conditions.Count;
        conditions.Add(new(Pcre2ConditionKind.Assertion, -1, assertion, 0, 0, false));
        return Add(Pcre2NodeKind.Conditional, [assertion, yes, no], index);
    }
    internal int Version(int yes, int no, bool truth = true)
    {
        var index = conditions.Count;
        conditions.Add(new(Pcre2ConditionKind.Version, -1, -1, truth ? 10 : 99, 46, false));
        return Add(Pcre2NodeKind.Conditional, [yes, no], index);
    }
    internal int Scan(int child, int group)
    {
        int reference = Reference(group), index = scans.Count;
        scans.Add(new(reference, 1));
        return Add(Pcre2NodeKind.ScanSubstring, [child], index);
    }
    private int Add(Pcre2NodeKind kind, int[] xs, int operand = 0, int min = 0, int max = 0)
    {
        int id = nodes.Count, offset = children.Count;
        children.AddRange(xs);
        nodes.Add(new(kind, new(offset, xs.Length), operand, min, max, Pcre2Quantifier.Greedy,
            Profile.Flags, Profile.ExtraFlags, new(0, 1, RegexOffsetUnit.Utf16CodeUnits)));
        return id;
    }
    internal Pcre2Program Build(Pcre2PatternLimits limits = default, Pcre2MatchFlags patternFlags = default,
        Pcre2SearchHints? hints = null)
    {
        var sorted = groups.OrderByDescending(g => g.EntryNode).ToArray();
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var c = 0; c < nodes[i].Children.Count; c++)
            {
                parents[children[nodes[i].Children.Offset + c]] = i;
            }
        }

        for (var i = 0; i < sorted.Length; i++)
        {
            nodes[sorted[i].EntryNode] = nodes[sorted[i].EntryNode] with { Operand = i };
        }

        for (var i = 0; i < sorted.Length; i++)
        {
            var parent = parents[sorted[i].EntryNode];
            while (parent >= 0 && nodes[parent].Kind != Pcre2NodeKind.Capture)
            {
                parent = parents[parent];
            }

            sorted[i] = sorted[i] with { ParentDefinition = parent < 0 ? -1 : nodes[parent].Operand };
        }
        for (var i = 0; i < references.Count; i++)
        {
            if (references[i].Candidates.Count > 0)
            {
                references[i] = references[i] with { TargetDefinition = Array.FindIndex(sorted, g => g.Number == numbers[references[i].Candidates.Offset]) };
            }
        }

        var plan = new Pcre2PlanData(nodes.ToArray(), children.ToArray(), groups: sorted, numbers: numbers.ToArray(),
            references: references.ToArray(), assertions: assertions.ToArray(), conditions: conditions.ToArray(),
            scanReferences: scans.ToArray(), units: [120], sourceMap: [0, 1],
            lookbehindBranches: behind.OrderBy(b => b.AssertionNode).ToArray(), hasLookbehindMetadata: true);
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(Profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            sorted.Length == 0 ? 1 : sorted.Max(g => g.Number) + 1, plan, limits, ref budget,
            out var program, out var diagnostic, patternMatchFlags: patternFlags, searchHints: hints), diagnostic.ToString());
        return program!;
    }
}
