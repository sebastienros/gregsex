using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Host;

public class Pcre2HostResourceTests
{
    [Fact]
    public void CalloutCeilingIsCumulativeAcrossMatchesAndHostCallbacks()
    {
        var fixture = new HostFixture("a(?C1)");
        var output = new char[32];
        int match = 0, substitute = 0, casing = 0;
        var callbacks = new Pcre2CharCallbacks(
            Match: (in c, _) => { match++; return 0; },
            Substitute: (in c, _) => { substitute++; return 0; },
            Case: (input, target, mode, _) => { casing++; input.CopyTo(target); return input.Length; });
        var result = fixture.Substitute("aa", "\\Ux", output, Pcre2SubstituteFlags.Extended | Pcre2SubstituteFlags.Global,
            options: new() { Limits = new(maxCallouts: 3) }, callbacks: callbacks);
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, result.Failure.Code);
        Assert.Equal(1, match);
        Assert.Equal(1, substitute);
        Assert.Equal(1, casing);
        Assert.Equal(2, fixture.Substitute("aa", "x", output, Pcre2SubstituteFlags.Global, callbacks: callbacks).NativeReturnCode);
    }
    [Fact]
    public void CancellationAfterCallbackAndExceptionUnwindPermitReuse()
    {
        var fixture = new HostFixture("a(?C1)");
        var output = new char[16];
        using var cancellation = new CancellationTokenSource();
        var result = fixture.Substitute("a", "x", output, options: new() { CancellationToken = cancellation.Token },
            callbacks: new(Match: (in c, _) => { cancellation.Cancel(); return 0; }));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, result.Failure.Code);
        Assert.True(fixture.Released);
        Assert.Throws<ApplicationException>(() => fixture.Substitute("a", "\\Ux", output, Pcre2SubstituteFlags.Extended,
            callbacks: new(Case: (input, target, mode, _) => throw new ApplicationException("case"))));
        Assert.True(fixture.Released);
        Assert.Equal(1, fixture.Substitute("a", "x", output).NativeReturnCode);
    }
    [Fact]
    public void ReducedPositiveWorkCeilingBoundsTextCallbacksAndOutputTogether()
    {
        var fixture = new HostFixture(".", new(flags: Pcre2CompileFlags.Utf));
        var output = new char[64];
        var full = fixture.Substitute("a😀b", "\\U$0", output, Pcre2SubstituteFlags.Global | Pcre2SubstituteFlags.Extended);
        Assert.Equal(3, full.NativeReturnCode);
        var consumed = fixture.Work;
        var bounded = fixture.Substitute("a😀b", "\\U$0", output, Pcre2SubstituteFlags.Global | Pcre2SubstituteFlags.Extended,
            options: new() { Limits = new(maxWork: consumed - 1) });
        Assert.True(bounded.NativeReturnCode < 0);
        Assert.True(fixture.Work <= consumed - 1);
        Assert.Equal(3, fixture.Substitute("a😀b", "x", output, Pcre2SubstituteFlags.Global).NativeReturnCode);
    }
    [Fact]
    public void HostOutputMemoryAndResultLimitsAreFinite()
    {
        var fixture = new HostFixture("a");
        var output = new char[64];
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, fixture.Substitute("a", "x", output, limits: new(maxMemoryBytes: 1)).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, fixture.Substitute("a", "LONG", output, limits: new(maxOutputUnits: 4)).Failure.Code);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, fixture.Substitute("aa", "x", output, Pcre2SubstituteFlags.Global, limits: new(maxResults: 1)).Failure.Code);
    }
    [Theory]
    [InlineData("(*NOTEMPTY)(?:|a)", "a", "X", 1)]
    [InlineData("(*NOTEMPTY_ATSTART)(?:|a)", "a", "X", 1)]
    [InlineData("(?<=\\G.)", "ab", "aXbX", 2)]
    public void NativeInvocationOffsetsAndPatternMatchFlagsSurviveOuterRetries(string pattern, string input, string expected, int substitutions)
    {
        var fixture = new HostFixture(pattern);
        var output = new char[64];
        var result = fixture.Substitute(input, "X", output, Pcre2SubstituteFlags.Global);
        Assert.Equal(substitutions, result.NativeReturnCode);
        Assert.Equal(expected, new string(output, 0, result.OutputUnits));
    }
    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    public void EmptyIterationAdvancesNativeCharactersAndJoinsCrlf(int domain, int terminal)
    {
        var utf = domain % 2 == 1;
        var width = domain < 2 ? 8 : 16;
        var fixture = new HostFixture("", new((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0, newline: Pcre2Newline.Anycrlf));
        var iterator = new Pcre2HostIteration(0);
        var state = fixture.State();
        var pairs = new Pcre2OffsetPair[1];
        if (width == 16)
        {
            var input = utf ? "\r\n😀" : "\r\nabc";
            int last = -1, matches = 0;
            while (iterator.Next(fixture, input, pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status == Pcre2MatchStatus.Match)
            { Assert.True(pairs[0].End > last); last = pairs[0].End; Assert.NotEqual(1, last); matches++; Assert.True(matches < 10); }
            Assert.Equal(input.Length, last);
        }
        else
        {
            var input = utf ? "\r\né"u8.ToArray() : "\r\na"u8.ToArray();
            int last = -1, matches = 0;
            while (iterator.Next(fixture, input, pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status == Pcre2MatchStatus.Match)
            { Assert.True(pairs[0].End > last); last = pairs[0].End; Assert.NotEqual(1, last); matches++; Assert.True(matches < 10); }
            Assert.Equal(input.Length, last);
        }
        _ = terminal;
    }
    [Fact]
    public void IterationCallbackBudgetAndBorrowedMutationRemainVisible()
    {
        var fixture = new HostFixture("a(?C1)");
        var options = new Pcre2MatchOptions { Limits = new(maxCallouts: 1) };
        var state = fixture.State(options);
        var iterator = new Pcre2HostIteration(0);
        char[] input = ['a', 'a'];
        var pairs = new Pcre2OffsetPair[1];
        var calls = 0;
        var callbacks = new Pcre2CharCallbacks(Match: (in c, _) => { calls++; return 0; });
        Assert.Equal(Pcre2MatchStatus.Match, iterator.Next(fixture, input, pairs, options, callbacks, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, iterator.Next(fixture, input, pairs, options, callbacks, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        Assert.Equal(1, calls);
        state = fixture.State();
        iterator = new(0);
        Assert.Equal(Pcre2MatchStatus.Match, iterator.Next(fixture, input, pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
        input[1] = 'b';
        Assert.Equal(Pcre2MatchStatus.NoMatch, iterator.Next(fixture, input, pairs, default, null, fixture.Capacity, new(), ref state, fixture.CallbackPairs).Status);
    }
    [Fact]
    public void MetadataNativeNamesUseWidthOrdering()
    {
        foreach (var width in new[] { 8, 16 })
        {
            var fixture = new HostFixture("(?<𐐀>a)(?<Ａ>b)", new((Pcre2CodeUnitWidth)width, Pcre2CompileFlags.Utf));
            var budget = new GregsEx.Contracts.CompileBudget(new());
            Assert.True(Pcre2HostMetadata.TryCreate(fixture.Program, ref budget, out var metadata, out _));
            Assert.Equal(width == 8 ? ["Ａ", "𐐀"] : ["𐐀", "Ａ"], metadata!.GetNamedGroups().ToArray().Select(n => n.Name).ToArray());
        }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SubstituteCallbacksExposeFullSuppliedVectorWithUpperUnset(int domain)
    {
        var width = domain < 2 ? 8 : 16;
        var fixture = new HostFixture("(a)(b)?", new((Pcre2CodeUnitWidth)width, domain % 2 == 1 ? Pcre2CompileFlags.Utf : 0));
        var expectedFull = new[] { new Pcre2OffsetPair(true, 0, 1), new Pcre2OffsetPair(true, 0, 1), default };
        Pcre2OffsetPair[] full = new Pcre2OffsetPair[3], shortVector = new Pcre2OffsetPair[2];
        int observed = 0, calls = 0;
        if (width == 16)
        {
            var callback = new Pcre2CharCallbacks(Substitute: (in context, _) =>
            { calls++; observed = context.Captures.Length; Assert.Equal(expectedFull[..observed], context.Captures.ToArray()); return 0; });
            Assert.Equal(1, fixture.Substitute("a", "$1", new char[8], callbacks: callback).NativeReturnCode);
            Assert.Equal(3, observed);
            var state = fixture.State();
            var result = fixture.Match("a", 0, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, full);
            Assert.Equal(2, result.NativeReturnCode);
            full[2] = new(true, 0, 1); // Upper native rc2 slot is undefined; frozen safety policy clears it.
            var before = full.ToArray();
            Assert.Equal(1, fixture.Substitute("a", "$1", new char[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: full, result: result).NativeReturnCode);
            Assert.Equal(3, observed);
            Assert.Equal(before, full);
            state = fixture.State();
            result = fixture.Match("a", 0, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, shortVector);
            Assert.Equal(2, result.NativeReturnCode);
            Assert.Equal(1, fixture.Substitute("a", "$1", new char[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: shortVector, result: result).NativeReturnCode);
            Assert.Equal(2, observed);
            Assert.Equal(-54, fixture.Substitute("a", "$2", new char[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: shortVector, result: result).NativeReturnCode);
        }
        else
        {
            var callback = new Pcre2ByteCallbacks(Substitute: (in context, _) =>
            { calls++; observed = context.Captures.Length; Assert.Equal(expectedFull[..observed], context.Captures.ToArray()); return 0; });
            byte[] subject = "a"u8.ToArray(), replacement = "$1"u8.ToArray();
            Assert.Equal(1, fixture.Substitute(subject, replacement, new byte[8], callbacks: callback).NativeReturnCode);
            Assert.Equal(3, observed);
            var state = fixture.State();
            var result = fixture.Match(subject, 0, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, full);
            Assert.Equal(2, result.NativeReturnCode);
            full[2] = new(true, 0, 1);
            var before = full.ToArray();
            Assert.Equal(1, fixture.Substitute(subject, replacement, new byte[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: full, result: result).NativeReturnCode);
            Assert.Equal(3, observed);
            Assert.Equal(before, full);
            state = fixture.State();
            result = fixture.Match(subject, 0, default, null, fixture.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, fixture.CallbackPairs, shortVector);
            Assert.Equal(2, result.NativeReturnCode);
            Assert.Equal(1, fixture.Substitute(subject, replacement, new byte[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: shortVector, result: result).NativeReturnCode);
            Assert.Equal(2, observed);
            Assert.Equal(-54, fixture.Substitute(subject, "$2"u8.ToArray(), new byte[8], Pcre2SubstituteFlags.Matched, callbacks: callback, precomputed: shortVector, result: result).NativeReturnCode);
        }
        Assert.Equal(3, calls); // Failed unavailable expansion never invokes substitution callback.
    }

}
