using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Composition;

public class Pcre2CompositionTests
{
    public static IEnumerable<TheoryDataRow<int, string, string, int, int>> Vectors()
    {
        (string Pattern, string Input, int Start, int End)[] cases = [
            (@"(ab)\1", "xxabab", 2, 6),
            (@"(?<=a{1,2})b", "aab", 2, 3),
            (@"(?=(a+))\1", "aaa", 0, 3),
            (@"(?!b)a", "ba", 1, 2),
            (@"(?<p>\((?:[^()]|(?&p))*\))", "x(())", 1, 5),
            (@"(?(DEFINE)(?<p>ab))(?&p)", "zab", 1, 3),
            (@"(a)?(?(1)b|c)", "ab", 0, 2),
            (@"(?(?=a)a|b)", "b", 0, 1),
            (@"(?|(a)|(b))\1", "bb", 0, 2),
            (@"(?J)(?<x>a)|(?<x>b)", "b", 0, 1),
            (@"(?>a|ab)c", "abcac", 3, 5),
            (@"a++b", "aaab", 0, 4),
            (@"a\Kb", "ab", 1, 2),
            (@"a(*ACCEPT)b", "a", 0, 1),
            (@"a(*PRUNE)b|ac", "acxab", 3, 5),
            (@"a(*THEN)b|ac", "ac", 0, 2),
            (@"a(*SKIP)(*FAIL)|b", "ab", 1, 2),
            (@"(*MARK:hit)a", "za", 1, 2),
            (@"(?<=a(*THEN)c|b)d", "bd", 1, 2),
            (@"(?*a|ab)\w+c", "abc", 0, 3),
            (@"\R", "x\r\ny", 1, 3),
            (@"\X", "ab", 0, 1),
            (@"(*script_run:a+)", "aa", 0, 2)
        ];
        for (var domain = 0; domain < 4; domain++)
        {
            foreach (var (Pattern, Input, Start, End) in cases)
            {
                yield return new TheoryDataRow<int, string, string, int, int>(domain, Pattern, Input, Start, End);
            }
        }
    }
    [Theory]
    [MemberData(nameof(Vectors))]
    public void RealFactoryBindsAdvancedSemantics(int domain, string pattern, string input, int start, int end)
    {
        var executor = RuntimeFixture.Compile(pattern, domain);
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
        var pairs = new Pcre2OffsetPair[executor.Program.GroupCount + 1];
        pairs[^1] = new(true, 77, 88);
        var result = RuntimeFixture.Match(workspace, domain, input, pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, start, end), pairs[0]);
        Assert.Equal(new(true, 77, 88), pairs[^1]);
        var bytes = workspace.ReservedBytes;
        Assert.Equal(result, RuntimeFixture.Match(workspace, domain, input, pairs));
        Assert.Equal(bytes, workspace.ReservedBytes);
        Assert.True(workspace.IsIdle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShortSuccessPartialAndFailurePublicationPreserveNativeResults(int domain)
    {
        var executor = RuntimeFixture.Compile("(a)(b)", domain);
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
        var sentinel = new Pcre2OffsetPair(true, 42, 43);
        var pairs = new[] { sentinel };
        Assert.Equal(0, RuntimeFixture.Match(workspace, domain, "ab", pairs).NativeReturnCode);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        pairs[0] = sentinel;
        Assert.Equal(Pcre2MatchStatus.NoMatch, RuntimeFixture.Match(workspace, domain, "zzz", pairs).Status);
        Assert.Equal(sentinel, pairs[0]);
        var full = new[] { sentinel, sentinel, sentinel, sentinel };
        var partial = RuntimeFixture.Match(workspace, domain, "a", full, new() { Flags = Pcre2MatchFlags.PartialHard });
        Assert.Equal(Pcre2MatchStatus.Partial, partial.Status);
        Assert.Equal(new(true, 0, 1), full[0]);
        Assert.All(full.Skip(1), p => Assert.Equal(sentinel, p));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RealHostUsesUpdatedOuterInvocationOffset(int domain)
    {
        var executor = RuntimeFixture.Compile(@"\G(a)", domain);
        using var workspace = new Pcre2PreparedWorkspace(executor, RuntimeFixture.Capacity);
        if (domain < 2)
        {
            var output = new byte[16];
            var result = workspace.Substitute("aaa"u8, 0, "$1$1"u8, output, Pcre2SubstituteFlags.Global);
            Assert.Equal(3, result.NativeReturnCode);
            Assert.Equal("aaaaaa"u8.ToArray(), output[..result.OutputUnits]);
            Assert.Equal(0, output[result.OutputUnits]);
            using var iterator = workspace.BeginIteration();
            var pairs = new Pcre2OffsetPair[2];
            for (var i = 0; i < 3; i++)
            { Assert.Equal(Pcre2MatchStatus.Match, iterator.Next("aaa"u8, pairs).Status); Assert.Equal(new(true, i, i + 1), pairs[0]); }
            Assert.Equal(Pcre2MatchStatus.NoMatch, iterator.Next("aaa"u8, pairs).Status);
        }
        else
        {
            var output = new char[16];
            var result = workspace.Substitute("aaa", 0, "$1$1", output, Pcre2SubstituteFlags.Global);
            Assert.Equal(3, result.NativeReturnCode);
            Assert.Equal("aaaaaa", new string(output, 0, result.OutputUnits));
            Assert.Equal('\0', output[result.OutputUnits]);
            using var iterator = workspace.BeginIteration();
            var pairs = new Pcre2OffsetPair[2];
            for (var i = 0; i < 3; i++)
            { Assert.Equal(Pcre2MatchStatus.Match, iterator.Next("aaa", pairs).Status); Assert.Equal(new(true, i, i + 1), pairs[0]); }
            Assert.Equal(Pcre2MatchStatus.NoMatch, iterator.Next("aaa", pairs).Status);
        }
        Assert.True(workspace.IsIdle);
    }

    [Fact]
    public void MetadataKeepsExactProducerFactsAndFullNames()
    {
        var executor = RuntimeFixture.Compile(@"(?J)(?-J)(?<x>a)\C{0}[\r]", 3);
        Assert.True(executor.Metadata.TryGetInfo(Pcre2PatternInfoKey.Jchanged, out var j, out _));
        Assert.Equal(1, j);
        Assert.True(executor.Metadata.TryGetInfo(Pcre2PatternInfoKey.Hasbackslashc, out var c, out _));
        Assert.Equal(1, c);
        Assert.True(executor.Metadata.TryGetInfo(Pcre2PatternInfoKey.Hascrorlf, out var newline, out _));
        Assert.Equal(1, newline);
        Assert.Equal(1, executor.Metadata.GroupNumberFromName("x"));
        Assert.False(executor.Metadata.TryGetInfo(Pcre2PatternInfoKey.Jitsize, out _, out var diagnostic));
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
    }

    [Fact]
    public void FactoryFailureReleasesOnlyItsOwnReservations()
    {
        var executor = RuntimeFixture.Compile("(?<x>a)(?C1)");
        var budget = new CompileBudget(new(maxPlanBytes: executor.RetainedBytes - 1));
        Assert.False(new Pcre2ExecutorFactory().TryCreate(executor.Program, GregsEx.Flavors.Pcre2.Tables.Pcre2Data.Instance,
            ref budget, out var failed, out var diagnostic));
        Assert.Null(failed);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, diagnostic.Diagnostic.Code);
        budget = new(new(maxCompileMemoryBytes: 129));
        Assert.False(new Pcre2ExecutorFactory().TryCreate(executor.Program, GregsEx.Flavors.Pcre2.Tables.Pcre2Data.Instance,
            ref budget, out _, out _));
        Assert.Equal(0, budget.ReservedBytes);
        budget = new(new(maxCompileWork: 1));
        Assert.False(Pcre2ExecutorFactory.TryCompile("a", new(), ref budget, out _, out _));
        Assert.Equal(0, budget.ReservedBytes);
        budget = new(new());
        Assert.True(Pcre2ExecutorFactory.TryCompile("a", new(), ref budget, out _, out _));
    }
}
