using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Locale;
using GregsEx.Flavors.Posix.Parsing;
using GregsEx.Tests.Contracts.Posix;
using Xunit;

namespace GregsEx.Tests.Flavors.Posix.Parsing;

public class ParserTests
{
    private static string Root([CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../../../../.."));
    private static PosixCorpus Corpus() => PosixCorpus.Read(File.ReadAllText(Path.Combine(Root(), "test/GregsEx.Tests/Contracts/Posix/ContractVectors.json")));
    public static IEnumerable<TheoryDataRow<string>> Seeds() => Corpus().Cases.Where(c => c.Operation != "RegError").Select(c => new TheoryDataRow<string>(c.Id));
    [Theory]
    [MemberData(nameof(Seeds))]
    public void FrozenSeedGrammarAdmissionAndExactDiagnostics(string id)
    {
        var c = Corpus().Cases.Single(c => c.Id == id);
        var expected = c.Permitted[0];
        if (!PosixLocale.TryGet(c.Locale, out var locale, out var diagnostic))
        { AssertError(expected.Error!, diagnostic); return; }
        var limits = c.Limits;
        var budget = new CompileBudget(new(maxPatternUnits: limits?.MaxPatternUnits ?? 65536,
            maxCompileWork: limits?.MaxCompileWork ?? 10000000,
            maxPlanBytes: limits?.MaxPlanBytes ?? 67108864,
            maxCompileMemoryBytes: limits?.MaxCompileMemoryBytes ?? 67108864));
        var profile = new PosixProfile(c.ProfileId.Contains("Bre", StringComparison.Ordinal) ? PosixDialect.Basic : PosixDialect.Extended,
            (PosixCompileFlags)c.CompileFlags, locale);
        var compiler = new PosixCompiler();
        var inputKind = Enum.Parse<RegexInputKind>(c.InputKind);
        var success = c.PatternKind == "Bytes" ? compiler.TryCompile(c.PatternUnits.Select(i => (byte)i).ToArray(), profile, inputKind,
            CByteLocale.Instance, ref budget, out var program, out diagnostic) : compiler.TryCompile(c.PatternUnits.Select(i => (char)i).ToArray(), profile,
            inputKind, CByteLocale.Instance, ref budget, out program, out diagnostic);
        var compileFailure = expected.Error?.Location is "Pattern" or "Options" && expected.Status is not "OperationRejected";
        Assert.Equal(!compileFailure, success);
        if (compileFailure)
        { Assert.Null(program); AssertError(expected.Error!, diagnostic); Assert.Equal(0, budget.ReservedBytes); }
        else
        {
            Assert.NotNull(program);
            Assert.Equal(default, diagnostic);
            if (expected.GroupCount is { } count)
            {
                Assert.Equal(count, program.GroupCount);
            }

            Assert.Equal(program.RetainedBytes, budget.ReservedBytes);
        }
    }
    private static void AssertError(PosixFixtureError error, PosixRegexDiagnostic d)
    {
        Assert.Equal(error.Code, d.Diagnostic.Code.ToString());
        Assert.Equal(error.PosixCode, d.ErrorCode.ToString());
        Assert.Equal(error.Disposition, d.Disposition.ToString());
        Assert.Equal(error.Location, d.Location.ToString());
        Assert.Equal(error.Offset, d.Diagnostic.Offset);
        Assert.Equal(error.Length, d.Diagnostic.Length);
        Assert.Equal(error.OffsetUnit, d.Diagnostic.LocationUnit.ToString());
    }
    [Fact]
    public void AllFrozenLocaleRowsAndSerializedDigestAreExact()
    {
        var data = CByteLocale.Instance;
        using var table = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "test/GregsEx.Tests/Contracts/Posix/LocaleTable.json")));
        var rows = table.RootElement.GetProperty("rows");
        Assert.Equal(256, rows.GetArrayLength());
        var serialized = new byte[1280];
        for (var b = 0; b < 256; b++)
        {
            var row = rows[b];
            Assert.Equal(b, row[0].GetInt32());
            Assert.Equal(row[1].GetInt32(), data.ClassMasks[b]);
            Assert.Equal(row[2].GetInt32(), data.FoldToLower[b]);
            Assert.Equal(row[3].GetInt32(), data.CollationOrdinals[b]);
            serialized[b * 5] = (byte)b;
            BinaryPrimitives.WriteUInt16LittleEndian(serialized.AsSpan(b * 5 + 1), data.ClassMasks[b]);
            serialized[b * 5 + 3] = data.FoldToLower[b];
            serialized[b * 5 + 4] = data.CollationOrdinals[b];
        }
        Assert.Equal(data.Identity.DataDigest, Convert.ToHexStringLower(SHA256.HashData(serialized)));
        Assert.True(data.TryGetCollatingElement([255], out var high));
        Assert.Equal(255, high);
        Assert.False(data.TryGetCollatingElement([0], out _));
        Assert.False(data.TryGetCollatingElement("ch"u8, out _));
        Assert.False(data.TryGetClass("ALPHA"u8, out _));
        Assert.False(data.TryGetClass("ascii"u8, out _));
    }
    [Theory]
    [InlineData("a", PosixCompileFlags.IgnoreCase, "aA")]
    [InlineData("[a-c]", PosixCompileFlags.IgnoreCase, "abcABC")]
    [InlineData("[[:upper:]]", PosixCompileFlags.IgnoreCase, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz")]
    [InlineData("[[.a.]]", PosixCompileFlags.None, "a")]
    [InlineData("[[=a=]]", PosixCompileFlags.None, "a")]
    [InlineData("[--@]", PosixCompileFlags.None, "-./0123456789:;<=>?@")]
    [InlineData("[%--]", PosixCompileFlags.None, "%&'()*+,-")]
    [InlineData("[\\]", PosixCompileFlags.None, "\\")]
    [InlineData("[]a]", PosixCompileFlags.None, "]a")]
    public void PositiveSetsRetainExactlyAdmittedBytes(string expression, PosixCompileFlags flags, string members)
    {
        var program = Parse(expression, flags: flags);
        Assert.Single(program.Sets.ToArray());
        for (var b = 0; b < 256; b++)
        {
            Assert.Equal(members.Contains((char)b), program.Sets[0].Contains((byte)b));
        }
    }
    [Fact]
    public void ComplementCaseAndNewlineOrderAreIndependentOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var p = Parse("[^a]", flags: PosixCompileFlags.IgnoreCase | PosixCompileFlags.Newline);
            for (var b = 0; b < 256; b++)
            {
                Assert.Equal(b is not (0 or 10 or 65 or 97), p.Sets[0].Contains((byte)b));
            }

            var positive = Parse("[[:space:]]", flags: PosixCompileFlags.Newline);
            Assert.True(positive.Sets[0].Contains(10));
            Assert.Equal(PosixNodeKind.Any, Parse(".", flags: PosixCompileFlags.Newline).Nodes[0].Kind);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [Fact]
    public void ContextualAnchorsEscapesAndBalancedOwnedTreePreserveNativeSources()
    {
        var p = Parse("\\(^a$\\)a^$", PosixDialect.Basic);
        Assert.Equal(2, p.GroupCount);
        Assert.Equal(new[] { -1, 0 }, p.GroupParents.ToArray());
        Assert.Equal(1, p.Nodes.ToArray().Count(n => n.Kind == PosixNodeKind.Start));
        Assert.Equal(2, p.Nodes.ToArray().Count(n => n.Kind == PosixNodeKind.End));
        Assert.Contains(p.Nodes.ToArray(), n => n.Kind == PosixNodeKind.Literal && n.Value == '^');
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, p.PatternUnit);
        Assert.Equal(511, Parse(new string('a', 256)).Nodes.Length);
        var nested = Parse("(a(b)c)");
        Assert.Equal(new[] { -1, 0, 1 }, nested.GroupParents.ToArray());
        Assert.Equal(new GregsEx.IR.SourceRange(0, 7, RegexOffsetUnit.Utf16CodeUnits), nested.Nodes[nested.Root].Source);
        Assert.Equal(PosixNodeKind.Literal, Parse("a)").Nodes[1].Kind);
        Assert.Equal(255, Parse("a{000255}").Nodes[1].Max);
        Assert.Equal(-1, Parse("a{0,}").Nodes[1].Max);
        Assert.Equal(PosixNodeKind.Backreference, Parse("\\(a\\)\\1", PosixDialect.Basic).Nodes[2].Kind);
    }
    [Theory]
    [InlineData("\\(\\1\\)", PosixDialect.Basic, PosixErrorCode.InvalidBackreference, PosixSyntaxDisposition.Invalid)]
    [InlineData("\\(a\\)\\9", PosixDialect.Basic, PosixErrorCode.InvalidBackreference, PosixSyntaxDisposition.Invalid)]
    [InlineData("\\(\\)", PosixDialect.Basic, PosixErrorCode.BadPattern, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("[[:unknown:]]", PosixDialect.Extended, PosixErrorCode.InvalidCharacterClass, PosixSyntaxDisposition.Invalid)]
    [InlineData("[[..]]", PosixDialect.Extended, PosixErrorCode.InvalidCollatingElement, PosixSyntaxDisposition.Invalid)]
    [InlineData("[a-[=b=]]", PosixDialect.Extended, PosixErrorCode.InvalidRange, PosixSyntaxDisposition.UnspecifiedRejected)]
    [InlineData("[a-[:alpha:]]", PosixDialect.Extended, PosixErrorCode.InvalidRange, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("a{99999999999999999999999999}", PosixDialect.Extended, PosixErrorCode.BadInterval, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("a{,2}", PosixDialect.Extended, PosixErrorCode.BadInterval, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("a{1,2", PosixDialect.Extended, PosixErrorCode.UnbalancedBrace, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("(?=a)", PosixDialect.Extended, PosixErrorCode.BadPattern, PosixSyntaxDisposition.ExtensionRejected)]
    [InlineData("(?:a)", PosixDialect.Extended, PosixErrorCode.BadPattern, PosixSyntaxDisposition.ExtensionRejected)]
    [InlineData("[[...]x.]", PosixDialect.Extended, PosixErrorCode.BadPattern, PosixSyntaxDisposition.UnspecifiedRejected)]
    [InlineData("a\\b", PosixDialect.Extended, PosixErrorCode.BadPattern, PosixSyntaxDisposition.ExtensionRejected)]
    [InlineData("(a", PosixDialect.Extended, PosixErrorCode.UnbalancedParenthesis, PosixSyntaxDisposition.UndefinedRejected)]
    [InlineData("[a", PosixDialect.Extended, PosixErrorCode.UnbalancedBracket, PosixSyntaxDisposition.UndefinedRejected)]
    public void StrictRejectionsPreserveClassification(string text, PosixDialect dialect, PosixErrorCode code, PosixSyntaxDisposition disposition)
    {
        var budget = new CompileBudget(new());
        Assert.False(new PosixCompiler().TryCompile(text, new(dialect), RegexInputKind.Bytes, CByteLocale.Instance,
            ref budget, out var program, out var d));
        Assert.Null(program);
        Assert.Equal(code, d.ErrorCode);
        Assert.Equal(disposition, d.Disposition);
        Assert.Equal(disposition == PosixSyntaxDisposition.ExtensionRejected ? RegexDiagnosticCode.UnsupportedFeature : RegexDiagnosticCode.InvalidSyntax, d.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void DomainPrecedenceOriginalCharCoordinatesAndBuffersAreOwned()
    {
        var compiler = new PosixCompiler();
        var budget = new CompileBudget(new());
        Assert.False(compiler.TryCompile("[a\u0100", new(PosixDialect.Extended), RegexInputKind.Bytes, CByteLocale.Instance,
            ref budget, out _, out var d));
        Assert.Equal(2, d.Diagnostic.Offset);
        Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding, d.Diagnostic.Code);
        budget = new(new());
        byte[] bytes = [97, 255];
        Assert.True(compiler.TryCompile(bytes, new(PosixDialect.Extended), RegexInputKind.Bytes, CByteLocale.Instance, ref budget, out var p, out _));
        bytes[0] = 0;
        Assert.Equal(97, p!.Nodes[0].Value);
        Assert.Equal(255, p.Nodes[1].Value);
        budget = new(new());
        Assert.False(compiler.TryCompile("a\0", new(), RegexInputKind.Utf8, CByteLocale.Instance, ref budget, out _, out d));
        Assert.Equal(PosixDiagnosticLocation.Options, d.Location);
        Assert.Equal(0, budget.WorkConsumed);
    }
    [Fact]
    public void EveryCompilerResourceFailureIsDistinctAndReleasesTemporaryStorage()
    {
        Check("aa", new(maxPatternUnits: 1), RegexDiagnosticCode.PatternLimitExceeded);
        Check("aa", new(maxInstructions: 2), RegexDiagnosticCode.ProgramLimitExceeded);
        Check("[a][b]", new(maxClassRanges: 1), RegexDiagnosticCode.ClassLimitExceeded);
        Check("(a)", new(maxCaptureGroups: 1), RegexDiagnosticCode.CaptureLimitExceeded);
        Check("((a))", new(maxNestingDepth: 2), RegexDiagnosticCode.NestingLimitExceeded);
        Check("a", new(maxCompileWork: 1), RegexDiagnosticCode.CompileWorkLimitExceeded);
        Check("a", new(maxPlanBytes: 1), RegexDiagnosticCode.PlanLimitExceeded);
        Check("a", new(maxCompileMemoryBytes: 1), RegexDiagnosticCode.CompileMemoryLimitExceeded);
        // Sweep failure budgets across domain, buffer creation/growth, tree validation and copy.
        for (var ceiling = 1; ceiling < 5000; ceiling += 31)
        {
            var budget = new CompileBudget(new(maxCompileMemoryBytes: ceiling));
            var ok = new PosixCompiler().TryCompile("(ab|[[:alpha:]]){255}", new(PosixDialect.Extended), RegexInputKind.Bytes,
                CByteLocale.Instance, ref budget, out var p, out var d);
            Assert.True(budget.PeakBytes <= ceiling);
            Assert.Equal(ok ? p!.RetainedBytes : 0, budget.ReservedBytes);
            if (!ok)
            {
                Assert.Equal(RegexDiagnosticCode.CompileMemoryLimitExceeded, d.Diagnostic.Code);
            }
        }
    }
    [Theory]
    [InlineData("a}b]c", PosixDialect.Extended)]
    [InlineData("\\.\\[\\\\\\*\\^\\$\\(\\)\\+\\?\\{\\|", PosixDialect.Extended)]
    [InlineData("\\.\\[\\\\\\*\\^\\$()+?{}|", PosixDialect.Basic)]
    [InlineData("^*a", PosixDialect.Basic)]
    [InlineData("\\(^*a$\\)", PosixDialect.Basic)]
    [InlineData("[][.-.]-0]", PosixDialect.Extended)]
    [InlineData("[[]", PosixDialect.Extended)]
    [InlineData("[[.].]]", PosixDialect.Extended)]
    [InlineData("[^]a-]", PosixDialect.Extended)]
    [InlineData("((a|bc){0,255})+", PosixDialect.Extended)]
    [InlineData("\\(a\\)\\1\\{0,255\\}", PosixDialect.Basic)]
    public void DefinedBoundarySyntaxIsAdmitted(string text, PosixDialect dialect)
        => Assert.NotNull(Parse(text, dialect));
    [Fact]
    public void LongPatternAndDeepGrammarAreFiniteAndKeepTheirLimits()
    {
        var p = Parse(new string('a', 65536));
        Assert.Equal(131071, p.Nodes.Length);
        Check(new string('a', 65537), new(), RegexDiagnosticCode.PatternLimitExceeded);
        p = Parse(new string('(', 255) + "a" + new string(')', 255));
        Assert.Equal(256, p.GroupCount);
        Check(new string('(', 256) + "a" + new string(')', 256), new(), RegexDiagnosticCode.NestingLimitExceeded);
        var budget = new CompileBudget(new(maxCompileWork: 1000));
        Assert.False(new PosixCompiler().TryCompile("[" + new string('a', 4096) + "]", new(PosixDialect.Extended), RegexInputKind.Bytes,
            CByteLocale.Instance, ref budget, out _, out var d));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
    }
    [Fact]
    public void SeededSyntaxMutationsNeverPublishInvalidTreesOrLoseBudgetOwnership()
    {
        var random = new Random(74109);
        const string alphabet = "ab[](){}\\0129*+?|^$.-:=,";
        for (var n = 0; n < 2000; n++)
        {
            var text = new char[random.Next(0, 48)];
            for (var i = 0; i < text.Length; i++)
            {
                text[i] = alphabet[random.Next(alphabet.Length)];
            }

            var budget = new CompileBudget(new());
            var success = new PosixCompiler().TryCompile(text, new((PosixDialect)(n % 2)), RegexInputKind.Bytes,
                CByteLocale.Instance, ref budget, out var p, out var d);
            if (success)
            { Assert.NotNull(p); Assert.Equal(default, d); Assert.Equal(p.RetainedBytes, budget.ReservedBytes); }
            else
            {
                Assert.NotEqual(RegexDiagnosticCode.InvalidProgram, d.Diagnostic.Code);
                Assert.InRange(d.Diagnostic.Offset, 0, text.Length);
                Assert.InRange(d.Diagnostic.Length, 0, text.Length - d.Diagnostic.Offset);
                Assert.Equal(0, budget.ReservedBytes);
            }
        }
    }
    [Fact]
    public void WorkFailureAtEveryParserPhaseReleasesScratchAndPreservesIncomingBudget()
    {
        for (var ceiling = 1; ceiling < 3500; ceiling += 17)
        {
            var budget = new CompileBudget(new(maxCompileWork: ceiling));
            var ok = new PosixCompiler().TryCompile("(ab|[[:alpha:]]){255}", new(PosixDialect.Extended), RegexInputKind.Bytes,
                CByteLocale.Instance, ref budget, out var p, out var d);
            Assert.InRange(budget.WorkConsumed, 0, ceiling);
            Assert.Equal(ok ? p!.RetainedBytes : 0, budget.ReservedBytes);
            if (!ok)
            {
                Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Diagnostic.Code);
            }
        }
        var entry = new CompileBudget(new());
        Assert.True(entry.TryReserveBytes(200, new(0, 1, RegexOffsetUnit.Bytes), out _));
        Assert.True(new PosixCompiler().TryCompile("a", new(PosixDialect.Extended), RegexInputKind.Bytes,
            CByteLocale.Instance, ref entry, out var plan, out _));
        Assert.Equal(200 + plan!.RetainedBytes, entry.ReservedBytes);
    }
    [Fact]
    public void UnmatchedRightParenthesisIsOrdinaryInByteAndCharacterAlternatives()
    {
        var compiler = new PosixCompiler();
        var profile = new PosixProfile(PosixDialect.Extended);
        var byteBudget = new CompileBudget(new());
        var charBudget = new CompileBudget(new());
        Assert.True(compiler.TryCompile("a|)"u8, profile, RegexInputKind.Bytes, CByteLocale.Instance,
            ref byteBudget, out var bytes, out var byteError));
        Assert.True(compiler.TryCompile("a|)", profile, RegexInputKind.Bytes, CByteLocale.Instance,
            ref charBudget, out var chars, out var charError));
        Assert.Equal(default, byteError);
        Assert.Equal(default, charError);
        foreach (var p in new[] { bytes!, chars! })
        {
            Assert.Equal(1, p.GroupCount);
            Assert.Equal(3, p.Nodes.Length);
            Assert.Equal(PosixNodeKind.Alternative, p.Nodes[p.Root].Kind);
            Assert.Equal(PosixNodeKind.Literal, p.Nodes[0].Kind);
            Assert.Equal('a', p.Nodes[0].Value);
            Assert.Equal(PosixNodeKind.Literal, p.Nodes[1].Kind);
            Assert.Equal(')', p.Nodes[1].Value);
        }
        Assert.Equal(RegexOffsetUnit.Bytes, bytes!.PatternUnit);
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, chars!.PatternUnit);
        var invalidBudget = new CompileBudget(new());
        Assert.False(compiler.TryCompile("(a|)"u8, profile, RegexInputKind.Bytes, CByteLocale.Instance,
            ref invalidBudget, out _, out var emptyBranch));
        Assert.Equal(PosixErrorCode.BadPattern, emptyBranch.ErrorCode);
        Assert.Equal(PosixSyntaxDisposition.UndefinedRejected, emptyBranch.Disposition);
        var afterGroup = Parse("(a)|)");
        Assert.Equal(PosixNodeKind.Alternative, afterGroup.Nodes[afterGroup.Root].Kind);
    }
    private static void Check(string text, RegexCompileLimits limits, RegexDiagnosticCode code)
    {
        var budget = new CompileBudget(limits);
        Assert.False(new PosixCompiler().TryCompile(text, new(PosixDialect.Extended), RegexInputKind.Bytes, CByteLocale.Instance,
            ref budget, out _, out var d));
        Assert.Equal(code, d.Diagnostic.Code);
        Assert.Equal(PosixErrorCode.None, d.ErrorCode);
        Assert.Equal(PosixSyntaxDisposition.None, d.Disposition);
        Assert.Equal(0, budget.ReservedBytes);
    }
    private static PosixProgram Parse(string text, PosixDialect dialect = PosixDialect.Extended, PosixCompileFlags flags = PosixCompileFlags.None)
    {
        var budget = new CompileBudget(new());
        Assert.True(new PosixCompiler().TryCompile(text, new(dialect, flags), RegexInputKind.Bytes, CByteLocale.Instance,
            ref budget, out var p, out var d), d.ToString());
        return p!;
    }
}
