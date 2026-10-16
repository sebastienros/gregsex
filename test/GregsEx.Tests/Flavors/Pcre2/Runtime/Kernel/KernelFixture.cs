using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

// Deliberately direct validated executable plans. No production parser or
// peer's unverified output is an oracle or an execution dependency.
internal sealed class KernelPlan
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
    internal Pcre2Profile Profile { get; }
    internal KernelPlan(Pcre2Profile? profile = null) => Profile = profile ?? new();
    internal int Leaf(Pcre2NodeKind kind, int operand = 0, Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null) =>
        Add(kind, [], operand, flags: flags, extra: extra);
    internal int Literal(int cp, Pcre2CompileFlags? flags = null, Pcre2ExtraFlags? extra = null) => Leaf(Pcre2NodeKind.Literal, cp, flags, extra);
    internal int Seq(params int[] children) => Add(Pcre2NodeKind.Sequence, children);
    internal int Alt(params int[] children) => Add(Pcre2NodeKind.Alternative, children);
    internal int Repeat(int child, int min = 0, int max = -1, Pcre2Quantifier quantifier = Pcre2Quantifier.Greedy) => Add(Pcre2NodeKind.Repeat, [child], min: min, max: max, quantifier: quantifier);
    internal int Atomic(int child) => Add(Pcre2NodeKind.Atomic, [child]);
    internal int Scan(int child, int group = 1)
    {
        int number = _numbers.Count, reference = _references.Count, index = _scans.Count;
        _numbers.Add(group);
        _references.Add(new(Pcre2ReferenceKind.Absolute, group, -1, new(number, 1), -1));
        _scans.Add(new(reference, 1));
        return Add(Pcre2NodeKind.ScanSubstring, [child], index);
    }
    internal int Assertion(int child, Pcre2AssertionKind kind = Pcre2AssertionKind.PositiveAhead)
    {
        var index = _assertions.Count;
        _assertions.Add(new(kind, 0, 0));
        return Add(Pcre2NodeKind.Assertion, [child], index);
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
            newGroups, numbers: _numbers.ToArray(), references: _references.ToArray(), scanReferences: _scans.ToArray(), assertions: _assertions.ToArray(), units: [120], sourceMap: [0, 1]);
        Assert.True(Pcre2Program.TryCreate(Profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            _groups.Count == 0 ? 1 : _groups.Max(g => g.Number) + 1, data, limits, ref budget, out var program, out var diagnostic,
            patternMatchFlags: patternFlags), diagnostic.ToString());
        return program!;
    }
}

internal sealed class KernelFixture
{
    internal readonly Pcre2Program Program;
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly int[] States, Captures, Scratch;
    internal KernelFixture(Pcre2Program program, Pcre2ExecutionCapacity? capacity = null)
    {
        Program = program;
        Capacity = capacity ?? new(256, 2048, 32, 512);
        var sizes = Capacity.Required(program.GroupCount);
        States = new int[sizes.StateSlots];
        Captures = new int[sizes.CaptureSlots];
        Scratch = new int[sizes.ScratchSlots];
    }
    internal RunState State(Pcre2OperationLimits limits, Pcre2MatchOptions options = default) =>
        Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), limits, options);
    internal Pcre2MatchResult Match(string input, Span<Pcre2OffsetPair> pairs, int start = 0, Pcre2MatchOptions options = default,
        Pcre2MatchLimits? limits = null) => Match<char, Pcre2CharTextReader>(input.AsSpan(), pairs, start, options, limits);
    internal Pcre2MatchResult Match(byte[] input, Span<Pcre2OffsetPair> pairs, int start = 0, Pcre2MatchOptions options = default,
        Pcre2MatchLimits? limits = null) => Match<byte, Pcre2ByteTextReader>(input.AsSpan(), pairs, start, options, limits);
    private Pcre2MatchResult Match<TUnit, TReader>(ReadOnlySpan<TUnit> input, Span<Pcre2OffsetPair> pairs, int start,
        Pcre2MatchOptions options, Pcre2MatchLimits? ceiling)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(ceiling ?? new()), input.Length, Program.PatternLimits, start);
        var state = State(machine.Limits, options);
        if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
        {
            return Pcre2Kernel.Failure(ref machine, ref state);
        }
        // Concrete fragment binding is tested by byte/char methods separately.
        var validation = TReader.Validate(input, start, Program.Nodes.ToArray().Any(n => n.Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary) ? 1 : 0, Program.Profile,
            options.Flags | Program.PatternMatchFlags, Pcre2ValidationPolicy.NativeWindow, ref state);
        if (!validation.Valid)
        {
            return state.Failure.Code != RegexExecutionFailureCode.None ? Pcre2Kernel.Failure(ref machine, ref state) :
                new(Pcre2MatchStatus.Error, validation.NativeError, validation.Failure.Offset, Failure: validation.Failure);
        }

        var data = Pcre2Data.Instance.Bind(Program.Profile.CharacterTables);
        while (true)
        {
            if (!Pcre2Kernel.BeginCandidate(Program, 0, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            while (true)
            {
                var step = Pcre2Kernel.Step<TUnit, TReader>(Program, input, data, options, ref machine, ref state);
                if (step.Kind == Pcre2StepKind.Continue)
                {
                    continue;
                }

                if (step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.Partial or Pcre2StepKind.ResourceFailure)
                {
                    return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
                }

                Assert.Equal(Pcre2StepKind.Backtrack, step.Kind); // no fake modules in this fixture
                break;
            }
            if ((options.Flags & Pcre2MatchFlags.Anchored) != 0 || (Program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0 || machine.CandidateStart == input.Length)
            {
                return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
            }

            var next = TReader.AdvanceAfterEmpty(input, machine.CandidateStart, Program.Profile, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            machine.CandidateStart = next;
        }
    }
}
