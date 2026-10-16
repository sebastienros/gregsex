using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Kernel;

public sealed class Pcre2NativeKernelTests
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
    private static bool Selected(string id) => id.StartsWith("PCRE-C", StringComparison.Ordinal) &&
        int.TryParse(id.AsSpan(6, 3), out var n) && n is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or
        16 or 17 or 18 or 19 or 26 or 29 or 35 or 36 or 37 or 40 or 43 or 44 or 45 or 66 or 67;
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
        var p = new KernelPlan(profile);
        var n = int.Parse(id.AsSpan(6, 3), System.Globalization.CultureInfo.InvariantCulture);
        // Explicit semantic trees mapped to independently frozen source patterns;
        // this switch is not a parser and cannot derive expected results.
        switch (n)
        {
            case 1:
                p.Literal(0);
                break;
            case 2:
            case 3:
            case 4:
                p.Leaf(Pcre2NodeKind.Dot);
                break;
            case 5:
            case 6:
                var digit = profile.Ucp ? p.Class(Pcre2ClassKind.Property, property: new(Pcre2PropertyKind.ParticularCategory, 13)) :
                    p.Class(Pcre2ClassKind.TableClass, table: 2);
                p.Leaf(Pcre2NodeKind.Class, digit);
                break;
            case 7:
            case 40:
            case 44:
            case 45:
                p.Literal('a');
                break;
            case 8:
            case 66:
            case 67:
                p.Leaf(Pcre2NodeKind.Newline);
                break;
            case 9:
                p.Leaf(Pcre2NodeKind.Class, p.Class(Pcre2ClassKind.TableClass, table: 2));
                break;
            case 10:
            case 11:
                var alphabet = p.Class(Pcre2ClassKind.Intervals, intervals: [new('a', 'z')]);
                var vowels = p.Class(Pcre2ClassKind.Intervals, intervals: [new('a', 'a'), new('e', 'e'), new('i', 'i'), new('o', 'o'), new('u', 'u')]);
                var notVowels = p.Class(Pcre2ClassKind.Complement, vowels);
                p.Leaf(Pcre2NodeKind.Class, p.Class(Pcre2ClassKind.Intersection, alphabet, notVowels));
                break;
            case 12:
                p.Leaf(Pcre2NodeKind.Class, p.Class(Pcre2ClassKind.Property, property: new(Pcre2PropertyKind.Script, 1)));
                break;
            case 13:
                p.Literal('I');
                break;
            case 16:
            case 17:
                p.Seq(p.Leaf(Pcre2NodeKind.StartOffset), p.Literal('a'));
                break;
            case 18:
                p.Seq(p.Repeat(p.Literal('a'), 1, -1, Pcre2Quantifier.Possessive), p.Literal('a'));
                break;
            case 19:
                p.Alt(p.Capture(p.Literal('a')), p.Capture(p.Literal('b')));
                break;
            case 26:
                p.Seq(p.Literal('a', profile.Flags | Pcre2CompileFlags.Caseless), p.Literal('b'));
                break;
            case 29:
                p.Leaf(Pcre2NodeKind.CodeUnit);
                break;
            case 35:
                p.Seq(p.Literal('a'), p.Literal('b'), p.Literal('c'));
                break;
            case 36:
            case 37:
                p.Seq(p.Literal('a'), p.Repeat(p.Literal('b'), 1));
                break;
            case 43:
                p.Seq(p.Capture(p.Literal('a'), 1), p.Capture(p.Literal('b'), 2));
                break;
            default:
                throw new InvalidOperationException("Unmapped native case.");
        }
        var fixture = new KernelFixture(p.Build());
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
