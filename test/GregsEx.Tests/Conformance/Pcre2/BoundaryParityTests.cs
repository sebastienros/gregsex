using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Conformance.Pcre2;

public sealed class BoundaryParityTests
{
    [Fact]
    public void C059RequiresTypedExecutionAndC058RetainsValidExcludedHostPattern()
    {
        var profile = new Pcre2Profile(extraFlags: Pcre2ExtraFlags.AllowLookaroundBsk);
        var budget = new CompileBudget(new());
        Assert.False(new Pcre2Compiler().TryCompile(@"(?=ab\K)".AsSpan(), profile, Pcre2Data.Instance,
            Pcre2ProjectionPolicy.WholeSubjectValidated, ref budget, out _, out var diagnostic));
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
        Assert.Equal(0, budget.ReservedBytes);
        budget = new(new());
        Assert.True(Pcre2ExecutorFactory.TryCompile(@"(?=ab\K)".AsSpan(), profile, ref budget, out var typed, out diagnostic), diagnostic.ToString());
        using var workspace = new Pcre2PreparedWorkspace(typed!, new(64, 512, 16, 512));
        var pairs = new Pcre2OffsetPair[1];
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("ab", 0, pairs).Status);
        Assert.Equal(new(true, 2, 0), pairs[0]);
        budget = new(new());
        Assert.True(Pcre2ExecutorFactory.TryCompile("a".AsSpan(), new(), ref budget, out var admitted, out diagnostic), diagnostic.ToString());
        // DFA has no admitted host method. Valid standard syntax remains accepted;
        // physical/JIT metadata queries have a source capability diagnosis, not fabricated values.
        Assert.False(admitted!.Metadata.TryGetInfo(Pcre2PatternInfoKey.Jitsize, out _, out diagnostic));
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
    }
}
