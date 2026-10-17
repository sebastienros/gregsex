#nullable enable
using GregsEx.Contracts;

namespace GregsEx.Flavors.XPath.Host;

internal static partial class XPathHostFunctions
{
    internal static XPathAnalyzeResult AnalyzeString(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        var context = HostContext.Create(pattern, flags, compile, match, host, unicodeVersion);
        context.NonEmptyPattern();
        input ??= "";
        context.Validate(input);
        // Strings are immutable owners; retain the original, never a caller span.
        // Conservatively count its storage even when no copying is necessary.
        context.Units(input.Length);
        context.Reserve(32L + 2L * input.Length);
        var count = context.Groups.Length;
        context.Reserve(224L + 44L * count);
        var order = new int[count];
        var first = new int[count];
        var next = new int[count];
        var tail = new int[count];
        var effectiveParents = new int[count];
        var frames = new TreeFrame[count];
        var nodes = new HostBuffer<XPathAnalyzeNode>(20);
        var cursor = 0;
        while (cursor < input.Length && context.Search(input, cursor, SearchOperation.Captures, out var range))
        {
            if (cursor < range.Index)
            {
                Node(new(XPathAnalyzeNodeKind.NonMatch, -1, 0,
                new(cursor, range.Index - cursor)), ref nodes, ref context);
            }

            var root = nodes.Count;
            Node(new(XPathAnalyzeNodeKind.Match, -1, 0, range), ref nodes, ref context);
            BuildTree(input, order, first, next, tail, effectiveParents, frames, root, ref nodes, ref context);
            cursor = checked(range.Index + range.Length);
        }
        if (cursor < input.Length)
        {
            Node(new(XPathAnalyzeNodeKind.NonMatch, -1, 0,
            new(cursor, input.Length - cursor)), ref nodes, ref context);
        }
        // Constructor copies and wraps the array; count old/new node storage.
        context.Reserve(96L + 20L * nodes.Count);
        context.Charge(nodes.Count);
        return new(input, nodes.Items);
    }

    private readonly record struct TreeFrame(int Group, int Node, int Cursor, int Next);
    private static void BuildTree(string input, int[] order, int[] first, int[] next, int[] tail, int[] effectiveParents,
        TreeFrame[] frames, int root, ref HostBuffer<XPathAnalyzeNode> nodes, ref HostContext context)
    {
        var groups = context.Groups;
        var parents = context.Executor!.Program.GroupParents;
        context.Charge(3L * groups.Length);
        Array.Fill(first, -1);
        Array.Fill(next, -1);
        Array.Fill(tail, -1);
        var size = 0;
        for (var g = 1; g < groups.Length; g++)
        {
            context.Charge(1);
            if (!groups[g].Participated)
            {
                continue;
            }

            var capture = groups[g];
            var parent = parents[g];
            if (!Boundary(input, capture.Index) || !Boundary(input, capture.Index + capture.Length))
            {
                throw new InvalidOperationException("Executor capture is not on scalar boundaries.");
            }
            // Retain every final participating group. An earlier
            // repeated child can escape its parent's final iteration; climb
            // only syntactic ancestors until a participating range contains it.
            while (!groups[parent].Participated || !Contains(groups[parent], capture))
            {
                context.Charge(1);
                if (parent == 0)
                {
                    throw new InvalidOperationException("Executor capture escapes the match.");
                }

                parent = parents[parent];
            }
            effectiveParents[g] = parent;
            // Position order is required for last captures from different repeat
            // iterations. At a shared boundary put empty sibling groups first.
            var at = size;
            while (at > 0)
            {
                context.Charge(1);
                var previous = groups[order[at - 1]];
                var comparison = previous.Index.CompareTo(capture.Index);
                if (comparison == 0)
                {
                    comparison = (previous.Length != 0).CompareTo(capture.Length != 0);
                }

                if (comparison == 0)
                {
                    comparison = order[at - 1].CompareTo(g);
                }

                if (comparison <= 0)
                {
                    break;
                }

                order[at] = order[at - 1];
                at--;
            }
            order[at] = g;
            size++;
        }
        for (var i = 0; i < size; i++)
        {
            context.Charge(1);
            int g = order[i], parent = effectiveParents[g];
            if (first[parent] < 0)
            {
                first[parent] = g;
            }
            else
            {
                next[tail[parent]] = g;
            }

            tail[parent] = g;
        }
        var depth = 0;
        frames[0] = new(0, root, groups[0].Index, first[0]);
        while (depth >= 0)
        {
            context.Charge(1);
            var frame = frames[depth];
            var parent = groups[frame.Group];
            if (frame.Next < 0)
            {
                TextNode(frame.Cursor, parent.Index + parent.Length, frame.Node, ref nodes, ref context);
                depth--;
                continue;
            }
            var child = frame.Next;
            var capture = groups[child];
            if (capture.Index < frame.Cursor)
            {
                throw new InvalidOperationException("Final sibling captures overlap.");
            }

            TextNode(frame.Cursor, capture.Index, frame.Node, ref nodes, ref context);
            var node = nodes.Count;
            Node(new(XPathAnalyzeNodeKind.Group, frame.Node, child, new(capture.Index, capture.Length)), ref nodes, ref context);
            frames[depth] = frame with { Cursor = capture.Index + capture.Length, Next = next[child] };
            frames[++depth] = new(child, node, capture.Index, first[child]);
        }
    }
    private static bool Contains(RegexCapture parent, RegexCapture child) => child.Index >= parent.Index &&
        (long)child.Index + child.Length <= (long)parent.Index + parent.Length;
    private static bool Boundary(string input, int offset) => (uint)offset <= (uint)input.Length &&
        (offset == input.Length || !char.IsLowSurrogate(input[offset]));
    private static void TextNode(int start, int end, int parent,
        ref HostBuffer<XPathAnalyzeNode> nodes, ref HostContext context)
    {
        if (start < end)
        {
            Node(new(XPathAnalyzeNodeKind.Text, parent, 0, new(start, end - start)), ref nodes, ref context);
        }
    }
    private static void Node(XPathAnalyzeNode node, ref HostBuffer<XPathAnalyzeNode> nodes, ref HostContext context)
    { context.Result(); nodes.Add(node, ref context); }
}
