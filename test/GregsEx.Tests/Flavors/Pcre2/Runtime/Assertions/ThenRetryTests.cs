using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Assertions;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Parsing.Lowering;
using GregsEx.Flavors.Pcre2.Parsing.Syntax;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

// Explicit protocol occurrences on real parsed plans and VM continuations.
// This fixture handles one lexical THEN only; it is not a control-policy engine.
public sealed class ThenRetryTests
{
    public static TheoryData<int, string, string, int, int> NativeCases
    {
        get
        {
            var data = new TheoryData<int, string, string, int, int>();
            foreach (var domain in new[] { 0, 1, 2, 3 })
            {
                data.Add(domain, "(?<=a(*THEN)b|ac)x", "acx", 2, 1);
                data.Add(domain, "(?<!a(*THEN)b|ac)x", "acx", 2, -1);
                data.Add(domain, "(?<!a(*THEN)b|ad)x", "acx", 2, 1);
                data.Add(domain, "(?<=a(*THEN)b|ad)x", "acx", 2, -1);
                data.Add(domain, "(?(?<=a(*THEN)b|ac)x|y)", "acx", 2, 1);
                data.Add(domain, "(?(?<!a(*THEN)b|ac)y|x)", "acx", 2, 1);
                data.Add(domain, "(?<=a{1,2}(*THEN)b|ac)x", "aacx", 3, 1);
                data.Add(domain, "(?<=a(*THEN)(?!)|b{2,3})x", "ax", 1, -1);
                data.Add(domain, "(?<=a(*THEN)(?!)|bc)x", "ax", 1, -1);
                data.Add(domain, "(?<=a(?=c)(*THEN)b|ac)x", "acx", 2, 1);
                data.Add(domain, "(?<=(a)(*THEN)b|(ac))x", "acx", 2, 3);
                data.Add(domain, "(?<=a?(*THEN)bc|zz)x", "zbcx", 3, -1);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public void SourceNativeFiniteRetryCases(int domain, string pattern, string input, int start, int rc)
    {
        var fixture = new Fixture(pattern, domain);
        var pairs = new Pcre2OffsetPair[fixture.Program.GroupCount];
        var result = fixture.Match(input, start, pairs);
        Assert.Equal(rc, result.NativeReturnCode);
        Assert.Equal(1, fixture.ThenReturns);
        if (rc > 0)
        {
            Assert.Equal(new(true, start, start + 1), pairs[0]);
        }

        if (rc == 3)
        { Assert.Equal(default, pairs[1]); Assert.Equal(new(true, 0, 2), pairs[2]); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NextVariableBranchUsesActualGlobalMinimumAndRestoresEntryScope(int domain)
    {
        var f = new Fixture("(?<=(a)(*THEN)(?!)|b{2,3})x", domain);
        var m = f.Machine(2, 1);
        var s = f.State(m);
        f.AtReturnedThen("ax", ref m, ref s, out var current, out var frame);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, f.Boundary, false, ref m, ref s));
        // Dirty state inside the actual failed scope must not survive the retry.
        var storage = new Pcre2Storage(s.Buffers, f.Capacity, f.Program.GroupCount);
        Assert.True(storage.WriteCapture(1, 0, 1, ref m, ref s));
        m.CaptureTop = 2;
        m.CaptureLast = 1;
        m.ReportStart = 0;
        m.MarkIndex = 19;
        Assert.True(Pcre2Kernel.SetNoteolCleared(true, ref m, ref s));
        m.LastUsedPosition = 2;
        var pending = m.PendingFlags;
        var before = s.Budget.WorkConsumed;
        Assert.Equal(Pcre2StepKind.Continue, f.Retry("ax", current, ref m, ref s).Kind);
        Assert.True(s.Budget.WorkConsumed > before);
        Assert.Equal(0, m.Position); // global min1 admits next max3 with prefix1
        Assert.Equal(frame.ReportStart, m.ReportStart);
        Assert.Equal(frame.MarkIndex, m.MarkIndex);
        Assert.Equal(frame.CaptureTop, m.CaptureTop);
        Assert.Equal(frame.LastCapture, m.CaptureLast);
        Assert.Equal(frame.NoteolCleared, m.NoteolCleared);
        Assert.Equal(default, storage.Capture(1));
        Assert.Equal(pending, m.PendingFlags);
        Assert.Equal(2, m.LastUsedPosition);
        Assert.True(Pcre2Kernel.ReadModuleContinuation(f.Program, ref m, ref s, out _, out var next));
        Assert.Equal(frame.InputPosition, next.InputPosition);
        Assert.Equal(Pcre2StepKind.Backtrack, f.Until("ax", ref m, ref s).Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExhaustionUsesActualStandaloneOrPredicatePolarity(bool predicate, bool negative)
    {
        var assertion = negative ? "(?<!a(*THEN)b)" : "(?<=a(*THEN)b)";
        var pattern = predicate ? (negative ? "(?(?<!a(*THEN)b)x|y)" : "(?(?<=a(*THEN)b)y|x)") : assertion + "x";
        var f = new Fixture(pattern);
        var m = f.Machine(3, 2);
        var s = f.State(m);
        f.AtReturnedThen("acx", ref m, ref s, out var current, out var frame);
        Assert.Equal(predicate ? Pcre2FrameKind.Predicate : Pcre2FrameKind.Assertion, frame.Kind);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, f.Boundary, false, ref m, ref s));
        var flags = m.PendingFlags;
        var watermark = m.LastUsedPosition;
        Assert.Equal(Pcre2StepKind.Continue, f.Retry("acx", current, ref m, ref s).Kind);
        Assert.Equal(flags, m.PendingFlags);
        Assert.Equal(watermark, m.LastUsedPosition);
        Assert.Equal(predicate || negative ? Pcre2StepKind.Accept : Pcre2StepKind.Backtrack,
            f.Until("acx", ref m, ref s).Kind);
    }

    [Fact]
    public void NestedScanWindowAndSubroutineCallRemainActualCallerContext()
    {
        foreach (var pattern in new[] {
            "(ac)(*scs:(1)ac(?<=a(*THEN)b|ac))x",
            "(?(DEFINE)(?<r>ac(?<=a(*THEN)b|ac)x))(?&r)" })
        {
            var f = new Fixture(pattern);
            var m = f.Machine(3, 0);
            var s = f.State(m);
            f.AtReturnedThen("acx", ref m, ref s, out var current, out var frame);
            Assert.True(frame.WindowDepth > 0 || frame.CallDepth > 0);
            Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, f.Boundary, false, ref m, ref s));
            int end = m.VisibleEnd, depth = m.CallDepth;
            Assert.Equal(Pcre2StepKind.Continue, f.Retry("acx", current, ref m, ref s).Kind);
            Assert.Equal(frame.WindowDepth, m.WindowDepth);
            Assert.Equal(depth, m.CallDepth);
            Assert.Equal(end, m.VisibleEnd);
            Assert.Equal(0, Pcre2Kernel.ScanStart(ref s));
            Assert.Equal(Pcre2StepKind.Accept, f.Until("acx", ref m, ref s).Kind);
            var pairs = new Pcre2OffsetPair[f.Program.GroupCount];
            Assert.Equal(Pcre2MatchStatus.Match, Pcre2Kernel.Publish(f.Program, ref m, ref s, pairs).Status);
            Assert.Equal(new(true, 0, 3), pairs[0]);
            Assert.Equal(0, m.WindowDepth);
            Assert.Equal(0, m.CallDepth);
        }
    }

    [Fact]
    public void InvalidBranchIsRejectedBeforeCutAndStickyDenialDoesNotMutateScope()
    {
        var f = new Fixture("(?<=a(*THEN)b|ac)x");
        var m = f.Machine(3, 2);
        var s = f.State(m);
        f.AtReturnedThen("acx", ref m, ref s, out var current, out _);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, f.Boundary, false, ref m, ref s));
        int count = m.FrameCount, node = m.Node;
        // ref structs cannot be captured by Assert.Throws delegates.
        var rejected = false;
        try
        { f.Retry("acx", f.Program.Root, ref m, ref s); }
        catch (InvalidOperationException) { rejected = true; }
        Assert.True(rejected);
        Assert.Equal(count, m.FrameCount);
        Assert.Equal(node, m.Node);
        s.Failure = new(RegexExecutionFailureCode.WorkLimitExceeded, m.Position, s.Budget.WorkConsumed);
        Assert.Equal(Pcre2StepKind.ResourceFailure, f.Retry("acx", current, ref m, ref s).Kind);
        Assert.Equal(count, m.FrameCount);
        Assert.Equal(node, m.Node);
    }

    [Fact]
    public void ExistingInvocationWorkCeilingAppliesAtThenRetry()
    {
        var f = new Fixture("(?<=a(*THEN)b|ac)x");
        var m = f.Machine(3, 2);
        var s = f.State(m);
        f.AtReturnedThen("acx", ref m, ref s, out var current, out _);
        Assert.True(Pcre2Kernel.UnwindToContinuation(f.Program, f.Boundary, false, ref m, ref s));
        Assert.True(s.TryCharge(m.Limits.MaxWork - s.Budget.WorkConsumed, m.Position));
        int count = m.FrameCount, position = m.Position;
        Assert.Equal(Pcre2StepKind.ResourceFailure, f.Retry("acx", current, ref m, ref s).Kind);
        Assert.Equal((int)Pcre2ErrorCode.Matchlimit, m.SourceReturnCode);
        Assert.Equal(m.Limits.MaxWork, s.Budget.WorkConsumed);
        Assert.Equal(count, m.FrameCount);
        Assert.Equal(position, m.Position);
    }

    [Fact]
    public void BoundedWorkFrameCaptureDenialsAndCancellationLeavePreparedWorkspaceReusable()
    {
        var f = new Fixture("(?<=(a)(*THEN)b|(ac))x");
        var states = f.States;
        var scratch = f.Scratch;
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 7, 8), 3).ToArray();
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("acx", 2, pairs, new(maxWork: 30)).Status);
        Assert.All(pairs, pair => Assert.Equal(new(true, 7, 8), pair));
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("acx", 2, pairs, new(maxCaptureEvents: 1)).Status);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, f.Match("acx", 2, pairs, new(maxDepth: 2)).Status);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(Pcre2MatchStatus.Cancelled, f.Match("acx", 2, pairs, options: new() { CancellationToken = cts.Token }).Status);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Pcre2MatchStatus.Match, f.Match("acx", 2, pairs).Status);
            Assert.Equal(new(true, 0, 2), pairs[2]);
            Assert.Equal(default, pairs[1]);
            Assert.Equal(Pcre2MatchStatus.NoMatch, f.Match("adx", 2, pairs).Status);
            Assert.Same(states, f.States);
            Assert.Same(scratch, f.Scratch);
        }
    }

    private sealed class Fixture
    {
        internal readonly Pcre2Program Program;
        internal readonly Pcre2ExecutionCapacity Capacity = new(256, 2048, 32, 512);
        internal readonly int[] States, Captures, Scratch;
        internal int ThenReturns, Boundary;
        private readonly bool _bytes;
        internal Fixture(string pattern, int domain = 1)
        {
            _bytes = domain % 2 == 0;
            var flags = Pcre2CompileFlags.NoAutoPossess | Pcre2CompileFlags.NoStartOptimize;
            if (domain >= 2)
            {
                flags |= Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp;
            }

            var profile = new Pcre2Profile(_bytes ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, flags: flags);
            var budget = new CompileBudget(new());
            Assert.True(new Pcre2SyntaxParser().TryParse(pattern, profile, Pcre2Data.Instance, ref budget, out var syntax, out var error), error.ToString());
            Assert.True(new Pcre2Lowerer().TryLower(syntax!, Pcre2Data.Instance, ref budget, out var executable, out error), error.ToString());
            Program = executable!;
            var size = Capacity.Required(Program.GroupCount);
            States = new int[size.StateSlots];
            Captures = new int[size.CaptureSlots];
            Scratch = new int[size.ScratchSlots];
        }
        internal Pcre2VmState Machine(int length, int start, Pcre2MatchLimits? limits = null) => new(Capacity, limits ?? new(maxWork: 100000), length, startAt: start);
        internal RunState State(Pcre2VmState machine, Pcre2MatchOptions options = default) =>
            Pcre2KernelOperation.CreateRunState(new(States, Captures, Scratch), machine.Limits, options);
        internal Pcre2Step Until(string input, ref Pcre2VmState machine, ref RunState state)
        {
            var data = Pcre2Data.Instance.Bind(Program.Profile.CharacterTables);
            for (var transitions = 0; transitions < 10000; transitions++)
            {
                var step = _bytes ? Pcre2Vm.Step<byte, Pcre2ByteTextReader>(Program, Encoding.UTF8.GetBytes(input), data, default, ref machine, ref state) :
                    Pcre2Vm.Step<char, Pcre2CharTextReader>(Program, input, data, default, ref machine, ref state);
                if (step.Kind != Pcre2StepKind.Continue)
                {
                    return step;
                }
            }
            throw new InvalidOperationException("Finite THEN test transition bound.");
        }
        internal void AtReturnedThen(string input, ref Pcre2VmState machine, ref RunState state,
            out int currentBranch, out Pcre2Frame assertion)
        {
            Assert.True(Pcre2Kernel.BeginCandidate(Program, 0, ref machine, ref state));
            Assert.Equal(Pcre2StepKind.Control, Until(input, ref machine, ref state).Kind);
            Assert.Equal(Pcre2VerbKind.Then, Program.Controls[Program.Nodes[machine.Node].Operand].Kind);
            Assert.True(Pcre2Kernel.ReadModuleContinuation(Program, ref machine, ref state, out var boundary, out assertion));
            Boundary = boundary;
            var child = Program.Children[Program.Nodes[assertion.ScopeNode].Children.Offset];
            currentBranch = Program.Nodes[child].Kind == Pcre2NodeKind.Alternative ? Program.Children[Program.Nodes[child].Children.Offset] : child;
            var verb = machine.Node;
            Assert.True(Pcre2Kernel.PushVerbBoundary(Program, verb, ref machine, ref state));
            Pcre2Kernel.CompleteNode(verb, ref machine, ref state);
            Assert.Equal(Pcre2StepKind.Control, Until(input, ref machine, ref state).Kind);
            Assert.True(Pcre2Kernel.TakeVerbReturn(Program, ref machine, ref state, out _));
            ThenReturns++;
        }
        internal Pcre2Step Retry(string input, int branch, ref Pcre2VmState machine, ref RunState state) =>
            _bytes ? Pcre2AssertionOps.ResumeAfterThen<byte, Pcre2ByteTextReader>(Program, Encoding.UTF8.GetBytes(input), branch, ref machine, ref state) :
                Pcre2AssertionOps.ResumeAfterThen<char, Pcre2CharTextReader>(Program, input, branch, ref machine, ref state);
        internal Pcre2MatchResult Match(string input, int start, Span<Pcre2OffsetPair> pairs,
            Pcre2MatchLimits? limits = null, Pcre2MatchOptions options = default)
        {
            ThenReturns = 0;
            var machine = Machine(input.Length, start, limits);
            var state = State(machine, options);
            if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }
            // Entry resource failures can precede the explicit source occurrence.
            if (!Pcre2Kernel.BeginCandidate(Program, 0, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            var step = Until(input, ref machine, ref state);
            if (step.Kind == Pcre2StepKind.Control)
            {
                Assert.True(Pcre2Kernel.ReadModuleContinuation(Program, ref machine, ref state, out var boundary, out var assertion));
                var child = Program.Children[Program.Nodes[assertion.ScopeNode].Children.Offset];
                var branch = Program.Nodes[child].Kind == Pcre2NodeKind.Alternative ? Program.Children[Program.Nodes[child].Children.Offset] : child;
                var verb = machine.Node;
                Assert.Equal(Pcre2VerbKind.Then, Program.Controls[Program.Nodes[verb].Operand].Kind);
                if (!Pcre2Kernel.PushVerbBoundary(Program, verb, ref machine, ref state))
                {
                    return Pcre2Kernel.Failure(ref machine, ref state);
                }

                Pcre2Kernel.CompleteNode(verb, ref machine, ref state);
                step = Until(input, ref machine, ref state);
                if (step.Kind == Pcre2StepKind.Control)
                {
                    Assert.True(Pcre2Kernel.TakeVerbReturn(Program, ref machine, ref state, out _));
                    ThenReturns++;
                    if (!Pcre2Kernel.UnwindToContinuation(Program, boundary, false, ref machine, ref state))
                    {
                        return Pcre2Kernel.Failure(ref machine, ref state);
                    }

                    step = Retry(input, branch, ref machine, ref state);
                    if (step.Kind == Pcre2StepKind.Continue)
                    {
                        step = Until(input, ref machine, ref state);
                    }
                }
            }
            Assert.True(step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.Backtrack or Pcre2StepKind.ResourceFailure, step.ToString());
            return Pcre2Kernel.Publish(Program, ref machine, ref state, pairs);
        }
    }
}
