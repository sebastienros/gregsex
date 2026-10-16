using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

public class Pcre2HostBoundaryTests
{
    [Fact]
    public void PrecomputedMatchDataIsReadOnlyAndRetainsFailure()
    {
        var fixture = new HostFixture("a");
        var output = new char[32];
        Pcre2OffsetPair[] pairs = [new(true, 1, 2)];
        var original = pairs.ToArray();
        var result = fixture.Substitute("zaq", "$0", output, Pcre2SubstituteFlags.Matched, precomputed: pairs, result: new(Pcre2MatchStatus.Match, 1, 1));
        Assert.Equal(1, result.NativeReturnCode);
        Assert.Equal("zaq", new string(output, 0, result.OutputUnits));
        Assert.Equal(original, pairs);
        Assert.Equal(-37, fixture.Substitute("zaq", "$0", output, Pcre2SubstituteFlags.Matched, precomputed: pairs, result: new(Pcre2MatchStatus.Error, -37, 0)).NativeReturnCode);
        Assert.Equal(-51, fixture.Substitute("a", "x", output, Pcre2SubstituteFlags.Matched).NativeReturnCode);
    }
    [Fact]
    public void BadSubstituteEndpointsAndPartialOptionsAreNotSuccessful()
    {
        var fixture = new HostFixture("a");
        var output = new char[16];
        Assert.Equal(-60, fixture.Substitute("ab", "x", output, Pcre2SubstituteFlags.Matched,
            precomputed: [new Pcre2OffsetPair(true, 2, 1)], result: new(Pcre2MatchStatus.Match, 1, 0)).NativeReturnCode);
        Assert.Equal(-60, fixture.Substitute("ab", "x", output, Pcre2SubstituteFlags.Matched, start: 1,
            precomputed: [new Pcre2OffsetPair(true, 0, 1)], result: new(Pcre2MatchStatus.Match, 1, 0)).NativeReturnCode);
        Assert.Equal(-34, fixture.Substitute("a", "x", output, options: new() { Flags = Pcre2MatchFlags.PartialHard }).NativeReturnCode);
    }
    [Fact]
    public void ReplacementEncodingIsCheckedBeforeNoMatch()
    {
        var chars = new HostFixture("z", new(flags: Pcre2CompileFlags.Utf));
        Assert.Equal((int)Pcre2ErrorCode.Utf16Err1, chars.Substitute("a", "\uD800", new char[8]).NativeReturnCode);
        var bytes = new HostFixture("z", new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf | Pcre2CompileFlags.MatchInvalidUtf));
        Assert.Equal((int)Pcre2ErrorCode.Utf8Err20, bytes.Substitute("a"u8.ToArray(), [0x80], new byte[8]).NativeReturnCode);
    }
    [Fact]
    public void ConditionalReprocessingStackIsBoundedAndNonrecursive()
    {
        var fixture = new HostFixture("(a)?b");
        var output = new char[64];
        var replacement = "x";
        for (var i = 0; i < 10; i++)
        {
            replacement = "${1:-" + replacement + "}";
        }

        Assert.Equal(1, fixture.Substitute("b", replacement, output, Pcre2SubstituteFlags.Extended).NativeReturnCode);
        replacement = "${1:-" + replacement + "}";
        Assert.Equal((int)Pcre2ErrorCode.Badreplacement, fixture.Substitute("b", replacement, output, Pcre2SubstituteFlags.Extended).NativeReturnCode);
    }
    [Fact]
    public void ImportedRawTablesControlCaseAndGroupNameScanning()
    {
        var table = Pcre2CharacterTables.Default.Bytes.ToArray();
        table[256 + 'a'] = (byte)'Z';
        var fixture = new HostFixture("(?<a>a)", new(characterTables: new(table)));
        var output = new char[8];
        Assert.Equal(1, fixture.Substitute("a", "\\U$a", output, Pcre2SubstituteFlags.Extended).NativeReturnCode);
        Assert.Equal('Z', output[0]);
        table[832 + 'a'] &= unchecked((byte)~0x10);
        // Profile copies table input; changing the caller array cannot mutate it.
        Assert.Equal(1, fixture.Substitute("a", "$a", output).NativeReturnCode);
    }
    [Fact]
    public void StatelessAndProgramAwareSubstringAdmissionIsExplicit()
    {
        Pcre2OffsetPair[] pairs = [new(true, 2, 1)];
        Assert.Equal(0, Pcre2Substrings.Length(pairs, 0));
        Assert.Equal((int)Pcre2ErrorCode.Nosubstring, Pcre2Substrings.Length(pairs, 1));
        Assert.Equal((int)Pcre2ErrorCode.Unavailable, Pcre2Substrings.Length(pairs, 1, 2));
        Assert.Equal((int)Pcre2ErrorCode.Invalidoffset, Pcre2Substrings.Copy<char>("a", pairs, 0, new char[1]).NativeReturnCode);
        pairs[0] = new(true, 0, 1);
        char[] output = ['U'];
        var small = Pcre2Substrings.Copy<char>("a", pairs, 0, output);
        Assert.Equal(-48, small.NativeReturnCode);
        Assert.Equal(2, small.RequiredCapacity);
        Assert.Equal('U', output[0]);
        Assert.Throws<InvalidOperationException>(() => Pcre2Substrings.Materialize<char>("a", pairs, 0, new(maxOutputUnits: 1)));
    }
    [Fact]
    public async Task SharedImmutablePlanUsesPrivateBuffersAcrossConcurrentOperations()
    {
        var source = new HostFixture("(a)");
        async Task<string> Run()
        {
            await Task.Yield();
            var capacity = source.Capacity.Required(source.Program.GroupCount);
            var state = new RunState(new(new int[capacity.StateSlots], new int[capacity.CaptureSlots], new int[capacity.ScratchSlots]), new());
            var output = new char[32];
            var result = new Pcre2Host().Substitute(source, "aaa", 0, "$1$1", output, Pcre2SubstituteFlags.Global, default, null, new(), default, default,
                source.Capacity, ref state, new Pcre2OffsetPair[source.Program.GroupCount]);
            Assert.Equal(3, result.NativeReturnCode);
            return new string(output, 0, result.OutputUnits);
        }
        var results = await Task.WhenAll(Run(), Run(), Run());
        Assert.All(results, r => Assert.Equal("aaaaaa", r));
    }
    [Fact]
    public void DeadlineAfterSubstituteCallbackIsDistinctAndReusable()
    {
        var fixture = new HostFixture("a");
        var output = new char[8];
        var result = fixture.Substitute("a", "x", output, options: new() { Timeout = TimeSpan.FromMilliseconds(100) },
            callbacks: new(Substitute: (in context, _) => { Thread.Sleep(120); return 0; }));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, result.Failure.Code);
        Assert.Equal(1, fixture.Substitute("a", "x", output).NativeReturnCode);
    }
    [Fact]
    public void SplitUtfCaptureCopiesNativeBytesWithoutOutputTranscoding()
    {
        var fixture = new HostFixture("(\\C)\\C", new(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf));
        var output = new byte[8];
        var result = fixture.Substitute("é"u8.ToArray(), "$1"u8.ToArray(), output);
        Assert.Equal(1, result.NativeReturnCode);
        Assert.Equal(1, result.OutputUnits);
        Assert.Equal(0xC3, output[0]);
        Assert.Equal(0, output[1]);
    }

}
