using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

public sealed class ParityCapacityTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void OriginalC048UsesExplicitNonMatchedCapacityAndSeparateAutoFull(int domain)
    {
        // Independent native10.46/b2bd4254 C048-reduction.json and published erratum.
        // Flags stay zero; the original cap1 invocation is never simulated with MATCHED.
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)", domain), RuntimeFixture.Capacity);
        foreach (var capacity in new int?[] { 1, 2, null })
        {
            var result = Substitute(workspace, domain, "$1$1", capacity);
            Assert.Equal(capacity == 1 ? -54 : 1, result.NativeReturnCode);
            Assert.Equal(2, result.OutputUnits);
            Assert.True(workspace.IsIdle);
        }
        // Non-MATCHED must ignore supplied precomputed data; it performs the actual match.
        Pcre2OffsetPair[] stale = [new(true, 9, 9)];
        var original = domain < 2
            ? workspace.Substitute("a"u8, 0, "$1$1"u8, new byte[128], precomputed: stale, pairCapacity: 1)
            : workspace.Substitute("a", 0, "$1$1", new char[128], precomputed: stale, pairCapacity: 1);
        Assert.Equal(-54, original.NativeReturnCode);
        Assert.Equal(new(true, 9, 9), stale[0]);
        Assert.Equal(1, Substitute(workspace, domain, "$1$1", null).NativeReturnCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ActualChosenCapacityIsAdmittedBeforeAllocationAndDenialAllowsReuse(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)", domain), RuntimeFixture.Capacity);
        var reserved = workspace.ReservedBytes;
        // Existing fixed256 host reserve plus16bytes per actually allocated pair.
        Assert.Equal(1, Substitute(workspace, domain, "$0", 1, limits: new(maxMemoryBytes: 272)).NativeReturnCode);
        var denied = Substitute(workspace, domain, "$0", 2, limits: new(maxMemoryBytes: 272));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, denied.Failure.Code);
        Assert.Equal(288, denied.Failure.RequiredCapacity);
        denied = Substitute(workspace, domain, "$0", int.MaxValue);
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, denied.Failure.Code);
        Assert.Equal(256L + 16L * int.MaxValue, denied.Failure.RequiredCapacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => Substitute(workspace, domain, "$0", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Substitute(workspace, domain, "$0", -1));
        Assert.True(workspace.IsIdle);
        Assert.Equal(reserved, workspace.ReservedBytes);
        Assert.Equal(1, Substitute(workspace, domain, "$1$1", 2).NativeReturnCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void UnknownUnsetAndUnavailableRemainDistinctForExplicitData(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)(b)?", domain), RuntimeFixture.Capacity);
        Assert.Equal(-54, Substitute(workspace, domain, "$2", 2, Pcre2SubstituteFlags.UnsetEmpty | Pcre2SubstituteFlags.UnknownUnset).NativeReturnCode);
        Assert.Equal((int)Pcre2ErrorCode.Unset, Substitute(workspace, domain, "$2", 3).NativeReturnCode);
        Assert.Equal(1, Substitute(workspace, domain, "$2", 3, Pcre2SubstituteFlags.UnsetEmpty).NativeReturnCode);
        Assert.Equal((int)Pcre2ErrorCode.Nosubstring, Substitute(workspace, domain, "$3", 3).NativeReturnCode);
        Assert.Equal(1, Substitute(workspace, domain, "$3", 3, Pcre2SubstituteFlags.UnsetEmpty | Pcre2SubstituteFlags.UnknownUnset).NativeReturnCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchStagingIsFullAndSubstitutionViewUsesExplicitSafeVector(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)(?C7)(b)?", domain), RuntimeFixture.Capacity);
        int matches = 0, substitutions = 0, expectedCapacity = 0;
        void InspectMatch(ReadOnlySpan<Pcre2OffsetPair> pairs)
        {
            matches++;
            Assert.Equal(3, pairs.Length);
            Assert.False(pairs[0].Participated);
            Assert.Equal(new(true, 0, 1), pairs[1]);
            Assert.False(pairs[2].Participated);
        }
        void InspectSubstitution(ReadOnlySpan<Pcre2OffsetPair> pairs)
        {
            substitutions++;
            Assert.Equal(expectedCapacity, pairs.Length);
            Assert.Equal(new(true, 0, 1), pairs[0]);
            if (pairs.Length > 1)
            {
                Assert.Equal(new(true, 0, 1), pairs[1]);
            }

            for (var i = 2; i < pairs.Length; i++)
            {
                Assert.False(pairs[i].Participated);
            }
        }
        var bytes = new Pcre2ByteCallbacks(
            Match: (in c, _) => { InspectMatch(c.Captures); return 0; },
            Substitute: (in c, _) => { InspectSubstitution(c.Captures); return 0; });
        var chars = new Pcre2CharCallbacks(
            Match: (in c, _) => { InspectMatch(c.Captures); return 0; },
            Substitute: (in c, _) => { InspectSubstitution(c.Captures); return 0; });
        foreach (var cap in new int?[] { 1, 2, 3, 8, null })
        {
            expectedCapacity = cap ?? 3;
            var result = domain < 2 ? workspace.Substitute("a"u8, 0, "$0"u8, new byte[8], callbacks: bytes, pairCapacity: cap)
                : workspace.Substitute("a", 0, "$0", new char[8], callbacks: chars, pairCapacity: cap);
            Assert.Equal(1, result.NativeReturnCode);
        }
        // An expansion failure runs the real matcher but never invokes the substitute callback.
        Assert.Equal(-54, domain < 2 ? workspace.Substitute("a"u8, 0, "$1"u8, new byte[8], callbacks: bytes, pairCapacity: 1).NativeReturnCode
            : workspace.Substitute("a", 0, "$1", new char[8], callbacks: chars, pairCapacity: 1).NativeReturnCode);
        Assert.Equal(6, matches);
        Assert.Equal(5, substitutions);
        Assert.Throws<ApplicationException>(() =>
        {
            if (domain < 2)
            {
                workspace.Substitute("a"u8, 0, "$0"u8, new byte[8], pairCapacity: 1,
                callbacks: new(Substitute: (in _, __) => throw new ApplicationException()));
            }
            else
            {
                workspace.Substitute("a", 0, "$0", new char[8], pairCapacity: 1,
                callbacks: new(Substitute: (in _, __) => throw new ApplicationException()));
            }
        });
        Assert.True(workspace.IsIdle);
        Assert.Equal(1, Substitute(workspace, domain, "$0", 1).NativeReturnCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MatchedDataCapacityAndFrozenDefaultInterfaceRemainUnchanged(int domain)
    {
        using var workspace = new Pcre2PreparedWorkspace(RuntimeFixture.Compile("(a)", domain), RuntimeFixture.Capacity);
        var shortPairs = new Pcre2OffsetPair[1];
        var match = RuntimeFixture.Match(workspace, domain, "a", shortPairs);
        Assert.Equal(0, match.NativeReturnCode);
        var before = shortPairs.ToArray();
        var result = domain < 2
            ? workspace.Substitute("a"u8, 0, "$1$1"u8, new byte[128], Pcre2SubstituteFlags.Matched, precomputed: shortPairs, precomputedResult: match, pairCapacity: 8)
            : workspace.Substitute("a", 0, "$1$1", new char[128], Pcre2SubstituteFlags.Matched, precomputed: shortPairs, precomputedResult: match, pairCapacity: 8);
        Assert.Equal(-54, result.NativeReturnCode);
        Assert.Equal(2, result.OutputUnits);
        Assert.Equal(before, shortPairs);
        Assert.Equal(1, Substitute(workspace, domain, "$1$1", null).NativeReturnCode);
    }

    private static Pcre2SubstituteResult Substitute(Pcre2PreparedWorkspace workspace, int domain, string replacement,
        int? pairCapacity, Pcre2SubstituteFlags flags = default, Pcre2HostLimits? limits = null)
    {
        if (domain < 2)
        {
            var output = new byte[128];
            var result = workspace.Substitute("a"u8, 0, System.Text.Encoding.UTF8.GetBytes(replacement), output, flags, limits: limits, pairCapacity: pairCapacity);
            if (result.NativeReturnCode >= 0 && replacement == "$1$1")
            {
                Assert.True(output.AsSpan(0, result.OutputUnits).SequenceEqual("aa"u8));
            }

            return result;
        }
        else
        {
            var output = new char[128];
            var result = workspace.Substitute("a", 0, replacement, output, flags, limits: limits, pairCapacity: pairCapacity);
            if (result.NativeReturnCode >= 0 && replacement == "$1$1")
            {
                Assert.True(output.AsSpan(0, result.OutputUnits).SequenceEqual("aa"));
            }

            return result;
        }
    }
}
