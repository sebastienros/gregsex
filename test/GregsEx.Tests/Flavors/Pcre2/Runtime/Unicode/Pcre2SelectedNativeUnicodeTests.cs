using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Unicode;

public sealed class Pcre2SelectedNativeUnicodeTests
{
    private static string Report(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test/corpora/pcre2/unicode/native-inputs.json")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "test/corpora/pcre2/unicode", file);
    }
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Report("native-inputs.json")));
        foreach (var c in inputs.RootElement.EnumerateArray())
        {
            yield return new TheoryDataRow<string>(c.GetProperty("id").GetString()!);
        }
    }
    [Theory, MemberData(nameof(Cases))]
    public void BoundedPinnedSourceNativeProbe(string id)
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Report("native-inputs.json")));
        var c = inputs.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var width = c.GetProperty("width").GetInt32();
        var utf = c.GetProperty("utf").GetBoolean();
        var p = new AssertionPlan(new((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : Pcre2CompileFlags.None));
        // Trees reflect the retained independent native source patterns, not a
        // production parser or unverified lowering result.
        if (id.StartsWith("g-", StringComparison.Ordinal))
        {
            p.Leaf(Pcre2NodeKind.Grapheme);
        }
        else if (id.StartsWith("s-k-", StringComparison.Ordinal))
        {
            p.Add(Pcre2NodeKind.ScriptRun, [p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.ResetStart), p.Literal(0x3b1))]);
        }
        else if (id.StartsWith("s-retry-", StringComparison.Ordinal) || id.StartsWith("s-atomic-", StringComparison.Ordinal))
        {
            var body = p.Capture(p.Repeat(p.Literal('a'), 1, 2));
            p.Seq(p.Add(Pcre2NodeKind.ScriptRun, [body], id.StartsWith("s-atomic-", StringComparison.Ordinal) ? 1 : 0), p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        }
        else
        {
            var body = p.Repeat(p.Leaf(Pcre2NodeKind.Dot, flags: p.Profile.Flags | Pcre2CompileFlags.Dotall));
            p.Seq(p.Add(Pcre2NodeKind.ScriptRun, [body]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
        }
        var f = new UnicodeFixture(p.Build());
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 7, 8), f.Program.GroupCount).ToArray();
        var units = c.GetProperty("subjectUnits").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var flags = Pcre2MatchFlags.Anchored | (c.GetProperty("hard").GetBoolean() ? Pcre2MatchFlags.PartialHard : 0);
        var options = new Pcre2MatchOptions { Flags = flags };
        var start = c.GetProperty("start").GetInt32();
        var actual = width == 8 ? f.Match([.. units.Select(x => (byte)x)], pairs, start, options) : f.Match(new string([.. units.Select(x => (char)x)]), pairs, start, options);
        var observed = File.ReadAllLines(Report("native-" + width + ".log")).Single(x => x.StartsWith(id + " ", StringComparison.Ordinal)).Split(' ');
        Assert.Equal(int.Parse(observed[1], System.Globalization.CultureInfo.InvariantCulture), actual.NativeReturnCode);
        for (var i = 2; i < observed.Length; i += 2)
        {
            Assert.Equal(new(true, int.Parse(observed[i], System.Globalization.CultureInfo.InvariantCulture), int.Parse(observed[i + 1], System.Globalization.CultureInfo.InvariantCulture)), pairs[(i - 2) / 2]);
        }
    }
}
