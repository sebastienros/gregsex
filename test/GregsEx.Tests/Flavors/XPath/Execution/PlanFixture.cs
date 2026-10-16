using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Execution;
using Xunit;

namespace GregsEx.Tests.Flavors.XPath.Execution;

// Test-owned tree construction, independent of XPath grammar/parser output.
internal static class PlanFixture
{
    internal sealed record Tree(XmlNodeKind Kind, Tree? X = null, Tree? Y = null,
        int Value = 0, int Min = 0, int Max = 0, bool Lazy = false, int Offset = 0, int Length = 100);
    internal static Tree L(int cp, int offset = 0) => new(XmlNodeKind.Literal, Value: cp, Offset: offset, Length: 1);
    internal static Tree E(int offset = 0) => new(XmlNodeKind.Empty, Offset: offset, Length: 0);
    internal static Tree S(Tree x, Tree y) => new(XmlNodeKind.Sequence, x, y);
    internal static Tree A(Tree x, Tree y) => new(XmlNodeKind.Alternative, x, y);
    internal static Tree R(Tree x, int min = 0, int max = -1, bool lazy = false) => new(XmlNodeKind.Repeat, x, Min: min, Max: max, Lazy: lazy);
    internal static Tree C(Tree x, int group, int offset, int length) => new(XmlNodeKind.Capture, x, Value: group, Offset: offset, Length: length);
    internal static Tree B(int group, int offset = 90) => new(XmlNodeKind.Backreference, Value: group, Offset: offset, Length: 1);
    internal static Tree Anchor(bool end) => new(end ? XmlNodeKind.End : XmlNodeKind.Start, Length: 1);
    internal static XmlUnicodeData Data()
    { var b = new CompileBudget(new()); Assert.True(XmlUnicodeData.TryCreate(ref b, out var d, out _)); return d!; }
    internal static IXmlExecutor Executor(Tree tree, XPathFlags flags = 0, RegexInputKind kind = RegexInputKind.Utf16,
        int[]? parents = null, XmlInterval[]? intervals = null)
    {
        var nodes = new List<XmlNode>();
        int Emit(Tree t)
        {
            int x = t.X is null ? -1 : Emit(t.X), y = t.Y is null ? -1 : Emit(t.Y);
            nodes.Add(new(t.Kind, x, y, t.Value, t.Min, t.Max, t.Lazy,
                new(t.Offset, t.Length, RegexOffsetUnit.Utf16CodeUnits)));
            return nodes.Count - 1;
        }
        Emit(tree);
        var b = new CompileBudget(new());
        Assert.True(XmlProgram.TryCreate(new XPathProfile(flags), kind, 100, nodes.ToArray(),
            intervals is null ? [] : [new(0, intervals.Length)], intervals ?? [], parents ?? [-1],
            ref b, out var program, out var diag), diag.ToString());
        Assert.True(new XPathExecutorFactory().TryCreate(program!, Data(), ref b, out var executor, out diag), diag.ToString());
        return executor!;
    }
    internal sealed class Storage
    {
        internal readonly int[] States, Captures, Scratch;
        internal Storage(IXmlExecutor e, int frames = 32, int spare = 0)
        {
            var c = e.GetRequiredCapacity(SearchOperation.Captures, frames);
            States = new int[c.StateSlots + spare];
            Captures = new int[c.CaptureSlots + spare];
            Scratch = new int[c.ScratchSlots + spare];
        }
        internal WorkspaceBuffers Buffers => new(States, Captures, Scratch);
    }
    internal static RegexSearchStatus Char(IXmlExecutor e, string input, out RegexMatchRange match,
        RegexCapture[] groups, int start = 0, int frames = 32, Storage? storage = null,
        RegexMatchOptions options = default, RegexMatchLimits? limits = null)
    {
        storage ??= new(e, frames);
        var state = new RunState(storage.Buffers, limits ?? new(), options);
        var status = e.Search<char, XmlCharTextReader>(input, start, SearchOperation.Captures, frames, ref state, groups, out match);
        // Repeat each hand-plan semantic operation on an independently validated
        // UTF8 plan and compare native ranges to explicit encoding coordinates.
        var b = new CompileBudget(new());
        var p = e.Program;
        Assert.True(XmlProgram.TryCreate(p.Profile, RegexInputKind.Utf8, p.PatternLength, p.Nodes, p.Sets,
            p.Intervals, p.GroupParents, ref b, out var bytePlan, out var diagnostic), diagnostic.ToString());
        Assert.True(new XPathExecutorFactory().TryCreate(bytePlan!, Data(), ref b, out var byteExecutor, out diagnostic), diagnostic.ToString());
        var byteStorage = new Storage(byteExecutor!, frames);
        var byteGroups = new RegexCapture[groups.Length];
        var bytes = Encoding.UTF8.GetBytes(input);
        var byteState = new RunState(byteStorage.Buffers, limits ?? new(), options);
        var byteStart = Encoding.UTF8.GetByteCount(input.AsSpan(0, start));
        Assert.Equal(status, byteExecutor!.Search<byte, XmlByteTextReader>(bytes, byteStart, SearchOperation.Captures,
            frames, ref byteState, byteGroups, out var byteMatch));
        if (status == RegexSearchStatus.Match)
        {
            Assert.Equal(new RegexMatchRange(Encoding.UTF8.GetByteCount(input.AsSpan(0, match.Index)),
                Encoding.UTF8.GetByteCount(input.AsSpan(match.Index, match.Length))), byteMatch);
            for (var g = 0; g < p.GroupCount; g++)
            {
                var c = groups[g];
                Assert.Equal(c.Participated ? new RegexCapture(true, Encoding.UTF8.GetByteCount(input.AsSpan(0, c.Index)),
                    Encoding.UTF8.GetByteCount(input.AsSpan(c.Index, c.Length))) : default, byteGroups[g]);
            }
        }
        return status;
    }
}
