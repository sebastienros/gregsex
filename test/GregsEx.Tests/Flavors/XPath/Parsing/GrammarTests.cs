using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XPath.Parsing;
using GregsEx.Flavors.XmlShared;
using Xunit;

namespace GregsEx.Tests.Flavors.XPath.Parsing;

public sealed class GrammarTests
{
    internal static XmlUnicodeData Data()
    {
        var budget = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(ref budget, out var data, out _));
        return data!;
    }
    internal static bool Compile(string text, XPathFlags flags, RegexCompileLimits limits,
        out XmlProgram? program, out XmlRegexDiagnostic diagnostic, out CompileBudget budget,
        RegexInputKind kind = RegexInputKind.Utf16)
    {
        budget = new(limits);
        return new XPathCompiler().TryCompile(text, new XPathProfile(flags), kind, Data(), new XmlClassAlgebra(),
            ref budget, out program, out diagnostic);
    }
    internal static XmlProgram Plan(string text, XPathFlags flags = XPathFlags.None)
    {
        Assert.True(Compile(text, flags, new(), out var plan, out var diagnostic, out var budget), diagnostic.ToString());
        Assert.Equal(plan!.RetainedBytes, budget.ReservedBytes);
        return plan;
    }
    internal static bool Member(XmlProgram plan, int scalar)
    {
        var node = plan.Nodes[plan.Root];
        if (node.Kind == XmlNodeKind.Literal)
        {
            return node.Value == scalar;
        }

        Assert.Equal(XmlNodeKind.Class, node.Kind);
        var set = plan.Sets[node.Value];
        foreach (var interval in plan.Intervals.Slice(set.Offset, set.Count))
        {
            if (scalar >= interval.First && scalar <= interval.Last)
            {
                return true;
            }
        }

        return false;
    }
    [Theory]
    [InlineData("")]
    [InlineData("|")]
    [InlineData("()")]
    [InlineData("(?:)")]
    [InlineData("a|bc|d")]
    [InlineData("ab(c|de)f")]
    [InlineData("a{0002,003}")]
    [InlineData("a?")]
    [InlineData("a*")]
    [InlineData("a+")]
    [InlineData("a{0}")]
    [InlineData("a{1,}")]
    [InlineData("a{2,3}")]
    [InlineData("^a$")]
    [InlineData("(^|$)*")]
    [InlineData("(a)|(b)\\1\\2")]
    public void RatifiedPositiveGrammar(string pattern) => Plan(pattern);

    [Theory]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("+")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData("]")]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("(?=a)")]
    [InlineData("(?<n>a)")]
    [InlineData("(?i:a)")]
    [InlineData("(?>a)")]
    [InlineData("a++")]
    [InlineData("a???")]
    [InlineData("a**")]
    [InlineData("a{,3}")]
    [InlineData("a{3,2}")]
    [InlineData("a{1,2,3}")]
    [InlineData("a{1")]
    [InlineData("a{1 }")]
    [InlineData("\\x61")]
    [InlineData("\\u0061")]
    [InlineData("\\0")]
    [InlineData("\\b")]
    [InlineData("\\ ")]
    [InlineData("\\1(a)")]
    [InlineData("(a\\1)")]
    [InlineData("((a)\\1)")]
    public void InvalidGrammarIsForx0002(string pattern)
    {
        Assert.False(Compile(pattern, 0, new(), out var plan, out var diagnostic, out var budget));
        Assert.Null(plan);
        Assert.Equal(RegexDiagnosticCode.InvalidSyntax, diagnostic.Diagnostic.Code);
        Assert.Equal(XPathErrorCode.FORX0002, diagnostic.ErrorCode);
        Assert.Equal(XmlRegexDiagnosticLocation.Pattern, diagnostic.Location);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, diagnostic.Diagnostic.LocationUnit);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Theory]
    [InlineData("a??", 0, 1)]
    [InlineData("a*?", 0, -1)]
    [InlineData("a+?", 1, -1)]
    [InlineData("a{2}?", 2, 2)]
    [InlineData("a{2,}?", 2, -1)]
    [InlineData("a{2,4}?", 2, 4)]
    public void AllReluctantFormsRetainBoundsAndPriority(string text, int min, int max)
    {
        var plan = Plan(text);
        var node = plan.Nodes[plan.Root];
        Assert.Equal(XmlNodeKind.Repeat, node.Kind);
        Assert.Equal(min, node.Min);
        Assert.Equal(max, node.Max);
        Assert.True(node.Lazy);
        Assert.Equal('a', plan.Nodes[node.X].Value);
        Assert.Equal(text.Length, node.Source.Length);
        var greedy = Plan(text[..^1]);
        Assert.False(greedy.Nodes[greedy.Root].Lazy);
    }
    [Fact]
    public void CapturesKeepOpeningOrderSyntacticParentsAndClosedReferences()
    {
        var plan = Plan("((?:a)(b))(c)\\2\\1\\3");
        Assert.Equal(new[] { -1, 0, 1, 0 }, plan.GroupParents.ToArray());
        Assert.Equal(new[] { 2, 1, 3 }, plan.Nodes.ToArray().Where(n => n.Kind == XmlNodeKind.Capture).Select(n => n.Value));
        Assert.Equal(new[] { 2, 1, 3 }, plan.Nodes.ToArray().Where(n => n.Kind == XmlNodeKind.Backreference).Select(n => n.Value));
        Assert.Equal(4, plan.GroupCount);
        // The optional group's reference stays a Backreference, allowing the
        // executor to distinguish unmatched (empty) from participating captures.
        var optional = Plan("(a)?\\1");
        Assert.Equal(XmlNodeKind.Backreference, optional.Nodes[^2].Kind);
    }
    [Fact]
    public void DecimalReferenceDigitsUseAlreadyOpenedGroupCount()
    {
        var one = Plan("(a)\\123");
        Assert.Single(one.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Backreference && n.Value == 1);
        Assert.Contains(one.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Literal && n.Value == '2');
        Assert.Contains(one.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Literal && n.Value == '3');
        var twelve = Plan(string.Concat(Enumerable.Repeat("()", 12)) + "\\123");
        Assert.Single(twelve.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Backreference && n.Value == 12);
        Assert.Contains(twelve.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Literal && n.Value == '3');
        Assert.False(Compile(string.Concat(Enumerable.Repeat("()", 11)) + "(\\12)", 0, new(), out _, out var d, out _));
        Assert.Equal(XPathErrorCode.FORX0002, d.ErrorCode);
        // Future openings never make an earlier second digit part of a ref.
        var later = Plan("()\\12" + string.Concat(Enumerable.Repeat("()", 11)));
        Assert.Contains(later.Nodes.ToArray(), n => n.Kind == XmlNodeKind.Backreference && n.Value == 1);
    }
    [Fact]
    public void AnchorsAndDotAreDistinctFromLiteralEscapes()
    {
        var anchors = Plan("^$");
        Assert.Equal(XmlNodeKind.Start, anchors.Nodes[0].Kind);
        Assert.Equal(XmlNodeKind.End, anchors.Nodes[1].Kind);
        Assert.Equal('^', Plan("\\^").Nodes[0].Value);
        Assert.Equal('$', Plan("\\$").Nodes[0].Value);
        var dot = Plan(".");
        Assert.True(Member(dot, 9));
        Assert.False(Member(dot, 10));
        Assert.False(Member(dot, 13));
        Assert.True(Member(dot, 0x10FFFF));
        Assert.False(Member(dot, 0xD800));
        var all = Plan(".", XPathFlags.DotAll);
        Assert.True(Member(all, 10));
        Assert.True(Member(all, 13));
        Assert.Equal(XPathFlags.Multiline, ((XPathProfile)Plan("^$", XPathFlags.Multiline).Profile).Flags);
    }
    [Fact]
    public void FlagsAdmitRepetitionAndReportFirstUnknownOriginalUnit()
    {
        Assert.True(XPathProfile.TryParseFlags("qqxismsiq", out var profile, out _));
        Assert.Equal((XPathFlags)31, profile!.Flags);
        Assert.False(XPathProfile.TryParseFlags("smizq", out _, out var d));
        Assert.Equal(XPathErrorCode.FORX0001, d.ErrorCode);
        Assert.Equal(XmlRegexDiagnosticLocation.Flags, d.Location);
        Assert.Equal(3, d.Diagnostic.Offset);
        Assert.Equal(1, d.Diagnostic.Length);
    }
    [Fact]
    public void QuotePreservesEveryScalarAndIgnoresXMSWhileCaseRemainsActive()
    {
        const string text = "a [^$\\.?*{}()]\t\n\r😀";
        var plan = Plan(text, XPathFlags.Quote | XPathFlags.Multiline | XPathFlags.DotAll | XPathFlags.IgnoreWhitespace);
        Assert.Equal(1, plan.GroupCount);
        Assert.All(plan.Nodes.ToArray(), n => Assert.Contains(n.Kind, new[] { XmlNodeKind.Literal, XmlNodeKind.Sequence }));
        var leaves = plan.Nodes.ToArray().Where(n => n.Kind == XmlNodeKind.Literal).Select(n => n.Value);
        Assert.Equal(text.EnumerateRunes().Select(r => r.Value), leaves);
        Assert.True(Member(Plan("a", XPathFlags.Quote | XPathFlags.IgnoreCase), 'A'));
        Assert.Equal(XmlNodeKind.Empty, Plan("", XPathFlags.Quote).Nodes[0].Kind);
    }
    [Fact]
    public void XProcessingRetainsNativeLocationsAcrossEveryGrammarToken()
    {
        var plan = Plan(" \t( ? : a ) { 2 , 3 } ? ", XPathFlags.IgnoreWhitespace);
        var repeat = plan.Nodes[plan.Root];
        Assert.True(repeat.Lazy);
        Assert.Equal(2, repeat.Min);
        Assert.Equal(3, repeat.Max);
        Assert.Equal(2, repeat.Source.Offset);
        Assert.Equal(21, repeat.Source.Length);
        Assert.True(Member(Plan("[ \t\n\r]", XPathFlags.IgnoreWhitespace), ' '));
        Assert.Equal(XmlNodeKind.Literal, Plan("\u00A0", XPathFlags.IgnoreWhitespace).Nodes[0].Kind);
        Assert.Equal('#', Plan("#", XPathFlags.IgnoreWhitespace).Nodes[0].Value);
        Assert.Equal('[', Plan("\\ [", XPathFlags.IgnoreWhitespace).Nodes[0].Value);
        Assert.Equal(XmlNodeKind.Class, Plan("\\ s", XPathFlags.IgnoreWhitespace).Nodes[0].Kind);
        Assert.False(Compile(" a \n \\ z", XPathFlags.IgnoreWhitespace, new(), out _, out var d, out _));
        Assert.Equal(5, d.Diagnostic.Offset);
        Assert.Equal(3, d.Diagnostic.Length);
        Assert.Equal(2, Plan(" 😀 ", XPathFlags.IgnoreWhitespace).Nodes[0].Source.Length);
    }
    [Fact]
    public void LongFlatPatternsAndBranchesHaveBoundedBalancedTrees()
    {
        var plan = Plan(new string('a', 4096));
        Assert.Equal(8191, plan.Nodes.Length);
        var alternatives = Plan(string.Join('|', Enumerable.Repeat("a", 4096)));
        Assert.Equal(8191, alternatives.Nodes.Length);
        Assert.True(Compile(new string('a', 1024), 0, new(maxNestingDepth: 11), out _, out _, out _));
        Assert.False(Compile(new string('a', 1025), 0, new(maxNestingDepth: 11), out _, out var d, out _));
        Assert.Equal(RegexDiagnosticCode.NestingLimitExceeded, d.Diagnostic.Code);
    }
    [Fact]
    public void IndependentFrozenCorpusHasCorrectGrammarAdmission()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "test/corpora/xml/xpath/Corpus.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "test/corpora/xml/xpath/Corpus.json")));
        var checkedCases = 0;
        foreach (var c in corpus.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (c.GetProperty("patternKind").GetString() != "Utf16" || c.GetProperty("inputKind").GetString() == "Bytes" || c.GetProperty("limits").ValueKind != JsonValueKind.Null)
            {
                continue;
            }

            var flags = c.GetProperty("flags").GetString()!;
            if (!XPathProfile.TryParseFlags(flags, out var profile, out _))
            {
                continue;
            }

            string pattern = new([.. c.GetProperty("patternUnits").EnumerateArray().Select(u => (char)u.GetInt32())]);
            var outcomes = c.GetProperty("permitted").EnumerateArray().ToArray();
            var syntaxError = outcomes.All(o => o.GetProperty("error").ValueKind != JsonValueKind.Null && o.GetProperty("error").GetProperty("code").GetString() == "FORX0002");
            var domainError = outcomes.Any(o => o.GetProperty("error").ValueKind != JsonValueKind.Null && o.GetProperty("error").GetProperty("code").GetString() == "InvalidPatternEncoding");
            var ok = Compile(pattern, profile!.Flags, new(), out _, out var diagnostic, out _);
            Assert.True(ok == !(syntaxError || domainError), $"{c.GetProperty("id").GetString()}: {pattern}: {diagnostic}");
            checkedCases++;
        }
        Assert.True(checkedCases > 600, $"Only {checkedCases} independent cases inspected.");
    }
}
