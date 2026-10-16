using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class GrammarTests
{
    internal static EcmaScriptProfile Profile(string flags)
    {
        Assert.True(EcmaScriptProfile.TryParseFlags(flags, out var profile, out var error), error.ToString());
        return profile!;
    }
    internal static EcmaProgram Compile(string pattern, string flags = "", RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.True(new EcmaCompiler().TryCompile(pattern, Profile(flags), ContractUnicode.Instance, ref budget, out var program, out var diagnostic), $"{pattern} / {flags}: {diagnostic}");
        Assert.NotNull(program);
        Assert.Equal(default, diagnostic);
        Assert.Equal(program.RetainedBytes, budget.ReservedBytes);
        return program;
    }
    internal static EcmaScriptDiagnostic Reject(string pattern, string flags, RegexDiagnosticCode code = RegexDiagnosticCode.InvalidSyntax, RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.False(new EcmaCompiler().TryCompile(pattern, Profile(flags), ContractUnicode.Instance, ref budget, out var program, out var diagnostic), pattern + " / " + flags);
        Assert.Null(program);
        Assert.Equal(code, diagnostic.Diagnostic.Code);
        Assert.Equal(EcmaScriptDiagnosticLocation.Pattern, diagnostic.Location);
        Assert.InRange(diagnostic.Diagnostic.Offset, 0, pattern.Length);
        Assert.InRange(diagnostic.Diagnostic.Length, 0, pattern.Length - diagnostic.Diagnostic.Offset);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.Diagnostic.LocationUnit);
        Assert.NotNull(diagnostic.FeatureId);
        Assert.Equal(0, budget.ReservedBytes);
        return diagnostic;
    }
    public static IEnumerable<TheoryDataRow<string, string>> CommonAdmission()
    {
        string[] patterns = ["", "|", "a||b|", "abc", "😀", "()", "(?:)", "((a)|(b))*", "(a?)*", "(?=a)(?!b)(?<=c*)(?<!d+)",
            ".^$", @"\b\B", @"\d\D\w\W\s\S", "[]", "[^]", "[a-z]", "[a-b-c]", "[-a-z]", "[a-]", "[--a]", @"[\b\-\[\]]",
            @"\n\r\t\v\f\0\cA\cz\x00\xff\u0000\uFFFF", @"\^\$\\\.\*\+\?\(\)\[\]\{\}\|\/", "a*", "a+", "a?", "a*?", "a+?", "a??", "a{0}", "a{2}", "a{2,}", "a{2,4}?", "a{00002,00003}",
            @"\1(a)", @"(a)\1", @"(a|(b))\2", @"\k<x>(?<x>a)", @"(?<x>a)\k<x>", "(?<x>a)|(?<x>b)", "(?:(?<x>a)|(?<x>b))*", "(?<α>a)(?<π0>b)", @"(?<\u{10400}>a)", "(?<a\u200Cb>a)",
            "(?i:a)(?-i:b)", "(?ims-s:a)", "(?im-s:a)", "(?i-:a)", "(?-ms:a)", "(?s:.)", "(?m:^$)"];
        foreach (var flags in new[] { "", "u", "v" })
        {
            foreach (var pattern in patterns)
            {
                // Dash literals and chained ranges use a different v grammar.
                if (flags == "v" && pattern is "[a-b-c]" or "[-a-z]" or "[a-]" or "[--a]")
                {
                    continue;
                }

                if (pattern == "(?ims-s:a)")
                {
                    continue; // modifier add/remove overlap is an early error.
                }

                yield return new TheoryDataRow<string, string>(pattern, flags);
            }
        }
    }
    [Theory, MemberData(nameof(CommonAdmission))]
    public void SourceDerivedProductionAdmission(string pattern, string flags) => Compile(pattern, flags);

    [Theory]
    [InlineData(0xD800, "")]
    [InlineData(0xDC00, "")]
    [InlineData(0xD800, "u")]
    [InlineData(0xDC00, "u")]
    [InlineData(0xD800, "v")]
    [InlineData(0xDC00, "v")]
    public void RawSurrogateProductionAdmission(int codeUnit, string flags) =>
        Compile(new string(checked((char)codeUnit), 1), flags);

    public static IEnumerable<TheoryDataRow<string, string>> CommonRejections()
    {
        string[] patterns = ["(", ")", "[", "\\", "(?", "(?<", "(?<>", "(?<0>a)", "(?<a-b>x)", @"(?<\u0030>x)", "(?<\uD800>x)", "(?<a>a)(?<a>b)", "(?<x>(?<x>a))", "(?:(?<x>a)|b)(?<x>c)",
            "(?i-i:a)", "(?ii:a)", "(?-ii:a)", "(?-:a)", "(?u:a)", "(?d:a)", "(?i)", "(?i--m:a)", "(?im-mi:a)", "(?g:a)", "(?i+ms:a)",
            "(?>a)", "(?|a)", "(?R)", "(*FAIL)", "(?(1)a)", "(?P<x>a)", "(?#a)", "(?<a-b>a)", "a**", "a*+", "a?+", "a{2,3}+", "*", "+", "?", "{2}", "[z-a]", "^*", "$+", @"\b?", "(?<=a)*", "(?<!a)?", @"\k<bad>(?<x>a)", @"(?<x>a)\k", @"(?<x>a)[\k]", "a{3,2}"];
        foreach (var flags in new[] { "", "u", "v" })
        {
            foreach (var pattern in patterns)
            {
                yield return new TheoryDataRow<string, string>(pattern, flags);
            }
        }
    }
    [Theory, MemberData(nameof(CommonRejections))]
    public void SourceDerivedEarlyErrorsAndForeignSyntax(string pattern, string flags) => Reject(pattern, flags);
    public static IEnumerable<TheoryDataRow<string>> AnnexBCases()
    {
        foreach (var pattern in new[] { @"\1", @"\8\9", @"\07\377\400", @"[\1\8\9]", @"\xQ\uQ", @"\x1\u12", @"\c", @"\c0", @"[\c_]", @"[\c1]", @"\a\q\p{L}", @"[a-\d]", @"[\w-z]", "{", "}", "]", "a{,2}", "a{foo}", "a{2", "a{1,2,3}", "(?=a)*", "(?!a)+", @"\k<x>" })
        {
            yield return new TheoryDataRow<string>(pattern);
        }
    }
    [Theory, MemberData(nameof(AnnexBCases))]
    public void AnnexBAdmittedOnlyInLegacy(string pattern) { Compile(pattern); Reject(pattern, "u"); Reject(pattern, "v"); }
    [Fact]
    public void AnnexBValuesPreserveFallbackAndOctalConsumption()
    {
        int[] expected = [7, 255, 32, '0', '8', '9', 'x', 'G', 'G', 'u', '1', '2', '\\', 'c', '0'];
        var p = Compile(@"\07\377\400\8\9\xGG\u12\c0");
        Assert.Equal(expected, p.Nodes.ToArray().Where(n => n.Kind == EcmaNodeKind.Literal).Select(n => n.Value));
        var cls = Compile(@"[\c_\c1\c]");
        Assert.True(SetTests.Contains(cls, 0, 31));
        Assert.True(SetTests.Contains(cls, 0, 17));
        Assert.True(SetTests.Contains(cls, 0, '\\'));
        Assert.True(SetTests.Contains(cls, 0, 'c'));
    }
    [Fact]
    public void CapturesReferencesAssertionsAndRepeatClearingRangesRemainExplicit()
    {
        var p = Compile(@"\k<x>(?<x>(a)|(?<=b+)(c))|(?<x>d)\1", "u");
        Assert.Equal(5, p.GroupCount);
        Assert.Single(p.Names.ToArray());
        Assert.Equal("x", p.Names[0].Name);
        Assert.Equal(new[] { 1, 4 }, p.NameAliases.ToArray());
        Assert.Contains(p.Nodes.ToArray(), n => n.Kind == EcmaNodeKind.Backreference && n.NameIndex == 0 && n.Value == 0);
        Assert.Contains(p.Nodes.ToArray(), n => n.Kind == EcmaNodeKind.Backreference && n.Value == 1 && n.NameIndex == -1);
        Assert.Contains(p.Nodes.ToArray(), n => n.Kind == EcmaNodeKind.Lookbehind && p.Nodes[n.X].Kind == EcmaNodeKind.Repeat && p.Nodes[n.X].Max == -1);
        var repeat = Compile("((a)|(b)){2,4}?");
        var root = repeat.Nodes[repeat.Root];
        Assert.Equal(EcmaNodeKind.Repeat, root.Kind);
        Assert.Equal(2, root.Min);
        Assert.Equal(4, root.Max);
        Assert.True(root.Lazy);
        Assert.Equal(1, root.FirstCapture);
        Assert.Equal(3, root.CaptureCount);
        Assert.Equal(1, repeat.Nodes[root.X].Value);
        Assert.Equal(3, repeat.Nodes[root.X].CaptureCount);
    }
    [Fact]
    public void ScopedFlagsDoNotLeakAndIntervalsReflectLocalCaseAndDotRules()
    {
        var p = Compile("(?i:A(?-i:B))C(?s:.)D(?m:^$)", "u");
        var literals = p.Nodes.ToArray().Where(n => n.Kind == EcmaNodeKind.Literal).ToArray();
        Assert.Equal(new[] { 'a', 'B', 'C', 'D' }.Select(c => (int)c), literals.Select(n => n.Value));
        Assert.True(literals[0].EffectiveFlags.HasFlag(EcmaScriptFlags.IgnoreCase));
        Assert.Equal(EcmaScriptFlags.None, literals[1].EffectiveFlags);
        Assert.True(SetTests.Contains(p, 0, 10));
        Assert.Equal(EcmaScriptFlags.DotAll, p.Nodes.ToArray().Single(n => n.Kind == EcmaNodeKind.Set).EffectiveFlags);
        Assert.All(p.Nodes.ToArray().Where(n => n.Kind is EcmaNodeKind.Start or EcmaNodeKind.End), n => Assert.Equal(EcmaScriptFlags.Multiline, n.EffectiveFlags));
    }
    [Theory]
    [InlineData("", 2, 0xD83D)]
    [InlineData("u", 1, 0x1F600)]
    [InlineData("v", 1, 0x1F600)]
    public void RawAndEscapedSurrogatesKeepNativeUnits(string flags, int count, int first)
    {
        foreach (var text in new[] { "😀", @"\uD83D\uDE00" })
        {
            var p = Compile(text, flags);
            var literals = p.Nodes.ToArray().Where(n => n.Kind == EcmaNodeKind.Literal).ToArray();
            Assert.Equal(count, literals.Length);
            Assert.Equal(first, literals[0].Value);
            Assert.Equal(text.Length, p.Nodes[p.Root].Source.Length);
            Assert.Equal(0, literals[0].Source.Offset);
        }
        foreach (var text in new[] { "\uD800", @"\uD800", @"\uDC00" })
        {
            Assert.InRange(Compile(text, flags).Nodes[0].Value, 0xD800, 0xDFFF);
        }
    }
    [Fact]
    public void CodePointEscapesNamesAndAdjacentSurrogateEscapeGrammar()
    {
        Assert.Equal(0x1F600, Compile(@"\u{00001F600}", "u").Nodes[0].Value);
        var p = Compile(@"\u{D83D}\uDE00", "u");
        Assert.Equal(2, p.Nodes.ToArray().Count(n => n.Kind == EcmaNodeKind.Literal));
        Assert.Equal("𐐀", Compile(@"(?<\uD801\uDC00>a)").Names[0].Name);
        foreach (var pattern in new[] { @"\u{}", @"\u{110000}", @"\u{123456789abcdef}", @"\u{G}", @"\u{41", @"[\B]", @"[\1]", @"\00" })
        { Reject(pattern, "u"); Reject(pattern, "v"); }
    }
    [Fact]
    public void BalancedLongConcatenationAndAlternativesPreserveSourceOrder()
    {
        var pattern = new string('a', 8192);
        var p = Compile(pattern, "u", new(maxNestingDepth: 16));
        Assert.Equal(16383, p.Nodes.Length);
        var stack = new Stack<int>();
        var leaves = new List<int>();
        stack.Push(p.Root);
        while (stack.TryPop(out var id))
        { var n = p.Nodes[id]; if (n.Kind == EcmaNodeKind.Literal) { leaves.Add(n.Source.Offset); } else { stack.Push(n.Y); stack.Push(n.X); } }
        Assert.Equal(Enumerable.Range(0, 8192), leaves);
        p = Compile(string.Join('|', Enumerable.Range(0, 1024).Select(i => i % 2 == 0 ? "a" : "b")), "u", new(maxNestingDepth: 12));
        Assert.Equal(2047, p.Nodes.Length);
        Assert.Equal(EcmaNodeKind.Alternative, p.Nodes[p.Root].Kind);
    }
    [Fact]
    public void ImmutableProgramOwnsEveryInputAndNoAmbientCultureAffectsParsing()
    {
        var pattern = "(?<x>abc)[a-z]".ToCharArray();
        var budget = new CompileBudget(new());
        Assert.True(new EcmaCompiler().TryCompile(pattern, Profile("ui"), ContractUnicode.Instance, ref budget, out var p, out _));
        Array.Fill(pattern, 'z');
        Assert.Equal("x", p!.Names[0].Name);
        Assert.Equal('a', p.Nodes[0].Value);
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        { System.Globalization.CultureInfo.CurrentCulture = new("tr-TR"); Assert.Equal('i', Compile("I", "ui").Nodes[0].Value); }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
