using Xunit;
using F = GregsEx.Flavors.XPath.Host.XPathHostFunctions;

namespace GregsEx.Tests.Flavors.XPath.Host;

// Expectations from the frozen F&O 3.1 contract, not GregsEx outputs.
public sealed class FunctionTests
{
    [Theory]
    [InlineData(null, "a*", "", true)]
    [InlineData("ba", "a", "", true)]
    [InlineData("ba", "^a", "", false)]
    [InlineData("a\n", "a$", "", false)]
    [InlineData("a\n", "a$", "mmm", true)]
    [InlineData("Mr. B. Obama", "B. OBAMA", "iq", true)]
    [InlineData("ß", "SS", "i", false)]
    [InlineData("😀", ".", "", true)]
    public void MatchesUsesPinnedScalarGrammar(string? input, string pattern, string flags, bool expected)
        => Assert.Equal(expected, F.Matches(input, pattern, flags));

    [Theory]
    [InlineData("abracadabra", "bra", "*", "", "a*cada*")]
    [InlineData("abracadabra", "a.*?a", "*", "", "*c*bra")]
    [InlineData("abracadabra", "a(.)", "a$1$1", "", "abbraccaddabbra")]
    [InlineData("z", "(z)", "$12", "", "z2")]
    [InlineData("z", "(z)", "$0$2$99$0001$0000$00023", "", "z9zz3")]
    [InlineData("z", "(z)", "$999999999999999999999", "", "99999999999999999999")]
    [InlineData("z", "(z)", "\\$\\\\", "", "$\\")]
    [InlineData("a/b/c", "/", "$", "q", "a$b$c")]
    [InlineData("x", "x", "$1\\", "q", "$1\\")]
    [InlineData("ab", "((a)|b)+", "$1/$2", "", "b/a")]
    [InlineData("b", "(a)|(b)", "$1-$2", "", "-b")]
    [InlineData(null, "x", "a", "", "")]
    [InlineData("😀x😀", "(😀)", "$1$1", "", "😀😀x😀😀")]
    public void ReplacementFollowsFoRules(string? input, string pattern, string replacement, string flags, string expected)
        => Assert.Equal(expected, F.Replace(input, pattern, replacement, flags));

    [Fact]
    public void MultiDigitReferencesUseLongestAdmittedNumericPrefix()
    {
        Assert.Equal("a2-a-a3", F.Replace("aaaaaaaaaa", "(a)(a)(a)(a)(a)(a)(a)(a)(a)(a)", "$102-$10-$103"));
    }
    [Fact]
    public void TokenizeRetainsEdgesAndExcludesCaptures()
    {
        Assert.Equal(new[] { "", "a", "", "b", "" }, F.Tokenize(",a,,b,", "(,)"));
        Assert.Equal(new[] { "12", "3", "5", "6" }, F.Tokenize("12.3.5.6", ".", "q"));
        Assert.Equal(new[] { "", "r", "c", "d", "r", "" }, F.Tokenize("abracadabra", "(ab)|(a)"));
        Assert.Empty(F.Tokenize(null, ","));
        Assert.Empty(F.Tokenize("", ","));
        Assert.Equal(new[] { "a\u00a0b", "😀" }, F.Tokenize(" \r\n\t a\u00a0b  😀 \t"));
        Assert.Empty(F.Tokenize(" \t\n\r "));
        Assert.Empty(F.Tokenize(null));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)F.Tokenize("a,b", ","))[0] = "changed");
    }
    [Theory]
    [InlineData("a*", "")]
    [InlineData("", "q")]
    [InlineData("^$", "m")]
    [InlineData("(a?)\\1", "")]
    public void EmptyPatternPrecheckAppliesEvenToEmptyOrNonmatchingInput(string pattern, string flags)
    {
        foreach (var input in new string?[] { null, "", "bbb" })
        {
            Error(XPathErrorCode.FORX0003, XmlRegexDiagnosticLocation.Pattern,
                () => F.Replace(input, pattern, "$", flags));
            Error(XPathErrorCode.FORX0003, XmlRegexDiagnosticLocation.Pattern,
                () => F.Tokenize(input, pattern, flags));
        }
    }
    [Fact]
    public void StandardErrorsRetainNamespaceOriginalLocationsAndPrecedence()
    {
        var ex = Error(XPathErrorCode.FORX0001, XmlRegexDiagnosticLocation.Flags, () => F.Replace("\0", "[", "$", "iz"));
        Assert.Equal(1, ex.Diagnostic.Diagnostic.Offset);
        Assert.Equal(1, ex.Diagnostic.Diagnostic.Length);
        ex = Error(XPathErrorCode.FORX0002, XmlRegexDiagnosticLocation.Pattern, () => F.Replace("\0", "[", "$"));
        Assert.Equal(1, ex.Diagnostic.Diagnostic.Offset); // missing closing class at original EOF
        foreach (var text in new[] { "$", "$a", "\\", "\\a", "a$" })
        {
            Error(XPathErrorCode.FORX0004, XmlRegexDiagnosticLocation.Replacement, () => F.Replace("\0", "x", text));
        }

        Assert.Equal("http://www.w3.org/2005/xqt-errors", XmlRegexDiagnostic.ErrorNamespace);
    }
    internal static XPathRegexException Error(XPathErrorCode code, XmlRegexDiagnosticLocation location, Action action)
    {
        var ex = Assert.Throws<XPathRegexException>(action);
        Assert.Equal(code, ex.Diagnostic.ErrorCode);
        Assert.Equal(location, ex.Diagnostic.Location);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, ex.Diagnostic.Diagnostic.LocationUnit);
        return ex;
    }
}
