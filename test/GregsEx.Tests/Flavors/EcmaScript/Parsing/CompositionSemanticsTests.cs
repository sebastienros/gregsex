using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

// Hand-authored expectations from edition16 §22.2.2 and the pinned Unicode16
// CaseFolding C/S and legacy uppercase rules. No matcher observation is a golden.
public sealed class CompositionSemanticsTests
{
    [Theory]
    [InlineData("^.$", "", "😀", false)]
    [InlineData("^.$", "u", "😀", true)]
    [InlineData("^.$", "v", "😀", true)]
    [InlineData(@"^\uD83D\uDE00$", "u", "😀", true)]
    [InlineData(@"^\u{1F600}$", "v", "😀", true)]
    [InlineData("^k$", "i", "K", false)]
    [InlineData("^k$", "ui", "K", true)]
    [InlineData("^s$", "vi", "ſ", true)]
    [InlineData("^s$", "i", "ſ", false)]
    [InlineData("^Σ$", "i", "ς", true)]
    [InlineData("^Σ$", "ui", "σ", true)]
    [InlineData("^ß$", "ui", "SS", false)]
    [InlineData("^ß$", "vi", "ẞ", true)]
    [InlineData("^𐐀$", "ui", "𐐨", true)]
    [InlineData("^𐐀$", "i", "𐐨", false)]
    [InlineData(@"^\w$", "ui", "K", true)]
    [InlineData(@"^\w$", "i", "K", false)]
    [InlineData(@"^\P{Lowercase_Letter}$", "ui", "a", true)]
    [InlineData(@"^\P{Lowercase_Letter}$", "vi", "a", false)]
    [InlineData(@"^[^\p{Lowercase_Letter}]$", "ui", "a", false)]
    [InlineData(@"^[^\p{Lowercase_Letter}]$", "vi", "a", false)]
    [InlineData(@"^[\p{ASCII}&&\p{Letter}]$", "v", "A", true)]
    [InlineData(@"^[\p{ASCII}&&\p{Letter}]$", "v", "α", false)]
    [InlineData(@"^[[\p{Script=Greek}]--[\p{Lowercase_Letter}]]$", "v", "Α", true)]
    [InlineData(@"^[[\p{Script=Greek}]--[\p{Lowercase_Letter}]]$", "vi", "Α", false)]
    [InlineData(@"^[\q{AB}&&\q{ab}]$", "vi", "Ab", true)]
    [InlineData(@"^[\q{AB}--\q{ab}]$", "vi", "AB", false)]
    [InlineData("^(?i:a)(?-i:b)$", "u", "Ab", true)]
    [InlineData("^(?i:a)(?-i:b)$", "ui", "AB", false)]
    [InlineData("^(?s:.)(?-s:.)$", "v", "\nx", true)]
    [InlineData("^(?s:.)(?-s:.)$", "v", "x\n", false)]
    [InlineData(@"^\cA\x42\u0043\0$", "", "\u0001BC\0", true)]
    [InlineData(@"^\141\8$", "", "a8", true)]
    public void RealCompilerAndExecutorAgreeWithIndependentSemanticCases(string pattern, string flags, string subject, bool matches)
    {
        var c = new Composition(pattern, flags);
        Assert.Equal(matches ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, c.Search(subject));
        if (matches)
        {
            Assert.Equal(new(0, subject.Length), c.Match);
        }
    }

    [Theory]
    [InlineData(0xD800, "")]
    [InlineData(0xDC00, "")]
    [InlineData(0xD800, "u")]
    [InlineData(0xDC00, "u")]
    [InlineData(0xD800, "v")]
    [InlineData(0xDC00, "v")]
    public void LoneSurrogatesUseNumericUnitsInsteadOfLossyInlineMetadata(int unit, string flags)
    {
        string subject = new((char)unit, 1);
        Assert.Equal(1, subject.Length);
        Assert.Equal(unit, subject[0]);
        new Composition("^.$", flags).Expect(subject, 0, 1);
        new Composition("^" + subject + "$", flags).Expect(subject, 0, 1);
        new Composition(@"^\u" + unit.ToString("X4", System.Globalization.CultureInfo.InvariantCulture) + "$", flags).Expect(subject, 0, 1);
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition("^.$", flags).Search(subject + "x"));
        string other = new((char)(unit == 0xD800 ? 0xDC00 : 0xD800), 1);
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition("^" + subject + "$", flags).Search(other));
    }

    [Theory]
    [InlineData("")]
    [InlineData("u")]
    [InlineData("v")]
    public void OrderedAlternativesReferencesAssertionsAndCaptureClearing(string flags)
    {
        new Composition("a|aa", flags).Expect("aa", 0, 1);
        new Composition(@"^(a|(b))\2$", flags).Expect("a", 0, 1, new RegexCapture(true, 0, 1), default);
        new Composition(@"^\1(a)$", flags).Expect("a", 0, 1, new RegexCapture(true, 0, 1));
        new Composition(@"^(a|(b))*$", flags).Expect("ba", 0, 2, new RegexCapture(true, 1, 1), default);
        new Composition(@"^(?=(a+))\1$", flags).Expect("aaa", 0, 3, new RegexCapture(true, 0, 3));
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition(@"^(?=(a+))a+\1$", flags).Search("aaaa"));
        new Composition(@"^(?!(a))b\1$", flags).Expect("b", 0, 1, new RegexCapture(false, 0, 0));
        new Composition(@"(?<=([ab]+)([bc]+))$", flags).Expect("abc", 3, 0, new RegexCapture(true, 0, 1), new RegexCapture(true, 1, 2));
        new Composition(@"(?<!a)b", flags).Expect("xb", 1, 1);
        new Composition(@"(?:(?<x>a)|(?<x>b))\k<x>", flags).Expect("bb", 0, 2, default, new RegexCapture(true, 0, 1));
        new Composition(@"(?<\u{10400}>a)\k<𐐀>", flags).Expect("aa", 0, 2, new RegexCapture(true, 0, 1));
        new Composition("(?<α\u200Cb>a)\\k<α\u200Cb>", flags).Expect("aa", 0, 2, new RegexCapture(true, 0, 1));
        new Composition("(?m:^a$)", flags).Expect("x\u2028a\u2029y", 2, 1);
        new Composition(@"(?<=a)b", flags).Expect("ab", 1, 1);
        var before = new Composition(@"(?<=a)b", flags);
        Assert.Equal(RegexSearchStatus.Match, before.Search("ab", 1));
        Assert.Equal(new(1, 1), before.Match);
    }

    [Fact]
    public void FiniteStringsAreLongestFirstButAllowContinuationBacktrackingAndReverseMatching()
    {
        new Composition(@"[\q{a|ab|abc}]", "v").Expect("xabc", 1, 3);
        new Composition(@"^[\q{a|ab|abc}]bc$", "v").Expect("abc", 0, 3);
        new Composition(@"(?<=[\q{a|ab}])c", "v").Expect("abc", 2, 1);
        new Composition(@"^[\q{|a}]$", "v").Expect("", 0, 0);
        new Composition(@"^[\p{RGI_Emoji}&&\q{\u{1F469}\u200D\u{1F4BB}}]$", "v")
            .Expect("👩‍💻", 0, 5);
    }

    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    public void NativeOffsetsAndBuiltinInteriorPairPolicyRemainDistinct(string mode)
    {
        var c = new Composition(".", mode + "y");
        // Existing ECMA-C018 project resolution; disclose its qualified Node/edition
        // See the interior-surrogate qualification in docs/specs/ecmascript.md.
        Assert.Equal(RegexSearchStatus.Match, c.Search("😀", 1, true, EcmaStartPolicy.BuiltinLastIndex));
        Assert.Equal(new(0, 2), c.Match);
        Assert.Equal(RegexSearchStatus.InvalidInput, c.Search("😀", 1, true, EcmaStartPolicy.LowerBound));
        Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, c.Failure.Code);
        Assert.Equal(RegexSearchStatus.Match, c.Search("x😀\uD800", 1));
        Assert.Equal(new(1, 2), c.Match);
        Assert.Equal(RegexSearchStatus.Match, c.Search("x😀\uD800", 3));
        Assert.Equal(new(3, 1), c.Match);
        new Composition("(?<p>😀)(?<l>\uD800)", mode).Expect("x😀\uD800", 1, 3, new RegexCapture(true, 1, 2), new RegexCapture(true, 3, 1));
    }
}
