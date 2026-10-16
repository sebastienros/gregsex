using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class SetTests
{
    internal static bool Contains(EcmaProgram p, int setIndex, int point)
    {
        var set = p.Sets[setIndex];
        foreach (var r in p.Intervals.Slice(set.IntervalOffset, set.IntervalCount))
        {
            if (point >= r.First && point <= r.Last)
            {
                return true;
            }
        }

        return false;
    }
    private static int[][] Strings(EcmaProgram p, int setIndex = 0)
    {
        var set = p.Sets[setIndex];
        var result = new int[set.StringCount][];
        for (var i = 0; i < result.Length; i++)
        { var s = p.Strings[set.StringOffset + i]; result[i] = p.StringCodePoints.Slice(s.Offset, s.Length).ToArray(); }
        return result;
    }
    public static TheoryData<string> Admission => new()
    {
        "[]", "[^]", "[a]", "[[a]]", "[[^a]]", "[a-z]", "[[a-z]--[aeiou]]", "[a&&a]", "[a--b]", "[a&&a&&a]", "[a--b--c]",
        @"[\q{ab|cd}]", @"[\q{|a|ab}]", @"[\q{a}]", @"[\q{\||\}}]", @"[\q{😀|𐐀a}]", @"[\q{ab|cd}&&\q{ab|ef}]", @"[\q{ab|cd}--\q{ab}]",
        @"[^\q{a|b}]", @"[^\q{ab}&&a]", @"[^a--\q{ab}]", @"[\p{ASCII}&&\P{Lowercase_Letter}]", @"[\d\w\s]", @"[\-\&\!\#\%\,\:\;\<\=\>\@\`\~]",
        "[&]", "[!]", "[#]", "[$]", "[%]", "[*]", "[+]", "[,]", "[.]", "[:]", "[;]", "[<]", "[=]", "[>]", "[?]", "[@]", "[^a^]", "[`]", "[~]"
    };
    [Theory, MemberData(nameof(Admission))]
    public void UnicodeSetsGrammarAdmission(string pattern) { GrammarTests.Compile(pattern, "v"); GrammarTests.Compile(pattern, "vi"); }
    public static TheoryData<string> Rejections => new()
    {
        "[-]", "[a-]", "[-a]", "[a&&]", "[&&a]", "[a--]", "[--a]", "[a&&&b]", "[a&&b--c]", "[a--b&&c]", "[ab&&c]", "[a&&bc]", "[a-z&&b]", "[a&&b-c]", "[a-z--b]", "[a--b-c]",
        "[[]", "[a[]", "[(]", "[)]", "[{]", "[}]", "[/]", "[|]", "[!!]", "[##]", "[$$]", "[%%]", "[**]", "[++]", "[,,]", "[..]", "[::]", "[;;]", "[<<]", "[==]", "[>>]", "[??]", "[@@]", "[a^^]", "[``]", "[~~]",
        @"[^\q{ab}]", @"[^\q{}]", @"[^\q{ab}--\q{ab}]", @"[^\q{ab}&&\q{xy}]", @"[\P{Basic_Emoji}]", @"[\q{\d}]", @"[\q{\p{ASCII}}]", @"[\q{\q}]", @"[\q{ab]", @"[\q{a-b}]", @"[\q{!!}]", @"[a-\d]", @"[a-[b]]"
    };
    [Theory, MemberData(nameof(Rejections))]
    public void ReservedSyntaxAndStaticStringNegationAreEarlyErrors(string pattern) => GrammarTests.Reject(pattern, "v");
    [Theory]
    [InlineData(@"[[a-z]--[aeiou]]", "bcdfghjklmnpqrstvwxyz")]
    [InlineData(@"[[a-f]&&[d-z]]", "def")]
    [InlineData(@"[[a-z]--[a-c]--[x-z]]", "defghijklmnopqrstuvw")]
    [InlineData(@"[[ab][bc]]", "abc")]
    [InlineData(@"[\q{ab|cd}&&\q{a|ab|ef}]", "")]
    public void AlgebraHasIndependentFiniteMembershipExpectations(string pattern, string expected)
    {
        var p = GrammarTests.Compile(pattern, "v");
        for (var point = 0; point < 128; point++)
        {
            Assert.Equal(expected.Contains((char)point), Contains(p, 0, point));
        }

        if (pattern.Contains("q{"))
        {
            Assert.Equal(new[] { (int)'a', 'b' }, Assert.Single(Strings(p)));
        }
    }
    [Fact]
    public void FiniteStringsAreDistinctLongestFirstAndSingletonsAreIntervals()
    {
        var p = GrammarTests.Compile(@"[\q{a|ab|xyz||AB|ab|😀}]", "v");
        var strings = Strings(p);
        Assert.Equal(4, strings.Length);
        Assert.Equal(new[] { (int)'x', 'y', 'z' }, strings[0]);
        Assert.Equal(new[] { (int)'A', 'B' }, strings[1]);
        Assert.Equal(new[] { (int)'a', 'b' }, strings[2]);
        Assert.Empty(strings[3]);
        Assert.True(Contains(p, 0, 'a'));
        Assert.True(Contains(p, 0, 0x1F600));
        p = GrammarTests.Compile(@"[\q{a|ab|xyz||AB|ab}]", "vi");
        strings = Strings(p);
        Assert.Equal(3, strings.Length);
        Assert.Equal(new[] { (int)'a', 'b' }, strings[1]);
        Assert.Empty(strings[2]);
    }
    [Theory]
    [InlineData("u", true)]
    [InlineData("v", false)]
    public void UnicodePropertyComplementOrderDiffersByMode(string mode, bool lowercaseAccepted)
    {
        var p = GrammarTests.Compile(@"\P{Lowercase_Letter}", mode + "i");
        Assert.Equal(lowercaseAccepted, Contains(p, 0, 'a'));
        Assert.True(Contains(p, 0, '!'));
        p = GrammarTests.Compile(@"[^\p{Lowercase_Letter}]", mode + "i");
        Assert.False(Contains(p, 0, 'a'));
        Assert.False(Contains(p, 0, 'z'));
    }
    [Fact]
    public void CaseSetsUseExactModeTablesIncludingNonAsciiToAsciiAndComplement()
    {
        foreach (var flags in new[] { "i", "ui", "vi" })
        {
            var p = GrammarTests.Compile("[a-z]", flags);
            int canonicalA = flags == "i" ? 'A' : 'a';
            Assert.True(Contains(p, 0, canonicalA));
            Assert.False(Contains(p, 0, flags == "i" ? 'a' : 'A'));
            p = GrammarTests.Compile(@"[\u017F\u212A]", flags);
            Assert.True(Contains(p, 0, flags == "i" ? 0x017F : 's'));
            Assert.True(Contains(p, 0, flags == "i" ? 0x212A : 'k'));
            p = GrammarTests.Compile(@"\W", flags);
            Assert.False(Contains(p, 0, canonicalA));
            Assert.False(Contains(p, 0, '_'));
            Assert.Equal(flags == "i", Contains(p, 0, 0x017F));
            p = GrammarTests.Compile("[^a]", flags);
            Assert.False(Contains(p, 0, canonicalA));
            Assert.True(Contains(p, 0, '!'));
        }
        var u = GrammarTests.Compile("[𐐀]", "ui");
        Assert.True(Contains(u, 0, 0x10428));
        Assert.False(Contains(u, 0, 0x10400));
        var v = GrammarTests.Compile("[a&&A]", "vi");
        Assert.True(Contains(v, 0, 'a'));
        v = GrammarTests.Compile("[a--A]", "vi");
        Assert.Empty(v.Intervals.ToArray());
    }
    [Fact]
    public void EveryStringPropertyUsesTheSuppliedInterfaceAndFoldsBeforeAlgebra()
    {
        foreach (var key in ContractUnicode.StringNames)
        {
            var p = GrammarTests.Compile(@"\p{" + key + "}", "v");
            Assert.Equal(3, Strings(p).Length);
            p = GrammarTests.Compile(@"\p{" + key + "}", "vi");
            Assert.Equal(2, Strings(p).Length);
            GrammarTests.Reject(@"\p{" + key + "}", "u");
            GrammarTests.Reject(@"\P{" + key + "}", "v");
            GrammarTests.Reject(@"[^\p{" + key + @"}--\p{" + key + "}]", "v");
        }
        var folded = GrammarTests.Compile(@"[\p{RGI_Emoji}&&\q{AB}]", "vi");
        Assert.Equal(new[] { (int)'a', 'b' }, Assert.Single(Strings(folded)));
    }
    [Fact]
    public void PropertyAliasesAreExactAndNonBinaryValuesRequireEquals()
    {
        foreach (var flags in new[] { "u", "v" })
        {
            foreach (var text in new[] { "Ll", "Lowercase_Letter", "gc=Ll", "General_Category=Lowercase_Letter", "ASCII", "Any", "Emoji", "sc=Grek", "Script=Greek", "Script_Extensions=Grek", "scx=Greek" })
            {
                GrammarTests.Compile(@"\p{" + text + "}", flags);
            }

            foreach (var text in new[] { "", "lowercase_letter", "ll", "Script=greek", "script=Greek", "Scx=Grek", "Greek", "gc=", "=Lu", "ASCII=True", "gc=ASCII", "gc=Ll=Lu", "gc= Ll", "Ll ", "RGI_Emoji=true" })
            {
                GrammarTests.Reject(@"\p{" + text + "}", flags);
            }
        }
    }
    [Fact]
    public void EmptyClassesDotsAndWhitespaceRetainEntireUtf16Domain()
    {
        foreach (var flags in new[] { "", "u", "v" })
        {
            Assert.Empty(GrammarTests.Compile("[]", flags).Intervals.ToArray());
            var all = GrammarTests.Compile("[^]", flags);
            Assert.True(Contains(all, 0, 0xD800));
            Assert.True(Contains(all, 0, 0xFFFF));
            Assert.Equal(flags != "", Contains(all, 0, 0x1F600));
            var dot = GrammarTests.Compile(".", flags);
            foreach (var cp in new[] { 10, 13, 0x2028, 0x2029 })
            {
                Assert.False(Contains(dot, 0, cp));
            }

            dot = GrammarTests.Compile(".", flags + "s");
            Assert.True(Contains(dot, 0, 10));
            Assert.True(Contains(dot, 0, 0xD800));
            var space = GrammarTests.Compile(@"\s", flags);
            foreach (var cp in new[] { 9, 10, 11, 12, 13, 32, 0xA0, 0x1680, 0x2007, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF })
            {
                Assert.True(Contains(space, 0, cp));
            }

            foreach (var cp in new[] { 0, 'a', 0x85, 0x180E, 0x200B })
            {
                Assert.False(Contains(space, 0, cp));
            }
        }
    }
}
