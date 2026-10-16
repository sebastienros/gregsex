using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

public class Pcre2ComposedCallbackTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RealMatchAndSubstituteCallbacksBorrowFullSafeVectors(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)(?C7)(b)?", domain), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[3];
        object state = new();
        int match = 0, substitute = 0;
        void Inspect(Pcre2CalloutInfo info, ReadOnlySpan<Pcre2OffsetPair> captures, object? actual)
        {
            match++;
            Assert.Same(state, actual);
            Assert.Equal(7, info.Number);
            Assert.Equal(2, info.CaptureTop);
            Assert.Equal(3, captures.Length);
            Assert.False(captures[0].Participated);
            Assert.Equal(new(true, 0, 1), captures[1]);
            Assert.False(captures[2].Participated);
        }
        void InspectFinal(ReadOnlySpan<Pcre2OffsetPair> captures, object? actual)
        {
            substitute++;
            Assert.Same(state, actual);
            Assert.Equal(3, captures.Length);
            Assert.Equal(new(true, 0, 1), captures[0]);
            Assert.Equal(new(true, 0, 1), captures[1]);
            Assert.False(captures[2].Participated);
        }
        if (domain < 2)
        {
            var callbacks = new Pcre2ByteCallbacks(
                Match: (in context, s) => { Assert.True(context.Subject.SequenceEqual("a"u8)); Inspect(context.Info, context.Captures, s); return 0; },
                Substitute: (in context, s) => { InspectFinal(context.Captures, s); return 0; }, State: state);
            Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a"u8, 0, pairs, callbacks: callbacks).Status);
            Assert.Equal(1, workspace.Substitute("a"u8, 0, "$1"u8, new byte[8], callbacks: callbacks).NativeReturnCode);
        }
        else
        {
            var callbacks = new Pcre2CharCallbacks(
                Match: (in context, s) => { Assert.True(context.Subject.SequenceEqual("a")); Inspect(context.Info, context.Captures, s); return 0; },
                Substitute: (in context, s) => { InspectFinal(context.Captures, s); return 0; }, State: state);
            Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a", 0, pairs, callbacks: callbacks).Status);
            Assert.Equal(1, workspace.Substitute("a", 0, "$1", new char[8], callbacks: callbacks).NativeReturnCode);
        }
        Assert.Equal(2, match);
        Assert.Equal(1, substitute);
        Assert.True(workspace.IsIdle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeSignedAbortAndGlobalCallbackCeilingAreCumulative(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile(@"(?C1)\Ga", domain), RuntimeFixture.Capacity);
        Pcre2OffsetPair[] pairs = [new(true, 7, 8)];
        var calls = 0;
        var limits = new Pcre2MatchOptions { Limits = new(maxCallouts: 2) };
        Pcre2SubstituteResult substitution;
        if (domain < 2)
        {
            var abort = new Pcre2ByteCallbacks(Match: (in _, __) => -123);
            var result = workspace.Match("a"u8, 0, pairs, callbacks: abort);
            Assert.Equal(Pcre2MatchStatus.Error, result.Status);
            Assert.Equal(-123, result.NativeReturnCode);
            Assert.Equal(new(true, 7, 8), pairs[0]);
            var callbacks = new Pcre2ByteCallbacks(Match: (in _, __) => { calls++; return 0; });
            substitution = workspace.Substitute("aaa"u8, 0, "x"u8, new byte[8], Pcre2SubstituteFlags.Global, limits, callbacks);
            Assert.Equal(2, calls);
            calls = 0;
            var session = workspace.BeginIteration(options: limits, bytes: callbacks);
            try
            {
                Assert.Equal(Pcre2MatchStatus.Match, session.Next("aaa"u8, pairs).Status);
                Assert.Equal(Pcre2MatchStatus.Match, session.Next("aaa"u8, pairs).Status);
                Assert.Equal(Pcre2MatchStatus.LimitExceeded, session.Next("aaa"u8, pairs).Status);
            }
            finally { session.Dispose(); }
        }
        else
        {
            var abort = new Pcre2CharCallbacks(Match: (in _, __) => -123);
            var result = workspace.Match("a", 0, pairs, callbacks: abort);
            Assert.Equal(Pcre2MatchStatus.Error, result.Status);
            Assert.Equal(-123, result.NativeReturnCode);
            Assert.Equal(new(true, 7, 8), pairs[0]);
            var callbacks = new Pcre2CharCallbacks(Match: (in _, __) => { calls++; return 0; });
            substitution = workspace.Substitute("aaa", 0, "x", new char[8], Pcre2SubstituteFlags.Global, limits, callbacks);
            Assert.Equal(2, calls);
            calls = 0;
            var session = workspace.BeginIteration(options: limits, chars: callbacks);
            try
            {
                Assert.Equal(Pcre2MatchStatus.Match, session.Next("aaa", pairs).Status);
                Assert.Equal(Pcre2MatchStatus.Match, session.Next("aaa", pairs).Status);
                Assert.Equal(Pcre2MatchStatus.LimitExceeded, session.Next("aaa", pairs).Status);
            }
            finally { session.Dispose(); }
        }
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, substitution.Failure.Code);
        Assert.Equal(2, calls);
        Assert.True(workspace.IsIdle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ActualAutomaticSitesAreBoundWithoutPlaceholderSuccess(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("a", domain,
            RuntimeFixture.Profile(domain, Pcre2CompileFlags.AutoCallout)), RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[1];
        List<int> positions = [];
        if (domain < 2)
        {
            Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a"u8, 0, pairs,
                callbacks: new(Match: (in context, _) => { Assert.Equal(255, context.Info.Number); positions.Add(context.Info.CurrentPosition); return 0; })).Status);
        }
        else
        {
            Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("a", 0, pairs,
                callbacks: new(Match: (in context, _) => { Assert.Equal(255, context.Info.Number); positions.Add(context.Info.CurrentPosition); return 0; })).Status);
        }

        Assert.Equal(new[] { 0, 1 }, positions);
        Assert.Equal(2, workspace.Executor.Metadata.GetCalloutSites().Length);
    }
}
