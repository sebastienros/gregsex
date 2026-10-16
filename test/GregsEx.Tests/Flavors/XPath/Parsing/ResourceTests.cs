using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XPath.Parsing;
using GregsEx.Flavors.XmlShared;
using GregsEx.IR;
using Xunit;
using static GregsEx.Tests.Flavors.XPath.Parsing.GrammarTests;

namespace GregsEx.Tests.Flavors.XPath.Parsing;

public sealed class ResourceTests
{
    [Fact]
    public void EveryCeilingFailsDistinctlyAndReleasesAllParserScratch()
    {
        foreach (var entry in new[] {
            ("ab", new RegexCompileLimits(maxPatternUnits:1), RegexDiagnosticCode.PatternLimitExceeded),
            ("((a))", new RegexCompileLimits(maxNestingDepth:1), RegexDiagnosticCode.NestingLimitExceeded),
            ("[a-[b-[c]]]", new RegexCompileLimits(maxNestingDepth:2), RegexDiagnosticCode.NestingLimitExceeded),
            ("()", new RegexCompileLimits(maxCaptureGroups:1), RegexDiagnosticCode.CaptureLimitExceeded),
            ("ab", new RegexCompileLimits(maxInstructions:2), RegexDiagnosticCode.ProgramLimitExceeded),
            ("[a][b]", new RegexCompileLimits(maxClassRanges:1), RegexDiagnosticCode.ClassLimitExceeded),
            ("a", new RegexCompileLimits(maxCompileWork:1), RegexDiagnosticCode.CompileWorkLimitExceeded),
            ("a", new RegexCompileLimits(maxCompileMemoryBytes:1), RegexDiagnosticCode.CompileMemoryLimitExceeded),
            ("a", new RegexCompileLimits(maxPlanBytes:1), RegexDiagnosticCode.PlanLimitExceeded),
            ("a{2147483648}", new RegexCompileLimits(), RegexDiagnosticCode.ProgramLimitExceeded) })
        {
            Assert.False(Compile(entry.Item1, 0, entry.Item2, out var program, out var d, out var budget));
            Assert.Null(program);
            Assert.Equal(entry.Item3, d.Diagnostic.Code);
            Assert.Equal(XPathErrorCode.None, d.ErrorCode);
            Assert.Equal(0, budget.ReservedBytes);
        }
    }
    [Fact]
    public void BoundaryAdmissionAndCompactRepetitionKeepExactProgramBudgets()
    {
        Assert.True(Compile("ab", 0, new(maxPatternUnits: 2, maxInstructions: 3), out _, out _, out _));
        Assert.True(Compile("()", 0, new(maxCaptureGroups: 2), out _, out _, out _));
        Assert.True(Compile("[a][b]", 0, new(maxClassRanges: 2), out _, out _, out _));
        var plan = Plan("a{2147483647}");
        Assert.Equal(2, plan.Nodes.Length);
        Assert.Equal(int.MaxValue, plan.Nodes[^1].Max);
        Plan("a{0000000000000000000000000000000000}");
    }
    [Fact]
    public void PeakMemoryIncludesGrowthCopiesXMapAndFinalValidatorCopies()
    {
        const string text = " (a|[b-d-[c]]){2,3}? ";
        const XPathFlags flags = XPathFlags.IgnoreWhitespace;
        Assert.True(Compile(text, flags, new(), out var plan, out _, out var baseline));
        Assert.True(baseline.PeakBytes > plan!.RetainedBytes);
        Assert.True(Compile(text, flags, new(maxCompileMemoryBytes: baseline.PeakBytes), out _, out _, out var boundary));
        Assert.Equal(baseline.PeakBytes, boundary.PeakBytes);
        Assert.False(Compile(text, flags, new(maxCompileMemoryBytes: baseline.PeakBytes - 1), out _, out var d, out var limited));
        Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, d.Diagnostic.Code);
        Assert.Equal(0, limited.ReservedBytes);
        // The verified algebra conservatively includes the caller's live
        // buffers in its retained ceiling while admitting a class result.
        // Isolate final-copy exactness from that earlier class admission.
        const string retainedText = " (a|b){2,3}? ";
        var retainedPlan = Plan(retainedText, flags);
        Assert.True(Compile(retainedText, flags, new(maxPlanBytes: retainedPlan.RetainedBytes), out _, out _, out _));
        Assert.False(Compile(retainedText, flags, new(maxPlanBytes: retainedPlan.RetainedBytes - 1), out _, out d, out limited));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Diagnostic.Code);
        Assert.Equal(0, limited.ReservedBytes);
    }
    [Fact]
    public void WorkIsCumulativeAndIncludesValidationAlgebraAndPublication()
    {
        const string text = "[A-Z-[Q]](a)\\1";
        const XPathFlags flags = XPathFlags.IgnoreCase;
        Assert.True(Compile(text, flags, new(), out _, out _, out var baseline));
        Assert.True(baseline.WorkConsumed > text.Length);
        Assert.True(Compile(text, flags, new(maxCompileWork: baseline.WorkConsumed), out _, out _, out var boundary));
        Assert.Equal(baseline.WorkConsumed, boundary.WorkConsumed);
        Assert.False(Compile(text, flags, new(maxCompileWork: baseline.WorkConsumed - 1), out _, out var d, out var budget));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void IncomingBudgetIsPreservedAndRetainedCeilingIncludesPriorData()
    {
        var data = Data();
        var budget = new CompileBudget(new(maxPlanBytes: 500));
        var source = new SourceRange(0, 0, RegexOffsetUnit.Utf16CodeUnits);
        Assert.True(budget.TryReserveBytes(100, source, out _));
        Assert.True(budget.TryCharge(10, source, out _));
        Assert.False(new XPathCompiler().TryCompile("a", new XPathProfile(), RegexInputKind.Utf16, data, new XmlClassAlgebra(), ref budget, out _, out var d));
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, d.Diagnostic.Code);
        Assert.Equal(100, budget.ReservedBytes);
        Assert.True(budget.WorkConsumed > 10);
    }
    [Fact]
    public void WellFormedPatternsHaveStrictXmlDomainEvenInQuoteMode()
    {
        foreach (var bad in new[] { "\0", "\u000B", "\u001F", "\uFFFE", "\uFFFF", "\uD800", "\uDC00", "\uD800x" })
        {
            foreach (var flags in new[] { XPathFlags.None, XPathFlags.Quote, XPathFlags.IgnoreWhitespace })
            {
                Assert.False(Compile("😀" + bad, flags, new(), out _, out var d, out var budget));
                Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding, d.Diagnostic.Code);
                Assert.Equal(2, d.Diagnostic.Offset);
                Assert.Equal(XPathErrorCode.None, d.ErrorCode);
                Assert.Equal(0, budget.ReservedBytes);
            }
        }

        Plan("\t\n\r\uFFFD\U0010FFFF");
    }
    [Fact]
    public void BothSubjectRepresentationsRetainOriginalUtf16PatternCoordinates()
    {
        foreach (var kind in new[] { RegexInputKind.Utf16, RegexInputKind.Utf8 })
        {
            Assert.True(Compile("😀(b)\\1", 0, new(), out var plan, out _, out _, kind));
            Assert.Equal(kind, plan!.InputKind);
            Assert.Equal(7, plan.PatternLength);
            Assert.Equal(new SourceRange(0, 2, RegexOffsetUnit.Utf16CodeUnits), plan.Nodes[0].Source);
            Assert.Equal(new SourceRange(2, 3, RegexOffsetUnit.Utf16CodeUnits), plan.Nodes[2].Source);
        }
        Assert.False(Compile("a", 0, new(), out _, out var d, out var budget, RegexInputKind.Bytes));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, d.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void AdversarialTokenMutationsNeverEscapeStructuredAdmissionOrLeakScratch()
    {
        var random = new Random(0x58504154);
        const string tokens = "ab01()[]{}?*+|^$-\\pnrtIS: ";
        for (var trial = 0; trial < 600; trial++)
        {
            var units = new char[random.Next(0, 32)];
            for (var i = 0; i < units.Length; i++)
            {
                units[i] = tokens[random.Next(tokens.Length)];
            }

            string pattern = new(units);
            var flags = (XPathFlags)random.Next(0, 32);
            var success = Compile(pattern, flags, new(maxCompileWork: 500_000), out var plan, out var d, out var budget);
            Assert.Equal(success ? plan!.RetainedBytes : 0, budget.ReservedBytes);
            Assert.NotEqual(RegexDiagnosticCode.InvalidProgram, d.Diagnostic.Code);
            Assert.InRange(d.Diagnostic.Offset, 0, pattern.Length);
            Assert.InRange(d.Diagnostic.Length, 0, pattern.Length - d.Diagnostic.Offset);
        }
    }
    [Fact]
    public void PublishedPlanOwnsItsDataAndEmptyXMapsToOriginalEnd()
    {
        var data = Data();
        var compiler = new XPathCompiler();
        var budget = new CompileBudget(new());
        var chars = "(a)[b-d]".ToCharArray();
        Assert.True(compiler.TryCompile(chars, new XPathProfile(), RegexInputKind.Utf16, data, new XmlClassAlgebra(), ref budget, out var plan, out _));
        Array.Fill(chars, 'z');
        Assert.Equal('a', plan!.Nodes[0].Value);
        Assert.Equal(new XmlInterval('b', 'd'), plan.Intervals[0]);
        Assert.Equal(new[] { -1, 0 }, plan.GroupParents.ToArray());
        var empty = Plan(" \t\n\r", XPathFlags.IgnoreWhitespace);
        Assert.Equal(new SourceRange(4, 0, RegexOffsetUnit.Utf16CodeUnits), empty.Nodes[0].Source);
    }

}
