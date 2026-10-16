using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

// Direct executable source trees until verified parser/lowering intake.
internal sealed class ControlPlan(Pcre2Profile? profile = null)
{
    internal Pcre2Profile Profile { get; } = profile ?? new();
    private readonly List<Pcre2Node> nodes = [];
    private readonly List<int> children = [], units = [120], numbers = [];
    private readonly List<Pcre2GroupDefinition> groups = [];
    private readonly List<Pcre2CalloutSite> callouts = [];
    private readonly List<Pcre2Control> controls = [];
    private readonly List<Pcre2Slice> marks = [];
    private readonly List<Pcre2Assertion> assertions = [];
    private readonly List<Pcre2Reference> references = [];
    internal int Leaf(Pcre2NodeKind kind, int operand = 0) => Add(kind, [], operand);
    internal int Lit(int value) => Leaf(Pcre2NodeKind.Literal, value);
    internal int Seq(params int[] xs) => Add(Pcre2NodeKind.Sequence, xs);
    internal int Alt(params int[] xs) => Add(Pcre2NodeKind.Alternative, xs);
    internal int Repeat(int child, int min = 0, int max = -1, Pcre2Quantifier q = Pcre2Quantifier.Greedy) => Add(Pcre2NodeKind.Repeat, [child], min: min, max: max, q: q);
    internal int Atomic(int child) => Add(Pcre2NodeKind.Atomic, [child]);
    internal int Script(int child) => Add(Pcre2NodeKind.ScriptRun, [child]);
    internal int Define(int child) => Add(Pcre2NodeKind.Define, [child]);
    internal int Capture(int child, int number)
    {
        var index = groups.Count;
        groups.Add(new(number, -1, nodes.Count));
        return Add(Pcre2NodeKind.Capture, [child], index);
    }
    internal int Call(int number)
    {
        int index = references.Count, offset = numbers.Count;
        if (number > 0)
        {
            numbers.Add(number);
        }

        references.Add(new(number == 0 ? Pcre2ReferenceKind.WholePattern : Pcre2ReferenceKind.Absolute,
            number, -1, new(offset, number == 0 ? 0 : 1), -1));
        return Leaf(Pcre2NodeKind.Subroutine, index);
    }
    internal int Assertion(int child, Pcre2AssertionKind kind = Pcre2AssertionKind.PositiveAhead)
    {
        var index = assertions.Count;
        assertions.Add(new(kind, 0, 0));
        return Add(Pcre2NodeKind.Assertion, [child], index);
    }
    internal int Mark(params int[] text)
    {
        var index = marks.Count;
        marks.Add(Payload(text));
        return index;
    }
    internal int Verb(Pcre2VerbKind kind, int mark = -1)
    {
        var index = controls.Count;
        controls.Add(new(kind, mark));
        return Leaf(Pcre2NodeKind.Verb, index);
    }
    internal int Callout(int number = 0, int[]? text = null, bool automatic = false)
    {
        var index = callouts.Count;
        callouts.Add(new(number, automatic, text is not null, text is null ? default : Payload(text), 1, 0, 0));
        return Leaf(Pcre2NodeKind.Callout, index);
    }
    private Pcre2Slice Payload(int[] text)
    {
        var slice = new Pcre2Slice(units.Count, text.Length);
        units.AddRange(text);
        return slice;
    }
    private int Add(Pcre2NodeKind kind, int[] xs, int operand = 0, int min = 0, int max = 0,
        Pcre2Quantifier q = Pcre2Quantifier.Greedy)
    {
        int id = nodes.Count, offset = children.Count;
        children.AddRange(xs);
        nodes.Add(new(kind, new(offset, xs.Length), operand, min, max, q,
            Profile.Flags, Profile.ExtraFlags, new(0, 1, RegexOffsetUnit.Utf16CodeUnits)));
        return id;
    }
    internal Pcre2Program Build(Pcre2SearchHints? hints = null, byte[]? bitmap = null,
        bool hasCrOrLf = false, Pcre2PatternLimits limits = default)
    {
        var sorted = groups.OrderByDescending(g => g.EntryNode).ToArray();
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (var i = 0; i < nodes.Count; i++)
        {
            foreach (var child in children.Skip(nodes[i].Children.Offset).Take(nodes[i].Children.Count))
            {
                parents[child] = i;
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
            if (references[i].Number > 0)
            {
                references[i] = references[i] with { TargetDefinition = Array.FindIndex(sorted, g => g.Number == references[i].Number) };
            }
        }

        var data = new Pcre2PlanData(nodes.ToArray(), children.ToArray(), groups: sorted,
            numbers: numbers.ToArray(), references: references.ToArray(), assertions: assertions.ToArray(),
            callouts: callouts.ToArray(), controls: controls.ToArray(), marks: marks.ToArray(), units: units.ToArray(), sourceMap: [0, 1]);
        var budget = new CompileBudget(new());
        Assert.True(Pcre2Program.TryCreate(Profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            sorted.Length == 0 ? 1 : sorted.Max(g => g.Number) + 1, data, limits, ref budget,
            out var program, out var diagnostic, searchHints: hints, startBitmap: bitmap, hasExplicitCrOrLf: hasCrOrLf), diagnostic.ToString());
        return program!;
    }
}
