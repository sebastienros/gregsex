#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.IR;
using GregsEx.Text;

namespace GregsEx.Execution.Compiled;

internal enum KernelOp : byte
{
    Literal, Class, Any, Split, Jump, CaptureStart, CaptureEnd, Clear, AssertStart, AssertEnd, Match, LiteralRun, ClassRun,
    // Multiline anchors (Operand = line-terminator style) and word boundaries (Class = the word-character class).
    AssertLineStart, AssertLineEnd, WordBoundary, NotWordBoundary,
    // Lookaround: LookStart (Operand bits: 1 = negative, 2 = behind, 4 = atomic group (the body's end position is
    // kept), 8 = the body may not consume past the saved position and must end there) saves the position, runs the
    // body at Next and continues at Alternate; LookEnd (Operand = its LookStart) ends the body. StepBack moves Count
    // units back (a fixed-length lookbehind branch); BehindScan tries every start from Count units back to Operand
    // units back (-1: to the input's start), nearest first. AssertSearchStart is \G: the position where the search started.
    LookStart, LookEnd, StepBack, AssertSearchStart, BehindScan,
    // Subexpression calls (Oniguruma's \g<name>): Call (Operand = the group, Alternate = the group's entry node, Next =
    // the continuation the callee returns to, Count = 1 when the callee can match empty, Groups = the groups whose open
    // starts the call saves) pushes a call frame and jumps to the entry; CallReturn (Operand = the group, Next = the
    // definition's continuation, Targets = every Call of the group, Groups = the same saved groups) follows the group's
    // CaptureEnd and, when the innermost frame is a call of this group, restores the saved starts and returns to that
    // call's Next. A saved group keeps its open start in a register of its own, as the interpreter does, so a call's
    // return cannot move a capture the callee closed.
    Call, CallReturn
}

// LookStart flag bits. An atomic group is a LookStart/LookEnd pair with Count = 1 and no Alternate edge: the body's
// continuation is the LookEnd's Next.
internal static class LookFlags
{
    internal const int Negative = 1, Behind = 2, Atomic = 4, Limit = 8;
}

// The units a branch can start with, as an ASCII bitmap plus whether any non-ASCII unit can (the interpreters' start
// bits): a Split skips a branch whose first units exclude the unit at the position, and pushes no choice point when
// the other branch cannot start either. Null when the branch may match empty or its first units are unknown.
internal sealed class KernelStartUnits
{
    internal ulong Low;
    internal ulong High;
    internal bool NonAscii;
    // Whether the branch can match at the end of the input without consuming (it ends there, or asserts the end).
    internal bool End;

    // Whether a branch with these first units can match at a position holding c (-1 at the end of the input).
    internal bool CanStart(int c) => c < 0 ? End : c < 128 ? ((c < 64 ? Low : High) & (1UL << c)) != 0 : NonAscii;
}

// One node of the structure-aware plan. A node is either a 1:1 image of a SemanticProgram instruction or a fused
// superinstruction (literal run, greedy single-class loop) recovered from the instruction graph.
internal struct KernelNode
{
    internal KernelOp Op;
    internal int Next;          // fall-through / taken continuation
    internal int Alternate;     // Split: lower-priority branch
    internal int Operand;       // Literal char, capture group, Clear first group, ClassRun: 1 when the loop is "any", line anchors: LineStyle
    internal int Count;         // Clear: group count; ClassRun: minimum repetitions
    internal string? Text;      // LiteralRun
    internal CharacterClass? Class;
    internal bool Possessive;   // Split/ClassRun: lowered as a greedy loop, valid only if the continuation is disjoint (Atomic)
    internal bool Atomic;       // ClassRun: the continuation can never use a shorter run, so no backoff is recorded
    internal bool SurrogateSensitive; // consumes a unit that might be half of a scalar in UnicodeScalars mode
    internal bool Backward;     // Literal/Class/Any inside a backward-matched lookbehind: reads the unit before the position
    // ClassRun (non-atomic): units the continuation can start with, so the run gives units back only to a position
    // holding one; FollowExcept lists the ASCII units it cannot start with instead (non-ASCII units all can).
    internal char[]? Follow;
    internal bool FollowExcept;
    // CallReturn: the Call nodes of its group (the return addresses). Call/CallReturn: the groups whose open start a
    // call saves and its return restores, the called group and those inside it.
    internal int[]? Targets;
    internal int[]? Groups;
    // Split: the first units of the Next and Alternate branches, when known.
    internal KernelStartUnits? NextStart;
    internal KernelStartUnits? AlternateStart;
}

// Analysis half of compilation: validates eligibility and recovers structure. It emits no code, so it is
// NativeAOT-safe and testable without a JIT; CoreKernelLowering turns the plan into a delegate.
internal sealed class CoreKernelPlan
{
    internal const int MaxFirstSet = 512;
    internal const int MaxLiteralRunInline = 6;
    internal const int MaxClassRangesInline = 4;

    internal KernelNode[] Nodes { get; private init; } = [];
    internal bool[] Reachable { get; private init; } = [];
    internal int GroupCount { get; private init; }
    internal bool UnicodeScalars { get; private init; }
    internal bool DotAll { get; private init; }
    internal bool Sticky { get; private init; }
    internal char[]? FirstSet { get; private init; }
    // FirstSet lists the ASCII units a match cannot start with (any non-ASCII unit can), for IndexOfAnyExcept.
    internal bool FirstSetExcept { get; private init; }
    internal string? LiteralPrefix { get; private init; }
    // Search-level prefilters: every match contains one of RequiredSet at or after its start, so none starts after the
    // last; RequiredLiteral occurs at or after startAt + RequiredLiteralMinOffset in any subject with a match.
    internal char[]? RequiredSet { get; private init; }
    internal string? RequiredLiteral { get; private init; }
    internal int RequiredLiteralMinOffset { get; private init; }
    // Whether every match contains RequiredLiteral (the Oniguruma compiler's Required), so no attempt starts after its
    // last occurrence, as the interpreter's search bounds its starts.
    internal bool RequiredLiteralInMatch { get; private init; }
    // Every match spans at least this many units, so no attempt starts closer to the end.
    internal int MinLength { get; private init; }
    // Whether a LookStart limits its body's consumption (LookFlags.Limit), so the kernel tracks that limit.
    internal bool HasLimitFrames { get; private init; }
    // Whether the kernel holds subexpression calls, so it keeps a call frame pointer and undoes it on backtracking.
    internal bool HasCalls { get; private init; }
    // The groups a call saves and restores (besides group zero), each with an open-start register of its own at the end
    // of the stack span when captures are tracked.
    internal int[] OpenGroups { get; private init; } = [];
    // Whether the kernel models no surrogate at all: a subject holding one is reported (SurrogateEncountered) after the
    // search-level prefilters, so a subject the prefilters reject costs no scan.
    internal bool SurrogateScan { get; private init; }
    internal int LeadingRunNode { get; private init; } = -1;
    internal bool Anchored { get; private init; }
    internal int FusedNodeCount { get; private init; }
    internal int ReachableCount { get; private init; }

    internal static bool TryCreate(SemanticProgram program, in LoweringLimits limits, out CoreKernelPlan? plan, out string reason)
    {
        plan = null;
        var profile = program.Profile;
        if (profile.InputKind != RegexInputKind.Utf16 || profile.TextModel == TextModel.RawBytes)
        {
            reason = "Only UTF-16 inputs are lowered.";
            return false;
        }
        var scalars = profile.TextModel == TextModel.UnicodeScalars;
        var dotAll = (profile.Flags & RegularCoreFlags.DotAll) != 0;
        var ins = program.Instructions;
        if (ins.Length > limits.MaxNodes / 16)
        {
            reason = "The program exceeds the lowering node bound.";
            return false;
        }
        var nodes = new KernelNode[ins.Length];
        for (var i = 0; i < ins.Length; i++)
        {
            var x = ins[i];
            switch (x.Opcode)
            {
                case CoreOpcode.Literal:
                    if ((uint)x.Operand > 0xFFFF)
                    { reason = "A literal outside the BMP is not lowered."; return false; }
                    nodes[i] = new() { Op = KernelOp.Literal, Next = x.X, Operand = x.Operand };
                    break;
                case CoreOpcode.Class:
                    {
                        var cls = program.Classes[x.Operand];
                        if (!cls.Negated)
                        {
                            foreach (var r in cls.Ranges)
                            {
                                if (r.Last > 0xFFFF)
                                { reason = "A positive class with non-BMP ranges is not lowered."; return false; }
                            }
                        }

                        nodes[i] = new() { Op = KernelOp.Class, Next = x.X, Class = cls, SurrogateSensitive = scalars && Sensitive(cls) };
                        break;
                    }
                case CoreOpcode.Any:
                    nodes[i] = new() { Op = KernelOp.Any, Next = x.X, SurrogateSensitive = scalars };
                    break;
                case CoreOpcode.Split:
                    nodes[i] = new() { Op = KernelOp.Split, Next = x.X, Alternate = x.Y };
                    break;
                case CoreOpcode.Jump:
                    nodes[i] = new() { Op = KernelOp.Jump, Next = x.X };
                    break;
                case CoreOpcode.CaptureStart:
                    nodes[i] = new() { Op = KernelOp.CaptureStart, Next = x.X, Operand = x.Operand };
                    break;
                case CoreOpcode.CaptureEnd:
                    nodes[i] = new() { Op = KernelOp.CaptureEnd, Next = x.X, Operand = x.Operand };
                    break;
                case CoreOpcode.ClearCaptures:
                    nodes[i] = new() { Op = KernelOp.Clear, Next = x.X, Operand = x.Operand, Count = x.Count };
                    break;
                case CoreOpcode.AssertStart:
                    nodes[i] = new() { Op = KernelOp.AssertStart, Next = x.X };
                    break;
                case CoreOpcode.AssertEnd:
                    nodes[i] = new() { Op = KernelOp.AssertEnd, Next = x.X };
                    break;
                case CoreOpcode.Match:
                    nodes[i] = new() { Op = KernelOp.Match };
                    break;
                default:
                    reason = "Unknown opcode.";
                    return false;
            }
        }
        return Analyze(nodes, program.GroupCount, scalars, dotAll, sticky: false, limits, out plan, out reason);
    }

    // Flavor-neutral half of plan creation: front ends translate their own IR into nodes (1:1 images of backtracking
    // instructions, entry node 0, every path ending in Match) and share the structural analysis below.
    internal static bool Analyze(KernelNode[] nodes, int groupCount, bool scalars, bool dotAll, bool sticky,
        in LoweringLimits limits, out CoreKernelPlan? plan, out string reason, char[]? requiredSet = null,
        string? requiredLiteral = null, int requiredLiteralMinOffset = 0, int minLength = 0, string? literalPrefix = null,
        bool surrogateScan = false, bool requiredLiteralInMatch = false)
    {
        plan = null;
        if (nodes.Length == 0 || nodes.Length > limits.MaxNodes / 16)
        {
            reason = "The program exceeds the lowering node bound.";
            return false;
        }
        if (HasEpsilonCycle(nodes))
        {
            reason = "A loop whose body can match the empty string relies on Pike deduplication and is not lowered.";
            return false;
        }

        // First-unit sets are memoized per root for this run: the Split tests, the runs' follow sets and the atomic-run
        // analysis all ask for them, and a class's members are enumerated once from its ranges.
        var memo = new FirstUnitMemo(nodes, scalars);
        var fused = Fuse(nodes, Predecessors(nodes), dotAll, memo);
        var reachable = Reach(nodes);
        // A possessive single-unit loop is exactly an atomic class run: the longest run, never given back.
        var limitFrames = false;
        var calls = false;
        var open = new SortedSet<int>();
        for (var i = 0; i < nodes.Length; i++)
        {
            if (!reachable[i])
            {
                continue;
            }

            if (nodes[i].Possessive)
            {
                if (nodes[i].Op != KernelOp.ClassRun)
                {
                    reason = "A possessive loop over more than one unit is not lowered.";
                    return false;
                }

                nodes[i].Atomic = true;
            }

            limitFrames |= nodes[i].Op == KernelOp.LookStart && (nodes[i].Operand & LookFlags.Limit) != 0;
            if (nodes[i].Op == KernelOp.Call)
            {
                calls = true;
                foreach (var g in nodes[i].Groups ?? [])
                {
                    if (g != 0)
                    {
                        open.Add(g);
                    }
                }
            }
            if (nodes[i].Op == KernelOp.ClassRun && !nodes[i].Atomic && memo.Collect(nodes[i].Next) is { } follow)
            {
                // The continuation needs one of these at the position it starts from; a run backs off straight to one.
                if (!follow.Big)
                {
                    if (follow.Set.Count > 0)
                    {
                        nodes[i].Follow = follow.Set.ToArray();
                    }
                }
                else
                {
                    var excluded = new List<char>();
                    for (var c = 0; c < 128; c++)
                    {
                        if (!follow.Ascii[c])
                        {
                            excluded.Add((char)c);
                        }
                    }

                    if (excluded.Count > 0)
                    {
                        nodes[i].Follow = excluded.ToArray();
                        nodes[i].FollowExcept = true;
                    }
                }
            }
        }

        // A Split tests the unit at the position against each branch's first units, as the interpreters' alternation does.
        for (var i = 0; i < nodes.Length; i++)
        {
            if (reachable[i] && nodes[i].Op == KernelOp.Split)
            {
                nodes[i].NextStart = StartUnitsOf(memo.Collect(nodes[i].Next, cutAware: true));
                nodes[i].AlternateStart = StartUnitsOf(memo.Collect(nodes[i].Alternate, cutAware: true));
            }
        }

        var reachableCount = 0;
        foreach (var r in reachable)
        {
            if (r)
            {
                reachableCount++;
            }
        }

        if (reachableCount * 16 > limits.MaxNodes || (long)reachableCount * 160 > limits.MaxEstimatedCodeBytes)
        {
            reason = "The lowered program exceeds the node or code-size bound.";
            return false;
        }
        // A sticky search, or one whose every path begins with \G, makes one attempt at the start offset, so it cannot
        // skip ahead to a candidate.
        Leading(nodes, out var lead, out var anchored, out var searchAnchored);
        sticky |= searchAnchored;
        if (sticky)
        {
            lead = -1;
            anchored = false;
        }

        string? prefix = null;
        var except = false;
        var first = sticky ? null : FirstSetOf(nodes, scalars, memo, out prefix, out except);
        if (literalPrefix is { Length: >= 2 } && !sticky)
        {
            prefix = literalPrefix;
        }

        plan = new()
        {
            Nodes = nodes,
            Reachable = reachable,
            GroupCount = groupCount,
            UnicodeScalars = scalars,
            DotAll = dotAll,
            Sticky = sticky,
            FirstSet = first,
            FirstSetExcept = except,
            LiteralPrefix = prefix,
            RequiredSet = requiredSet,
            RequiredLiteral = requiredLiteral,
            RequiredLiteralMinOffset = requiredLiteralMinOffset,
            RequiredLiteralInMatch = requiredLiteralInMatch,
            MinLength = minLength,
            HasLimitFrames = limitFrames,
            HasCalls = calls,
            OpenGroups = [.. open],
            SurrogateScan = surrogateScan,
            LeadingRunNode = lead,
            Anchored = anchored,
            FusedNodeCount = fused,
            ReachableCount = reachableCount
        };
        reason = string.Empty;
        return true;
    }

    // Deterministic identity of everything the emitted kernel depends on. The source generator embeds it and
    // WithGeneratedKernels recomputes it, so kernels generated by a different generator/library revision are rejected.
    internal ulong Fingerprint()
    {
        // One multiply-xorshift step per value (binding recomputes this for every pattern a kernel is bound to).
        var h = 14695981039346656037UL;
        void Mix(long v)
        {
            h ^= (ulong)v;
            h *= 0x9E3779B97F4A7C15UL;
            h ^= h >> 29;
        }
        void MixText(string? s)
        {
            Mix(s is null ? -1 : s.Length);
            if (s is not null)
            {
                foreach (var ch in s)
                {
                    Mix(ch);
                }
            }
        }
        void MixStart(KernelStartUnits? units)
        {
            if (units is null)
            {
                Mix(-1);
                return;
            }

            Mix((long)units.Low);
            Mix((long)units.High);
            Mix((units.NonAscii ? 1 : 0) | (units.End ? 2 : 0));
        }
        void MixInts(int[]? values)
        {
            Mix(values is null ? -1 : values.Length);
            if (values is not null)
            {
                foreach (var v in values)
                {
                    Mix(v);
                }
            }
        }
        Mix(GroupCount);
        Mix(UnicodeScalars ? 1 : 0);
        Mix(DotAll ? 1 : 0);
        Mix(Sticky ? 1 : 0);
        Mix(Anchored ? 1 : 0);
        Mix(LeadingRunNode);
        MixText(LiteralPrefix);
        Mix(FirstSet is null ? -1 : FirstSet.Length);
        if (FirstSet is not null)
        {
            foreach (var ch in FirstSet)
            {
                Mix(ch);
            }
        }

        Mix(FirstSetExcept ? 1 : 0);
        Mix(RequiredSet is null ? -1 : RequiredSet.Length);
        if (RequiredSet is not null)
        {
            foreach (var ch in RequiredSet)
            {
                Mix(ch);
            }
        }

        MixText(RequiredLiteral);
        Mix(RequiredLiteralMinOffset);
        Mix(RequiredLiteralInMatch ? 1 : 0);
        Mix(MinLength);
        Mix(HasLimitFrames ? 1 : 0);
        Mix(SurrogateScan ? 1 : 0);
        Mix(HasCalls ? 1 : 0);
        MixInts(OpenGroups);

        Mix(Nodes.Length);
        var classHashes = new Dictionary<CharacterClass, ulong>();
        for (var i = 0; i < Nodes.Length; i++)
        {
            ref readonly var n = ref Nodes[i];
            Mix(Reachable[i] ? 1 : 0);
            if (!Reachable[i])
            {
                continue;
            }

            Mix((int)n.Op);
            Mix(n.Next);
            Mix(n.Alternate);
            Mix(n.Operand);
            Mix(n.Count);
            Mix((n.Possessive ? 1 : 0) | (n.Atomic ? 2 : 0) | (n.SurrogateSensitive ? 4 : 0) | (n.Backward ? 8 : 0) | (n.FollowExcept ? 16 : 0));
            MixText(n.Text);
            MixText(n.Follow is null ? null : new string(n.Follow));
            MixInts(n.Targets);
            MixInts(n.Groups);
            MixStart(n.NextStart);
            MixStart(n.AlternateStart);
            if (n.Class is { } cls)
            {
                // A class is shared by every node that tests it: its ranges are hashed once.
                if (!classHashes.TryGetValue(cls, out var classHash))
                {
                    classHash = 14695981039346656037UL;
                    void MixClass(long v)
                    {
                        classHash ^= (ulong)v;
                        classHash *= 0x9E3779B97F4A7C15UL;
                        classHash ^= classHash >> 29;
                    }
                    MixClass(cls.Negated ? 1 : 0);
                    MixClass(cls.Ranges.Length);
                    foreach (var r in cls.Ranges)
                    { MixClass(r.First); MixClass(r.Last); }
                    classHashes[cls] = classHash;
                }

                Mix((long)classHash);
            }
            else
            {
                Mix(-1);
            }
        }
        return h;
    }

    // In UnicodeScalars mode a surrogate unit is half of a non-BMP scalar the kernel does not model, so any class
    // whose answer for such a scalar differs from "no match" (negated, or ranges touching the surrogate block) bails.
    private static bool Sensitive(CharacterClass cls)
    {
        if (cls.Negated)
        {
            return true;
        }

        foreach (var r in cls.Ranges)
        {
            if (r.First <= 0xDFFF && r.Last >= 0xD800)
            {
                return true;
            }
        }

        return false;
    }

    // A lookaround body ends at its LookEnd, which is treated as a dead end: the continuation is reached through
    // LookStart's Alternate edge, so a cycle can only pass through a lookaround without entering its body. An atomic
    // group's LookEnd continues into the body's continuation instead (its LookStart has no Alternate edge).
    // A Call is an epsilon step to its continuation only when the callee can match empty (as a grammar's nullable
    // nonterminal); the callee's own nodes are walked from their own roots. A CallReturn continues the definition.
    private static int EpsilonEdge(in KernelNode node, int edge)
    {
        if (node.Op is KernelOp.Literal or KernelOp.Class or KernelOp.Any or KernelOp.Match || (node.Op == KernelOp.LookEnd && node.Count == 0))
        {
            return -1;
        }

        if (node.Op == KernelOp.Call)
        {
            return edge == 0 && node.Count != 0 ? node.Next : -1;
        }

        if (edge == 0)
        {
            return node.Next;
        }

        return edge == 1 && (node.Op == KernelOp.Split || (node.Op == KernelOp.LookStart && node.Count == 0)) ? node.Alternate : -1;
    }

    private static bool HasEpsilonCycle(KernelNode[] nodes)
    {
        var color = new byte[nodes.Length];
        var stack = new Stack<(int Node, int Edge)>();
        for (var root = 0; root < nodes.Length; root++)
        {
            if (color[root] != 0)
            {
                continue;
            }

            color[root] = 1;
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (n, edge) = stack.Pop();
                var target = EpsilonEdge(nodes[n], edge);
                if (target < 0)
                { color[n] = 2; continue; }
                stack.Push((n, edge + 1));
                if (color[target] == 1)
                {
                    return true;
                }

                if (color[target] == 0)
                {
                    color[target] = 1;
                    stack.Push((target, 0));
                }
            }
        }
        return false;
    }

    private static int[] Predecessors(KernelNode[] nodes)
    {
        var pred = new int[nodes.Length];
        pred[0]++;
        for (var i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].Op == KernelOp.Match)
            {
                continue;
            }

            pred[nodes[i].Next]++;
            if (nodes[i].Op == KernelOp.Split || (nodes[i].Op == KernelOp.LookStart && nodes[i].Count == 0) || nodes[i].Op == KernelOp.Call)
            {
                pred[nodes[i].Alternate]++;
            }
        }
        return pred;
    }

    // Recovers fused nodes from the graph: runs of single-predecessor literals and greedy single-class loops
    // (`[c]*` is Split(body, exit) with body looping back; `[c]+` additionally has one entry copy of the body).
    private static int Fuse(KernelNode[] nodes, int[] pred, bool dotAll, FirstUnitMemo memo)
    {
        var n = nodes.Length;
        for (var s = 0; s < n; s++)
        {
            if (nodes[s].Op != KernelOp.Split)
            {
                continue;
            }

            int body = nodes[s].Next, exit = nodes[s].Alternate;
            var b = nodes[body];
            if (b.Op is not (KernelOp.Class or KernelOp.Any) || b.Backward || b.Next != s || pred[body] != 1)
            {
                continue;
            }

            var loop = new KernelNode
            {
                Op = KernelOp.ClassRun,
                Class = b.Class,
                Next = exit,
                Count = 0,
                Operand = b.Op == KernelOp.Any ? 1 : 0,
                SurrogateSensitive = b.SurrogateSensitive,
                Possessive = nodes[s].Possessive
            };
            var plus = false;
            if (pred[s] == 2)
            {
                for (var a = 0; a < n; a++)
                {
                    if (a != body && a != s && nodes[a].Op == b.Op && !nodes[a].Backward && nodes[a].Next == s && nodes[a].Class == b.Class)
                    {
                        loop.Count = 1;
                        nodes[a] = loop;
                        plus = true;
                        break;
                    }
                }
            }

            if (!plus)
            {
                nodes[s] = loop;
            }
        }

        var continuation = new bool[n];
        for (var j = 0; j < n; j++)
        {
            if (Forward(nodes, j) && Forward(nodes, nodes[j].Next) && pred[nodes[j].Next] == 1)
            {
                continuation[nodes[j].Next] = true;
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (!Forward(nodes, i) || continuation[i])
            {
                continue;
            }

            if (!Forward(nodes, nodes[i].Next) || pred[nodes[i].Next] != 1 || nodes[i].Next == i)
            {
                continue;
            }

            using var text = new ValueStringBuilder();
            text.Append((char)nodes[i].Operand);
            var cur = i;
            while (Forward(nodes, nodes[cur].Next) && pred[nodes[cur].Next] == 1 && nodes[cur].Next != i)
            {
                cur = nodes[cur].Next;
                text.Append((char)nodes[cur].Operand);
            }
            nodes[i] = new() { Op = KernelOp.LiteralRun, Text = text.ToString(), Next = nodes[cur].Next };
        }

        var fused = 0;
        for (var i = 0; i < n; i++)
        {
            if (nodes[i].Op == KernelOp.ClassRun)
            {
                nodes[i].Atomic = FollowDisjoint(nodes, nodes[i].Next, nodes[i].Class, nodes[i].Operand == 1, dotAll, memo, new byte[n]);
            }

            if (nodes[i].Op is KernelOp.ClassRun or KernelOp.LiteralRun)
            {
                fused++;
            }
        }
        return fused;
    }

    private static bool Forward(KernelNode[] nodes, int i) => nodes[i].Op == KernelOp.Literal && !nodes[i].Backward;

    // True when whatever follows the loop cannot start with a unit the loop accepts, so giving units back is futile.
    // Reaching the end of a lookaround body settles the assertion and discards the body's choices, like reaching Match.
    // state: per node, 0 unvisited, 1 on the current path (a cycle through return edges, which consumes nothing: the
    // consumers reached elsewhere decide), 2 known disjoint, 3 known not.
    private static bool FollowDisjoint(KernelNode[] nodes, int pc, CharacterClass? cls, bool any, bool dotAll, FirstUnitMemo memo, byte[] state)
    {
        // The chain walked here shares the answer of its end: settle every node on it.
        var chain = new List<int>();
        var result = Walk();
        foreach (var at in chain)
        {
            state[at] = result ? (byte)2 : (byte)3;
        }

        return result;

        bool Walk()
        {
            for (var hops = 0; hops < nodes.Length; hops++)
            {
                switch (state[pc])
                {
                    case 1:
                    case 2:
                        return true;
                    case 3:
                        return false;
                }

                state[pc] = 1;
                chain.Add(pc);
                var o = nodes[pc];
                switch (o.Op)
                {
                    case KernelOp.CaptureStart:
                    case KernelOp.CaptureEnd:
                    case KernelOp.Clear:
                    case KernelOp.Jump:
                        pc = o.Next;
                        continue;
                    case KernelOp.Call:
                        // The callee consumes first.
                        pc = o.Alternate;
                        continue;
                    case KernelOp.CallReturn:
                        {
                            // The group's definition continues at Next, a call at its site's continuation: all must exclude.
                            if (!FollowDisjoint(nodes, o.Next, cls, any, dotAll, memo, state))
                            {
                                return false;
                            }

                            foreach (var site in o.Targets ?? [])
                            {
                                if (!FollowDisjoint(nodes, nodes[site].Next, cls, any, dotAll, memo, state))
                                {
                                    return false;
                                }
                            }

                            return true;
                        }
                    case KernelOp.Match:
                    case KernelOp.AssertEnd:
                        return true;
                    case KernelOp.LookEnd when o.Count == 0:
                        return true;
                    case KernelOp.LookEnd:
                        // An atomic group's end: whatever follows the group follows the run.
                        pc = o.Next;
                        continue;
                    case KernelOp.Literal when !o.Backward:
                        return any ? !dotAll && o.Operand == '\n' : !cls!.MatchesBmpChar((char)o.Operand);
                    case KernelOp.LiteralRun:
                        return any ? !dotAll && o.Text![0] == '\n' : !cls!.MatchesBmpChar(o.Text![0]);
                    case KernelOp.Class when !o.Backward:
                        if (any)
                        {
                            return false;
                        }

                        return BmpDisjoint(cls!, o.Class!);
                    default:
                        // A loop, an alternation, a look-around: the units the continuation can start with, as the
                        // interpreters' follow sets, when every path consumes.
                        return !any && memo.Collect(pc) is { } follow && Disjoint(cls!, follow);
                }
            }

            return false;
        }
    }

    // Whether no unit of cls can start the continuation whose first units are follow.
    private static bool Disjoint(CharacterClass cls, FirstUnits follow)
    {
        for (var c = 0; c < 128; c++)
        {
            if (follow.Ascii[c] && cls.MatchesBmpChar((char)c))
            {
                return false;
            }
        }

        if (follow.Big)
        {
            // Any non-ASCII unit may start the continuation: the class must hold none.
            return !HasBmpMemberFrom(cls, 128);
        }

        foreach (var c in follow.Set)
        {
            if (c >= 128 && cls.MatchesBmpChar(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool[] Reach(KernelNode[] nodes)
    {
        var seen = new bool[nodes.Length];
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            if (seen[i])
            {
                continue;
            }

            seen[i] = true;
            if (nodes[i].Op == KernelOp.Match)
            {
                continue;
            }

            stack.Push(nodes[i].Next);
            if (nodes[i].Op == KernelOp.Split || (nodes[i].Op == KernelOp.LookStart && nodes[i].Count == 0) || nodes[i].Op == KernelOp.Call)
            {
                stack.Push(nodes[i].Alternate);
            }
        }
        return seen;
    }

    // Units that can start a match, or null when the match may be empty or the set is unknown. A positive look-ahead
    // at the very start constrains the first unit as much as the continuation does, so the two sets are intersected.
    // When the set is too large to enumerate but its ASCII part is not everything, the ASCII units that cannot start a
    // match are returned instead (except = true), as the interpreter's FirstUnitsExcept filter does.
    private static char[]? FirstSetOf(KernelNode[] nodes, bool scalars, FirstUnitMemo memo, out string? literalPrefix, out bool except)
    {
        literalPrefix = null;
        except = false;
        var pc = 0;
        for (var hops = 0; hops < nodes.Length && nodes[pc].Op is KernelOp.CaptureStart or KernelOp.CaptureEnd or KernelOp.Clear or KernelOp.Jump; hops++)
        {
            pc = nodes[pc].Next;
        }

        FirstUnits? units;
        string? run = null;
        if (nodes[pc].Op == KernelOp.LookStart && nodes[pc].Operand == 0)
        {
            var body = memo.Collect(nodes[pc].Next);
            var rest = memo.Collect(nodes[pc].Alternate);
            units = body is { Big: false } && rest is { Big: false } ? body.Intersect(rest)
                : body is { Big: false } ? body : rest is { Big: false } ? rest : body ?? rest;
        }
        else
        {
            units = memo.Collect(0, out run);
        }

        if (units is null || units.End)
        {
            // A match may end at the input's end without consuming: no unit to look for.
            return null;
        }

        if (units.Big)
        {
            // Every non-ASCII unit stays a candidate; the surrogate guard after the skip covers UnicodeScalars mode.
            var excluded = new List<char>();
            for (var c = 0; c < 128; c++)
            {
                if (!units.Ascii[c])
                {
                    excluded.Add((char)c);
                }
            }

            if (excluded.Count == 0)
            {
                return null;
            }

            except = true;
            return excluded.ToArray();
        }

        if (units.Set.Count == 0)
        {
            return null;
        }

        if (scalars)
        {
            foreach (var c in units.Set)
            {
                if (char.IsSurrogate(c))
                {
                    return null;
                }
            }
        }

        literalPrefix = run;
        return units.Set.ToArray();
    }

    private static KernelStartUnits? StartUnitsOf(FirstUnits? units)
    {
        if (units is null)
        {
            return null;
        }

        var start = new KernelStartUnits { NonAscii = units.Big, End = units.End };
        for (var c = 0; c < 128; c++)
        {
            if (units.Ascii[c])
            {
                if (c < 64)
                {
                    start.Low |= 1UL << c;
                }
                else
                {
                    start.High |= 1UL << (c - 64);
                }
            }
        }

        if (!units.Big)
        {
            foreach (var c in units.Set)
            {
                start.NonAscii |= c >= 128;
            }
        }

        return start;
    }

    private sealed class FirstUnits
    {
        internal readonly HashSet<char> Set = [];
        internal readonly bool[] Ascii = new bool[128];
        internal bool Big;
        // A path asserts the end of the input (or the end of a line, which the terminator units cover elsewhere).
        internal bool End;

        internal void Add(char c)
        {
            if (c < 128)
            {
                Ascii[c] = true;
            }

            if (!Big && Set.Add(c) && Set.Count > MaxFirstSet)
            {
                Big = true;
                Set.Clear();
            }
        }

        internal void AddAll(FirstUnits other)
        {
            End |= other.End;
            for (var c = 0; c < 128; c++)
            {
                Ascii[c] |= other.Ascii[c];
            }

            if (other.Big)
            {
                Big = true;
                Set.Clear();
            }
            else
            {
                foreach (var c in other.Set)
                {
                    Add(c);
                }
            }
        }

        internal FirstUnits Intersect(FirstUnits other)
        {
            var result = new FirstUnits { End = End && other.End };
            foreach (var c in Set)
            {
                if (other.Set.Contains(c))
                {
                    result.Add(c);
                }
            }

            return result;
        }
    }

    // The union of first units over every path from root, or null when a path consumes nothing it constrains (an empty
    // match, a surrogate-sensitive class, an unconstrained run). Zero-width assertions are stepped over; a positive
    // look-ahead whose body has a first set constrains the position's unit even when the continuation is empty.
    // literalPrefix is set when the only consumer is one literal run. cutAware stops at an atomic group's end: a branch
    // that fails past that cut does not fall back to its Split's other branch, so skipping it there would be wrong.
    // The first-unit sets of one Analyze run: Collect's result per (root, cutAware), and each class's BMP members.
    private sealed class FirstUnitMemo(KernelNode[] nodes, bool scalars)
    {
        private readonly Dictionary<(int, bool), (FirstUnits? Units, string? Prefix)> _results = [];
        private readonly Dictionary<CharacterClass, FirstUnits> _classes = [];

        internal FirstUnits? Collect(int root, bool cutAware = false) => Collect(root, out _, cutAware);

        internal FirstUnits? Collect(int root, out string? literalPrefix, bool cutAware = false)
        {
            if (_results.TryGetValue((root, cutAware), out var cached))
            {
                literalPrefix = cached.Prefix;
                return cached.Units;
            }

            var units = CollectCore(nodes, root, scalars, out literalPrefix, cutAware);
            _results[(root, cutAware)] = (units, literalPrefix);
            return units;
        }

        // The units a class accepts, enumerated once from its ranges (their complement for a negated class).
        private FirstUnits ClassUnits(CharacterClass cls)
        {
            if (_classes.TryGetValue(cls, out var cached))
            {
                return cached;
            }

            var units = new FirstUnits();
            foreach (var (first, last) in BmpRanges(cls))
            {
                AddRange(units, first, last);
            }

            _classes[cls] = units;
            return units;
        }

        private static void AddRange(FirstUnits units, int first, int last)
        {
            // Past the enumeration bound the set is Big: only the ASCII bits are kept, so add those and mark it.
            if (last - first + 1 > MaxFirstSet)
            {
                for (var c = first; c <= Math.Min(last, 127); c++)
                {
                    units.Ascii[c] = true;
                }

                units.Big = true;
                units.Set.Clear();
                return;
            }

            for (var c = first; c <= last; c++)
            {
                units.Add((char)c);
            }
        }

        private FirstUnits? CollectCore(KernelNode[] nodes, int root, bool scalars, out string? literalPrefix, bool cutAware)
        {
            literalPrefix = null;
            var units = new FirstUnits();
            var seen = new bool[nodes.Length];
            var stack = new Stack<int>();
            stack.Push(root);
            var consumers = 0;
            string? run = null;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                if (seen[i])
                {
                    continue;
                }

                seen[i] = true;
                var o = nodes[i];
                switch (o.Op)
                {
                    case KernelOp.CaptureStart:
                    case KernelOp.CaptureEnd:
                    case KernelOp.Clear:
                    case KernelOp.Jump:
                    case KernelOp.AssertStart:
                    case KernelOp.AssertSearchStart:
                    case KernelOp.AssertLineStart:
                    case KernelOp.WordBoundary:
                    case KernelOp.NotWordBoundary:
                        stack.Push(o.Next);
                        break;
                    case KernelOp.AssertEnd:
                        // The path holds at the input's end only: no unit, as the interpreters' follow sets record it.
                        units.End = true;
                        consumers++;
                        break;
                    case KernelOp.AssertLineEnd:
                        // At the end, or before a line terminator of the anchor's style (what follows only narrows this).
                        units.End = true;
                        units.Add('\n');
                        if (o.Operand == 0)
                        {
                            units.Add('\r');
                            units.Add('\u2028');
                            units.Add('\u2029');
                        }

                        consumers++;
                        break;
                    case KernelOp.Split:
                        stack.Push(o.Alternate);
                        stack.Push(o.Next);
                        break;
                    case KernelOp.Call:
                        // The callee consumes first; its CallReturn leads back to this call's continuation.
                        stack.Push(o.Alternate);
                        break;
                    case KernelOp.CallReturn:
                        // The definition's continuation, or any call site's: the union covers whichever applies.
                        stack.Push(o.Next);
                        foreach (var site in o.Targets ?? [])
                        {
                            stack.Push(nodes[site].Next);
                        }

                        break;
                    case KernelOp.LookStart when o.Count != 0:
                        // An atomic group (Count 1) consumes like its body, which continues at its LookEnd.
                        consumers += 2;
                        stack.Push(o.Next);
                        break;
                    case KernelOp.LookEnd when o.Count != 0:
                        if (cutAware)
                        {
                            return null;
                        }

                        stack.Push(o.Next);
                        break;
                    case KernelOp.LookStart:
                        consumers += 2;
                        if (o.Operand == 0 && Collect(o.Next) is { } body)
                        {
                            // The look-ahead reads the unit at this position, whatever the continuation does.
                            units.AddAll(body);
                            break;
                        }

                        stack.Push(o.Alternate);
                        break;
                    case KernelOp.Literal when !o.Backward:
                        units.Add((char)o.Operand);
                        consumers++;
                        break;
                    case KernelOp.LiteralRun:
                        units.Add(o.Text![0]);
                        consumers++;
                        run = o.Text;
                        break;
                    case KernelOp.Class when !o.Backward:
                    case KernelOp.ClassRun:
                        if (o.Op == KernelOp.ClassRun && o.Operand == 1)
                        {
                            return null;
                        }

                        if (scalars && o.SurrogateSensitive)
                        {
                            return null;
                        }

                        units.AddAll(ClassUnits(o.Class!));
                        consumers++;
                        if (o.Op == KernelOp.ClassRun && o.Count == 0)
                        {
                            stack.Push(o.Next);
                        }

                        break;
                    default:
                        return null;
                }
            }

            if (consumers == 1 && run is { Length: >= 2 } && !units.End)
            {
                literalPrefix = run;
            }

            return units;
        }
    }

    // Whether cls accepts any BMP unit at or above from.
    private static bool HasBmpMemberFrom(CharacterClass cls, int from)
    {
        foreach (var (_, last) in BmpRanges(cls))
        {
            if (last >= from)
            {
                return true;
            }
        }

        return false;
    }

    // Whether no BMP unit is in both classes: a merge of their sorted member ranges.
    private static bool BmpDisjoint(CharacterClass a, CharacterClass b)
    {
        var ra = BmpRanges(a);
        var rb = BmpRanges(b);
        int i = 0, j = 0;
        while (i < ra.Count && j < rb.Count)
        {
            if (ra[i].Last < rb[j].First)
            {
                i++;
            }
            else if (rb[j].Last < ra[i].First)
            {
                j++;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    // The BMP units a class accepts as sorted ranges: its ranges, or their complement for a negated class.
    private static List<(int First, int Last)> BmpRanges(CharacterClass cls)
    {
        var list = new List<(int First, int Last)>();
        var cursor = 0;
        foreach (var r in cls.Ranges)
        {
            if (r.First > 0xFFFF)
            {
                break;
            }

            var last = Math.Min(r.Last, 0xFFFF);
            if (cls.Negated)
            {
                if (r.First - 1 >= cursor)
                {
                    list.Add((cursor, r.First - 1));
                }
            }
            else
            {
                list.Add((r.First, last));
            }

            cursor = last + 1;
        }

        if (cls.Negated && cursor <= 0xFFFF)
        {
            list.Add((cursor, 0xFFFF));
        }

        return list;
    }

    // A leading anchor allows a single attempt (at 0 for ^, at the search start for \G); a leading atomic `[c]+` lets a
    // failed attempt skip the whole run.
    private static void Leading(KernelNode[] nodes, out int leadingRun, out bool anchored, out bool searchAnchored)
    {
        leadingRun = -1;
        anchored = false;
        searchAnchored = false;
        var pc = 0;
        for (var hops = 0; hops < nodes.Length; hops++)
        {
            var o = nodes[pc];
            switch (o.Op)
            {
                case KernelOp.CaptureStart:
                case KernelOp.CaptureEnd:
                case KernelOp.Clear:
                case KernelOp.Jump:
                    pc = o.Next;
                    continue;
                case KernelOp.AssertStart:
                    anchored = true;
                    return;
                case KernelOp.AssertSearchStart:
                    searchAnchored = true;
                    return;
                case KernelOp.ClassRun:
                    if (o.Count == 1 && o.Atomic && o.Operand == 0)
                    {
                        leadingRun = pc;
                    }

                    return;
                default:
                    return;
            }
        }
    }
}
