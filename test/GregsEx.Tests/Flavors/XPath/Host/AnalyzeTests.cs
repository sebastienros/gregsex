using Xunit;
using F = GregsEx.Flavors.XPath.Host.XPathHostFunctions;

namespace GregsEx.Tests.Flavors.XPath.Host;

public sealed class AnalyzeTests
{
    [Fact]
    public void OrderedPreorderNodesPreserveNestedGroupsAndOriginalUtf16Ranges()
    {
        var result = F.AnalyzeString("x😀by", "(😀(b))");
        Assert.Equal("x😀by", result.Input);
        Assert.Equal(new XPathAnalyzeNode[] {
            new(XPathAnalyzeNodeKind.NonMatch,-1,0,new(0,1)),
            new(XPathAnalyzeNodeKind.Match,-1,0,new(1,3)),
            new(XPathAnalyzeNodeKind.Group,1,1,new(1,3)),
            new(XPathAnalyzeNodeKind.Text,2,0,new(1,2)),
            new(XPathAnalyzeNodeKind.Group,2,2,new(3,1)),
            new(XPathAnalyzeNodeKind.Text,4,0,new(3,1)),
            new(XPathAnalyzeNodeKind.NonMatch,-1,0,new(4,1)) }, result.Nodes);
        Assert.Throws<NotSupportedException>(() => ((IList<XPathAnalyzeNode>)result.Nodes)[0] = default);
        Assert.Equal("http://www.w3.org/2005/xpath-functions", XPathAnalyzeResult.Namespace);
    }
    [Fact]
    public void AbsentGroupsAreOmittedAndMatchedEmptyGroupsRemain()
    {
        var result = F.AnalyzeString("b", "(a)?()(b)");
        Assert.DoesNotContain(result.Nodes, n => n.Kind == XPathAnalyzeNodeKind.Group && n.GroupNumber == 1);
        Assert.Contains(result.Nodes, n => n.Kind == XPathAnalyzeNodeKind.Group && n.GroupNumber == 2 && n.Range == new RegexMatchRange(0, 0));
        Assert.Contains(result.Nodes, n => n.Kind == XPathAnalyzeNodeKind.Group && n.GroupNumber == 3 && n.Range == new RegexMatchRange(0, 1));
        Assert.Empty(F.AnalyzeString(null, "a").Nodes);
        FunctionTests.Error(XPathErrorCode.FORX0003, XmlRegexDiagnosticLocation.Pattern, () => F.AnalyzeString(null, "a*"));
    }
    [Fact]
    public void EscapingFinalSubgroupPromotesToContainingMatchAndRetainsEveryCapture()
    {
        var result = F.AnalyzeString("ab", "((a)|b)+");
        Assert.Equal(new XPathAnalyzeNode[] {
            new(XPathAnalyzeNodeKind.Match,-1,0,new(0,2)),
            new(XPathAnalyzeNodeKind.Group,0,2,new(0,1)),
            new(XPathAnalyzeNodeKind.Text,1,0,new(0,1)),
            new(XPathAnalyzeNodeKind.Group,0,1,new(1,1)),
            new(XPathAnalyzeNodeKind.Text,3,0,new(1,1)) }, result.Nodes);
        Assert.Equal("b/a", F.Replace("ab", "((a)|b)+", "$1/$2"));
        Partition(result);
    }
    [Fact]
    public void EscapingSiblingUsesPositionOrderingWhileContainedChildKeepsItsParent()
    {
        var result = F.AnalyzeString("ba", "((a)|(b))+");
        Assert.Equal(new XPathAnalyzeNode[] {
            new(XPathAnalyzeNodeKind.Match,-1,0,new(0,2)),
            new(XPathAnalyzeNodeKind.Group,0,3,new(0,1)),
            new(XPathAnalyzeNodeKind.Text,1,0,new(0,1)),
            new(XPathAnalyzeNodeKind.Group,0,1,new(1,1)),
            new(XPathAnalyzeNodeKind.Group,3,2,new(1,1)),
            new(XPathAnalyzeNodeKind.Text,4,0,new(1,1)) }, result.Nodes);
        Partition(result);
    }
    [Fact]
    public void EscapingGroupSelectsNearestContainingSyntacticAncestor()
    {
        var result = F.AnalyzeString("abc", "(((a)|b)+c)");
        var groups = result.Nodes.Where(n => n.Kind == XPathAnalyzeNodeKind.Group).ToArray();
        Assert.Equal(new[] { 1, 3, 2 }, groups.Select(n => n.GroupNumber));
        Assert.Equal(1, groups[1].ParentIndex); // group3 under group1, not match or group2
        Assert.Equal(1, groups[2].ParentIndex);
        Partition(result);
        result = F.AnalyzeString("abc", "(((a)|b)+|c)+");
        groups = [.. result.Nodes.Where(n => n.Kind == XPathAnalyzeNodeKind.Group)];
        Assert.Equal(new[] { 3, 2, 1 }, groups.Select(n => n.GroupNumber));
        Assert.All(groups, n => Assert.Equal(0, n.ParentIndex));
        Partition(result);
    }
    [Fact]
    public void EmptyParticipatingGroupsSurvivePromotionAndRightEdgeContainment()
    {
        var result = F.AnalyzeString("ab", "((()a)|b)+");
        var empty = Assert.Single(result.Nodes, n => n.Kind == XPathAnalyzeNodeKind.Group && n.GroupNumber == 3);
        Assert.Equal(new(0, 0), empty.Range);
        Assert.Equal(1, empty.ParentIndex); // retained group2 promoted to match owns empty group3
        Partition(result);
        result = F.AnalyzeString("aa", "(a())+");
        empty = Assert.Single(result.Nodes, n => n.Kind == XPathAnalyzeNodeKind.Group && n.GroupNumber == 2);
        Assert.Equal(new(2, 0), empty.Range);
        Assert.Equal(2, empty.ParentIndex); // group1 remains parent at inclusive end boundary
        Partition(result);
    }
    [Fact]
    public void DeepTreeUsesBoundedExplicitStorageAndPreservesEveryGroup()
    {
        var result = F.AnalyzeString("😀", new string('(', 100) + "." + new string(')', 100));
        Assert.Equal(100, result.Nodes.Count(n => n.Kind == XPathAnalyzeNodeKind.Group));
        Assert.Equal(102, result.Nodes.Count);
        Partition(result);
    }
    private static void Partition(XPathAnalyzeResult result)
    {
        // Independently verify every direct-child partition and the complete
        // string value, with empty groups retaining boundaries but adding no text.
        var topCursor = 0;
        for (var i = 0; i < result.Nodes.Count; i++)
        {
            var node = result.Nodes[i];
            if (node.ParentIndex < 0)
            { Assert.Equal(topCursor, node.Range.Index); topCursor += node.Range.Length; }
            else
            {
                var parent = result.Nodes[node.ParentIndex];
                Assert.InRange(node.Range.Index, parent.Range.Index, parent.Range.Index + parent.Range.Length);
                Assert.InRange(node.Range.Index + node.Range.Length, parent.Range.Index, parent.Range.Index + parent.Range.Length);
            }
            if (node.Kind is not (XPathAnalyzeNodeKind.Match or XPathAnalyzeNodeKind.Group))
            {
                continue;
            }

            var cursor = node.Range.Index;
            foreach (var child in result.Nodes.Where(n => n.ParentIndex == i))
            { Assert.Equal(cursor, child.Range.Index); cursor += child.Range.Length; }
            Assert.Equal(node.Range.Index + node.Range.Length, cursor);
        }
        Assert.Equal(result.Input.Length, topCursor);
        Assert.Equal(result.Input, string.Concat(result.Nodes.Where(n => n.Kind is XPathAnalyzeNodeKind.Text or XPathAnalyzeNodeKind.NonMatch)
            .Select(n => result.Input.Substring(n.Range.Index, n.Range.Length))));
    }
    [Fact]
    public void NodeCountInputUnitsAndCumulativeWorkHaveFiniteLimits()
    {
        Assert.Equal(XPathHostFailureCode.ResultLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.AnalyzeString("a", "(a)", host: new(maxResults: 2))).Failure.Code);
        Assert.Equal(XPathHostFailureCode.OutputLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.AnalyzeString("abc", "a", host: new(maxOutputUnits: 2))).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded,
            Assert.Throws<RegexExecutionException>(() => F.AnalyzeString(new string('a', 100), "a",
                match: new() { Limits = new(maxWork: 500) })).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain,
            Assert.Throws<RegexExecutionException>(() => F.AnalyzeString("a\0", "a")).Failure.Code);
    }
}
