#nullable enable
using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if NET8_0_OR_GREATER
using FastExpressionCompiler;
#endif
using GregsEx.Contracts;
using GregsEx.IR;
using E = System.Linq.Expressions.Expression;

namespace GregsEx.Execution.Compiled;

// A lowered match kernel. Pure function of its span arguments: no workspace, budget or subject capture.
// Returns CoreKernelResult codes; caps receives group offsets (2 ints per group, -1 = unset) when captures are tracked,
// otherwise only slots 0 and 1. work receives the kernel's own step count in every case. guard carries the operation's
// cancellation token and deadline; the kernel polls it every few hundred work units at start/backtrack points.
/// <summary>
/// A match kernel emitted by the GregsEx source generator. Not intended to be called or implemented by hand: it is a pure
/// function of its span arguments that returns kernel result codes and publishes capture offsets, the stack scratch and
/// its own work count through the arguments.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public delegate int CoreKernel(ReadOnlySpan<char> input, int startAt, Span<int> caps, Span<int> stack, long maxSteps, ref long work, ref KernelGuard guard);

internal static class CoreKernelResult
{
    internal const int Match = 1;
    internal const int NoMatch = 0;
    internal const int StackExhausted = -1;
    internal const int SurrogateEncountered = -2; // UnicodeScalars subject whose surrogates the kernel does not model
    internal const int StepBudgetExceeded = -3;
    internal const int Cancelled = -4;
    internal const int TimedOut = -5;
}

// Cooperative cancellation/deadline polling for kernels. The guard holds a copy of the operation's RunBudget and asks it
// for a zero-unit checkpoint, so timeout and cancellation semantics are exactly the interpreter's. The clock is read only
// when a deadline or token exists, once per RunBudget.ClockStride work units. A kernel compares its work counter with
// NextPoll (long.MaxValue when nothing needs enforcing) so the unconstrained hot path costs one comparison.
/// <summary>
/// Cooperative cancellation and deadline polling for generated kernels. A kernel compares its work counter with
/// <see cref="NextPoll"/> and calls <see cref="Poll"/> when it is reached.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct KernelGuard
{
    /// <summary>The work count at which the kernel must next call <see cref="Poll"/>; <see cref="long.MaxValue"/> when nothing is enforced.</summary>
    public long NextPoll;
    private RunBudget _budget;

    internal KernelGuard(in RunBudget budget)
    {
        _budget = budget;
        NextPoll = budget.IsUnconstrained(0) ? long.MaxValue : 0;
    }

    internal static KernelGuard None => new() { NextPoll = long.MaxValue };

    /// <summary>Checks cancellation and the deadline; returns 0 to continue or a negative kernel result code to return.</summary>
    /// <param name="work">The kernel's current work count.</param>
    public int Poll(long work)
    {
        if (!_budget.TryChargeAt(0, 0, Stopwatch.GetTimestamp(), out var failure))
        {
            return failure.Code == RegexExecutionFailureCode.Cancelled ? CoreKernelResult.Cancelled : CoreKernelResult.TimedOut;
        }

        NextPoll = work + RunBudget.ClockStride;
        return 0;
    }
}

// Static helpers called from generated code. Expression trees cannot index spans or use ref-struct members directly.
internal static class CoreKernelHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static char At(ReadOnlySpan<char> s, int i) => Unsafe.Add(ref MemoryMarshal.GetReference(s), i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Get(Span<int> s, int i) => Unsafe.Add(ref MemoryMarshal.GetReference(s), i);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Set(Span<int> s, int i, int v) => Unsafe.Add(ref MemoryMarshal.GetReference(s), i) = v;
    internal static int Length(Span<int> s) => s.Length;
    internal static int Poll(ref KernelGuard guard, long work) => guard.Poll(work);

    internal static int IndexOfAny(ReadOnlySpan<char> s, int from, SearchValues<char> values)
    {
        var r = s.Slice(from).IndexOfAny(values);
        return r < 0 ? -1 : r + from;
    }

    internal static int IndexOfAnyExcept(ReadOnlySpan<char> s, int from, SearchValues<char> values)
    {
        var r = s.Slice(from).IndexOfAnyExcept(values);
        return r < 0 ? -1 : r + from;
    }

    internal static int LastIndexOfAny(ReadOnlySpan<char> s, int from, SearchValues<char> values)
    {
        var r = s.Slice(from).LastIndexOfAny(values);
        return r < 0 ? -1 : r + from;
    }

    // The last position in [from, to) holding one of values (or, except, a non-ASCII unit or an ASCII unit outside values).
    internal static int LastInRange(ReadOnlySpan<char> s, int from, int to, SearchValues<char> values)
    {
        var r = s.Slice(from, to - from).LastIndexOfAny(values);
        return r < 0 ? -1 : r + from;
    }

    internal static int LastInRangeExcept(ReadOnlySpan<char> s, int from, int to, SearchValues<char> values)
    {
        var r = s.Slice(from, to - from).LastIndexOfAnyExcept(values);
        return r < 0 ? -1 : r + from;
    }

    internal static bool HasAnySurrogate(ReadOnlySpan<char> s) => s.IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;

    internal static int IndexOfPrefix(ReadOnlySpan<char> s, int from, string text)
    {
        var r = s.Slice(from).IndexOf(text.AsSpan(), StringComparison.Ordinal);
        return r < 0 ? -1 : r + from;
    }

    // The last occurrence of text at or after from, or -1.
    internal static int LastIndexOfLiteral(ReadOnlySpan<char> s, int from, string text)
    {
        var r = s.Slice(from).LastIndexOf(text.AsSpan(), StringComparison.Ordinal);
        return r < 0 ? -1 : r + from;
    }

    // Runs stop at lim, the consumption limit (the input's end, or an enclosing look-behind's position).
    internal static int RunEnd(ReadOnlySpan<char> s, int from, int lim, SearchValues<char> accepted)
    {
        var r = s.Slice(from, lim - from).IndexOfAnyExcept(accepted);
        return r < 0 ? lim : r + from;
    }

    internal static int RunEndNegated(ReadOnlySpan<char> s, int from, int lim, SearchValues<char> rejected)
    {
        var r = s.Slice(from, lim - from).IndexOfAny(rejected);
        return r < 0 ? lim : r + from;
    }

    internal static bool Equals(ReadOnlySpan<char> s, int at, int lim, string text)
        => lim - at >= text.Length && s.Slice(at, text.Length).SequenceEqual(text.AsSpan());

    internal static bool HasSurrogate(ReadOnlySpan<char> s, int from, int to)
        => s.Slice(from, to - from).IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;
}

#if !NET8_0_OR_GREATER
// netstandard2.0 ships no FastExpressionCompiler. CompilationContract.IsRuntimeCompilationAvailable is false there, so
// explicit Compile() throws PlatformNotSupportedException before reaching this stub and Auto stays interpreted/accelerated.
internal static class CoreKernelLowering
{
    internal static CoreKernel? Compile(CoreKernelPlan plan, bool trackCaptures) => null;
}
#else
// Plan-to-expression lowering (structure-aware: fused literal runs, vectorizable class runs, possessive loops,
// search prefilters). The only member that emits code is Lower/Compile; both are trim- and AOT-unsafe by contract.
[RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
[RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
internal static class CoreKernelLowering
{
    private const int Back = 1 << 20; // resume id base for class-run backoff entries
    private const int Look = 2 << 20; // id base for lookaround frames (b = saved position, c = enclosing frame)
    private const int Limit = 3 << 20; // id base for frames that also limit the body's consumption to the saved position
    private const int Call = 4 << 20; // id base for call frames (b = the caller's frame, c = group << 5 | callee depth)
    private static readonly SearchValues<char> Newline = SearchValues.Create("\n");

    // Returns null when FastExpressionCompiler declines the tree; there is deliberately no Expression.Compile fallback.
    internal static CoreKernel? Compile(CoreKernelPlan plan, bool trackCaptures)
    {
        var lambda = Lower(plan, trackCaptures);
        return lambda.CompileFast<CoreKernel>(ifFastFailedReturnNull: true);
    }

    private static MethodInfo H(string name) => typeof(CoreKernelHelpers).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    private static Expression<CoreKernel> Lower(CoreKernelPlan plan, bool track)
    {
        var nodes = plan.Nodes;
        var input = E.Parameter(typeof(ReadOnlySpan<char>), "input");
        var startAt = E.Parameter(typeof(int), "startAt");
        var caps = E.Parameter(typeof(Span<int>), "caps");
        var stack = E.Parameter(typeof(Span<int>), "stack");
        var maxSteps = E.Parameter(typeof(long), "maxSteps");
        var work = E.Parameter(typeof(long).MakeByRefType(), "work");
        var guard = E.Parameter(typeof(KernelGuard).MakeByRefType(), "guard");
        var poll = E.Variable(typeof(long), "poll");
        var pr = E.Variable(typeof(int), "pr");
        var len = E.Variable(typeof(int), "len");
        var stackLen = E.Variable(typeof(int), "stackLen");
        var start = E.Variable(typeof(int), "start");
        var p = E.Variable(typeof(int), "p");
        var sp = E.Variable(typeof(int), "sp");
        var w = E.Variable(typeof(long), "w");
        var id = E.Variable(typeof(int), "id");
        var b = E.Variable(typeof(int), "b");
        var c = E.Variable(typeof(int), "c");
        var ci = E.Variable(typeof(int), "ci");
        var end = E.Variable(typeof(int), "end");
        var resume = E.Variable(typeof(int), "resume");
        var fp = E.Variable(typeof(int), "fp");
        var r = E.Variable(typeof(int), "r");
        var d = E.Variable(typeof(int), "d");
        var lastStart = E.Variable(typeof(int), "lastStart");
        var lim = E.Variable(typeof(int), "lim");
        var cfp = E.Variable(typeof(int), "cfp");
        var ret = E.Label(typeof(int), "ret");
        var overflow = E.Label("overflow");
        var surrogate = E.Label("surrogate");
        var budget = E.Label("budget");
        var noMatch = E.Label("noMatch");
        var nextStart = E.Label("nextStart");
        var advance = E.Label("advance");
        var fail = E.Label("fail");
        var labels = new LabelTarget[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            labels[i] = E.Label("L" + i);
        }

        var at = H(nameof(CoreKernelHelpers.At));
        var get = H(nameof(CoreKernelHelpers.Get));
        var set = H(nameof(CoreKernelHelpers.Set));
        var length = H(nameof(CoreKernelHelpers.Length));
        var pollHelper = H(nameof(CoreKernelHelpers.Poll));
        var nextPollField = typeof(KernelGuard).GetField(nameof(KernelGuard.NextPoll), BindingFlags.Instance | BindingFlags.Public)!;
        var indexOfAny = H(nameof(CoreKernelHelpers.IndexOfAny));
        var indexOfAnyExcept = H(nameof(CoreKernelHelpers.IndexOfAnyExcept));
        var lastIndexOfAny = H(nameof(CoreKernelHelpers.LastIndexOfAny));
        var lastInRange = H(nameof(CoreKernelHelpers.LastInRange));
        var lastInRangeExcept = H(nameof(CoreKernelHelpers.LastInRangeExcept));
        var hasAnySurrogate = H(nameof(CoreKernelHelpers.HasAnySurrogate));
        // The next back-off position of a non-atomic class run below to, or -1: the previous unit, or the last unit in
        // [c, to) the continuation can start with when that set is known.
        Expression BackOff(in KernelNode run, Expression to)
            => run.Follow is null
                ? E.Condition(E.GreaterThan(to, c), E.Subtract(to, I(1)), I(-1))
                : E.Call(run.FollowExcept ? lastInRangeExcept : lastInRange, input, c, to, E.Constant(SearchValues.Create(run.Follow), typeof(SearchValues<char>)));
        var indexOfPrefix = H(nameof(CoreKernelHelpers.IndexOfPrefix));
        var lastIndexOfLiteral = H(nameof(CoreKernelHelpers.LastIndexOfLiteral));
        var runEnd = H(nameof(CoreKernelHelpers.RunEnd));
        var runEndNegated = H(nameof(CoreKernelHelpers.RunEndNegated));
        var equals = H(nameof(CoreKernelHelpers.Equals));
        var hasSurrogate = H(nameof(CoreKernelHelpers.HasSurrogate));
        var matches = typeof(CharacterClass).GetMethod(nameof(CharacterClass.MatchesBmpChar), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        var inputLength = typeof(ReadOnlySpan<char>).GetProperty(nameof(ReadOnlySpan<>.Length))!;

        Expression I(int v) => E.Constant(v);
        Expression SetCap(Expression slot, Expression v) => E.Call(set, caps, slot, v);
        Expression GetCap(Expression slot) => E.Call(get, caps, slot);
        Expression Push(Expression idE, Expression bE, Expression cE) => E.Block(
            E.IfThen(E.GreaterThan(E.Add(sp, I(3)), stackLen), E.Goto(overflow)),
            E.Call(set, stack, sp, idE), E.Call(set, stack, E.Add(sp, I(1)), bE), E.Call(set, stack, E.Add(sp, I(2)), cE),
            E.AddAssign(sp, I(3)));
        Expression Undo(int slot) => E.IfThen(E.NotEqual(sp, I(0)), Push(I(-1 - slot), GetCap(I(slot)), I(0)));
        // Subexpression calls: the call frame pointer and the open-start registers of the saved groups (at the end of the
        // stack span, after stackLen) are undone like capture slots, under slot numbers past the capture slots.
        var cfpSlot = 2 * plan.GroupCount;
        var openIndex = new Dictionary<int, int>();
        if (track)
        {
            foreach (var g in plan.OpenGroups)
            {
                openIndex[g] = openIndex.Count;
            }
        }
        Expression GetReg(int k) => E.Call(get, stack, E.Add(stackLen, I(k)));
        Expression SetReg(int k, Expression v) => E.Call(set, stack, E.Add(stackLen, I(k)), v);
        Expression Restore(Expression slot, Expression value)
            => !plan.HasCalls ? SetCap(slot, value)
                : openIndex.Count == 0 ? E.IfThenElse(E.Equal(slot, I(cfpSlot)), E.Assign(cfp, value), SetCap(slot, value))
                : E.IfThenElse(E.Equal(slot, I(cfpSlot)), E.Assign(cfp, value),
                    E.IfThenElse(E.GreaterThan(slot, I(cfpSlot)), E.Call(set, stack, E.Add(stackLen, E.Subtract(slot, I(cfpSlot + 1))), value), SetCap(slot, value)));
        Expression UndoReg(int k) => E.IfThen(E.NotEqual(sp, I(0)), Push(I(-1 - (cfpSlot + 1 + k)), GetReg(k), I(0)));
        // The groups whose open start a call saves: every group when captures are tracked, else only the match start.
        int[] Saves(in KernelNode node) => (node.Groups ?? []).Where(g => track || g == 0).ToArray();
        // After a frame is left, the consumption limit is the nearest enclosing limit frame's saved position.
        var limRestores = 0;
        Expression RestoreLim()
        {
            if (!plan.HasLimitFrames)
            {
                return E.Empty();
            }

            var loop = E.Label("limLoop" + limRestores);
            var done = E.Label("limDone" + ++limRestores);
            return E.Block(
                E.Assign(r, fp),
                E.Label(loop),
                E.IfThen(E.LessThan(r, I(0)), E.Block(E.Assign(lim, len), E.Goto(done))),
                E.IfThen(E.GreaterThanOrEqual(E.Call(get, stack, r), I(Limit)), E.Block(E.Assign(lim, E.Call(get, stack, E.Add(r, I(1)))), E.Goto(done))),
                E.Assign(r, E.Call(get, stack, E.Add(r, I(2)))),
                E.Goto(loop),
                E.Label(done));
        }
        int FrameId(int pc) => ((nodes[pc].Operand & LookFlags.Limit) != 0 ? Limit : Look) + pc;
        // Line terminators: style 0 = ECMAScript (LF CR LS PS), style 1 = LF only, style 2 = XPath/PCRE2 start (LF; see the assertion cases), style 3 = PCRE2 end before a final LF.
        Expression LineTerminator(Expression ch, int style)
            => style == 1 ? E.Equal(ch, I('\n'))
                : E.OrElse(E.OrElse(E.Equal(ch, I('\n')), E.Equal(ch, I('\r'))),
                    E.LessThanOrEqual(E.Convert(E.Subtract(ch, I(0x2028)), typeof(uint)), E.Constant(1u)));
        Expression IsSurrogate(Expression unit)
            => E.LessThanOrEqual(E.Convert(E.Subtract(unit, I(0xD800)), typeof(uint)), E.Constant(0x7FFu));
        // Whether a branch with these first units can match at a unit (-1 at the end of the input).
        Expression CanStart(KernelStartUnits units, Expression unit)
            => E.Condition(E.LessThan(unit, I(0)), E.Constant(units.End), E.Condition(E.LessThan(unit, I(128)),
                E.NotEqual(E.And(E.RightShift(E.Condition(E.LessThan(unit, I(64)), E.Constant(units.Low), E.Constant(units.High)), unit), E.Constant(1UL)), E.Constant(0UL)),
                E.Constant(units.NonAscii)));
        Expression Test(CharacterClass cls, Expression chr)
        {
            var ranges = cls.Ranges.ToArray();
            if (ranges.Length == 0)
            {
                return E.Constant(cls.Negated);
            }

            if (ranges.Length <= CoreKernelPlan.MaxClassRangesInline)
            {
                Expression? e = null;
                foreach (var r in ranges)
                {
                    Expression term = r.First == r.Last ? E.Equal(chr, I(r.First))
                        : E.LessThanOrEqual(E.Convert(E.Subtract(chr, I(r.First)), typeof(uint)), E.Constant((uint)(r.Last - r.First)));
                    e = e is null ? term : E.OrElse(e, term);
                }
                return cls.Negated ? E.Not(e!) : e!;
            }
            return E.Call(E.Constant(cls), matches, E.Convert(chr, typeof(char)));
        }

        Expression PollCheck() => E.IfThen(E.GreaterThanOrEqual(w, poll), E.Block(
            E.Assign(pr, E.Call(pollHelper, guard, w)),
            E.IfThen(E.NotEqual(pr, I(0)), E.Block(E.Assign(work, w), E.Goto(ret, pr))),
            E.Assign(poll, E.Field(guard, nextPollField))));

        var firstSet = plan.FirstSet is { Length: > 0 } f ? SearchValues.Create(f) : null;
        var body = new List<Expression>
        {
            E.Assign(len, E.Property(input, inputLength)),
            E.Assign(stackLen, E.Subtract(E.Call(length, stack), I(openIndex.Count))),
            E.Assign(w, E.Constant(0L)),
            E.Assign(poll, E.Field(guard, nextPollField)),
            E.Assign(start, startAt),
            E.Assign(lastStart, E.Subtract(len, I(plan.MinLength))),
        };
        if (plan.RequiredLiteral is { } required && plan.RequiredLiteralInMatch)
        {
            // Every match contains the literal: no attempt starts after its last occurrence.
            body.Add(E.Assign(c, E.Call(lastIndexOfLiteral, input, startAt, E.Constant(required))));
            body.Add(E.IfThen(E.LessThan(c, I(0)), E.Goto(noMatch)));
            body.Add(E.IfThen(E.LessThan(c, lastStart), E.Assign(lastStart, c)));
        }
        else if (plan.RequiredLiteral is { } requiredAnywhere)
        {
            body.Add(E.IfThen(E.OrElse(E.GreaterThan(E.Add(startAt, I(plan.RequiredLiteralMinOffset)), len),
                E.LessThan(E.Call(indexOfPrefix, input, E.Add(startAt, I(plan.RequiredLiteralMinOffset)), E.Constant(requiredAnywhere)), I(0))), E.Goto(noMatch)));
        }

        if (plan.RequiredSet is { Length: > 0 } requiredSet)
        {
            body.Add(E.Assign(c, E.Call(lastIndexOfAny, input, startAt, E.Constant(SearchValues.Create(requiredSet), typeof(SearchValues<char>)))));
            body.Add(E.IfThen(E.LessThan(c, I(0)), E.Goto(noMatch)));
            body.Add(E.IfThen(E.LessThan(c, lastStart), E.Assign(lastStart, c)));
        }

        if (plan.SurrogateScan)
        {
            body.Add(E.IfThen(E.Call(hasAnySurrogate, input), E.Goto(surrogate)));
        }

        body.AddRange(new Expression[]
        {
            E.Label(nextStart),
            E.IfThen(E.GreaterThan(start, lastStart), E.Goto(noMatch)),
            E.IfThen(E.GreaterThan(w, maxSteps), E.Goto(budget)),
            PollCheck(),
        });
        if (plan.Anchored)
        {
            body.Add(E.IfThen(E.GreaterThan(start, I(0)), E.Goto(noMatch)));
        }

        if (plan.LiteralPrefix is { } prefix)
        {
            body.Add(E.Block(E.Assign(c, E.Call(indexOfPrefix, input, start, E.Constant(prefix))),
                E.IfThen(E.LessThan(c, I(0)), E.Goto(noMatch)), E.Assign(start, c)));
        }
        else if (firstSet is not null)
        {
            body.Add(E.Block(E.Assign(c, E.Call(plan.FirstSetExcept ? indexOfAnyExcept : indexOfAny, input, start, E.Constant(firstSet, typeof(SearchValues<char>)))),
                E.IfThen(E.LessThan(c, I(0)), E.Goto(noMatch)), E.Assign(start, c)));
            if (plan.FirstSetExcept && plan.UnicodeScalars)
            {
                body.Add(E.IfThen(E.AndAlso(E.LessThan(start, len), IsSurrogate(E.Convert(E.Call(at, input, start), typeof(int)))), E.Goto(surrogate)));
            }
        }
        else if (plan.UnicodeScalars)
        {
            body.Add(E.IfThen(E.AndAlso(E.LessThan(start, len), IsSurrogate(E.Convert(E.Call(at, input, start), typeof(int)))), E.Goto(surrogate)));
        }

        body.Add(E.AddAssign(w, E.Constant(1L)));
        body.Add(E.Assign(resume, I(0)));
        var slots = track ? 2 * plan.GroupCount : 2;
        for (var s = 0; s < slots; s++)
        {
            body.Add(SetCap(I(s), I(-1)));
        }

        body.Add(E.Assign(sp, I(0)));
        body.Add(E.Assign(fp, I(-1)));
        body.Add(E.Assign(cfp, I(-1)));
        body.Add(E.Assign(lim, len));
        body.Add(E.Assign(p, start));

        // Resume table: Split alternates and non-atomic class-run backoffs.
        var cases = new List<SwitchCase>();
        var order = Enumerable.Range(0, nodes.Length).Where(i => plan.Reachable[i]).ToList();
        foreach (var t in order.Where(i => nodes[i].Op == KernelOp.Split).Select(i => nodes[i].Alternate).Distinct())
        {
            cases.Add(E.SwitchCase(E.Goto(labels[t]), I(t)));
        }

        foreach (var i in order)
        {
            if (nodes[i].Op == KernelOp.ClassRun && !nodes[i].Atomic)
            {
                // Resume the continuation at the popped position, and arm the next back-off below it, down to c.
                cases.Add(E.SwitchCase(E.Block(
                    E.Assign(b, BackOff(nodes[i], p)),
                    E.IfThen(E.GreaterThanOrEqual(b, I(0)), Push(I(Back + i), b, c)),
                    E.Goto(labels[nodes[i].Next])), I(Back + i)));
            }
            else if (nodes[i].Op == KernelOp.BehindScan)
            {
                // The next candidate start, one unit nearer the input's start, down to c.
                cases.Add(E.SwitchCase(E.Block(
                    E.IfThen(E.GreaterThan(p, c), Push(I(Back + i), E.Subtract(p, I(1)), c)),
                    E.Goto(labels[nodes[i].Next])), I(Back + i)));
            }
            else if (nodes[i].Op == KernelOp.LookStart)
            {
                // The body ran out of alternatives (its capture changes are already undone): a positive assertion
                // fails, a negative one holds and continues at the saved position.
                cases.Add(E.SwitchCase(E.Block(E.Assign(fp, c), RestoreLim(),
                    (nodes[i].Operand & LookFlags.Negative) != 0 ? E.Goto(labels[nodes[i].Alternate]) : E.Goto(fail)), I(FrameId(i))));
            }
        }

        for (var k = 0; k < order.Count; k++)
        {
            var pc = order[k];
            int? fall = k + 1 < order.Count ? order[k + 1] : null;
            var o = nodes[pc];
            body.Add(E.Label(labels[pc]));
            switch (o.Op)
            {
                case KernelOp.Literal when o.Backward:
                    body.Add(E.IfThen(E.OrElse(E.LessThanOrEqual(p, I(0)), E.NotEqual(E.Call(at, input, E.Subtract(p, I(1))), E.Constant((char)o.Operand))), E.Goto(fail)));
                    body.Add(E.PreDecrementAssign(p));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.Literal:
                    body.Add(E.IfThen(E.OrElse(E.GreaterThanOrEqual(p, lim), E.NotEqual(E.Call(at, input, p), E.Constant((char)o.Operand))), E.Goto(fail)));
                    body.Add(E.PreIncrementAssign(p));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.Any when o.Backward:
                case KernelOp.Class when o.Backward:
                    body.Add(E.IfThen(E.LessThanOrEqual(p, I(0)), E.Goto(fail)));
                    body.Add(E.Assign(ci, E.Convert(E.Call(at, input, E.Subtract(p, I(1))), typeof(int))));
                    if (o.SurrogateSensitive)
                    {
                        body.Add(E.IfThen(IsSurrogate(ci), E.Goto(surrogate)));
                    }

                    if (o.Op == KernelOp.Class)
                    {
                        body.Add(E.IfThen(E.Not(Test(o.Class!, ci)), E.Goto(fail)));
                    }
                    else if (!plan.DotAll)
                    {
                        body.Add(E.IfThen(E.Equal(ci, I('\n')), E.Goto(fail)));
                    }

                    body.Add(E.PreDecrementAssign(p));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.Any:
                    body.Add(E.IfThen(E.GreaterThanOrEqual(p, lim), E.Goto(fail)));
                    body.Add(E.Assign(ci, E.Convert(E.Call(at, input, p), typeof(int))));
                    if (o.SurrogateSensitive)
                    {
                        body.Add(E.IfThen(IsSurrogate(ci), E.Goto(surrogate)));
                    }

                    if (!plan.DotAll)
                    {
                        body.Add(E.IfThen(E.Equal(ci, I('\n')), E.Goto(fail)));
                    }

                    body.Add(E.PreIncrementAssign(p));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.Class:
                    body.Add(E.IfThen(E.GreaterThanOrEqual(p, lim), E.Goto(fail)));
                    body.Add(E.Assign(ci, E.Convert(E.Call(at, input, p), typeof(int))));
                    if (o.SurrogateSensitive)
                    {
                        body.Add(E.IfThen(IsSurrogate(ci), E.Goto(surrogate)));
                    }

                    body.Add(E.IfThen(E.Not(Test(o.Class!, ci)), E.Goto(fail)));
                    body.Add(E.PreIncrementAssign(p));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.LiteralRun:
                    {
                        var t = o.Text!;
                        Expression ok = E.GreaterThanOrEqual(E.Subtract(lim, p), I(t.Length));
                        if (t.Length <= CoreKernelPlan.MaxLiteralRunInline)
                        {
                            for (var j = 0; j < t.Length; j++)
                            {
                                ok = E.AndAlso(ok, E.Equal(E.Call(at, input, E.Add(p, I(j))), E.Constant(t[j])));
                            }
                        }
                        else
                        {
                            ok = E.Call(equals, input, p, lim, E.Constant(t));
                        }

                        body.Add(E.IfThen(E.Not(ok), E.Goto(fail)));
                        body.Add(E.AddAssign(p, I(t.Length)));
                        body.Add(E.AddAssign(w, E.Constant((long)t.Length)));
                        break;
                    }
                case KernelOp.ClassRun:
                    {
                        if (o.Operand == 1)
                        {
                            if (plan.DotAll)
                            {
                                body.Add(E.Assign(end, lim));
                            }
                            else
                            {
                                body.Add(E.Assign(end, E.Call(runEndNegated, input, p, lim, E.Constant(Newline, typeof(SearchValues<char>)))));
                            }
                        }
                        else
                        {
                            var sv = o.Class!.CreateCharSearchValues(out var negated);
                            if (sv is not null)
                            {
                                body.Add(E.Assign(end, E.Call(negated ? runEndNegated : runEnd, input, p, lim, E.Constant(sv, typeof(SearchValues<char>)))));
                            }
                            else
                            {
                                body.Add(RunLoop(input, p, lim, end, at, ci, Test(o.Class!, ci)));
                            }
                        }
                        if (o.SurrogateSensitive)
                        {
                            body.Add(E.IfThen(E.Call(hasSurrogate, input, p, end), E.Goto(surrogate)));
                        }

                        if (pc == plan.LeadingRunNode)
                        {
                            body.Add(E.Assign(resume, E.Add(end, I(1))));
                        }

                        body.Add(E.Assign(c, E.Add(p, I(o.Count))));
                        body.Add(E.IfThen(E.LessThan(end, c), E.Goto(fail)));
                        body.Add(E.AddAssign(w, E.Convert(E.Subtract(end, p), typeof(long))));
                        if (!o.Atomic)
                        {
                            body.Add(E.Assign(b, BackOff(o, end)));
                            body.Add(E.IfThen(E.GreaterThanOrEqual(b, I(0)), Push(I(Back + pc), b, c)));
                        }

                        body.Add(E.Assign(p, end));
                        break;
                    }
                case KernelOp.Split:
                    if (o.NextStart is null && o.AlternateStart is null)
                    {
                        body.Add(Push(I(o.Alternate), p, I(0)));
                        break;
                    }

                    // The unit at the position rules a branch out when its first units exclude it: no choice point for
                    // a branch that cannot match, and the preferred branch is skipped outright when only it cannot.
                    body.Add(E.Assign(ci, E.Condition(E.LessThan(p, lim), E.Convert(E.Call(at, input, p), typeof(int)), I(-1))));
                    if (o.NextStart is { } firstOfNext)
                    {
                        body.Add(E.IfThen(E.Not(CanStart(firstOfNext, ci)), E.Goto(labels[o.Alternate])));
                    }

                    body.Add(o.AlternateStart is { } altStart ? E.IfThen(CanStart(altStart, ci), Push(I(o.Alternate), p, I(0))) : Push(I(o.Alternate), p, I(0)));
                    break;
                case KernelOp.CaptureStart:
                    if (o.Operand == 0)
                    {
                        body.Add(SetCap(I(0), p));
                    }
                    else if (openIndex.TryGetValue(o.Operand, out var reg))
                    {
                        // A group a call may re-enter: the open start waits in its register until the group closes.
                        body.Add(UndoReg(reg));
                        body.Add(SetReg(reg, p));
                    }
                    else if (track)
                    { body.Add(Undo(2 * o.Operand)); body.Add(SetCap(I(2 * o.Operand), p)); }
                    break;
                case KernelOp.CaptureEnd:
                    if (o.Operand == 0)
                    {
                        body.Add(SetCap(I(1), p));
                    }
                    else if (openIndex.TryGetValue(o.Operand, out var reg))
                    {
                        body.Add(Undo(2 * o.Operand));
                        body.Add(SetCap(I(2 * o.Operand), GetReg(reg)));
                        body.Add(Undo(2 * o.Operand + 1));
                        body.Add(SetCap(I(2 * o.Operand + 1), p));
                    }
                    else if (track)
                    { body.Add(Undo(2 * o.Operand + 1)); body.Add(SetCap(I(2 * o.Operand + 1), p)); }
                    break;
                case KernelOp.Clear:
                    if (track)
                    {
                        for (var g = o.Operand; g < o.Operand + o.Count; g++)
                        {
                            foreach (var slot in new[] { 2 * g, 2 * g + 1 })
                            {
                                body.Add(E.IfThen(E.NotEqual(GetCap(I(slot)), I(-1)), E.Block(Undo(slot), SetCap(I(slot), I(-1)))));
                            }
                        }
                    }

                    break;
                case KernelOp.AssertStart:
                    body.Add(E.IfThen(E.NotEqual(p, I(0)), E.Goto(fail)));
                    break;
                case KernelOp.AssertSearchStart:
                    body.Add(E.IfThen(E.NotEqual(p, startAt), E.Goto(fail)));
                    break;
                case KernelOp.StepBack:
                    body.Add(E.IfThen(E.LessThan(p, I(o.Count)), E.Goto(fail)));
                    body.Add(E.SubtractAssign(p, I(o.Count)));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.BehindScan:
                    // Candidate starts from Count back to Operand back (-1: to the input's start), nearest first.
                    body.Add(E.Assign(end, E.Subtract(p, I(o.Count))));
                    body.Add(E.IfThen(E.LessThan(end, I(0)), E.Goto(fail)));
                    body.Add(E.Assign(c, o.Operand < 0 ? I(0) : E.Condition(E.LessThan(E.Subtract(p, I(o.Operand)), I(0)), I(0), E.Subtract(p, I(o.Operand)))));
                    body.Add(E.IfThen(E.GreaterThan(end, c), Push(I(Back + pc), E.Subtract(end, I(1)), c)));
                    body.Add(E.Assign(p, end));
                    body.Add(E.PreIncrementAssign(w));
                    break;
                case KernelOp.LookStart:
                    // A frame entry delimits the body's choices and keeps the stack non-empty, so captures set in the
                    // body always record their undo entries.
                    body.Add(Push(I(FrameId(pc)), p, fp));
                    body.Add(E.Assign(fp, E.Subtract(sp, I(3))));
                    if ((o.Operand & LookFlags.Limit) != 0)
                    {
                        body.Add(E.Assign(lim, p));
                    }

                    break;
                case KernelOp.LookEnd when (nodes[o.Operand].Operand & LookFlags.Negative) != 0:
                    {
                        // The body of a negative assertion matched (a limited body only when it ended at the saved
                        // position): undo its captures, drop its choices, then fail.
                        var loop = E.Label("negLoop" + pc);
                        var done = E.Label("negDone" + pc);
                        if ((nodes[o.Operand].Operand & LookFlags.Limit) != 0)
                        {
                            body.Add(E.IfThen(E.NotEqual(p, E.Call(get, stack, E.Add(fp, I(1)))), E.Goto(fail)));
                        }
                        body.Add(E.AddAssign(w, E.Convert(E.Divide(E.Subtract(sp, fp), I(3)), typeof(long))));
                        body.Add(E.Assign(r, E.Subtract(sp, I(3))));
                        body.Add(E.Label(loop));
                        body.Add(E.IfThen(E.LessThanOrEqual(r, fp), E.Goto(done)));
                        body.Add(E.Assign(id, E.Call(get, stack, r)));
                        body.Add(E.IfThen(E.LessThan(id, I(0)), Restore(E.Subtract(I(-1), id), E.Call(get, stack, E.Add(r, I(1))))));
                        body.Add(E.SubtractAssign(r, I(3)));
                        body.Add(E.Goto(loop));
                        body.Add(E.Label(done));
                        body.Add(E.Assign(sp, fp));
                        body.Add(E.Assign(fp, E.Call(get, stack, E.Add(sp, I(2)))));
                        body.Add(RestoreLim());
                        body.Add(E.Goto(fail));
                        continue;
                    }
                case KernelOp.LookEnd:
                    {
                        // The body of a positive assertion matched: it is atomic, so drop its choices and the frame but keep
                        // the capture undo entries (compacted in order), and resume at the saved position (an atomic group
                        // keeps the body's end). A limited body must have ended exactly at the saved position.
                        var loop = E.Label("posLoop" + pc);
                        var done = E.Label("posDone" + pc);
                        var flags = nodes[o.Operand].Operand;
                        if ((flags & LookFlags.Limit) != 0)
                        {
                            body.Add(E.IfThen(E.NotEqual(p, E.Call(get, stack, E.Add(fp, I(1)))), E.Goto(fail)));
                        }

                        body.Add(E.AddAssign(w, E.Convert(E.Divide(E.Subtract(sp, fp), I(3)), typeof(long))));
                        body.Add(E.Assign(end, E.Call(get, stack, E.Add(fp, I(1)))));
                        body.Add(E.Assign(c, E.Call(get, stack, E.Add(fp, I(2)))));
                        body.Add(E.Assign(d, fp));
                        body.Add(E.Assign(r, E.Add(fp, I(3))));
                        body.Add(E.Label(loop));
                        body.Add(E.IfThen(E.GreaterThanOrEqual(r, sp), E.Goto(done)));
                        body.Add(E.Assign(id, E.Call(get, stack, r)));
                        body.Add(E.IfThen(E.LessThan(id, I(0)), E.Block(
                            E.Call(set, stack, d, id),
                            E.Call(set, stack, E.Add(d, I(1)), E.Call(get, stack, E.Add(r, I(1)))),
                            E.Call(set, stack, E.Add(d, I(2)), E.Call(get, stack, E.Add(r, I(2)))),
                            E.AddAssign(d, I(3)))));
                        body.Add(E.AddAssign(r, I(3)));
                        body.Add(E.Goto(loop));
                        body.Add(E.Label(done));
                        body.Add(E.Assign(sp, d));
                        body.Add(E.Assign(fp, c));
                        body.Add(RestoreLim());
                        if ((flags & LookFlags.Atomic) == 0)
                        {
                            body.Add(E.Assign(p, end));
                        }

                        break;
                    }
                case KernelOp.AssertEnd:
                    body.Add(E.IfThen(E.NotEqual(p, len), E.Goto(fail)));
                    break;
                case KernelOp.AssertLineStart when o.Operand == 2:
                    // XPath: a line starts at 0, or after an LF that is not the final unit.
                    body.Add(E.IfThen(E.AndAlso(E.NotEqual(p, I(0)),
                        E.OrElse(E.GreaterThanOrEqual(p, len), E.NotEqual(E.Convert(E.Call(at, input, E.Subtract(p, I(1))), typeof(int)), I('\n')))), E.Goto(fail)));
                    break;
                case KernelOp.AssertLineEnd when o.Operand == 2:
                    // XPath: a line ends before an LF, or at the end unless the input ends with an LF (and is non-empty).
                    body.Add(E.IfThen(E.Condition(E.LessThan(p, len),
                        E.NotEqual(E.Convert(E.Call(at, input, p), typeof(int)), I('\n')),
                        E.AndAlso(E.NotEqual(p, I(0)), E.Equal(E.Convert(E.Call(at, input, E.Subtract(p, I(1))), typeof(int)), I('\n')))), E.Goto(fail)));
                    break;
                case KernelOp.AssertLineEnd when o.Operand == 3:
                    // PCRE2 non-multiline $ and \Z: the end, or before a final LF.
                    body.Add(E.IfThen(E.AndAlso(E.NotEqual(p, len),
                        E.OrElse(E.NotEqual(E.Add(p, I(1)), len), E.NotEqual(E.Convert(E.Call(at, input, p), typeof(int)), I('\n')))), E.Goto(fail)));
                    break;
                case KernelOp.AssertLineStart:
                    body.Add(E.IfThen(E.AndAlso(E.NotEqual(p, I(0)), E.Not(E.Block(E.Assign(ci, E.Convert(E.Call(at, input, E.Subtract(p, I(1))), typeof(int))), LineTerminator(ci, o.Operand)))), E.Goto(fail)));
                    break;
                case KernelOp.AssertLineEnd:
                    body.Add(E.IfThen(E.AndAlso(E.NotEqual(p, len), E.Not(E.Block(E.Assign(ci, E.Convert(E.Call(at, input, p), typeof(int))), LineTerminator(ci, o.Operand)))), E.Goto(fail)));
                    break;
                case KernelOp.WordBoundary:
                case KernelOp.NotWordBoundary:
                    {
                        // Two reads into one temp: the left neighbour is classified before the right one overwrites it.
                        var left = E.Variable(typeof(bool), "wl");
                        Expression WordAt(Expression index) => E.Block(E.Assign(ci, E.Convert(E.Call(at, input, index), typeof(int))), Test(o.Class!, ci));
                        var test = E.Block(new[] { left },
                            E.Assign(left, E.AndAlso(E.GreaterThan(p, I(0)), WordAt(E.Subtract(p, I(1))))),
                            o.Op == KernelOp.WordBoundary
                                ? E.NotEqual(left, E.AndAlso(E.LessThan(p, len), WordAt(p)))
                                : E.Equal(left, E.AndAlso(E.LessThan(p, len), WordAt(p))));
                        body.Add(E.IfThen(E.Not(test), E.Goto(fail)));
                        break;
                    }
                case KernelOp.Call:
                    {
                        // The frame: the site (the return address), the caller's frame and the group with the callee's
                        // depth; below it the saved open starts. A call at the interpreter's nest limit fails.
                        body.Add(E.Assign(d, E.Condition(E.LessThan(cfp, I(0)), I(0), E.And(E.Call(get, stack, E.Add(cfp, I(2))), I(31)))));
                        body.Add(E.IfThen(E.GreaterThanOrEqual(d, I(19)), E.Goto(fail)));
                        body.Add(E.PreIncrementAssign(w));
                        body.Add(E.IfThen(E.GreaterThan(w, maxSteps), E.Goto(budget)));
                        // The caller's frame pointer is undone like a capture (and kept by a look-around's compaction);
                        // the saved starts sit right below the frame, at fixed offsets from it.
                        body.Add(E.IfThen(E.NotEqual(sp, I(0)), Push(I(-1 - cfpSlot), cfp, I(0))));
                        foreach (var g in Saves(o))
                        {
                            body.Add(g == 0 ? Push(I(-1), GetCap(I(0)), I(0)) : Push(I(-1 - (cfpSlot + 1 + openIndex[g])), GetReg(openIndex[g]), I(0)));
                        }

                        body.Add(Push(I(Call + pc), cfp, E.Or(I(o.Operand << 5), E.Add(d, I(1)))));
                        body.Add(E.Assign(cfp, E.Subtract(sp, I(3))));
                        body.Add(E.Goto(labels[o.Alternate]));
                        continue;
                    }
                case KernelOp.CallReturn:
                    {
                        var sites = o.Targets ?? [];
                        if (sites.Length == 0)
                        {
                            break;
                        }

                        var returning = new List<Expression>();
                        var saves = Saves(o);
                        for (var j = 0; j < saves.Length; j++)
                        {
                            var saved = E.Call(get, stack, E.Subtract(cfp, I(3 * (saves.Length - j) - 1)));
                            if (saves[j] == 0)
                            {
                                // The match start, which only the whole pattern's calls save: no choice point reads it.
                                returning.Add(SetCap(I(0), saved));
                            }
                            else
                            {
                                returning.Add(UndoReg(openIndex[saves[j]]));
                                returning.Add(SetReg(openIndex[saves[j]], saved));
                            }
                        }

                        returning.Add(Push(I(-1 - cfpSlot), cfp, I(0)));
                        if (sites.Length == 1)
                        {
                            returning.Add(E.Assign(cfp, E.Call(get, stack, E.Add(cfp, I(1)))));
                            returning.Add(E.Goto(labels[nodes[sites[0]].Next]));
                        }
                        else
                        {
                            returning.Add(E.Assign(id, E.Subtract(E.Call(get, stack, cfp), I(Call))));
                            returning.Add(E.Assign(cfp, E.Call(get, stack, E.Add(cfp, I(1)))));
                            returning.Add(E.Switch(id, E.Goto(fail), sites.Select(site => E.SwitchCase(E.Goto(labels[nodes[site].Next]), I(site))).ToArray()));
                        }

                        body.Add(E.IfThen(E.AndAlso(E.GreaterThanOrEqual(cfp, I(0)), E.Equal(E.RightShift(E.Call(get, stack, E.Add(cfp, I(2))), I(5)), I(o.Operand))),
                            E.Block(returning)));
                        break;
                    }
                case KernelOp.Match:
                    body.Add(E.Assign(work, w));
                    body.Add(E.Goto(ret, I(CoreKernelResult.Match)));
                    continue;
            }
            if (o.Next != fall)
            {
                body.Add(E.Goto(labels[o.Next]));
            }
        }

        body.Add(E.Label(fail));
        body.Add(E.IfThen(E.Equal(sp, I(0)), E.Goto(advance)));
        body.Add(E.PreIncrementAssign(w));
        body.Add(E.IfThen(E.GreaterThan(w, maxSteps), E.Goto(budget)));
        body.Add(PollCheck());
        body.Add(E.SubtractAssign(sp, I(3)));
        body.Add(E.Assign(id, E.Call(get, stack, sp)));
        body.Add(E.Assign(b, E.Call(get, stack, E.Add(sp, I(1)))));
        body.Add(E.Assign(c, E.Call(get, stack, E.Add(sp, I(2)))));
        body.Add(E.IfThen(E.LessThan(id, I(0)), E.Block(Restore(E.Subtract(I(-1), id), b), E.Goto(fail))));
        if (plan.HasCalls)
        {
            // A call frame: the callee ran out of choices, so the call fails; its caller's frame is current again.
            body.Add(E.IfThen(E.GreaterThanOrEqual(id, I(Call)), E.Block(E.Assign(cfp, b), E.Goto(fail))));
        }

        body.Add(E.Assign(p, b));
        body.Add(cases.Count > 0 ? E.Switch(id, E.Goto(advance), cases.ToArray()) : E.Goto(advance));
        body.Add(E.Label(advance));
        if (plan.Sticky)
        {
            body.Add(E.Goto(noMatch));
        }

        body.Add(E.AddAssign(start, I(1)));
        body.Add(E.IfThen(E.GreaterThan(resume, start), E.Assign(start, resume)));
        body.Add(E.Goto(nextStart));
        foreach (var (label, code) in new[] { (overflow, CoreKernelResult.StackExhausted), (surrogate, CoreKernelResult.SurrogateEncountered), (budget, CoreKernelResult.StepBudgetExceeded), (noMatch, CoreKernelResult.NoMatch) })
        {
            body.Add(E.Label(label));
            body.Add(E.Assign(work, w));
            body.Add(E.Goto(ret, I(code)));
        }
        body.Add(E.Label(ret, I(0)));

        var block = E.Block(typeof(int), new[] { len, stackLen, start, p, sp, w, id, b, c, ci, end, resume, fp, r, d, lastStart, lim, cfp, poll, pr }, body);
        return E.Lambda<CoreKernel>(block, input, startAt, caps, stack, maxSteps, work, guard);
    }

    private static BlockExpression RunLoop(ParameterExpression input, ParameterExpression p, ParameterExpression len, ParameterExpression end,
        MethodInfo at, ParameterExpression ci, Expression inClass)
    {
        var loop = E.Label("runLoop");
        var done = E.Label("runDone");
        return E.Block(
            E.Assign(end, p),
            E.Label(loop),
            E.IfThen(E.GreaterThanOrEqual(end, len), E.Goto(done)),
            E.Assign(ci, E.Convert(E.Call(at, input, end), typeof(int))),
            E.IfThen(E.Not(inClass), E.Goto(done)),
            E.PreIncrementAssign(end),
            E.Goto(loop),
            E.Label(done));
    }
}
#endif
