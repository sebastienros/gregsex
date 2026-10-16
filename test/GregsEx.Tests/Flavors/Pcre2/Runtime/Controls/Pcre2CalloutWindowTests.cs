using System.Text.Json;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2CalloutWindowTests
{
    private static string Report(int width)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var relative = $"test/corpora/pcre2/controls/native-callback-window-{width}.jsonl";
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, relative);
    }
    public static IEnumerable<TheoryDataRow<int, string>> Cases()
    {
        foreach (var width in new[] { 8, 16 })
        {
            foreach (var id in new[] { "initial-fragment", "invalid-prefix", "scan-raw", "scan-utf", "scan-accept", "called-lexical-assert-accept" })
            {
                yield return new TheoryDataRow<int, string>(width, id);
            }
        }
    }
    [Theory, MemberData(nameof(Cases))]
    public void ActualCallbackBlockKeepsInitialNativeLengthAcrossFragmentsAndScan(int width, string id)
    {
        var fragments = id is "initial-fragment" or "invalid-prefix";
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)width, Pcre2CompileFlags.NoStartOptimize | Pcre2CompileFlags.NoAutoPossess |
            (fragments ? Pcre2CompileFlags.MatchInvalidUtf : id == "scan-utf" ? Pcre2CompileFlags.Utf : 0));
        var pattern = fragments ? "(?C1)b" : id == "called-lexical-assert-accept" ? "(?=(?<r>a(*ACCEPT)b))(?&r)c" :
            id == "scan-accept" ? "(a)(*scs:(1)(?C1)a(*ACCEPT)b)(?C2)b" : "(a)(*scs:(1)(?C1)a)(?C2)b";
        var invalid = width == 8 ? 255 : 0xd800;
        int[] units = id == "initial-fragment" ? [97, invalid, 98] : id == "invalid-prefix" ? [invalid, 97, invalid, 98] : id == "called-lexical-assert-accept" ? [97, 99] : [97, 98];
        var f = new ControlFixture(pattern, profile);
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 77, 99), f.Program.GroupCount).ToArray();
        var actual = new List<Pcre2CalloutInfo>();
        Pcre2MatchResult result;
        if (width == 8)
        {
            var callbacks = new Pcre2ByteCallbacks((in c, _) =>
            { actual.Add(c.Info); Assert.Equal(units.Length, c.Subject.Length); return 0; });
            result = f.Match([.. units.Select(v => (byte)v)], pairs, callbacks: callbacks);
        }
        else
        {
            var callbacks = new Pcre2CharCallbacks((in c, _) =>
            { actual.Add(c.Info); Assert.Equal(units.Length, c.Subject.Length); return 0; });
            result = f.Match(new string([.. units.Select(v => (char)v)]), pairs, callbacks: callbacks);
        }
        var rows = File.ReadLines(Report(width)).Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.GetProperty("id").GetString() == id).ToArray();
        var expected = rows.Where(row => row.GetProperty("event").GetString() == "callout").ToArray();
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected[i].GetProperty("number").GetInt32(), actual[i].Number);
            Assert.Equal(expected[i].GetProperty("subjectLength").GetInt32(), actual[i].SubjectLength);
            Assert.Equal(expected[i].GetProperty("currentPosition").GetInt32(), actual[i].CurrentPosition);
            Assert.Equal(expected[i].GetProperty("startMatch").GetInt32(), actual[i].StartMatch);
            Assert.Equal(expected[i].GetProperty("flags").GetUInt32(), (uint)actual[i].Flags);
            Assert.Equal(expected[i].GetProperty("captureTop").GetInt32(), actual[i].CaptureTop);
            Assert.Equal(expected[i].GetProperty("captureLast").GetInt32(), actual[i].CaptureLast);
        }
        var terminal = rows.Single(row => row.GetProperty("event").GetString() == "match");
        Assert.Equal(terminal.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
        if (result.NativeReturnCode >= 0)
        {
            Assert.Equal(new(true, terminal.GetProperty("start").GetInt32(), terminal.GetProperty("end").GetInt32()), pairs[0]);
        }
        else
        {
            Assert.Equal(Pcre2MatchStatus.Error, result.Status);
            Assert.All(pairs, pair => Assert.Equal(new(true, 77, 99), pair)); // no undefined error captures inspected
        }
        Assert.True(f.Released);
    }
}
