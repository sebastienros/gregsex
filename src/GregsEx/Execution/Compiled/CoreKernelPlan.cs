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
    AssertLineStart, AssertLineEnd, WordBoundary, NotWordBoundary
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
    internal string? LiteralPrefix { get; private init; }
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
        in LoweringLimits limits, out CoreKernelPlan? plan, out string reason)
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

        var fused = Fuse(nodes, Predecessors(nodes), dotAll);
        var reachable = Reach(nodes);
        // A possessive loop equals the greedy kernel loop only when giving units back can never help.
        for (var i = 0; i < nodes.Length; i++)
        {
            if (reachable[i] && nodes[i].Possessive && !(nodes[i].Op == KernelOp.ClassRun && nodes[i].Atomic))
            {
                reason = "A possessive loop is followed by something that could use a shorter run.";
                return false;
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
        // A sticky search makes one attempt at the start offset, so it cannot skip ahead to a candidate.
        string? prefix = null;
        var first = sticky ? null : FirstSetOf(nodes, scalars, out prefix);
        var lead = -1;
        var anchored = false;
        if (!sticky)
        {
            Leading(nodes, out lead, out anchored);
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
            LiteralPrefix = prefix,
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
        var h = 14695981039346656037UL;
        void Mix(long v)
        {
            for (var i = 0; i < 8; i++)
            { h ^= (byte)(v >> (8 * i)); h *= 1099511628211UL; }
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

        Mix(Nodes.Length);
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
            Mix((n.Possessive ? 1 : 0) | (n.Atomic ? 2 : 0) | (n.SurrogateSensitive ? 4 : 0));
            MixText(n.Text);
            if (n.Class is { } cls)
            {
                Mix(cls.Negated ? 1 : 0);
                Mix(cls.Ranges.Length);
                foreach (var r in cls.Ranges)
                { Mix(r.First); Mix(r.Last); }
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

    private static int EpsilonEdge(in KernelNode node, int edge)
    {
        if (node.Op is KernelOp.Literal or KernelOp.Class or KernelOp.Any or KernelOp.Match)
        {
            return -1;
        }

        if (edge == 0)
        {
            return node.Next;
        }

        return edge == 1 && node.Op == KernelOp.Split ? node.Alternate : -1;
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
            if (nodes[i].Op == KernelOp.Split)
            {
                pred[nodes[i].Alternate]++;
            }
        }
        return pred;
    }

    // Recovers fused nodes from the graph: runs of single-predecessor literals and greedy single-class loops
    // (`[c]*` is Split(body, exit) with body looping back; `[c]+` additionally has one entry copy of the body).
    private static int Fuse(KernelNode[] nodes, int[] pred, bool dotAll)
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
            if (b.Op is not (KernelOp.Class or KernelOp.Any) || b.Next != s || pred[body] != 1)
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
                    if (a != body && a != s && nodes[a].Op == b.Op && nodes[a].Next == s && nodes[a].Class == b.Class)
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
            if (nodes[j].Op == KernelOp.Literal && nodes[nodes[j].Next].Op == KernelOp.Literal && pred[nodes[j].Next] == 1)
            {
                continuation[nodes[j].Next] = true;
            }
        }

        for (var i = 0; i < n; i++)
        {
            if (nodes[i].Op != KernelOp.Literal || continuation[i])
            {
                continue;
            }

            if (nodes[nodes[i].Next].Op != KernelOp.Literal || pred[nodes[i].Next] != 1 || nodes[i].Next == i)
            {
                continue;
            }

            using var text = new ValueStringBuilder();
            text.Append((char)nodes[i].Operand);
            var cur = i;
            while (nodes[nodes[cur].Next].Op == KernelOp.Literal && pred[nodes[cur].Next] == 1 && nodes[cur].Next != i)
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
                nodes[i].Atomic = FollowDisjoint(nodes, nodes[i].Next, nodes[i].Class, nodes[i].Operand == 1, dotAll);
            }

            if (nodes[i].Op is KernelOp.ClassRun or KernelOp.LiteralRun)
            {
                fused++;
            }
        }
        return fused;
    }

    // True when whatever follows the loop cannot start with a unit the loop accepts, so giving units back is futile.
    private static bool FollowDisjoint(KernelNode[] nodes, int pc, CharacterClass? cls, bool any, bool dotAll)
    {
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
                case KernelOp.Match:
                case KernelOp.AssertEnd:
                    return true;
                case KernelOp.Literal:
                    return any ? !dotAll && o.Operand == '\n' : !cls!.MatchesBmpChar((char)o.Operand);
                case KernelOp.LiteralRun:
                    return any ? !dotAll && o.Text![0] == '\n' : !cls!.MatchesBmpChar(o.Text![0]);
                case KernelOp.Class:
                    if (any)
                    {
                        return false;
                    }

                    for (var c = 0; c <= 0xFFFF; c++)
                    {
                        if (cls!.MatchesBmpChar((char)c) && o.Class!.MatchesBmpChar((char)c))
                        {
                            return false;
                        }
                    }

                    return true;
                default:
                    return false;
            }
        }
        return false;
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
            if (nodes[i].Op == KernelOp.Split)
            {
                stack.Push(nodes[i].Alternate);
            }
        }
        return seen;
    }

    // Units that can start a match, or null when the match may be empty/anchored or the set is too large to enumerate.
    private static char[]? FirstSetOf(KernelNode[] nodes, bool scalars, out string? literalPrefix)
    {
        literalPrefix = null;
        var set = new HashSet<char>();
        var seen = new bool[nodes.Length];
        var stack = new Stack<int>();
        stack.Push(0);
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
                    stack.Push(o.Next);
                    break;
                case KernelOp.Split:
                    stack.Push(o.Alternate);
                    stack.Push(o.Next);
                    break;
                case KernelOp.Literal:
                    set.Add((char)o.Operand);
                    consumers++;
                    break;
                case KernelOp.LiteralRun:
                    set.Add(o.Text![0]);
                    consumers++;
                    run = o.Text;
                    break;
                case KernelOp.Class:
                case KernelOp.ClassRun:
                    if (o.Op == KernelOp.ClassRun && o.Operand == 1)
                    {
                        return null;
                    }

                    if (scalars && o.SurrogateSensitive)
                    {
                        return null;
                    }

                    for (var c = 0; c <= 0xFFFF; c++)
                    {
                        if (o.Class!.MatchesBmpChar((char)c) && set.Add((char)c) && set.Count > MaxFirstSet)
                        {
                            return null;
                        }
                    }

                    consumers++;
                    if (o.Op == KernelOp.ClassRun && o.Count == 0)
                    {
                        stack.Push(o.Next);
                    }

                    break;
                default:
                    return null;
            }
            if (set.Count > MaxFirstSet)
            {
                return null;
            }
        }
        if (scalars)
        {
            foreach (var c in set)
            {
                if (char.IsSurrogate(c))
                {
                    return null;
                }
            }
        }

        if (consumers == 1 && run is { Length: >= 2 })
        {
            literalPrefix = run;
        }

        return set.Count == 0 ? null : set.ToArray();
    }

    // A leading anchor allows a single attempt; a leading atomic `[c]+` lets a failed attempt skip the whole run.
    private static void Leading(KernelNode[] nodes, out int leadingRun, out bool anchored)
    {
        leadingRun = -1;
        anchored = false;
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
