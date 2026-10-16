using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

public sealed class BoundaryTests
{
    private readonly ITestOutputHelper _output;
    public BoundaryTests(ITestOutputHelper output) => _output = output;
    [Fact]
    public void SyntaxNestingCeilingCountsGroupsRatherThanSyntheticRoot()
    {
        GrammarTests.Compile("(?:a)", "u", new(maxNestingDepth: 1));
        GrammarTests.Reject("(?:(?:a))", "u", RegexDiagnosticCode.NestingLimitExceeded, new(maxNestingDepth: 1));
        GrammarTests.Compile("[a]", "v", new(maxNestingDepth: 1));
        GrammarTests.Reject("[[a]]", "v", RegexDiagnosticCode.NestingLimitExceeded, new(maxNestingDepth: 1));
        foreach (var flags in new[] { "", "u", "v" })
        {
            GrammarTests.Compile("(?:[a])", flags, new(maxNestingDepth: 2));
            GrammarTests.Reject("(?:(?:[a]))", flags, RegexDiagnosticCode.NestingLimitExceeded, new(maxNestingDepth: 2));
        }
    }
    [Fact]
    public void NativeSourceSpansAndFlagDomainsHavePreciseWitnesses()
    {
        var p = GrammarTests.Compile("😀[a-z](?<x>b)\\k<x>", "u");
        var nodes = p.Nodes.ToArray();
        Assert.Equal(new GregsEx.IR.SourceRange(0, 2, RegexOffsetUnit.Utf16CodeUnits), nodes.Single(n => n.Kind == EcmaNodeKind.Literal && n.Value == 0x1F600).Source);
        Assert.Equal(new GregsEx.IR.SourceRange(2, 5, RegexOffsetUnit.Utf16CodeUnits), nodes.Single(n => n.Kind == EcmaNodeKind.Set).Source);
        Assert.Equal(new GregsEx.IR.SourceRange(7, 7, RegexOffsetUnit.Utf16CodeUnits), nodes.Single(n => n.Kind == EcmaNodeKind.Capture).Source);
        Assert.Equal(new GregsEx.IR.SourceRange(14, 5, RegexOffsetUnit.Utf16CodeUnits), nodes.Single(n => n.Kind == EcmaNodeKind.Backreference).Source);
        Assert.Equal(2, GrammarTests.Reject("😀\\a", "u").Diagnostic.Offset);
        foreach (var (flags, offset) in new[] { ("uv", 1), ("vu", 1), ("gg", 1), ("usgdiymvd", 7), ("u?", 1) })
        {
            Assert.False(EcmaScriptProfile.TryParseFlags(flags, out _, out var error));
            Assert.Equal(offset, error.Diagnostic.Offset);
            Assert.Equal(1, error.Diagnostic.Length);
            Assert.Equal(EcmaScriptDiagnosticLocation.Flags, error.Location);
            Assert.Equal("ES-001", error.FeatureId);
        }
    }
    [Fact]
    public void LiteralPatternCannotEscapeAndContainsNoHostUnicodeNormalization()
    {
        var p = GrammarTests.Compile("a\u0301", "u");
        Assert.Equal(2, p.Nodes.ToArray().Count(n => n.Kind == EcmaNodeKind.Literal));
        Assert.Equal(0x0301, p.Nodes[1].Value);
        p = GrammarTests.Compile("\0", "u");
        Assert.Equal(0, p.Nodes[0].Value);
        p = GrammarTests.Compile("[\0]", "v");
        Assert.True(SetTests.Contains(p, 0, 0));
    }
    [Fact]
    public void ParserCompilationReportsBoundedOwnedCosts()
    {
        var compiler = new EcmaCompiler();
        var profile = GrammarTests.Profile("vi");
        var limits = new RegexCompileLimits();
        var unicode = ContractUnicode.Instance;
        const string pattern = @"(?<x>[a-z]+)|(?<x>[\q{ab|xyz}&&\q{AB|xyz}])\k<x>";
        for (var i = 0; i < 20; i++)
        { var warm = new CompileBudget(limits); Assert.True(compiler.TryCompile(pattern, profile, unicode, ref warm, out _, out _)); }
        const int iterations = 100;
        long retained = 0, peak = 0, work = 0;
        var succeeded = true;
        for (var i = 0; i < iterations; i++)
        {
            var budget = new CompileBudget(limits);
            succeeded &= compiler.TryCompile(pattern, profile, unicode, ref budget, out var p, out _);
            retained += p?.RetainedBytes ?? 0;
            peak += budget.PeakBytes;
            work += budget.WorkConsumed;
        }
        Assert.True(succeeded);
        Assert.True(peak > retained);
        _output.WriteLine($"Cold owned parser compilation: {iterations} prepared positive cycles, conservative retained/compile {retained / iterations}; peak/compile {peak / iterations}; work/compile {work / iterations}. No zero-allocation or speed claim.");
    }
}
