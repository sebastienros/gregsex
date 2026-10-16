using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using Xunit;

namespace GregsEx.Tests.Contracts.Xml;

public class ContractTests
{
    [Fact]
    public void ProfilesHaveIndependentDataAndRepresentationAdmission()
    {
        RegexProfile[] profiles = [new XsdProfile(), new XPathProfile()];
        Assert.Equal("Xsd11", profiles[0].Id);
        Assert.Equal("XPath31", profiles[1].Id);
        foreach (var p in profiles)
        {
            Assert.Equal("Unicode-16.0.0+XML-1.0-5e", p.DataId);
        }

        Assert.True(new XsdProfile().Supports(RegexInputKind.Utf8));
        Assert.True(new XPathProfile().Supports(RegexInputKind.Utf16));
        Assert.False(new XsdProfile().Supports(RegexInputKind.Bytes));
        Assert.False(new XPathProfile().Supports((RegexInputKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPathProfile((XPathFlags)32));
        var a = typeof(XPathProfile).Assembly;
        Assert.DoesNotContain(a.GetExportedTypes(), t => t.Namespace?.StartsWith("GregsEx.Contracts", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("ssmmiixxqq", true, -1)]
    [InlineData("", true, -1)]
    [InlineData("qz", false, 1)]
    [InlineData(" i", false, 0)]
    public void FlagsPermitRepetitionButPreserveInvalidSource(string flags, bool valid, int offset)
    {
        Assert.Equal(valid, XPathProfile.TryParseFlags(flags, out var profile, out var error));
        if (valid)
        { Assert.NotNull(profile); Assert.Equal(default, error); }
        else
        {
            Assert.Null(profile);
            Assert.Equal(XPathErrorCode.FORX0001, error.ErrorCode);
            Assert.Equal(XmlRegexDiagnosticLocation.Flags, error.Location);
            Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, offset, 1, RegexOffsetUnit.Utf16CodeUnits), error.Diagnostic);
        }
    }

    [Theory]
    [InlineData(9, 10, true)]
    [InlineData(13, 13, true)]
    [InlineData(10, 13, false)]
    [InlineData(0, 0, false)]
    [InlineData(0xD7FF, 0xE000, false)]
    [InlineData(0xFFFD, 0x10000, false)]
    [InlineData(0x10000, 0x10FFFF, true)]
    public void AlphabetRangesCannotBridgeIllegalXmlCharacters(int first, int last, bool expected)
        => Assert.Equal(expected, XmlAlphabet.ContainsRange(new(first, last)));

    [Fact]
    public void ProgramAdmissionCopiesAndChargesBeforePublishing()
    {
        XmlNode[] nodes = [Leaf(XmlNodeKind.Class)];
        XmlInterval[] ranges = [new(9, 10)];
        var budget = new CompileBudget(new());
        Assert.True(XmlProgram.TryCreate(new XsdProfile(), RegexInputKind.Utf8, 1, nodes,
            [new(0, 1)], ranges, [-1], ref budget, out var plan, out _));
        nodes[0] = Leaf(XmlNodeKind.Literal, 'x');
        ranges[0] = new(0, 0);
        Assert.Equal(XmlNodeKind.Class, plan!.Nodes[0].Kind);
        Assert.Equal(new(9, 10), plan.Intervals[0]);
        Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
        Assert.True(budget.PeakBytes > budget.ReservedBytes);
        Assert.Equal(XmlDialect.Xsd, plan.Dialect);
        Assert.Equal(RegexInputKind.Utf8, plan.InputKind);
    }

    [Theory]
    [InlineData(0)] // scalar outside XML domain
    [InlineData(1)] // XSD anchor
    [InlineData(2)] // XSD lazy quantifier
    [InlineData(3)] // cycle
    [InlineData(4)] // orphan
    [InlineData(5)] // unused operand
    [InlineData(6)] // original source out of bounds
    [InlineData(7)] // byte source locations
    public void InvalidPlansCannotReachAnExecutor(int mutation)
    {
        var leaf = Leaf(XmlNodeKind.Literal, 'a');
        XmlNode[] nodes = mutation switch
        {
            0 => [leaf with { Value = 0 }],
            1 => [Leaf(XmlNodeKind.Start)],
            2 => [leaf, Leaf(XmlNodeKind.Repeat) with { X = 0, Min = 0, Max = -1, Lazy = true }],
            3 => [Leaf(XmlNodeKind.Repeat) with { X = 0, Max = -1 }],
            4 => [leaf, leaf],
            5 => [leaf with { Y = 1 }],
            6 => [leaf with { Source = new(1, 1, RegexOffsetUnit.Utf16CodeUnits) }],
            _ => [leaf with { Source = new(0, 1, RegexOffsetUnit.Bytes) }]
        };
        var budget = new CompileBudget(new());
        Assert.False(Create(nodes, ref budget, out var result, out var error));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, error.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void XPathCapturesRetainSyntacticParentsAndClosedBackreferences()
    {
        // (a(b))\1 : groups1/2 nested; numeric source offsets are original.
        XmlNode[] nodes = [Leaf(XmlNodeKind.Literal, 'a', 1), Leaf(XmlNodeKind.Literal, 'b', 3),
            Leaf(XmlNodeKind.Capture, 2, 2, 3) with { X = 1 },
            Leaf(XmlNodeKind.Sequence, 0, 1, 4) with { X = 0, Y = 2 },
            Leaf(XmlNodeKind.Capture, 1, 0, 6) with { X = 3 },
            Leaf(XmlNodeKind.Backreference, 1, 6, 2),
            Leaf(XmlNodeKind.Sequence, 0, 0, 8) with { X = 4, Y = 5 }];
        var budget = new CompileBudget(new());
        Assert.True(XmlProgram.TryCreate(new XPathProfile(), RegexInputKind.Utf16, 8,
            nodes, [], [], [-1, 0, 1], ref budget, out var program, out _));
        Assert.Equal(3, program!.GroupCount);
        Assert.Equal(1, program.GroupParents[2]);
        var broken = new CompileBudget(new());
        Assert.False(XmlProgram.TryCreate(new XPathProfile(), RegexInputKind.Utf16, 8,
            nodes, [], [], [-1, 0, 0], ref broken, out _, out _));
        nodes[5] = nodes[5] with { Source = new(0, 2, RegexOffsetUnit.Utf16CodeUnits) };
        var forward = new CompileBudget(new());
        Assert.False(XmlProgram.TryCreate(new XPathProfile(), RegexInputKind.Utf16, 8,
            nodes, [], [], [-1, 0, 1], ref forward, out _, out _));
    }

    [Fact]
    public void QuoteModeAndNormalizedIntervalsHaveExplicitAdmission()
    {
        var quote = new CompileBudget(new());
        Assert.False(XmlProgram.TryCreate(new XPathProfile(XPathFlags.Quote), RegexInputKind.Utf16, 1,
            [Leaf(XmlNodeKind.Start)], [], [], [-1], ref quote, out _, out _));
        var intervals = new CompileBudget(new());
        Assert.False(XmlProgram.TryCreate(new XsdProfile(), RegexInputKind.Utf16, 1,
            [Leaf(XmlNodeKind.Class)], [new(0, 2)], [new('a', 'b'), new('c', 'd')], [-1],
            ref intervals, out _, out _));
        var scalar = new CompileBudget(new());
        Assert.False(XmlProgram.TryCreate(new XsdProfile(), RegexInputKind.Utf16, 1,
            [Leaf(XmlNodeKind.Class)], [new(0, 1)], [new(10, 13)], [-1], ref scalar, out _, out _));
    }

    [Theory]
    [InlineData(0, RegexDiagnosticCode.PlanLimitExceeded)]
    [InlineData(1, RegexDiagnosticCode.CompileMemoryLimitExceeded)]
    [InlineData(2, RegexDiagnosticCode.CompileWorkLimitExceeded)]
    [InlineData(3, RegexDiagnosticCode.NestingLimitExceeded)]
    public void ValidatorBudgetsReleaseTemporaryStorage(int kind, RegexDiagnosticCode expected)
    {
        var limits = kind switch
        {
            0 => new RegexCompileLimits(maxPlanBytes: 1),
            1 => new RegexCompileLimits(maxCompileMemoryBytes: 1),
            2 => new RegexCompileLimits(maxCompileWork: 1),
            _ => new RegexCompileLimits(maxNestingDepth: 1)
        };
        var budget = new CompileBudget(limits);
        Assert.False(Create([Leaf(XmlNodeKind.Literal, 'a'), Leaf(XmlNodeKind.Repeat) with { X = 0, Max = -1 }],
            ref budget, out _, out var diagnostic));
        Assert.Equal(expected, diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void OwnedTreePreservesNestingAndEmptyParticipation()
    {
        XPathAnalyzeNode[] nodes = [new(XPathAnalyzeNodeKind.Match, -1, 0, new(0, 1)),
            new(XPathAnalyzeNodeKind.Group, 0, 1, new(0, 1)),
            new(XPathAnalyzeNodeKind.Text, 1, 0, new(0, 1)),
            new(XPathAnalyzeNodeKind.Group, 1, 2, new(1, 0))];
        var result = new XPathAnalyzeResult("a", nodes);
        nodes[1] = default;
        Assert.Equal(XPathAnalyzeNodeKind.Group, result.Nodes[1].Kind);
        Assert.Equal(1, result.Nodes[3].ParentIndex);
        Assert.Equal(0, result.Nodes[3].Range.Length);
        Assert.Equal("http://www.w3.org/2005/xpath-functions", XPathAnalyzeResult.Namespace);
        Assert.False(result.Nodes is XPathAnalyzeNode[]);
        Assert.Throws<NotSupportedException>(() => ((IList<XPathAnalyzeNode>)result.Nodes)[0] = default);
    }

    [Fact]
    public void ErrorAndOutputCeilingsAreDistinctFromStandardErrors()
    {
        Assert.Equal("http://www.w3.org/2005/xqt-errors", XmlRegexDiagnostic.ErrorNamespace);
        Assert.Throws<ArgumentException>(() => new XPathRegexException(default));
        Assert.Throws<ArgumentException>(() => new XPathHostException(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPathHostLimits(maxResults: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPathHostLimits(maxOutputUnits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new XPathHostLimits(maxMemoryBytes: 0));
        var limits = new XPathHostLimits();
        Assert.Equal(67_108_864, limits.MaxMemoryBytes);
        Assert.Equal(9, (int)RegexExecutionFailureCode.InvalidInputDomain);
        Assert.Equal(8, (int)RegexExecutionFailureCode.TimedOut);
    }

    private static XmlNode Leaf(XmlNodeKind kind, int value = 0, int offset = 0, int length = 1)
        => new(kind, -1, -1, value, 0, 0, false, new(offset, length, RegexOffsetUnit.Utf16CodeUnits));
    private static bool Create(ReadOnlySpan<XmlNode> nodes, ref CompileBudget budget,
        out XmlProgram? program, out RegexDiagnostic diagnostic)
        => XmlProgram.TryCreate(new XsdProfile(), RegexInputKind.Utf16, 1, nodes, [], [], [-1],
            ref budget, out program, out diagnostic);
}
