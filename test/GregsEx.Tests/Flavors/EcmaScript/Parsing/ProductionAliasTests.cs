using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class ProductionAliasTests
{
    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    public void CompletePinnedExactAliasInventoryCompilesToItsNormativeCanonicalSet(string flags)
    {
        Assert.Equal(1690, ProductionAliases.Values.Length);
        var canonical = new Dictionary<string, EcmaProgram>();
        foreach (var (alias, name) in ProductionAliases.Values)
        {
            if (!canonical.TryGetValue(name, out var expected))
            {
                canonical[name] = expected = Compile(name, flags);
            }

            var actual = Compile(alias, flags);
            Assert.Equal(expected.Intervals.ToArray(), actual.Intervals.ToArray());
            Assert.Empty(actual.Strings.ToArray());
            // ECMAScript disallows Unicode loose matching even when UCD suggests it.
            var budget = new CompileBudget(new());
            var data = Composition.Data(ref budget);
            var before = budget.ReservedBytes;
            Assert.False(new EcmaCompiler().TryCompile(@"\p{" + alias + " }", GrammarTests.Profile(flags), data,
                ref budget, out var result, out var diagnostic));
            Assert.Null(result);
            Assert.Equal(RegexDiagnosticCode.InvalidSyntax, diagnostic.Diagnostic.Code);
            Assert.Equal(before, budget.ReservedBytes);
        }
    }
    private static EcmaProgram Compile(string expression, string flags)
    {
        var budget = new CompileBudget(new());
        var data = Composition.Data(ref budget);
        Assert.True(new EcmaCompiler().TryCompile(@"\p{" + expression + "}", GrammarTests.Profile(flags), data,
            ref budget, out var result, out var diagnostic), expression + ": " + diagnostic);
        return result!;
    }
    [Theory]
    [InlineData("u")]
    [InlineData("v")]
    public void RealCategoriesScriptsExtensionsAndBinaryClassesHaveIndependentMembership(string flags)
    {
        foreach (var expression in new[] { "Lu", "gc=Uppercase_Letter", "General_Category=Lu" })
        { var c = new Composition(@"^\p{" + expression + "}$", flags); c.Expect("𐐀", 0, 2); Assert.Equal(RegexSearchStatus.NoMatch, c.Search("𐐨")); }
        foreach (var expression in new[] { "sc=Greek", "Script=Grek", "scx=Grek", "Script_Extensions=Greek" })
        { var c = new Composition(@"^\p{" + expression + "}$", flags); c.Expect("α", 0, 1); Assert.Equal(RegexSearchStatus.NoMatch, c.Search("a")); }
        // U+30FC has Script=Common but Script_Extensions includes Hiragana.
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition(@"^\p{sc=Hira}$", flags).Search("ー"));
        new Composition(@"^\p{scx=Hira}$", flags).Expect("ー", 0, 1);
        new Composition(@"^\p{Cs}$", flags).Expect("\uD800", 0, 1);
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition(@"^\p{Cs}$", flags).Search("😀"));
        new Composition(@"^\p{ASCII_Hex_Digit}$", flags).Expect("F", 0, 1);
        Assert.Equal(RegexSearchStatus.NoMatch, new Composition(@"^\p{AHex}$", flags).Search("Ｇ"));
    }
}
