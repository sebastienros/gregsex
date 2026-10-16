using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Unicode;

using GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2NativeUnicodeTests
{
    private static string Corpus(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test/corpora/pcre2", file)))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "test/corpora/pcre2", file);
    }
    private static bool Selected(string id) => id is "PCRE-C014" or "PCRE-C015" or "PCRE-C015-cross";
    public static IEnumerable<TheoryDataRow<string>> Cases()
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Corpus("inputs.json")));
        foreach (var input in inputs.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (Selected(input.GetProperty("id").GetString()!))
            {
                yield return new TheoryDataRow<string>(input.GetProperty("id").GetString()!);
            }
        }
    }
    [Theory, MemberData(nameof(Cases))]
    public void PinnedIndependentNativeObservation(string id)
    {
        using var inputs = JsonDocument.Parse(File.ReadAllText(Corpus("inputs.json")));
        using var observations = JsonDocument.Parse(File.ReadAllText(Corpus("observations.json")));
        var input = inputs.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var native = observations.RootElement.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id)
            .GetProperty("events").EnumerateArray().Single(e => e.GetProperty("event").GetString() == "match");
        var profile = new Pcre2Profile((Pcre2CodeUnitWidth)input.GetProperty("width").GetInt32(),
            (Pcre2CompileFlags)input.GetProperty("compileFlags").GetUInt32(), (Pcre2ExtraFlags)input.GetProperty("extraFlags").GetUInt32(),
            (Pcre2Newline)input.GetProperty("newline").GetInt32(), (Pcre2Bsr)input.GetProperty("bsr").GetInt32());
        var p = new AssertionPlan(profile);
        if (id == "PCRE-C014")
        {
            p.Leaf(Pcre2NodeKind.Grapheme);
        }
        else
        {
            p.Add(Pcre2NodeKind.ScriptRun, [p.Repeat(p.Literal('a'), 1)]);
        }

        var fixture = new UnicodeFixture(p.Build());
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 1234567, 1234567), input.GetProperty("pairCapacity").GetInt32()).ToArray();
        var units = input.GetProperty("subjectUnits").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var options = new Pcre2MatchOptions { Flags = (Pcre2MatchFlags)input.GetProperty("matchFlags").GetUInt32() };
        var start = input.GetProperty("startAt").GetInt32();
        var result = profile.Width == Pcre2CodeUnitWidth.Eight ? fixture.Match([.. units.Select(v => (byte)v)], pairs, start, options) :
            fixture.Match(new string([.. units.Select(v => (char)v)]), pairs, start, options);
        Assert.Equal(native.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
        if (native.GetProperty("startChar").ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(native.GetProperty("startChar").GetInt32(), result.StartChar);
        }

        var group = 0;
        foreach (var pair in native.GetProperty("pairs").EnumerateArray())
        {
            var endpoints = pair.EnumerateArray().ToArray();
            Assert.Equal(endpoints[0].ValueKind != JsonValueKind.Null, pairs[group].Participated);
            if (pairs[group].Participated)
            { Assert.Equal(endpoints[0].GetInt32(), pairs[group].Start); Assert.Equal(endpoints[1].GetInt32(), pairs[group].End); }
            group++;
        }
    }
}
