using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

public class Pcre2HostTests
{
    [Theory]
    [InlineData("(a)(b)", "cabd", "$2$1", 0, "cbad", 1)]
    [InlineData("(a)", "aa", "$1$1", 256, "aaaa", 2)]
    [InlineData("(a)", "zaq", "[$&][$`][$'][$_]", 0, "z[a][z][q][zaq]q", 1)]
    [InlineData("(?<x>a)", "a", "$<x>${x}$x", 0, "aaa", 1)]
    [InlineData("(?J)(?<x>a)|(?<x>b)", "b", "$x", 0, "b", 1)]
    [InlineData("(a)?b", "b", "${1:-default}", 512, "default", 1)]
    [InlineData("(a)?b", "ab", "${1:+set:unset}", 512, "set", 1)]
    [InlineData("(a)?b", "b", "${1:+set:unset}", 512, "unset", 1)]
    [InlineData("(a)?b", "b", "${1:-${2:-nested}}", 2560, "nested", 1)]
    [InlineData("a", "a", "$$", 0, "$", 1)]
    [InlineData("a", "a", "$9", 3072, "", 1)]
    [InlineData("a(*MARK:yes)", "a", "${*MARK}", 0, "yes", 1)]
    [InlineData("a", "a", "\\Q$1\\E!", 512, "$1!", 1)]
    [InlineData("a", "a", "\\x{41}\\o{102}\\cC\\n", 512, "AB\u0003\n", 1)]
    [InlineData("(a)", "a", "\\1\\g<1>", 512, "aa", 1)]
    [InlineData("(?<x>a)", "a", "\\g<x>", 512, "a", 1)]
    [InlineData("a", "a", "\\Uabc\\Edef", 512, "ABCdef", 1)]
    [InlineData("a", "a", "\\u\\LabC", 512, "Abc", 1)]
    [InlineData("a", "a", "\\l\\UAbc", 512, "aBC", 1)]
    [InlineData("a", "a", "\\uabc", 512, "Abc", 1)]
    [InlineData("a", "a", "$1\\n", 32768, "$1\\n", 1)]
    [InlineData("a", "zaq", "X", 131072, "X", 1)]
    [InlineData("", "ab", "X", 256, "XaXbX", 3)]
    [InlineData("(?:|a)", "a", "X", 256, "XXX", 3)]
    [InlineData("z", "abc", "$bad", 0, "abc", 0)]
    public void ReplacementGrammarInBothWidths(string pattern, string subject, string replacement, uint flags, string expected, int count)
    {
        var fixture = new HostFixture(pattern);
        var output = new char[256];
        var result = fixture.Substitute(subject, replacement, output, (Pcre2SubstituteFlags)flags);
        Assert.Equal(count, result.NativeReturnCode);
        Assert.Equal(expected, new string(output, 0, result.OutputUnits));
        Assert.Equal('\0', output[result.OutputUnits]);
        var bytes = new HostFixture(pattern, new(Pcre2CodeUnitWidth.Eight));
        var target = new byte[256];
        var native = bytes.Substitute(System.Text.Encoding.Latin1.GetBytes(subject), System.Text.Encoding.Latin1.GetBytes(replacement), target, (Pcre2SubstituteFlags)flags);
        Assert.Equal(count, native.NativeReturnCode);
        Assert.Equal(expected, System.Text.Encoding.Latin1.GetString(target, 0, native.OutputUnits));
    }
    [Theory]
    [InlineData("$", -35)]
    [InlineData("${", -35)]
    [InlineData("${1", -58)]
    [InlineData("$9", -49)]
    [InlineData("\\d", -57)]
    [InlineData("\\", -57)]
    [InlineData("\\x", -57)]
    [InlineData("${1:?bad}", -59)]
    public void ReplacementFailures(string replacement, int error)
    {
        var fixture = new HostFixture("(a)");
        var output = new char[128];
        var result = fixture.Substitute("a", replacement, output, Pcre2SubstituteFlags.Extended);
        Assert.Equal(error, result.NativeReturnCode);
    }
    [Fact]
    public void UnknownUnsetAndUnavailableRemainDistinct()
    {
        var fixture = new HostFixture("(a)?b");
        var output = new char[128];
        Assert.Equal((int)Pcre2ErrorCode.Unset, fixture.Substitute("b", "$1", output).NativeReturnCode);
        var c048 = new HostFixture("(a)");
        Pcre2OffsetPair[] shortPairs = [new(true, 0, 1)];
        var shortResult = c048.Substitute("a", "$1$1", output, Pcre2SubstituteFlags.Matched, precomputed: shortPairs, result: new(Pcre2MatchStatus.Match, 0, 0));
        Assert.Equal(-54, shortResult.NativeReturnCode);
        Assert.Equal(2, shortResult.OutputUnits);
        Pcre2OffsetPair[] fullPairs = [new(true, 0, 1), new(true, 0, 1)];
        Assert.Equal(1, c048.Substitute("a", "$1$1", output, Pcre2SubstituteFlags.Matched, precomputed: fullPairs, result: new(Pcre2MatchStatus.Match, 2, 0)).NativeReturnCode);
        Assert.Equal(1, c048.Substitute("a", "$1$1", output).NativeReturnCode);
        Assert.Equal("aa", new string(output, 0, 2));
    }
    [Fact]
    public void OutputSizingIncludesTerminatorAndSuppressesSubstituteCallout()
    {
        var fixture = new HostFixture("a");
        var calls = 0;
        var callbacks = new Pcre2CharCallbacks(Substitute: (in c, _) => { calls++; return 1; });
        var small = new char[1];
        var sized = fixture.Substitute("aa", "LONG", small, Pcre2SubstituteFlags.Global | Pcre2SubstituteFlags.OverflowLength, callbacks: callbacks);
        Assert.Equal(-48, sized.NativeReturnCode);
        Assert.Equal(9, sized.RequiredCapacity);
        Assert.Equal(0, calls);
        var output = new char[9];
        var full = fixture.Substitute("aa", "LONG", output, Pcre2SubstituteFlags.Global, callbacks: callbacks);
        Assert.Equal(2, full.NativeReturnCode);
        Assert.Equal("aa", new string(output, 0, full.OutputUnits));
        Assert.Equal(2, calls);
        Assert.Equal(-48, fixture.Substitute("a", "x", new char[1]).NativeReturnCode);
    }
    [Fact]
    public void SourceSimpleCasesConvertCodepointsIntoNativeOutputWidth()
    {
        var raw = new HostFixture(".", new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Ucp));
        var target = new byte[8];
        var result = raw.Substitute([0xB5], "\\U$0"u8.ToArray(), target, Pcre2SubstituteFlags.Extended);
        Assert.Equal(1, result.NativeReturnCode);
        Assert.Equal(1, result.OutputUnits);
        Assert.Equal(0xBC, target[0]);
        var utf = new HostFixture(".", new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp));
        var utfResult = utf.Substitute("µ"u8.ToArray(), "\\U$0"u8.ToArray(), target, Pcre2SubstituteFlags.Extended);
        Assert.Equal(1, utfResult.NativeReturnCode);
        Assert.Equal("μ", System.Text.Encoding.UTF8.GetString(target, 0, utfResult.OutputUnits));
    }
    [Fact]
    public void IterationRetriesEmptyBeforeAdvancingOriginalSubject()
    {
        var fixture = new HostFixture("(?:|a)");
        var state = fixture.State();
        var iterator = new Pcre2HostIteration(0);
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, iterator.Next(fixture, "a", pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        Assert.Equal(new(true, 0, 0), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.Match, iterator.Next(fixture, "a", pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        Assert.Equal(new(true, 0, 1), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.Match, iterator.Next(fixture, "a", pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        Assert.Equal(new(true, 1, 1), pairs[0]);
        Assert.Equal(Pcre2MatchStatus.NoMatch, iterator.Next(fixture, "a", pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
    }
    [Fact]
    public void LimitsPollZeroWorkAndExceptionsPermitReuse()
    {
        var fixture = new HostFixture("a");
        var output = new char[8];
        var zero = fixture.Substitute("a", "x", output, options: new() { Limits = new(maxWork: 0) });
        Assert.Equal(-47, zero.NativeReturnCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RegexExecutionFailureCode.Cancelled, fixture.Substitute("a", "x", output, options: new() { CancellationToken = cancellation.Token, Limits = new(maxWork: 0) }).Failure.Code);
        Assert.Throws<InvalidOperationException>(() => fixture.Substitute("a", "x", output, callbacks: new(Substitute: (in c, _) => throw new InvalidOperationException("user"))));
        Assert.Equal(1, fixture.Substitute("a", "x", output).NativeReturnCode);
    }
    [Fact]
    public void MetadataAndSubstringHelpersPreserveDuplicateNamesAndTerminator()
    {
        var fixture = new HostFixture("(?J)(?<x>a)|(?<x>b)(?C\"hi\")");
        var budget = new CompileBudget(new());
        Assert.True(Pcre2HostMetadata.TryCreate(fixture.Program, ref budget, out var metadata, out _));
        Assert.Equal(new[] { 1, 2 }, metadata!.GetGroupNumbers("x").ToArray());
        Assert.Equal(-50, metadata.GroupNumberFromName("x"));
        Assert.Equal(2, metadata.GetNamedGroups().Length);
        Assert.Single(metadata.GetCalloutSites().ToArray());
        Assert.Equal("hi", metadata.GetCalloutStringChars(0).ToString());
        Assert.True(metadata.TryGetInfo(Pcre2PatternInfoKey.Capturecount, out var count, out _));
        Assert.Equal(2, count);
        Assert.False(metadata.TryGetInfo(Pcre2PatternInfoKey.Size, out _, out var diagnostic));
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
        Pcre2OffsetPair[] pairs = [new(true, 0, 1), default, new(true, 0, 1)];
        var number = Pcre2Substrings.Number(fixture.Program, pairs, "x");
        Assert.Equal(2, number);
        var destination = new char[2];
        Assert.Equal(0, Pcre2Substrings.Copy<char>("b", pairs, number, destination, fixture.Program.GroupCount).NativeReturnCode);
        Assert.Equal(new[] { 'b', '\0' }, destination);
    }
}
