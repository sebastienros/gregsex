using System.Runtime.CompilerServices;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Contracts.EcmaScript;

public class ContractTests
{
    [Theory]
    [InlineData(EcmaScriptFlags.None, false)]
    [InlineData(EcmaScriptFlags.Unicode, true)]
    [InlineData(EcmaScriptFlags.UnicodeSets, true)]
    public void ProfileHasExplicitDomainAndData(EcmaScriptFlags flags, bool unicode)
    {
        var profile = new EcmaScriptProfile(flags);
        Assert.Equal("EcmaScript2025", profile.Id);
        Assert.Equal("Unicode-16.0.0+Emoji-16.0", profile.DataId);
        Assert.Equal(unicode, profile.IsUnicodeAware);
        Assert.Equal(!unicode, profile.UsesAnnexB);
        Assert.True(profile.Supports(RegexInputKind.Utf16));
        Assert.False(profile.Supports(RegexInputKind.Utf8));
        Assert.False(profile.Supports(RegexInputKind.Bytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptProfile((EcmaScriptFlags)256));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptProfile(EcmaScriptFlags.Unicode | EcmaScriptFlags.UnicodeSets));
    }

    [Fact]
    public void ContractEncapsulationPreservesTheDeliveredCore()
    {
        var assembly = typeof(EcmaScriptProfile).Assembly;
        Assert.DoesNotContain(assembly.GetExportedTypes(), type => type.Namespace?.StartsWith("GregsEx.Contracts", StringComparison.Ordinal) == true);
        // RegularCore remains usable; a profile descriptor isn't evidence of its engine.
        Assert.True(RegexPattern.Parse("a", new RegularCoreProfile()).IsMatch("ba"));
    }

    [Theory]
    [InlineData("dgimsuy", true, -1)]
    [InlineData("dgimsvy", true, -1)]
    [InlineData("uv", false, 1)]
    [InlineData("gg", false, 1)]
    [InlineData("x", false, 0)]
    [InlineData("\ud800", false, 0)]
    public void FlagFactoryKeepsTheOriginalSourceDomain(string flags, bool admitted, int offset)
    {
        Assert.Equal(admitted, EcmaScriptProfile.TryParseFlags(flags, out var profile, out var diagnostic));
        if (admitted)
        { Assert.NotNull(profile); Assert.Equal(default, diagnostic); }
        else
        {
            Assert.Null(profile);
            Assert.Equal(EcmaScriptDiagnosticLocation.Flags, diagnostic.Location);
            Assert.Equal("ES-001", diagnostic.FeatureId);
            Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, offset, 1, RegexOffsetUnit.Utf16CodeUnits), diagnostic.Diagnostic);
        }
    }

    [Fact]
    public void AllocatingHostLimitsAndFailuresHaveFiniteDistinctContracts()
    {
        var limits = new EcmaScriptHostLimits();
        Assert.Equal(1_000_000, limits.MaxResults);
        Assert.Equal(16_777_216, limits.MaxOutputUnits);
        Assert.Equal(67_108_864, limits.MaxMemoryBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptHostLimits(maxResults: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptHostLimits(maxOutputUnits: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EcmaScriptHostLimits(maxMemoryBytes: 0));
        var failure = new EcmaScriptHostFailure(EcmaScriptHostFailureCode.OutputLimitExceeded, 3, 12);
        Assert.Equal(failure, new EcmaScriptHostException(failure).Failure);
        Assert.Throws<ArgumentException>(() => new EcmaScriptHostException(default));
    }

    [Fact]
    public void RuntimeCaseTablesAreDirectImmutableViews()
    {
        EcmaCaseMapping[] unicode = [new(0x212A, 'k')];
        var budget = new CompileBudget(new());
        Assert.True(EcmaCaseTables.TryCreate(unicode, [new('a', 'A')], ref budget, out var tables, out _));
        unicode[0] = new(0x212A, 'x');
        Assert.Equal('k', tables!.UnicodeFold[0].Target);
        Assert.Equal('A', tables.LegacyUppercase[0].Target);
        Assert.Equal(tables.RetainedBytes, budget.ReservedBytes);
        var broken = new CompileBudget(new());
        Assert.False(EcmaCaseTables.TryCreate([], [new(0x212A, 'K')], ref broken, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    [Fact]
    public void ImmutableAdmissionPreservesLoneSurrogatesAndCopies()
    {
        var nodes = new[] { Leaf(EcmaNodeKind.Literal, 0xD800) };
        var budget = new CompileBudget(new());
        Assert.True(Create(nodes, ref budget, out var plan, out var diagnostic));
        nodes[0] = Leaf(EcmaNodeKind.Literal, 'a');
        Assert.Equal(0xD800, plan!.Nodes[0].Value);
        Assert.Equal(default, diagnostic);
        Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
        Assert.True(budget.WorkConsumed > 0);
    }

    [Theory]
    [InlineData(0)] // forward/cyclic child
    [InlineData(1)] // nonzero unused operand
    [InlineData(2)] // forged capture count
    [InlineData(3)] // out-of-source span
    [InlineData(4)] // host flag used as scoped matching flag
    [InlineData(5)] // supplementary literal in legacy
    public void InvalidPlanShapesCannotCrossTheBoundary(int mutation)
    {
        var n = Leaf(EcmaNodeKind.Empty);
        n = mutation switch
        {
            0 => n with { Kind = EcmaNodeKind.Repeat, X = 0, Max = -1 },
            1 => n with { Value = 1 },
            2 => n with { FirstCapture = 1, CaptureCount = 1 },
            3 => n with { Source = new(1, 1, RegexOffsetUnit.Utf16CodeUnits) },
            4 => n with { EffectiveFlags = EcmaScriptFlags.Global },
            _ => Leaf(EcmaNodeKind.Literal, 0x1F600)
        };
        var budget = new CompileBudget(new());
        Assert.False(Create([n], ref budget, out var plan, out var diagnostic));
        Assert.Null(plan);
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    [Fact]
    public void NestedCaptureRangesAndNamedAliasesAreValidated()
    {
        var leaf = Leaf(EcmaNodeKind.Literal, 'a');
        var capture = Leaf(EcmaNodeKind.Capture, 1) with { X = 0, NameIndex = 0, FirstCapture = 1, CaptureCount = 1 };
        var repeat = Leaf(EcmaNodeKind.Repeat) with { X = 1, Min = 0, Max = -1, FirstCapture = 1, CaptureCount = 1 };
        var budget = new CompileBudget(new());
        Assert.True(EcmaProgram.TryCreate(new(), 2, 2, 1, [leaf, capture, repeat], [], [], [], [],
            [new("x", 0, 1)], [1], ref budget, out var plan, out _));
        Assert.Equal(1, plan!.Nodes[2].CaptureCount);
        var broken = new CompileBudget(new());
        Assert.False(EcmaProgram.TryCreate(new(), 2, 2, 1, [leaf, capture, repeat], [], [], [], [],
            [new("x", 0, 1)], [0], ref broken, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    [Fact]
    public void StringSetsOwnOrderedFiniteCodePointStorage()
    {
        int[] points = ['a', 'b', 'a'];
        var budget = new CompileBudget(new());
        Assert.True(EcmaProgram.TryCreate(new(EcmaScriptFlags.UnicodeSets), 0, 1, 1,
            [Leaf(EcmaNodeKind.Set)], [new(0, 1, 0, 2)], [new(0xD800, 0xDFFF)],
            [new(0, 2), new(2, 1)], points, [], [], ref budget, out var plan, out _));
        points[0] = 'z';
        Assert.Equal('a', plan!.StringCodePoints[0]);
        Assert.Equal(2, plan.Strings[0].Length);
        var broken = new CompileBudget(new());
        Assert.False(EcmaProgram.TryCreate(new(EcmaScriptFlags.UnicodeSets), 0, 1, 1,
            [Leaf(EcmaNodeKind.Set)], [new(0, 0, 0, 2)], [],
            [new(0, 1), new(0, 1)], ['a'], [], [], ref broken, out _, out var diagnostic));
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
    }

    [Theory]
    [InlineData(0, RegexDiagnosticCode.PlanLimitExceeded)]
    [InlineData(1, RegexDiagnosticCode.CompileMemoryLimitExceeded)]
    [InlineData(2, RegexDiagnosticCode.CompileWorkLimitExceeded)]
    public void BudgetFailureDoesNotPublishAPlan(int kind, RegexDiagnosticCode expected)
    {
        var limits = kind switch
        {
            0 => new RegexCompileLimits(maxPlanBytes: 1),
            1 => new RegexCompileLimits(maxCompileMemoryBytes: 1),
            _ => new RegexCompileLimits(maxCompileWork: 1)
        };
        var budget = new CompileBudget(limits);
        Assert.False(Create([Leaf(EcmaNodeKind.Literal, 'a')], ref budget, out var plan, out var diagnostic));
        Assert.Null(plan);
        Assert.Equal(expected, diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void FixtureInterchangePreservesNativeUnitsAndHostState()
    {
        var json = File.ReadAllText(Path.Combine(SourceDirectory(), "ContractVectors.json"));
        var options = EcmaCorpus.JsonOptions;
        var corpus = EcmaCorpus.Read(json);
        Assert.Equal("ecmascript-fixtures-1", corpus.ContractVersion);
        Assert.Equal(new EcmaScriptProfile().DataId, corpus.DataId);
        Assert.Equal(corpus.Cases.Length, corpus.Cases.Select(c => c.Id).Distinct().Count());
        foreach (var c in corpus.Cases)
        {
            Assert.All(c.PatternUnits.Concat(c.InputUnits).Concat(c.ReplacementUnits ?? []), u => Assert.InRange(u, 0, 65535));
            Assert.Equal("Specification", c.Provenance.Kind);
            Assert.Null(c.Provenance.OracleId);
            Assert.NotEmpty(c.Capabilities);
            foreach (var m in c.Expected.Matches)
            {
                Assert.InRange(m.Index, 0, c.InputUnits.Length);
                Assert.InRange(m.Length, 0, c.InputUnits.Length - m.Index);
                Assert.NotEmpty(m.Captures);
                Assert.Equal(new EcmaCapture(true, m.Index, m.Length), m.Captures[0]);
                foreach (var cap in m.Captures)
                {
                    if (!cap.Participated)
                    {
                        Assert.Equal(new(false, 0, 0), cap);
                    }
                }
            }
        }
        Assert.Contains(corpus.Cases, c => c.InputUnits.Contains(0xD800));
        Assert.Contains(corpus.Cases, c => c.Operation == "Exec" && c.LastIndexBefore == "1" && c.Expected.Matches[0].Index == 0);
        Assert.Contains(corpus.Cases, c => c.Operation == "Split" && c.Expected.Texts!.Contains(null));
        var roundTrip = EcmaCorpus.Read(JsonSerializer.Serialize(corpus, options));
        Assert.Equal(corpus.Cases[0].PatternUnits, roundTrip.Cases[0].PatternUnits);
        Assert.Throws<JsonException>(() => EcmaCorpus.Read(json.Replace("\"contractVersion\"", "\"unknownVersion\"")));
        Assert.Throws<JsonException>(() => EcmaCorpus.Read(json.Replace("\"replacementUnits\": null,", "")));
        Assert.Throws<InvalidDataException>(() => EcmaCorpus.Read(json.Replace("ecmascript-fixtures-1", "ecmascript-fixtures-2")));
        var badUnits = corpus with { Cases = [corpus.Cases[0] with { InputUnits = [65536] }] };
        Assert.Throws<InvalidDataException>(() => EcmaCorpus.Read(JsonSerializer.Serialize(badUnits, options)));
        Assert.Throws<InvalidDataException>(() => EcmaCorpus.Read(json.Replace("\"matches\": []", "\"matches\": null")));
    }

    private static EcmaNode Leaf(EcmaNodeKind kind, int value = 0)
        => new(kind, -1, -1, value, -1, 0, 0, false, EcmaScriptFlags.None, 0, 0,
            new(0, 1, RegexOffsetUnit.Utf16CodeUnits));
    private static bool Create(ReadOnlySpan<EcmaNode> nodes, ref CompileBudget budget,
        out EcmaProgram? plan, out RegexDiagnostic diagnostic)
        => EcmaProgram.TryCreate(new(), nodes.Length - 1, 1, 1, nodes, [], [], [], [], [], [], ref budget, out plan, out diagnostic);
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
