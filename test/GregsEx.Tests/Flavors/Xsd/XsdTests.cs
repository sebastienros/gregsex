using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.Xsd;
using GregsEx.Tests.Contracts.Xml;
using Xunit;

namespace GregsEx.Tests.Flavors.Xsd;

public class XsdTests
{
    private static IXmlExecutor Compile(string pattern, RegexInputKind kind = RegexInputKind.Utf16,
        RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.True(XmlUnicodeData.TryCreate(ref budget, out var data, out var diagnostic), diagnostic.ToString());
        Assert.True(new XsdCompiler().TryCompile(pattern, new XsdProfile(), kind, data!, new XmlClassAlgebra(), ref budget, out var program, out var xml), xml.ToString());
        Assert.Equal(1, program!.GroupCount);
        Assert.True(new XsdExecutorFactory().TryCreate(program, data!, ref budget, out var executor, out diagnostic), diagnostic.ToString());
        return executor!;
    }
    private static (int[] States, int[] Captures, int[] Scratch) Buffers(IXmlExecutor executor, int frames = 4096, int spare = 0)
    {
        var required = executor.GetRequiredCapacity(SearchOperation.Captures, frames);
        return (new int[required.StateSlots + spare], new int[required.CaptureSlots + spare], new int[required.ScratchSlots + spare]);
    }
    private static RegexSearchStatus Search(IXmlExecutor executor, string input, out RegexMatchRange range,
        out RegexCapture capture, int frames = 4096, RegexMatchLimits? limits = null, RegexMatchOptions options = default)
    {
        var (States, Captures, Scratch) = Buffers(executor, frames);
        var state = new RunState(new(States, Captures, Scratch), limits ?? new(), options);
        var groups = new RegexCapture[2];
        groups[1] = new(true, 99, 99);
        var status = executor.Search<char, XmlCharTextReader>(input, 0, SearchOperation.Captures, frames, ref state, groups, out range);
        capture = groups[0];
        Assert.Equal(new(true, 99, 99), groups[1]);
        return status;
    }
    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("a", "ba", false)]
    [InlineData("a", "a\n", false)]
    [InlineData("^a$", "^a$", true)]
    [InlineData("^a$", "a", false)]
    [InlineData("(a|ab)c", "abc", true)]
    [InlineData("(a|ab)c", "ac", true)]
    [InlineData("a{0}", "", true)]
    [InlineData("a{02,03}", "aaa", true)]
    [InlineData("a{2,3}", "a", false)]
    [InlineData("a{2,3}", "aaaa", false)]
    [InlineData("(a?)*", "aaa", true)]
    [InlineData("()*", "", true)]
    [InlineData("(a|aa)*b", "aaaaaaaaaaaaac", false)]
    [InlineData("\\i\\c*", ":a\u0300\U00010000", true)]
    [InlineData("[a-z-[aeiou]]+", "bcd", true)]
    [InlineData("[a-z-[aeiou]]+", "abc", false)]
    [InlineData("[a-z-[d-f-[e]]]", "e", true)]
    [InlineData("[a-z-[d-f-[e]]]", "d", false)]
    [InlineData("[^a-[b]]", "b", false)]
    [InlineData("[^a-[b]]", "c", true)]
    [InlineData("[a--[b]]", "-", true)]
    [InlineData("[a-]", "-", true)]
    [InlineData("\\P{IsUnknown-123}", "\U0010FFFF", true)]
    [InlineData("\\w", "\U0001F600", true)]
    [InlineData("\\w", "\u0378", false)]
    [InlineData("\\W", "\u0378", true)]
    [InlineData("\\s", "\v", false)]
    [InlineData(".", "\n", false)]
    [InlineData(".", "\r", false)]
    [InlineData("a b", "a b", true)]
    [InlineData("a b", "a  b", false)]
    [InlineData("&amp;", "&", false)]
    public void GrammarAndMembership(string pattern, string input, bool expected)
    {
        var executor = Compile(pattern);
        // XML-invalid subjects are covered separately; grammar witnesses stay legal.
        if (input == "\v")
        { Assert.Equal(RegexSearchStatus.InvalidInput, Search(executor, input, out _, out _)); return; }
        Assert.Equal(expected ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch, Search(executor, input, out var match, out var capture));
        Assert.Equal(expected ? new(0, input.Length) : default, match);
        Assert.Equal(expected ? new(true, 0, input.Length) : default, capture);
    }
    [Theory]
    [InlineData("[--z]")]
    [InlineData("[a-\\d]")]
    [InlineData("[]")]
    [InlineData("[^]")]
    [InlineData("[a-[b]c]")]
    [InlineData("[a-[b]-[c]]")]
    [InlineData("[\\]")]
    [InlineData("\\$")]
    [InlineData("\\p{Cs}")]
    [InlineData("\\p{Is}")]
    [InlineData("\\p{IsBasic_Latin}")]
    [InlineData("\\p{Letter}")]
    [InlineData("\\x41")]
    [InlineData("(?=a)")]
    [InlineData("(?:a)")]
    [InlineData("(a)\\1")]
    [InlineData("a*?")]
    [InlineData("a++")]
    [InlineData("a{2,1}")]
    [InlineData("a{}")]
    [InlineData("a{,2}")]
    [InlineData("a)")]
    [InlineData("(a")]
    [InlineData("a{2")]
    [InlineData("a{2,x}")]
    public void MalformedGrammarIsRejected(string pattern)
    {
        var budget = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(ref budget, out var data, out _));
        var before = budget.ReservedBytes;
        Assert.False(new XsdCompiler().TryCompile(pattern, new XsdProfile(), RegexInputKind.Utf16, data!, new XmlClassAlgebra(), ref budget, out var program, out var diagnostic));
        Assert.Null(program);
        Assert.Equal(RegexDiagnosticCode.InvalidSyntax, diagnostic.Diagnostic.Code);
        Assert.Equal(before, budget.ReservedBytes);
    }
    [Theory]
    [InlineData("a\\x41", RegexDiagnosticCode.InvalidSyntax, 1, 2)]
    [InlineData("\U0001F600\uFFFE", RegexDiagnosticCode.InvalidPatternEncoding, 2, 1)]
    [InlineData("\U0001F600\\1", RegexDiagnosticCode.InvalidSyntax, 2, 2)]
    public void DiagnosticsRetainOriginalUtf16Coordinates(string pattern, RegexDiagnosticCode code, int offset, int length)
    {
        var budget = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(ref budget, out var data, out _));
        Assert.False(new XsdCompiler().TryCompile(pattern, new XsdProfile(), RegexInputKind.Utf16, data!, new XmlClassAlgebra(), ref budget, out _, out var diagnostic));
        Assert.Equal(new RegexDiagnostic(code, offset, length, RegexOffsetUnit.Utf16CodeUnits), diagnostic.Diagnostic);
    }
    [Fact]
    public void LoneSurrogateDiagnosticUsesNumericInputRatherThanAttributeSerialization()
    {
        // Custom-attribute UTF8 serialization cannot preserve a lone surrogate.
        string pattern = new(['a', (char)0xD800]);
        DiagnosticsRetainOriginalUtf16Coordinates(pattern, RegexDiagnosticCode.InvalidPatternEncoding, 1, 1);
    }
    [Fact]
    public void UnicodeDomainNativeOffsetsAndPreparedReuse()
    {
        var text = "a\U0001F600";
        var bytes = Encoding.UTF8.GetBytes(text);
        var executor = Compile("a.", RegexInputKind.Utf8);
        var (States, Captures, Scratch) = Buffers(executor);
        var groups = new RegexCapture[1];
        var state = new RunState(new(States, Captures, Scratch), new());
        Assert.Equal(RegexSearchStatus.Match, executor.Search<byte, XmlByteTextReader>(bytes, 0, SearchOperation.Captures, 4096, ref state, groups, out var range));
        Assert.Equal(new(0, 5), range);
        Assert.Equal(new(true, 0, 5), groups[0]);
        state = new(new(States, Captures, Scratch), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, executor.Search<byte, XmlByteTextReader>([(byte)'a', 0], 0, SearchOperation.Captures, 4096, ref state, groups, out range));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain, state.Failure.Code);
        Assert.Equal(1, state.Failure.Offset);
        Assert.Equal(default, range);
        Assert.Equal(default, groups[0]);
        state = new(new(States, Captures, Scratch), new());
        Assert.Equal(RegexSearchStatus.InvalidInput, executor.Search<byte, XmlByteTextReader>([(byte)'a', 0xF0, 0x80, 0x80, 0x80], 0, SearchOperation.Range, 4096, ref state, [], out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, state.Failure.Code);
        state = new(new(States, Captures, Scratch), new());
        Assert.Equal(RegexSearchStatus.Match, executor.Search<byte, XmlByteTextReader>(bytes, 0, SearchOperation.Range, 4096, ref state, [], out range));
        Assert.Equal(new(0, 5), range);
        var chars = Compile("a");
        Assert.Equal(RegexSearchStatus.InvalidInput, Search(chars, "a\0", out _, out _));
        Assert.Equal(RegexSearchStatus.InvalidInput, Search(chars, "a\uD800", out _, out _));
    }
    [Fact]
    public void CapacityCeilingPrecedenceAndFailureRecovery()
    {
        var executor = Compile("(a|b)|(c|d)");
        var (States, Captures, Scratch) = Buffers(executor, 1, 1000);
        var groups = new RegexCapture[1];
        groups[0] = new(true, 1, 2);
        var state = new RunState(new(States, Captures, Scratch), new());
        Assert.Equal(RegexSearchStatus.LimitExceeded, executor.Search<char, XmlCharTextReader>("a", 0, SearchOperation.Captures, 1, ref state, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, state.Failure.Code);
        Assert.Equal(default, groups[0]);
        Assert.Equal(RegexSearchStatus.Match, Search(executor, "a", out _, out _));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        state = new(new([], [], []), new(), new() { CancellationToken = cancellation.Token });
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, executor.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Captures, 4096, ref state, [], out _));
        Assert.Equal(1, state.Failure.RequiredCapacity);
        Assert.Equal(0, state.Budget.WorkConsumed);
        state = new(new([], [], []), new(), new() { CancellationToken = cancellation.Token });
        Assert.Equal(RegexSearchStatus.LimitExceeded, executor.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Range, 4096, ref state, [], out _));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, state.Failure.Code);
        Assert.Equal(RegexSearchStatus.Cancelled, Search(executor, "\0", out _, out _, options: new() { CancellationToken = cancellation.Token }));
        Assert.Equal(RegexSearchStatus.LimitExceeded, Search(executor, "a", out _, out _, limits: new(maxWork: 1)));
        Assert.Equal(RegexSearchStatus.TimedOut, Search(Compile(""), "", out _, out _, options: new() { Timeout = TimeSpan.FromTicks(1) }));
    }
    [Fact]
    public void ArgumentsAndEveryLogicalPartitionAreAdmittedBeforeInput()
    {
        var executor = Compile("a");
        var required = executor.GetRequiredCapacity(SearchOperation.Captures, 4);
        Assert.Equal(2 * ((XsdExecutor)executor).InstructionCount, required.StateSlots);
        Assert.Equal(0, required.CaptureSlots);
        Assert.Equal(((XsdExecutor)executor).InstructionCount + 4, required.ScratchSlots);
        CheckShort(required.StateSlots - 1, required.ScratchSlots);
        CheckShort(required.StateSlots, required.ScratchSlots - 1);
        var (States, Captures, Scratch) = Buffers(executor, 4);
        var state = new RunState(new(States, Captures, Scratch), new());
        var caught = false;
        try
        { executor.Search<char, XmlCharTextReader>("a", 1, SearchOperation.Range, 4, ref state, [], out _); }
        catch (ArgumentOutOfRangeException) { caught = true; }
        Assert.True(caught);
        Assert.Equal(0, state.Budget.WorkConsumed);
        Assert.Throws<ArgumentOutOfRangeException>(() => executor.GetRequiredCapacity(SearchOperation.Range, 0));
        void CheckShort(int states, int scratch)
        {
            var s = new RunState(new(new int[states], [], new int[scratch]), new());
            Assert.Equal(RegexSearchStatus.LimitExceeded, executor.Search<char, XmlCharTextReader>("\0", 0, SearchOperation.Range, 4, ref s, [], out _));
            Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, s.Failure.Code);
            Assert.Equal(0, s.Budget.WorkConsumed);
        }
    }
    [Fact]
    public void CompileCeilingsAndFailedScratchAreBounded()
    {
        Check("a{1000}", new(maxInstructions: 100), RegexDiagnosticCode.ProgramLimitExceeded, factory: true);
        Check("a{2147483648}", new(), RegexDiagnosticCode.ProgramLimitExceeded);
        Check("a", new(maxPatternUnits: 1, maxCompileWork: 1), RegexDiagnosticCode.CompileWorkLimitExceeded);
        Check("abc", new(maxPatternUnits: 2), RegexDiagnosticCode.PatternLimitExceeded);
        Check("((a))", new(maxNestingDepth: 1), RegexDiagnosticCode.NestingLimitExceeded);
        Check("[a-z-[b]]", new(maxClassRanges: 1), RegexDiagnosticCode.ClassLimitExceeded);
        Check("a", new(maxCompileMemoryBytes: XmlUnicodeData.RetainedBytes + 10), RegexDiagnosticCode.CompileMemoryLimitExceeded);
        Check("a", new(maxPlanBytes: XmlUnicodeData.RetainedBytes + 10), RegexDiagnosticCode.PlanLimitExceeded);
        Assert.Equal(RegexSearchStatus.Match, Search(Compile(new string('a', 1000)), new string('a', 1000), out _, out _));
        static void Check(string pattern, RegexCompileLimits limits, RegexDiagnosticCode expected, bool factory = false)
        {
            var budget = new CompileBudget(limits);
            Assert.True(XmlUnicodeData.TryCreate(ref budget, out var data, out _));
            var before = budget.ReservedBytes;
            var success = new XsdCompiler().TryCompile(pattern, new XsdProfile(), RegexInputKind.Utf16, data!, new XmlClassAlgebra(), ref budget, out var program, out var xml);
            var diagnostic = xml.Diagnostic;
            if (factory)
            { Assert.True(success, xml.ToString()); before = budget.ReservedBytes; success = new XsdExecutorFactory().TryCreate(program!, data!, ref budget, out _, out diagnostic); }
            Assert.False(success);
            Assert.Equal(expected, diagnostic.Code);
            Assert.Equal(before, budget.ReservedBytes);
        }
    }
    [Fact]
    public void SharedPlanHasIndependentConcurrentState()
    {
        var executor = Compile("(\\i\\c*|[a-z-[aeiou]]+)");
        Parallel.For(0, 32, i => Assert.Equal(i % 2 == 0 ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
            Search(executor, i % 2 == 0 ? "name" : "12", out _, out _)));
    }
    [Fact]
    public void EntireIndependentCorpusExecutesAtLaneBoundary()
    {
        var path = Path.GetFullPath(Path.Combine(SourceDirectory(), "../../../../test/corpora/xml/xsd/corpus.json"));
        var corpus = XmlCorpus.Read(File.ReadAllText(path));
        using var provenance = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "diagnostic-provenance.json")));
        var authorities = provenance.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("coordinateAuthority").GetString()!);
        var failures = new List<string>();
        int executed = 0, integrationOnly = 0;
        foreach (var fixture in corpus.Cases)
        {
            // Byte-pattern overload admission is owned by public integration.
            if (fixture.PatternKind != "Utf16" || fixture.InputKind == "Bytes")
            { integrationOnly++; continue; }
            try
            { Execute(fixture, authorities.GetValueOrDefault(fixture.Id)); executed++; }
            catch (Exception e) { failures.Add(fixture.Id + ": " + e.Message); }
        }
        Assert.Equal(4, integrationOnly);
        Assert.Equal(corpus.Cases.Length - integrationOnly, executed + failures.Count);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
    private static void Execute(XmlFixture fixture, string? coordinateAuthority)
    {
        var kind = Enum.Parse<RegexInputKind>(fixture.InputKind);
        var pattern = new string([.. fixture.PatternUnits.Select(x => (char)x)]);
        var budget = new CompileBudget(new(maxCompileWork: fixture.Limits?.MaxCompileWork ?? 10_000_000));
        // Data setup is separately admitted before per-pattern recipe work.
        var dataBudget = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(ref dataBudget, out var data, out _));
        var parsed = new XsdCompiler().TryCompile(pattern, new XsdProfile(), kind, data!, new XmlClassAlgebra(), ref budget, out var program, out var xml);
        var expected = fixture.Permitted[0];
        if (!parsed)
        {
            Assert.NotNull(expected.Error);
            Assert.Equal(expected.Error!.Code, xml.Diagnostic.Code.ToString());
            Assert.Equal(expected.Error.Location, xml.Location.ToString());
            Assert.Equal(expected.Error.OffsetUnit, xml.Diagnostic.LocationUnit.ToString());
            if (coordinateAuthority is "FrozenExactContract" or "FirstOffendingNative")
            { Assert.Equal(expected.Error.Offset, xml.Diagnostic.Offset); Assert.Equal(expected.Error.Length, xml.Diagnostic.Length); }
            Assert.InRange(xml.Diagnostic.Offset, 0, pattern.Length);
            Assert.InRange(xml.Diagnostic.Length, 0, pattern.Length - xml.Diagnostic.Offset);
            return;
        }
        Assert.True(new XsdExecutorFactory().TryCreate(program!, data!, ref budget, out var executor, out var diagnostic), diagnostic.ToString());
        if (fixture.Operation == "Parse")
        { Assert.Equal("ParseSuccess", expected.Status); return; }
        var frames = fixture.Limits?.FrameCapacity ?? 4096;
        var (States, Captures, Scratch) = Buffers(executor!, frames);
        using var cancellation = new CancellationTokenSource();
        if (fixture.Limits?.Cancelled == true)
        {
            cancellation.Cancel();
        }

        var state = new RunState(new(States, Captures, Scratch), new(maxWork: fixture.Limits?.MaxWork ?? 10_000_000), new() { CancellationToken = cancellation.Token });
        var groups = new RegexCapture[fixture.DestinationLength ?? 1];
        var op = fixture.Operation == "FindCaptures" ? SearchOperation.Captures : fixture.Operation == "Find" ? SearchOperation.Range : SearchOperation.Boolean;
        RegexSearchStatus status;
        RegexMatchRange range;
        if (kind == RegexInputKind.Utf16)
        {
            status = executor!.Search<char, XmlCharTextReader>(fixture.InputUnits.Select(x => (char)x).ToArray(), fixture.StartAt, op, frames, ref state, groups, out range);
        }
        else
        {
            status = executor!.Search<byte, XmlByteTextReader>(fixture.InputUnits.Select(x => (byte)x).ToArray(), fixture.StartAt, op, frames, ref state, groups, out range);
        }

        Assert.Equal(expected.Status, status.ToString());
        if (status == RegexSearchStatus.Match)
        {
            Assert.Equal(expected.Matches[0].Range, range);
            if (op == SearchOperation.Captures)
            {
                Assert.Equal(expected.Matches[0].Captures[0], groups[0]);
            }
        }
        if (expected.Error is { } error)
        {
            Assert.Equal(error.Code, state.Failure.Code.ToString());
            if (coordinateAuthority is "FrozenExactContract" or "FirstOffendingNative")
            {
                Assert.Equal(error.Offset, state.Failure.Offset);
            }

            Assert.InRange(state.Failure.Offset, 0, fixture.InputUnits.Length);
        }
    }
    private static string SourceDirectory([CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
