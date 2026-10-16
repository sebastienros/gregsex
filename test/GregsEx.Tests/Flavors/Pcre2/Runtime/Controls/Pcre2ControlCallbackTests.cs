using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2ControlCallbackTests
{
    public static IEnumerable<TheoryDataRow<int, string, string, string>> Traces()
    {
        (string Pattern, string Input, string Trace)[] rules =
        [
            ("a(?C1)b|a(?C2)c", "ac", "1@1:1,2@1:2"),
            ("b(?C1)", "ab", "1@2:3"),
            ("(?=a)(?C1)a", "a", "1@0:1"),
            ("(?!b)(?C1)a", "a", "1@0:3"),
            ("(?(?=a)(?C1)a|b)", "a", "1@0:3"),
            ("(?(DEFINE)(?<r>a))(?&r)(?C1)b", "ab", "1@1:1"),
            ("(*sr:a)(?C1)b", "ab", "1@1:1"),
            ("(?=a(*ACCEPT))(?C1)a", "a", "1@0:3"),
            ("(?(?=a(*ACCEPT))(?C1)a|b)", "a", "1@0:3"),
            ("(?(DEFINE)(?<r>a(*ACCEPT)b))(?&r)(?C1)c", "ac", "1@1:1"),
            ("a\\K(?C1)b", "ab", "1@1:1")
        ];
        for (var domain = 0; domain < 4; domain++)
        {
            foreach (var (Pattern, Input, Trace) in rules)
            {
                yield return new TheoryDataRow<int, string, string, string>(domain, Pattern, Input, Trace);
            }
        }
    }
    [Theory, MemberData(nameof(Traces))]
    public void CallbackFlagsFollowRealSourceReturnSites(int domain, string pattern, string input, string trace)
    {
        var f = new ControlFixture(pattern, ControlFixture.Profile(domain,
            Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoAutoPossess));
        var events = new List<string>();
        var pairs = new Pcre2OffsetPair[f.Program.GroupCount];
        Pcre2MatchResult result;
        if (domain < 2)
        {
            var callbacks = new Pcre2ByteCallbacks((in c, _) =>
            { events.Add($"{c.Info.Number}@{c.Info.CurrentPosition}:{(uint)c.Info.Flags}"); Assert.Equal(pattern == "b(?C1)" ? 1 : 0, c.Info.StartMatch); return 0; });
            result = f.Match([.. input.Select(c => (byte)c)], pairs, callbacks: callbacks);
        }
        else
        {
            var callbacks = new Pcre2CharCallbacks((in c, _) =>
            { events.Add($"{c.Info.Number}@{c.Info.CurrentPosition}:{(uint)c.Info.Flags}"); Assert.Equal(pattern == "b(?C1)" ? 1 : 0, c.Info.StartMatch); return 0; });
            result = f.Match(input, pairs, callbacks: callbacks);
        }
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(trace, string.Join(',', events));
        Assert.True(f.Released);
    }

    [Fact]
    public void GlobalNomatchMarkDiffersFromRollbackableSuccessMarkAndExplicitLengthSurvives()
    {
        var f = new ControlFixture("(?!a(*PRUNE:X)b)(?C1)ac");
        var pairs = new Pcre2OffsetPair[1];
        var count = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) =>
        { count++; Assert.True(c.Info.HasMark); Assert.Equal("X", c.Mark.ToString()); return 0; });
        var result = f.Match("ac", pairs, callbacks: callbacks);
        Assert.Equal(1, count);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(-1, result.MarkIndex);
        var binary = new ControlFixture("a(*MARK:X\0A)b(*SKIP:X\0B)c|ab");
        Assert.Equal(Pcre2MatchStatus.NoMatch, binary.Match("ab", pairs).Status);
        Assert.Equal("X\0A", binary.Program.NativeChars(binary.Program.Marks[0]).ToString());
    }

    [Theory]
    [InlineData(1, (int)Pcre2MatchStatus.Match)]
    [InlineData(-777, (int)Pcre2MatchStatus.Error)]
    public void RealCallbackResultControlsPathAndSignedAbort(int returned, int status)
    {
        var f = new ControlFixture("a(?C1)b|ab");
        Pcre2OffsetPair[] pairs = [new(true, 7, 9)];
        var count = 0;
        var callbacks = new Pcre2CharCallbacks((in c, _) => { count++; return returned; });
        var result = f.Match("ab", pairs, callbacks: callbacks);
        Assert.Equal(1, count);
        Assert.Equal((Pcre2MatchStatus)status, result.Status);
        if (returned < 0)
        { Assert.Equal(returned, result.NativeReturnCode); Assert.Equal(new(true, 7, 9), pairs[0]); }
        else
        {
            Assert.Equal(new(true, 0, 2), pairs[0]);
        }

        Assert.True(f.Released);
    }

    [Fact]
    public void ErrorRetainsExecutedGlobalMarkAfterPathRollback()
    {
        var f = new ControlFixture("a(*MARK:X)(*FAIL)|a(?C1)b");
        Pcre2OffsetPair[] pairs = [new(true, 7, 9)];
        var callbacks = new Pcre2CharCallbacks((in c, _) =>
        { Assert.True(c.Info.HasMark); Assert.Equal("X", c.Mark.ToString()); return -777; });
        var result = f.Match("ab", pairs, callbacks: callbacks);
        Assert.Equal(Pcre2MatchStatus.Error, result.Status);
        Assert.Equal(-777, result.NativeReturnCode);
        Assert.Equal("X", f.Program.NativeChars(f.Program.Marks[result.MarkIndex]).ToString());
        Assert.Equal(new(true, 7, 9), pairs[0]); // no error capture/startchar equality claim
    }
}
