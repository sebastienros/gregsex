using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Recursion;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Recursion;

// Independent direct executable trees, not peer parser/lowerer output.
internal sealed class RecursionPlan
{
    private readonly List<Pcre2Node> nodes = [];
    private readonly List<int> children = [];
    private readonly List<Pcre2GroupDefinition> groups = [];
    private readonly List<Pcre2Reference> references = [];
    private readonly List<Pcre2Condition> conditions = [];
    private readonly List<Pcre2Assertion> assertions = [];
    private readonly List<int> numbers = [];
    private readonly List<Pcre2Name> names = [];
    private readonly Dictionary<int, int> targetEntries = [];
    internal Pcre2Profile Profile { get; }
    internal RecursionPlan(Pcre2Profile? profile = null) => Profile = profile ?? new();
    internal int Leaf(Pcre2NodeKind kind, int operand = 0) => Add(kind, [], operand);
    internal int Lit(int cp) => Leaf(Pcre2NodeKind.Literal, cp);
    internal int Seq(params int[] xs) => Add(Pcre2NodeKind.Sequence, xs);
    internal int Alt(params int[] xs) => Add(Pcre2NodeKind.Alternative, xs);
    internal int Repeat(int child, int min = 0, int max = -1, Pcre2Quantifier q = Pcre2Quantifier.Greedy) => Add(Pcre2NodeKind.Repeat, [child], min: min, max: max, q: q);
    internal int Atomic(int child) => Add(Pcre2NodeKind.Atomic, [child]);
    internal int Define(int child) => Add(Pcre2NodeKind.Define, [child]);
    internal int Capture(int child, int number)
    {
        var i = groups.Count;
        groups.Add(new(number, -1, nodes.Count));
        return Add(Pcre2NodeKind.Capture, [child], i);
    }
    internal int Reference(int number, Pcre2ReferenceKind kind = Pcre2ReferenceKind.Absolute, params int[] duplicates)
    {
        int i = references.Count, offset = numbers.Count, name = -1;
        var candidates = kind == Pcre2ReferenceKind.WholePattern ? [] : duplicates.Length == 0 ? [number] : duplicates;
        numbers.AddRange(candidates);
        if (kind == Pcre2ReferenceKind.Named)
        { name = names.Count; names.Add(new("R", new(offset, candidates.Length))); }
        references.Add(new(kind, kind == Pcre2ReferenceKind.WholePattern ? 0 : number, name, new(offset, candidates.Length), -1));
        return i;
    }
    internal int Call(int number = 0, Pcre2ReferenceKind? kind = null) => Leaf(Pcre2NodeKind.Subroutine, Reference(number, kind ?? (number == 0 ? Pcre2ReferenceKind.WholePattern : Pcre2ReferenceKind.Absolute)));
    internal int CallTarget(int number, int entryNode)
    {
        var reference = Reference(number);
        targetEntries.Add(reference, entryNode);
        return Leaf(Pcre2NodeKind.Subroutine, reference);
    }
    internal int Backref(int number) => Leaf(Pcre2NodeKind.Backreference, Reference(number));
    internal int Condition(Pcre2ConditionKind kind, int yes, int? no = null, int reference = -1, int major = 10, int minor = 46, bool atLeast = false)
    {
        var i = conditions.Count;
        conditions.Add(new(kind, reference, -1, major, minor, atLeast));
        return Add(Pcre2NodeKind.Conditional, no is null ? [yes] : [yes, no.Value], i);
    }
    internal int Assertion(int child, bool negative = false)
    {
        var i = assertions.Count;
        assertions.Add(new(negative ? Pcre2AssertionKind.NegativeAhead : Pcre2AssertionKind.PositiveAhead, 0, 0));
        return Add(Pcre2NodeKind.Assertion, [child], i);
    }
    internal int Predicate(int assertion, int yes, int? no = null)
    {
        var i = conditions.Count;
        conditions.Add(new(Pcre2ConditionKind.Assertion, -1, assertion, 0, 0, false));
        return Add(Pcre2NodeKind.Conditional, no is null ? [assertion, yes] : [assertion, yes, no.Value], i);
    }
    private int Add(Pcre2NodeKind kind, int[] xs, int operand = 0, int min = 0, int max = 0, Pcre2Quantifier q = default)
    {
        int i = nodes.Count, offset = children.Count;
        children.AddRange(xs);
        nodes.Add(new(kind, new(offset, xs.Length), operand, min, max, q, Profile.Flags, Profile.ExtraFlags, new(0, 1, RegexOffsetUnit.Utf16CodeUnits)));
        return i;
    }
    internal Pcre2Program Build(Pcre2PatternLimits limits = default, Pcre2MatchFlags patternFlags = default)
    {
        var sorted = groups.OrderByDescending(x => x.EntryNode).ToArray();
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
            if (references[i].Candidates.Count > 0)
            {
                references[i] = references[i] with { TargetDefinition = Array.FindIndex(sorted, g => targetEntries.TryGetValue(i, out var entry) ? g.EntryNode == entry : g.Number == numbers[references[i].Candidates.Offset]) };
            }
        }

        var budget = new CompileBudget(new());
        var data = new Pcre2PlanData(nodes.ToArray(), children.ToArray(), groups: sorted, references: references.ToArray(), conditions: conditions.ToArray(),
            assertions: assertions.ToArray(), numbers: numbers.ToArray(), names: names.ToArray(), units: [120], sourceMap: [0, 1]);
        Assert.True(Pcre2Program.TryCreate(Profile, Pcre2PlanStage.Executable, 1, 1, RegexOffsetUnit.Utf16CodeUnits,
            sorted.Length == 0 ? 1 : sorted.Max(x => x.Number) + 1, data, limits, ref budget, out var program, out var diagnostic, patternMatchFlags: patternFlags), diagnostic.ToString());
        return program!;
    }
}

internal sealed class RecursionFixture
{
    internal readonly Pcre2Program Program;
    internal readonly Pcre2ExecutionCapacity Capacity;
    internal readonly int[] States, Captures, Scratch;
    internal int ReturnDepth, Calls, Steps;
    internal bool PredicateDriver;
    internal RecursionFixture(Pcre2Program program, Pcre2ExecutionCapacity? capacity = null)
    {
        Program = program;
        Capacity = capacity ?? new(1024, 16384, 32, 512);
        var required = Capacity.Required(program.GroupCount);
        States = new int[required.StateSlots];
        Captures = new int[required.CaptureSlots];
        Scratch = new int[required.ScratchSlots];
    }
    internal RunState State(Pcre2OperationLimits limits, Pcre2MatchOptions options = default) => Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), limits, options);
    internal Pcre2MatchResult Match(string input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default, Pcre2MatchLimits? limits = null, int start = 0) =>
        Match<char, Pcre2CharTextReader>(input.AsSpan(), pairs, options, limits, start);
    internal Pcre2MatchResult Match(byte[] input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default, Pcre2MatchLimits? limits = null, int start = 0) =>
        Match<byte, Pcre2ByteTextReader>(input.AsSpan(), pairs, options, limits, start);
    private Pcre2MatchResult Match<TUnit, TReader>(ReadOnlySpan<TUnit> input, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options, Pcre2MatchLimits? limits, int start)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var machine = new Pcre2VmState(Capacity, options.Resolve(limits ?? new()), input.Length, Program.PatternLimits, start);
        var state = State(machine.Limits, options);
        Steps = Calls = ReturnDepth = 0;
        if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
        {
            return Pcre2Kernel.Failure(ref machine, ref state);
        }

        var validation = default(TReader).Validate(input, start, 0, Program.Profile, options.Flags | Program.PatternMatchFlags, Pcre2ValidationPolicy.NativeWindow, ref state);
        if (!validation.Valid)
        {
            return state.Failure.Code != RegexExecutionFailureCode.None ? Pcre2Kernel.Failure(ref machine, ref state) : new(Pcre2MatchStatus.Error, validation.NativeError, validation.Failure.Offset);
        }

        var data = Pcre2Data.Instance.Bind(Program.Profile.CharacterTables);
        if (!Pcre2Kernel.BeginCandidate(Program, 0, ref machine, ref state))
        {
            return Pcre2Kernel.Failure(ref machine, ref state);
        }

        while (true)
        {
            Assert.True(++Steps < 100000, "Fixture bounded driver exhausted.");
            var step = Pcre2Kernel.Step<TUnit, TReader>(Program, input, data, options, ref machine, ref state);
            if (step.Kind == Pcre2StepKind.EnterSubroutine)
            {
                Calls++;
                step = Pcre2RecursionOps.Step(Program, options.Flags, ref machine, ref state);
            }
            else if (step.Kind is Pcre2StepKind.Return or Pcre2StepKind.Backtrack &&
                Pcre2Kernel.ReadModuleContinuation(Program, ref machine, ref state, out _, out var frame))
            {
                if (PredicateDriver && Program.Nodes[frame.ScopeNode].Kind == Pcre2NodeKind.Assertion)
                {
                    // Nonshipping fixed-ahead driver: actually executes its regular
                    // child; it supplies scoped truth to the real recursion module.
                    var storage = new Pcre2Storage(state.Buffers, Capacity, Program.GroupCount);
                    var matched = step.Kind == Pcre2StepKind.Return;
                    var negative = Program.Assertions[Program.Nodes[frame.ScopeNode].Operand].Kind == Pcre2AssertionKind.NegativeAhead;
                    if (!matched)
                    { if (!storage.RestoreCaptures(frame.CaptureCursor, ref machine, ref state)) { return Pcre2Kernel.Failure(ref machine, ref state); } machine.CaptureTop = frame.CaptureTop; machine.CaptureLast = frame.LastCapture; }
                    machine.Position = frame.InputPosition;
                    if (!Pcre2Kernel.LeaveModulePredicate(Program, matched != negative, true, ref machine, ref state))
                    {
                        return Pcre2Kernel.Failure(ref machine, ref state);
                    }

                    step = new(Pcre2StepKind.Continue, machine.Node);
                }
                else
                { step = Pcre2RecursionOps.Resume(Program, step, ref machine, ref state); ReturnDepth = machine.CallDepth; }
            }
            else if (step.Kind == Pcre2StepKind.EnterAssertion)
            {
                if (!PredicateDriver || Program.Nodes[machine.Node].Kind != Pcre2NodeKind.Assertion)
                {
                    throw new InvalidOperationException("Assertion module is outside this isolated fixture.");
                }

                var assertion = Program.Nodes[machine.Node];
                Assert.True(Pcre2Kernel.EnterModuleChild(Program, Program.Children[assertion.Children.Offset], Pcre2FrameKind.Predicate, ref machine, ref state));
                step = new(Pcre2StepKind.Continue, machine.Node);
            }
            if (step.Kind == Pcre2StepKind.Continue)
            {
                continue;
            }

            if (step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.Partial or Pcre2StepKind.ResourceFailure or Pcre2StepKind.SourceAbort or Pcre2StepKind.Backtrack)
            {
                return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
            }

            throw new InvalidOperationException(step.Kind.ToString());
        }
    }
}
