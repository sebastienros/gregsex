using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

public sealed class Pcre2ClassTests
{
    [Theory]
    [InlineData(0, "a", false)]
    [InlineData(0, "b", true)]
    [InlineData(1, "a", true)]
    [InlineData(1, "b", true)]
    [InlineData(2, "a", false)]
    [InlineData(2, "b", true)]
    [InlineData(3, "a", false)]
    [InlineData(3, "b", true)]
    public void ClassSetAlgebra(int operation, string input, bool success)
    {
        var p = new KernelPlan();
        var alphabet = p.Class(Pcre2ClassKind.Intervals, intervals: [new('a', 'z')]);
        var vowels = p.Class(Pcre2ClassKind.Intervals, intervals: [new('a', 'a'), new('e', 'e'), new('i', 'i'), new('o', 'o'), new('u', 'u')]);
        var complement = p.Class(Pcre2ClassKind.Complement, vowels);
        var set = p.Class(operation switch
        {
            0 => Pcre2ClassKind.Intersection,
            1 => Pcre2ClassKind.Union,
            2 => Pcre2ClassKind.Subtraction,
            _ => Pcre2ClassKind.SymmetricDifference
        }, alphabet, operation == 0 ? complement : vowels);
        p.Leaf(Pcre2NodeKind.Class, set);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new KernelFixture(p.Build()).Match(input, pairs).Status);
    }
    [Theory]
    [InlineData(0, 1, "α", true)] // major L
    [InlineData(0, 1, "1", false)]
    [InlineData(1, 13, "١", true)] // particular Nd
    [InlineData(2, 1, "α", true)] // native Greek script ID
    [InlineData(4, 54, "\u2000", true)] // White_Space bit54
    [InlineData(5, 9, "α", true)] // bidiL native9
    [InlineData(6, 8, "\u0301", true)] // word Mn
    [InlineData(6, 8, "\u203F", true)] // word Pc
    [InlineData(6, 0, "Α", true)] // Lc
    [InlineData(6, 0, "1", false)]
    [InlineData(6, 17, "Ｆ", true)] // source fullwidth xdigit
    [InlineData(6, 14, "\u061C", false)]
    [InlineData(6, 15, "\u180E", true)]
    public void PinnedPropertyNamespaces(int kind, int id, string input, bool success)
    {
        var p = new KernelPlan();
        var cls = p.Class(Pcre2ClassKind.Property, property: new((Pcre2PropertyKind)kind, id));
        p.Leaf(Pcre2NodeKind.Class, cls);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new KernelFixture(p.Build()).Match(input, pairs).Status);
    }
    [Theory]
    [InlineData(0, 'I', 'ı', false)]
    [InlineData(128, 'I', 'ı', false)]
    [InlineData(65536, 'I', 'i', false)]
    [InlineData(65536, 'I', 'ı', true)]
    [InlineData(65536, 'i', 'İ', true)]
    public void SourceCaselessSetsAndExtras(int extra, int pattern, int subject, bool success)
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Caseless, extraFlags: (Pcre2ExtraFlags)extra));
        p.Literal(pattern);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch,
            new KernelFixture(p.Build()).Match(new string((char)subject, 1), pairs).Status);
    }
    [Fact]
    public void CaselessComplementFoldsBeforeComplement()
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf | Pcre2CompileFlags.Caseless));
        var k = p.Class(Pcre2ClassKind.Intervals, intervals: [new('k', 'k')]);
        p.Leaf(Pcre2NodeKind.Class, p.Class(Pcre2ClassKind.Complement, k));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.NoMatch, new KernelFixture(p.Build()).Match("K", pairs).Status);
    }
    [Fact]
    public void ImportedFlipAndWordTablesAreUsedDirectly()
    {
        var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
        tables[Pcre2DataAbi.LowerOffset + 'a'] = (byte)'!';
        tables[Pcre2DataAbi.LowerOffset + '!'] = (byte)'!';
        tables[Pcre2DataAbi.TypeOffset + '!'] |= 0x10;
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Caseless, characterTables: new(tables)));
        p.Seq(p.Leaf(Pcre2NodeKind.WordBoundary), p.Literal('a'), p.Leaf(Pcre2NodeKind.WordBoundary));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, new KernelFixture(p.Build()).Match("!", pairs).Status);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void UcpWordUsesMarksAndNumbers(bool ucp, bool success)
    {
        var p = new KernelPlan(new(flags: Pcre2CompileFlags.Utf | (ucp ? Pcre2CompileFlags.Ucp : 0)));
        p.Seq(p.Leaf(Pcre2NodeKind.WordBoundary), p.Literal(0x0301), p.Leaf(Pcre2NodeKind.WordBoundary));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(success ? Pcre2MatchStatus.Match : Pcre2MatchStatus.NoMatch, new KernelFixture(p.Build()).Match("\u0301", pairs).Status);
    }
    [Fact]
    public void ScratchAdmissionIsExplicitAndReusable()
    {
        var p = new KernelPlan();
        var a = p.Class(Pcre2ClassKind.Intervals, intervals: [new('a', 'a')]);
        var b = p.Class(Pcre2ClassKind.Intervals, intervals: [new('b', 'b')]);
        p.Leaf(Pcre2NodeKind.Class, p.Class(Pcre2ClassKind.Union, a, b));
        var fixture = new KernelFixture(p.Build(), new(16, 16, 1, 2));
        var pairs = new Pcre2OffsetPair[1];
        var result = fixture.Match("a", pairs);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, result.Failure.Code);
        Assert.Equal(3, result.Failure.RequiredCapacity);
    }
}
