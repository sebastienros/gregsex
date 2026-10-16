using Xunit;
using GregsEx.Contracts;
using GregsEx.Flavors.XPath.Host;
using F = GregsEx.Flavors.XPath.Host.XPathHostFunctions;

namespace GregsEx.Tests.Flavors.XPath.Host;

public sealed class LimitTests
{
    [Fact]
    public void SubjectDomainAndEncodingAlwaysValidateBeforeReturn()
    {
        foreach (var input in new[] { "a\0", "a\u0001", "a\ud800", "a\udc00" })
        {
            var code = char.IsSurrogate(input[1]) ? RegexExecutionFailureCode.InvalidEncoding : RegexExecutionFailureCode.InvalidInputDomain;
            foreach (var operation in new Action[] { () => F.Matches(input, "a"),
                () => F.Replace(input, "a", "x"), () => F.Tokenize(input, ","), () => F.Tokenize(input) })
            { var ex = Assert.Throws<RegexExecutionException>(operation); Assert.Equal(code, ex.Failure.Code); Assert.Equal(1, ex.Failure.Offset); }
        }
        Assert.True(F.Matches(char.ConvertFromUtf32(0x10ffff), "."));
    }
    [Fact]
    public void CompileAndRepresentationFailuresRemainSeparate()
    {
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation,
            Assert.Throws<RegexParseException>(() => F.Matches("a", "a", compile: new(RegexInputKind.Utf8))).Diagnostic.Code);
        Assert.Equal(RegexDiagnosticCode.PatternLimitExceeded,
            Assert.Throws<RegexParseException>(() => F.Matches("a", "ab", compile: new(limits: new(maxPatternUnits: 1)))).Diagnostic.Code);
        Assert.Equal(RegexDiagnosticCode.InvalidPatternEncoding,
            Assert.Throws<RegexParseException>(() => F.Matches("a", "\0")).Diagnostic.Code);
        Assert.Throws<ArgumentNullException>(() => F.Matches("a", null!));
        Assert.Throws<ArgumentNullException>(() => F.Replace("a", "a", null!));
    }
    [Fact]
    public void WorkspaceAdmissionPrecedesCancellationAndAllocations()
    {
        var compile = new RegexCompileOptions(defaultMatchLimits: new(maxWorkspaceBytes: 1));
        var ex = Assert.Throws<RegexExecutionException>(() => F.Matches("\0", "a", compile: compile,
            match: new() { CancellationToken = new(true) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, ex.Failure.Code);
        Assert.Equal(0, ex.Failure.WorkConsumed);
    }
    [Fact]
    public void CancellationDeadlinesAndOverridesAreNotFalseOrForx()
    {
        var match = new RegexMatchOptions { CancellationToken = new(true) };
        Assert.Equal(RegexExecutionFailureCode.Cancelled,
            Assert.Throws<RegexExecutionException>(() => F.Replace(null, "a", "$", match: match)).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.Cancelled,
            Assert.Throws<RegexExecutionException>(() => F.Tokenize(null, match: match)).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.TimedOut,
            Assert.Throws<RegexExecutionException>(() => F.Matches("", "", match: new() { Timeout = TimeSpan.FromTicks(1) })).Failure.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => F.Matches("", "a", match: new() { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => F.Matches("", "a", match: new() { Limits = new(maxWork: 20_000_000) }));
    }
    [Fact]
    public void CumulativeWorkCannotResetForEachMatchOrReplacementCopy()
    {
        var match = new RegexMatchOptions { Limits = new(maxWork: 500) };
        foreach (var operation in new Action[] { () => F.Replace(new string('a', 100), "a", "x", match: match),
            () => F.Tokenize(new string('a', 100), "a", match: match),
            () => F.Replace("a", "a", new string('x', 1000), match: match) })
        {
            Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded,
                Assert.Throws<RegexExecutionException>(operation).Failure.Code);
        }

        Assert.Equal("x", F.Replace("a", "a", "x")); // failure owns no reusable shared state
    }
    [Fact]
    public void OutputCountLengthAndMemoryAreDistinctAndNoPartialResultEscapes()
    {
        Assert.Equal(XPathHostFailureCode.ResultLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.Tokenize("a,b", ",", host: new(maxResults: 1))).Failure.Code);
        Assert.Equal(XPathHostFailureCode.OutputLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.Replace("a", "a", "xx", host: new(maxOutputUnits: 1))).Failure.Code);
        Assert.Equal(XPathHostFailureCode.OutputLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.Tokenize("ab cd", host: new(maxOutputUnits: 3))).Failure.Code);
        Assert.Equal(XPathHostFailureCode.MemoryLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.Tokenize("ab", host: new(maxMemoryBytes: 400))).Failure.Code);
        Assert.Equal(XPathHostFailureCode.MemoryLimitExceeded,
            Assert.Throws<XPathHostException>(() => F.Replace("a", "a", "x", host: new(maxMemoryBytes: 100))).Failure.Code);
        Assert.Equal(new[] { "a", "b" }, F.Tokenize("a b"));
    }
    [Fact]
    public void BufferGrowthCountsOldAndNewArraysBeforeAllocation()
    {
        var context = new HostContext(null, new(), default, new(maxMemoryBytes: 648), 0);
        var output = new HostBuffer<char>(2);
        for (var i = 0; i < 16; i++)
        {
            output.Add('a', ref context);
        }

        Assert.Equal(624, context.PeakMemory); // conservative admission, not GC allocation measurement
        try
        { output.Add('a', ref context); Assert.Fail("Growth must fail before publishing replacement storage."); }
        catch (XPathHostException ex)
        {
            Assert.Equal(XPathHostFailureCode.MemoryLimitExceeded, ex.Failure.Code);
            Assert.Equal(672, ex.Failure.RequiredCapacity);
            Assert.Equal(16, output.Count);
            Assert.Equal(new string('a', 16), new string(output.Items));
        }
    }
    [Fact]
    public void WorkspaceMetadataIsIncludedAtTheExactMemoryBoundary()
    {
        var prepared = HostContext.Create("a", "", null, default, null);
        var arrays = prepared.Executor!.GetRequiredCapacity(SearchOperation.Captures, HostContext.Frames).RequiredBytes;
        var ex = Assert.Throws<RegexExecutionException>(() => F.Matches("a", "a",
            compile: new(defaultMatchLimits: new(maxWorkspaceBytes: arrays + 127))));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, ex.Failure.Code);
        Assert.Equal(arrays + 128, ex.Failure.RequiredCapacity);
        Assert.True(F.Matches("a", "a", compile: new(defaultMatchLimits: new(maxWorkspaceBytes: arrays + 128))));
    }
    [Fact]
    public async Task SeparateCallsHaveNoSharedWorkspaceOrMutableOutput()
    {
        var jobs = Enumerable.Range(0, 12).Select(async i =>
        {
            await Task.Yield();
            Assert.Equal("bb", F.Replace("aa", "(a)", "b"));
            Assert.Equal(new[] { "a", "b" }, F.Tokenize("a,b", ","));
        });
        await Task.WhenAll(jobs);
    }
}
