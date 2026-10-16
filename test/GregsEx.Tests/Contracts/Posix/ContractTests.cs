using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using Xunit;

namespace GregsEx.Tests.Contracts.Posix;

public class ContractTests
{
    [Fact]
    public void ClosedNamedProfilesCannotSilentlyAcquireLocaleOrUnicodeSemantics()
    {
        var basic = new PosixProfile();
        var extended = new PosixProfile(PosixDialect.Extended);
        Assert.Equal("PosixBre2018-CByte", basic.Id);
        Assert.Equal("PosixEre2018-CByte", extended.Id);
        Assert.Equal(PosixEdition.Issue7_2018, basic.Edition);
        Assert.Equal("GregsEx.Posix.C.Byte-v1", basic.DataId);
        Assert.True(basic.Supports(RegexInputKind.Bytes));
        Assert.False(basic.Supports(RegexInputKind.Utf8));
        Assert.False(basic.Supports(RegexInputKind.Utf16));
        Assert.False(basic.Supports((RegexInputKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosixProfile((PosixDialect)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosixProfile(flags: (PosixCompileFlags)8));
        Assert.True(PosixLocale.TryGet("POSIX", out var locale, out var error));
        Assert.Same(PosixLocale.C, locale);
        Assert.Equal(default, error);
        Assert.True(PosixLocale.TryGet("C", out var second, out _));
        Assert.Same(locale, second);
        foreach (var name in new[] { "", "c", "C.UTF-8", "en_US.UTF-8" })
        {
            Assert.False(PosixLocale.TryGet(name, out var unsupported, out var failure));
            Assert.Null(unsupported);
            Assert.Equal(RegexDiagnosticCode.UnsupportedFeature, failure.Diagnostic.Code);
            Assert.Equal(PosixDiagnosticLocation.Locale, failure.Location);
            Assert.Equal(PosixSyntaxDisposition.ProfileExclusion, failure.Disposition);
            Assert.Equal(new RegexDiagnostic(RegexDiagnosticCode.UnsupportedFeature, 0, name.Length,
                RegexOffsetUnit.Utf16CodeUnits), failure.Diagnostic);
        }
    }

    [Fact]
    public void TableDigestPinsEveryByteClassCaseAndCollationRow()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceDirectory(), "LocaleTable.json")));
        var rows = json.RootElement.GetProperty("rows");
        Assert.Equal(256, rows.GetArrayLength());
        var bytes = new byte[1280];
        for (var b = 0; b < 256; b++)
        {
            var r = rows[b];
            var mask = r[1].GetInt32();
            Assert.Equal(b, r[0].GetInt32());
            Assert.Equal(b, r[3].GetInt32());
            Assert.Equal(b is >= 65 and <= 90 ? b + 32 : b, r[2].GetInt32());
            if (b >= 128)
            {
                Assert.Equal(0, mask);
            }

            bytes[5 * b] = (byte)b;
            bytes[5 * b + 1] = (byte)mask;
            bytes[5 * b + 2] = (byte)(mask >> 8);
            bytes[5 * b + 3] = (byte)r[2].GetInt32();
            bytes[5 * b + 4] = (byte)b;
        }
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(PosixLocale.C.DataDigest, digest);
        Assert.Equal(digest, json.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(12, json.RootElement.GetProperty("classOrder").GetArrayLength());
    }

    [Fact]
    public void ProgramAdmissionOwnsCopiesAndPreservesByteLocations()
    {
        var nodes = new[] { Leaf(PosixNodeKind.Class) };
        var sets = new[] { new PosixByteSet(2, 0, 0, 0) };
        var parents = new[] { -1 };
        var budget = new CompileBudget(new());
        Assert.True(Create(nodes, ref budget, out var program, out _, sets: sets, parents: parents));
        nodes[0] = Leaf(PosixNodeKind.Literal, 'x');
        sets[0] = default;
        parents[0] = 42;
        Assert.Equal(PosixNodeKind.Class, program!.Nodes[0].Kind);
        Assert.True(program.Sets[0].Contains(1));
        Assert.Equal(-1, program.GroupParents[0]);
        Assert.Equal(RegexOffsetUnit.Bytes, program.PatternUnit);
        Assert.Equal(program.RetainedBytes, budget.ReservedBytes);
        Assert.True(budget.PeakBytes > budget.ReservedBytes);
        Assert.False(program.HasBackreferences);
    }

    [Theory]
    [InlineData(0)] // NUL literal
    [InlineData(1)] // high nonbyte literal
    [InlineData(2)] // cycle
    [InlineData(3)] // orphan
    [InlineData(4)] // unused operand
    [InlineData(5)] // source overrun
    [InlineData(6)] // source unit mismatch
    [InlineData(7)] // wrong nonrepeat payload
    [InlineData(8)] // invalid repeat bound
    [InlineData(9)] // unknown class
    [InlineData(10)] // reversed branch source order
    [InlineData(11)] // shared child
    [InlineData(12)] // BRE alternation
    public void MalformedPlansCannotReachExecutor(int mutation)
    {
        var leaf = Leaf(PosixNodeKind.Literal, 'a');
        PosixNode[] nodes = mutation switch
        {
            0 => [leaf with { Value = 0 }],
            1 => [leaf with { Value = 256 }],
            2 => [Leaf(PosixNodeKind.Repeat) with { X = 0, Max = -1 }],
            3 => [leaf, leaf],
            4 => [leaf with { Y = 0 }],
            5 => [leaf with { Source = new(1, 1, RegexOffsetUnit.Bytes) }],
            6 => [leaf with { Source = new(0, 1, RegexOffsetUnit.Utf16CodeUnits) }],
            7 => [leaf with { Min = 1 }],
            8 => [leaf, Leaf(PosixNodeKind.Repeat) with { X = 0, Min = 0, Max = 256 }],
            9 => [Leaf(PosixNodeKind.Class, 1)],
            10 => [leaf, leaf, Leaf(PosixNodeKind.Sequence) with { X = 1, Y = 0 }],
            11 => [leaf, Leaf(PosixNodeKind.Sequence) with { X = 0, Y = 0 }],
            _ => [leaf, leaf, Leaf(PosixNodeKind.Alternative) with { X = 0, Y = 1 }]
        };
        var budget = new CompileBudget(new());
        Assert.False(Create(nodes, ref budget, out var program, out var diagnostic));
        Assert.Null(program);
        Assert.Equal(RegexDiagnosticCode.InvalidProgram, diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void NestedCapturesAndClosedBasicReferencesAreStructuralObligations()
    {
        // \(a\(b\)\)\1, original byte source coordinates.
        PosixNode[] nodes = [Leaf(PosixNodeKind.Literal, 'a', 2), Leaf(PosixNodeKind.Literal, 'b', 5),
            Leaf(PosixNodeKind.Capture, 2, 3, 5) with { X = 1 },
            Leaf(PosixNodeKind.Sequence, 0, 2, 6) with { X = 0, Y = 2 },
            Leaf(PosixNodeKind.Capture, 1, 0, 10) with { X = 3 },
            Leaf(PosixNodeKind.Backreference, 1, 10, 2),
            Leaf(PosixNodeKind.Sequence, 0, 0, 12) with { X = 4, Y = 5 }];
        var budget = new CompileBudget(new());
        Assert.True(Create(nodes, ref budget, out var p, out _, length: 12, parents: [-1, 0, 1]));
        Assert.True(p!.HasBackreferences);
        Assert.Equal(1, p.GroupParents[2]);
        var wrongParent = new CompileBudget(new());
        Assert.False(Create(nodes, ref wrongParent, out _, out _, length: 12, parents: [-1, 0, 0]));
        var ere = new CompileBudget(new());
        Assert.False(Create(nodes, ref ere, out _, out _, length: 12, parents: [-1, 0, 1],
            dialect: PosixDialect.Extended));
        nodes[5] = nodes[5] with { Source = new(0, 2, RegexOffsetUnit.Bytes) };
        var forward = new CompileBudget(new());
        Assert.False(Create(nodes, ref forward, out _, out _, length: 12, parents: [-1, 0, 1]));
    }

    [Theory]
    [InlineData(0, RegexDiagnosticCode.PlanLimitExceeded)]
    [InlineData(1, RegexDiagnosticCode.CompileMemoryLimitExceeded)]
    [InlineData(2, RegexDiagnosticCode.CompileWorkLimitExceeded)]
    [InlineData(3, RegexDiagnosticCode.NestingLimitExceeded)]
    [InlineData(4, RegexDiagnosticCode.ProgramLimitExceeded)]
    [InlineData(5, RegexDiagnosticCode.PatternLimitExceeded)]
    public void AllAdmissionCeilingsFailBeforePublishing(int kind, RegexDiagnosticCode expected)
    {
        var limits = kind switch
        {
            0 => new RegexCompileLimits(maxPlanBytes: 1),
            1 => new RegexCompileLimits(maxCompileMemoryBytes: 1),
            2 => new RegexCompileLimits(maxCompileWork: 1),
            3 => new RegexCompileLimits(maxNestingDepth: 1),
            4 => new RegexCompileLimits(maxInstructions: 1),
            _ => new RegexCompileLimits(maxPatternUnits: 1)
        };
        var budget = new CompileBudget(limits);
        Assert.False(Create([Leaf(PosixNodeKind.Literal, 'a'),
            Leaf(PosixNodeKind.Repeat, length: 2) with { X = 0, Max = -1 }], ref budget,
            out var p, out var error, length: 2));
        Assert.Null(p);
        Assert.Equal(expected, error.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void SetAlphabetAndLogicalHistoryLimitsCannotUseSpareStorage()
    {
        var budget = new CompileBudget(new());
        Assert.False(Create([Leaf(PosixNodeKind.Class)], ref budget, out _, out _, sets: [new(1, 0, 0, 0)]));
        var classes = new CompileBudget(new(maxClassRanges: 1));
        Assert.False(Create([Leaf(PosixNodeKind.Class)], ref classes, out _, out var error, sets: [default, default]));
        Assert.Equal(RegexDiagnosticCode.ClassLimitExceeded, error.Code);
        Assert.False(default(PosixExecutionCapacity).IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosixExecutionCapacity(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PosixExecutionCapacity(1, 0));
        Assert.Equal(4096, PosixExecutionCapacity.Default.Frames);
        Assert.Equal(65536, PosixExecutionCapacity.Default.HistoryEvents);
        Assert.Throws<ArgumentException>(() => new PosixRegexException(default));
        Assert.Equal(0, default(PosixMatch).Start); // default is an empty endpoint, not absent
        Assert.DoesNotContain(typeof(PosixProfile).Assembly.GetExportedTypes(), t =>
            t.Namespace?.StartsWith("GregsEx.Contracts", StringComparison.Ordinal) == true);
    }

    private static PosixNode Leaf(PosixNodeKind kind, int value = 0, int offset = 0, int length = 1)
        => new(kind, -1, -1, value, 0, 0, new(offset, length, RegexOffsetUnit.Bytes));
    private static bool Create(ReadOnlySpan<PosixNode> nodes, ref CompileBudget budget,
        out PosixProgram? p, out RegexDiagnostic error, int length = 1,
        ReadOnlySpan<PosixByteSet> sets = default, ReadOnlySpan<int> parents = default,
        PosixDialect dialect = PosixDialect.Basic)
        => PosixProgram.TryCreate(new(dialect), length, RegexOffsetUnit.Bytes, nodes, sets,
            parents.IsEmpty ? [-1] : parents, ref budget, out p, out error);
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
