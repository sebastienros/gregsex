using System.Text;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using Xunit;
using static GregsEx.Tests.Flavors.XPath.Execution.PlanFixture;

namespace GregsEx.Tests.Flavors.XPath.Execution;

public sealed class ExecutorTests
{
    [Fact]
    public void EarliestStartAndFirstAlternativeWinAcrossOperationsAndEncodings()
    {
        var tree = A(L('a'), S(L('a'), L('b')));
        foreach (var kind in new[] { RegexInputKind.Utf16, RegexInputKind.Utf8 })
        {
            var e = Executor(tree, kind: kind);
            var storage = new Storage(e);
            foreach (var op in Enum.GetValues<SearchOperation>())
            {
                var state = new RunState(storage.Buffers, new());
                var groups = new RegexCapture[2];
                groups[1] = new(true, 77, 88);
                var status = kind == RegexInputKind.Utf16
                    ? e.Search<char, XmlCharTextReader>("xxab", 0, op, 32, ref state, groups, out var match)
                    : e.Search<byte, XmlByteTextReader>("xxab"u8, 0, op, 32, ref state, groups, out match);
                Assert.Equal(RegexSearchStatus.Match, status);
                Assert.Equal(new(2, 1), match);
                if (op == SearchOperation.Captures)
                {
                    Assert.Equal(new(true, 2, 1), groups[0]);
                }

                Assert.Equal(new(true, 77, 88), groups[1]);
            }
        }
    }
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    public void GreedyAndLazyRepeatsSelectOrderedRange(bool lazy, int length)
    {
        var e = Executor(S(R(L('a'), lazy: lazy), L('a')));
        var groups = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.Match, Char(e, "aaa", out var match, groups));
        Assert.Equal(new(0, length), match);
    }
    [Fact]
    public void RepeatsRetainLastParticipatingSubgroupInsteadOfEcmaClearing()
    {
        // ((a)|b)+ : group2 remains 'a' when the last outer iteration takes b.
        var nested = C(L('a', 2), 2, 1, 3);
        var outer = C(new(XmlNodeKind.Alternative, nested, L('b', 5), Offset: 1, Length: 5), 1, 0, 7);
        var e = Executor(R(outer, 1), parents: [-1, 0, 1]);
        var groups = new RegexCapture[3];
        Assert.Equal(RegexSearchStatus.Match, Char(e, "ab", out var match, groups));
        Assert.Equal(new(0, 2), match);
        Assert.Equal(new(true, 1, 1), groups[1]);
        Assert.Equal(new(true, 0, 1), groups[2]);
    }
    [Fact]
    public void AlternativeAndRepeatRollbackRestoreCaptures()
    {
        // ((a)c|b) is expressed with capture only in the failing first alternative.
        var e = Executor(A(S(C(L('a', 1), 1, 0, 3), L('c', 4)), L('b', 8)), parents: [-1, 0]);
        var groups = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Char(e, "ab", out var match, groups));
        Assert.Equal(new(1, 1), match);
        Assert.Equal(default, groups[1]);
        e = Executor(S(R(C(L('a', 1), 1, 0, 3), 1), S(L('a', 8), B(1))), parents: [-1, 0]);
        Assert.Equal(RegexSearchStatus.Match, Char(e, "aaa", out match, groups));
        Assert.Equal(new(0, 3), match);
        Assert.Equal(new(true, 0, 1), groups[1]);
    }
    [Fact]
    public void UnmatchedAndParticipatingEmptyBackreferencesMatchEmpty()
    {
        var e = Executor(S(A(C(L('a', 1), 1, 0, 3), L('b', 4)), B(1)), parents: [-1, 0]);
        var groups = new RegexCapture[2];
        Assert.Equal(RegexSearchStatus.Match, Char(e, "b", out var match, groups));
        Assert.Equal(new(0, 1), match);
        Assert.Equal(default, groups[1]);
        e = Executor(S(C(E(1), 1, 0, 2), B(1)), parents: [-1, 0]);
        Assert.Equal(RegexSearchStatus.Match, Char(e, "", out _, groups));
        Assert.Equal(new(true, 0, 0), groups[1]);
    }
    [Fact]
    public void RequiredAndOptionalEmptyIterationsTerminateWithParticipation()
    {
        var groups = new RegexCapture[2];
        foreach (var min in new[] { 0, 1, 3 })
        {
            var e = Executor(R(C(E(1), 1, 0, 2), min), parents: [-1, 0]);
            Assert.Equal(RegexSearchStatus.Match, Char(e, "", out var match, groups));
            Assert.Equal(new(0, 0), match);
            Assert.Equal(new(true, 0, 0), groups[1]);
        }
        var lazy = Executor(R(C(E(1), 1, 0, 2), lazy: true), parents: [-1, 0]);
        Assert.Equal(RegexSearchStatus.Match, Char(lazy, "", out _, groups));
        Assert.Equal(default, groups[1]);
    }
    [Theory]
    [InlineData('I', 'i', true)]
    [InlineData('I', 0x0131, true)]
    [InlineData(0x212A, 'k', true)]
    [InlineData(0x0130, 'i', false)]
    [InlineData('i', 0x0131, true)]
    [InlineData(0x0130, 0x0131, false)]
    [InlineData(0x00DF, 0x1E9E, true)]
    [InlineData(0x00DF, 's', false)]
    public void CaseComparisonUsesFullLowerOrUpperWithoutTransitiveClosure(int a, int b, bool accepts)
    {
        var e = Executor(S(C(L(a, 1), 1, 0, 3), B(1)), XPathFlags.IgnoreCase, parents: [-1, 0]);
        var groups = new RegexCapture[2];
        Assert.Equal(accepts ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
            Char(e, char.ConvertFromUtf32(a) + char.ConvertFromUtf32(b), out _, groups));
    }
    [Fact]
    public void Utf8CaseBackreferencesCompareScalarsWithDifferentNativeWidths()
    {
        var e = Executor(S(C(L(0x212A, 1), 1, 0, 3), B(1)), XPathFlags.IgnoreCase,
            RegexInputKind.Utf8, [-1, 0]);
        var storage = new Storage(e);
        var groups = new RegexCapture[2];
        foreach (var input in new[] { "Kk", "kK" })
        {
            var state = new RunState(storage.Buffers, new());
            var bytes = Encoding.UTF8.GetBytes(input);
            Assert.Equal(RegexSearchStatus.Match, e.Search<byte, XmlByteTextReader>(bytes, 0, SearchOperation.Captures, 32, ref state, groups, out var match));
            Assert.Equal(new(0, 4), match);
            Assert.Equal(new(true, 0, input[0] == 'k' ? 1 : 3), groups[1]);
        }
    }
    [Theory]
    [InlineData(false, "a\n", 0, false)]
    [InlineData(false, "a", 0, true)]
    [InlineData(true, "a\n", 0, true)]
    [InlineData(true, "a\r", 0, false)]
    [InlineData(true, "x\na", 2, true)]
    [InlineData(true, "x\ra", 2, false)]
    public void AnchorsSeeOriginalInputAndOnlyLf(bool multiline, string input, int start, bool accepts)
    {
        var e = Executor(S(Anchor(false), S(L('a'), Anchor(true))), multiline ? XPathFlags.Multiline : 0);
        Assert.Equal(accepts ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, Char(e, input, out _, new RegexCapture[1], start));
    }
    [Theory]
    [InlineData("", 0, true)]
    [InlineData("a\n", 2, false)]
    [InlineData("\n\n", 1, true)]
    [InlineData("a", 1, false)]
    [InlineData("\r", 1, false)]
    public void MultilineSuppressesTerminalEmptyLine(string input, int start, bool accepts)
    {
        var e = Executor(S(Anchor(false), Anchor(true)), XPathFlags.Multiline);
        Assert.Equal(accepts ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, Char(e, input, out _, new RegexCapture[1], start));
    }
    [Theory]
    [InlineData(0, 0, false, 0)]
    [InlineData(2, 3, false, 3)]
    [InlineData(2, 3, true, 2)]
    public void FiniteRepeatBoundsAreEnforced(int min, int max, bool lazy, int length)
    {
        var e = Executor(R(L('a'), min, max, lazy));
        Assert.Equal(RegexSearchStatus.Match, Char(e, "aaaa", out var match, new RegexCapture[1]));
        Assert.Equal(new(0, length), match);
        if (min > 0)
        {
            Assert.Equal(RegexSearchStatus.NoMatch, Char(e, "a", out _, new RegexCapture[1]));
        }
    }
    [Fact]
    public void ClassesRemainCompilerResolvedUnderIgnoreCase()
    {
        var e = Executor(new(XmlNodeKind.Class, Value: 0, Length: 1), XPathFlags.IgnoreCase, intervals: [new('A', 'Z')]);
        Assert.Equal(RegexSearchStatus.NoMatch, Char(e, "a", out _, new RegexCapture[1]));
        Assert.Equal(RegexSearchStatus.Match, Char(e, "A", out _, new RegexCapture[1]));
    }
    [Fact]
    public void SupplementaryNativeOffsetsAndResolvedClassIntervalsArePreserved()
    {
        var t = new Tree(XmlNodeKind.Class, Value: 0, Length: 1);
        foreach (var kind in new[] { RegexInputKind.Utf16, RegexInputKind.Utf8 })
        {
            var e = Executor(t, kind: kind, intervals: [new(0x10000, 0x10FFFF)]);
            var storage = new Storage(e);
            var state = new RunState(storage.Buffers, new());
            var groups = new RegexCapture[1];
            var status = kind == RegexInputKind.Utf16
                ? e.Search<char, XmlCharTextReader>("a😀", 0, SearchOperation.Captures, 32, ref state, groups, out var match)
                : e.Search<byte, XmlByteTextReader>("a😀"u8, 0, SearchOperation.Captures, 32, ref state, groups, out match);
            Assert.Equal(RegexSearchStatus.Match, status);
            Assert.Equal(new(1, kind == RegexInputKind.Utf16 ? 2 : 4), match);
        }
    }
}
