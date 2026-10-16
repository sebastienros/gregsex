using GregsEx.Contracts.Xml;
using Xunit;
using static GregsEx.Tests.Flavors.XPath.Parsing.GrammarTests;

namespace GregsEx.Tests.Flavors.XPath.Parsing;

public sealed class ClassTests
{
    [Theory]
    [InlineData("[a-z-[aeiou]]", 'b', 'a')]
    [InlineData("[a-z-[d-f-[e]]]", 'e', 'd')]
    [InlineData("[^a-z-[0-9]]", ':', '5')]
    [InlineData("[a-k-z]", '-', 'm')]
    [InlineData("[a--[b]]", '-', 'b')]
    [InlineData("[-a]", '-', 'b')]
    [InlineData("[a-]", '-', 'b')]
    [InlineData("[^^]", 'a', '^')]
    [InlineData("[a^]", '^', 'b')]
    [InlineData("[.?*+(){}|$]", '$', '^')]
    [InlineData("[\\[\\]\\\\]", '\\', 'a')]
    [InlineData("[\\t-\\r]", 10, 11)]
    [InlineData("[\uD7FF-\uE000]", 0xE000, 0xD800)]
    [InlineData("[😀-🙏]", 0x1F601, 0x1F650)]
    public void ScalarClassAlgebraMatchesIndependentMembership(string text, int included, int excluded)
    {
        var plan = Plan(text);
        Assert.True(Member(plan, included));
        Assert.False(Member(plan, excluded));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[^]")]
    [InlineData("[a")]
    [InlineData("[a[b]]")]
    [InlineData("[a-[b]c]")]
    [InlineData("[-[a]]")]
    [InlineData("[z-a]")]
    [InlineData("[--z]")]
    [InlineData("[a--]")]
    [InlineData("[a--b]")]
    [InlineData("[a-\\d]")]
    [InlineData("[\\1]")]
    [InlineData("[\\q]")]
    [InlineData("[\\]")]
    [InlineData("\\p{Cs}")]
    [InlineData("\\p{Letter}")]
    [InlineData("\\p{lu}")]
    [InlineData("\\p{Is}")]
    [InlineData("\\p{IsBadBlockName}")]
    [InlineData("\\P{IsBadBlockName}")]
    [InlineData("\\p{Isbasiclatin}")]
    [InlineData("\\p{IsBasic_Latin}")]
    [InlineData("\\p{IsBasic Latin}")]
    public void InvalidClassesAndUnknownBlocksAreRejected(string pattern)
    {
        Assert.False(Compile(pattern, 0, new(), out _, out var diagnostic, out var budget));
        Assert.Equal(XPathErrorCode.FORX0002, diagnostic.ErrorCode);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void EmptySubtractionIsAValidEmptyClass()
    {
        var plan = Plan("[a-[a]]");
        Assert.Equal(XmlNodeKind.Class, plan.Nodes[0].Kind);
        Assert.Equal(0, plan.Sets[0].Count);
        Assert.False(Member(plan, 'a'));
        Assert.False(Member(plan, 0x10FFFF));
    }
    [Theory]
    [InlineData("\\n", 10)]
    [InlineData("\\r", 13)]
    [InlineData("\\t", 9)]
    [InlineData("\\\\", '\\')]
    [InlineData("\\|", '|')]
    [InlineData("\\.", '.')]
    [InlineData("\\?", '?')]
    [InlineData("\\*", '*')]
    [InlineData("\\+", '+')]
    [InlineData("\\(", '(')]
    [InlineData("\\)", ')')]
    [InlineData("\\{", '{')]
    [InlineData("\\}", '}')]
    [InlineData("\\-", '-')]
    [InlineData("\\[", '[')]
    [InlineData("\\]", ']')]
    [InlineData("\\^", '^')]
    [InlineData("\\$", '$')]
    public void EverySingleEscapeHasExactSingletonSemantics(string pattern, int scalar)
    {
        Assert.True(Member(Plan(pattern), scalar));
        Assert.False(Member(Plan(pattern), scalar + 1));
        Assert.True(Member(Plan("[" + pattern + "]"), scalar));
    }
    [Fact]
    public void AllCategoryProductionsAndExactBlockAliasesUseFrozenData()
    {
        string[] categories = ["L","Lu","Ll","Lt","Lm","Lo","M","Mn","Mc","Me","N","Nd","Nl","No",
            "P","Pc","Pd","Ps","Pe","Pi","Pf","Po","Z","Zs","Zl","Zp","S","Sm","Sc","Sk","So","C","Cc","Cf","Co","Cn"];
        var data = Data();
        int[] probes = [9, 10, 13, 32, 'A', 'a', '0', 0x301, 0x1F600, 0x378, 0xE000, 0x10FFFF, 0xD800];
        foreach (var name in categories)
        {
            Assert.True(data.TryGetCategory(name, out var intervals));
            var positive = Plan("\\p{" + name + "}");
            var negative = Plan("\\P{" + name + "}");
            foreach (var cp in probes)
            {
                var expected = false;
                foreach (var interval in intervals)
                {
                    expected |= cp >= interval.First && cp <= interval.Last;
                }

                Assert.Equal(expected, Member(positive, cp));
                Assert.Equal(XmlAlphabet.Contains(cp) && !expected, Member(negative, cp));
            }
        }
        foreach (var name in new[] { "BasicLatin", "Greek", "CombiningMarksforSymbols", "PrivateUse", "Emoticons", "SupplementaryPrivateUseArea-A" })
        {
            Plan("\\p{Is" + name + "}");
        }

        Assert.True(Member(Plan("\\p{IsPrivateUse}"), 0x10FFFD));
        Assert.False(Member(Plan("\\p{IsPrivateUse}"), 0x10FFFE));
        Assert.True(Member(Plan("\\p{IsSupplementaryPrivateUseArea-B}"), 0x10FFFF));
    }
    [Theory]
    [InlineData("s", 32, 0xA0)]
    [InlineData("d", 0x661, 'a')]
    [InlineData("w", 0x301, '_')]
    [InlineData("w", 0x1F600, 0x378)]
    [InlineData("i", ':', '0')]
    [InlineData("i", 0xEFFFF, 0xF0000)]
    [InlineData("c", 0x301, 0xF0000)]
    public void PredefinedClassesAndTheirScalarComplements(string escape, int included, int excluded)
    {
        var positive = Plan("\\" + escape);
        var negative = Plan("\\" + escape.ToUpperInvariant());
        Assert.True(Member(positive, included));
        Assert.False(Member(positive, excluded));
        Assert.False(Member(negative, included));
        Assert.True(Member(negative, excluded));
        Assert.False(Member(negative, 0));
        Assert.False(Member(negative, 0xD800));
    }
    [Fact]
    public void CaseVariantsUseFullMappingsWithoutTransitiveClosureOrMultiScalarConsumption()
    {
        const XPathFlags i = XPathFlags.IgnoreCase;
        Assert.True(Member(Plan("ß", i), 0x1E9E));
        Assert.False(Member(Plan("ß", i), 's'));
        Assert.True(Member(Plan("ϑ", i), 0x398));
        Assert.False(Member(Plan("ϑ", i), 0x3F4));
        Assert.True(Member(Plan("[I]", i), 0x131));
        Assert.True(Member(Plan("[A-Z]", i), 0x212A));
        Assert.True(Member(Plan("𐐀", i), 0x10428));
        Assert.False(Member(Plan("[^Q]", i), 'q'));
        Assert.False(Member(Plan("[A-Z-[IO]]", i), 'i'));
        Assert.True(Member(Plan("[A-Z-[IO]]", i), 'b'));
        Assert.False(Member(Plan("\\p{Lu}", i), 'a'));
        Assert.True(Member(Plan("\\P{Lu}", i), 'a'));
        Assert.False(Member(Plan("\\d", i), 'A'));
        // A property in the same class is not inadvertently case-expanded.
        Assert.False(Member(Plan("[a\\p{Lu}]", i), 'b'));
        Assert.True(Member(Plan("[a\\p{Lu}]", i), 'B'));
        Assert.False(Member(Plan("[ϑ]", i), 0x3F4));
    }
}
