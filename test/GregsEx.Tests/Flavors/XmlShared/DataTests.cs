using System.Globalization;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using Xunit;

namespace GregsEx.Tests.Flavors.XmlShared.Semantics;

public class DataTests
{
    internal static XmlUnicodeData Data(UnicodeVersion version = UnicodeVersion.Unicode16_0)
    {
        var budget = new CompileBudget(new());
        Assert.True(XmlUnicodeData.TryCreate(version, ref budget, out var data, out var diagnostic), diagnostic.ToString());
        Assert.Equal(XmlUnicodeData.GetRetainedBytes(version), budget.ReservedBytes);
        return data!;
    }
    private static string Inputs
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GregsEx.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "tools/unicode/xml/inputs");
        }
    }
    private static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static string InputsFor(UnicodeVersion version) => version == UnicodeVersion.Unicode16_0
        ? Inputs : Path.Combine(Directory.GetParent(Inputs)!.FullName, "17.0.0", "inputs");

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0)]
    [InlineData(UnicodeVersion.Unicode17_0)]
    public void EveryRuntimeCategoryMatchesOriginalUnicodeDataIncludingCnAndAlgorithmicRanges(UnicodeVersion version)
    {
        var data = Data(version);
        var categories = new string[0x110000];
        Array.Fill(categories, "Cn");
        int start = -1, firstLast = 0;
        foreach (var line in File.ReadLines(Path.Combine(InputsFor(version), "UnicodeData.txt")))
        {
            var f = line.Split(';');
            var cp = Hex(f[0]);
            if (f[1].EndsWith(", First>", StringComparison.Ordinal))
            {
                start = cp;
            }
            else if (f[1].EndsWith(", Last>", StringComparison.Ordinal))
            { Assert.True(start >= 0); Array.Fill(categories, f[2], start, cp - start + 1); start = -1; firstLast++; }
            else
            {
                categories[cp] = f[2];
            }
        }
        Assert.Equal(-1, start);
        Assert.Equal(version == UnicodeVersion.Unicode16_0 ? 19 : 20, firstLast);
        string[] names = ["L", "Lu", "Ll", "Lt", "Lm", "Lo", "M", "Mn", "Mc", "Me", "N", "Nd", "Nl", "No", "P", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po", "Z", "Zs", "Zl", "Zp", "S", "Sm", "Sc", "Sk", "So", "C", "Cc", "Cf", "Co", "Cn"];
        foreach (var name in names)
        {
            Assert.True(data.TryGetCategory(name, out var actual));
            var interval = 0;
            for (var cp = 0; cp <= 0x10FFFF; cp++)
            {
                while (interval < actual.Length && cp > actual[interval].Last)
                {
                    interval++;
                }

                var included = interval < actual.Length && cp >= actual[interval].First;
                var expected = XmlAlphabet.Contains(cp) && (name.Length == 1 ? categories[cp][0] == name[0] : categories[cp] == name);
                if (included != expected)
                {
                    Assert.Fail($"{name} at U+{cp:X}: expected {expected}");
                }
            }
        }
        foreach (var bad in new[] { "Cs", "Letter", "gc=L", "l", " L", "L ", "LC", "Unknown" })
        {
            Assert.False(data.TryGetCategory(bad, out _));
        }
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0)]
    [InlineData(UnicodeVersion.Unicode17_0)]
    public void EveryRuntimeBlockMatchesOriginalBlocksAndExactLegacyAliases(UnicodeVersion version)
    {
        var data = Data(version);
        var count = 0;
        foreach (var line in File.ReadLines(Path.Combine(InputsFor(version), "Blocks.txt")))
        {
            var text = line.Split('#')[0].Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var fields = text.Split(';');
            var bounds = fields[0].Trim().Split("..");
            int a = Hex(bounds[0]), b = Hex(bounds[1]);
            var name = string.Concat(fields[1].Where(c => !char.IsWhiteSpace(c) && c != '_'));
            Assert.True(data.TryGetBlock(name, out var actual));
            count++;
            var expected = new List<XmlInterval>();
            foreach (var legal in data.Alphabet)
            {
                if (a <= legal.Last && legal.First <= b)
                {
                    expected.Add(new(Math.Max(a, legal.First), Math.Min(b, legal.Last)));
                }
            }

            Assert.Equal(expected, actual.ToArray());
        }
        Assert.Equal(version == UnicodeVersion.Unicode16_0 ? 338 : 346, count);
        Assert.True(data.TryGetBlock("Greek", out var greek));
        Assert.Equal(new XmlInterval(0x370, 0x3FF), Assert.Single(greek.ToArray()));
        Assert.True(data.TryGetBlock("CombiningMarksforSymbols", out var marks));
        Assert.Equal(new XmlInterval(0x20D0, 0x20FF), Assert.Single(marks.ToArray()));
        Assert.True(data.TryGetBlock("PrivateUse", out var privateUse));
        Assert.Equal(new XmlInterval[] { new(0xE000, 0xF8FF), new(0xF0000, 0xFFFFD), new(0x100000, 0x10FFFD) }, privateUse.ToArray());
        foreach (var bad in new[] { "basicLatin", "Basic Latin", "Basic_Latin", "IsBasicLatin", "ASCII", "", "NotARealBlock" })
        {
            Assert.False(data.TryGetBlock(bad, out _));
        }
    }

    [Theory]
    [InlineData(UnicodeVersion.Unicode16_0)]
    [InlineData(UnicodeVersion.Unicode17_0)]
    public void EveryRuntimeCaseMappingMatchesFullDefaultSourceSequences(UnicodeVersion version)
    {
        var data = Data(version);
        var lower = new Dictionary<int, int[]>();
        var upper = new Dictionary<int, int[]>();
        foreach (var line in File.ReadLines(Path.Combine(InputsFor(version), "UnicodeData.txt")))
        {
            var f = line.Split(';');
            var cp = Hex(f[0]);
            if (f[13].Length > 0)
            {
                lower[cp] = [Hex(f[13])];
            }

            if (f[12].Length > 0)
            {
                upper[cp] = [Hex(f[12])];
            }
        }
        foreach (var line in File.ReadLines(Path.Combine(InputsFor(version), "SpecialCasing.txt")))
        {
            var f = line.Split('#')[0].Split(';');
            if (f.Length < 5 || f[4].Trim().Length > 0)
            {
                continue;
            }

            var cp = Hex(f[0]);
            lower[cp] = [.. f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex)];
            upper[cp] = [.. f[3].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Hex)];
        }
        // Compare full sequence identities without relying on generator-assigned expansion numbers.
        var keys = new Dictionary<int, string>();
        var sequences = new Dictionary<string, int>();
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (!XmlAlphabet.Contains(cp))
            {
                continue;
            }

            var row = XmlUnicodeData.CaseMapping(data.CaseMappings, cp);
            Check(cp, row.LowerKey, lower, keys, sequences);
            Check(cp, row.UpperKey, upper, keys, sequences);
        }
        static void Check(int cp, int key, Dictionary<int, int[]> source, Dictionary<int, string> keys, Dictionary<string, int> sequences)
        {
            if (!source.TryGetValue(cp, out var values))
            { Assert.Equal(cp, key); return; }
            if (values.Length == 1)
            { Assert.Equal(values[0], key); return; }
            var seq = string.Join(',', values);
            if (keys.TryGetValue(key, out var previous))
            {
                Assert.Equal(previous, seq);
            }
            else
            {
                keys[key] = seq;
            }

            if (sequences.TryGetValue(seq, out var previousKey))
            {
                Assert.Equal(previousKey, key);
            }
            else
            {
                sequences[seq] = key;
            }

            Assert.True(key > 0x10FFFF);
        }
    }

    [Theory]
    [InlineData(0x3A, true, true, true)]
    [InlineData(0x30, true, false, true)]
    [InlineData(0xB7, true, false, true)]
    [InlineData(0x300, true, false, true)]
    [InlineData(0x37E, true, false, false)]
    [InlineData(0x200C, true, true, true)]
    [InlineData(0xFDD0, true, false, false)]
    [InlineData(0xFFFE, false, false, false)]
    [InlineData(0xFFFF, false, false, false)]
    [InlineData(0x10000, true, true, true)]
    [InlineData(0xEFFFF, true, true, true)]
    [InlineData(0xF0000, true, false, false)]
    [InlineData(0x10FFFF, true, false, false)]
    [InlineData(0xD800, false, false, false)]
    [InlineData(0x1F, false, false, false)]
    public void XmlCharacterAndNameBoundaries(int cp, bool legal, bool start, bool name)
    {
        var data = Data();
        Assert.Equal(legal, XmlUnicodeData.Contains(data.Alphabet, cp));
        Assert.Equal(start, XmlUnicodeData.Contains(data.GetPredefined(XmlPredefinedClass.NameStart), cp));
        Assert.Equal(name, XmlUnicodeData.Contains(data.GetPredefined(XmlPredefinedClass.NameChar), cp));
    }
    [Fact]
    public void CaseRelationsUseFullLowerOrUpperWithoutTransitiveClosureOrLocale()
    {
        var rows = Data().CaseMappings;
        Assert.True(XmlUnicodeData.CaseEquals(rows, 0xDF, 0x1E9E));
        Assert.True(XmlUnicodeData.CaseMapping(rows, 0xDF).UpperKey > 0x10FFFF);
        Assert.True(XmlUnicodeData.CaseEquals(rows, 'I', 0x131));
        Assert.True(XmlUnicodeData.CaseEquals(rows, 'I', 'i'));
        Assert.True(XmlUnicodeData.CaseEquals(rows, 'i', 0x131));
        Assert.True(XmlUnicodeData.CaseEquals(rows, 0x3D1, 0x398));
        Assert.True(XmlUnicodeData.CaseEquals(rows, 0x398, 0x3F4));
        Assert.False(XmlUnicodeData.CaseEquals(rows, 0x3D1, 0x3F4));
        Assert.False(XmlUnicodeData.CaseEquals(rows, 'i', 0x130));
        Assert.True(XmlUnicodeData.CaseEquals(rows, 0x10400, 0x10428));
        Assert.Equal(new XmlCaseMapping(0x10FFFF, 0x10FFFF, 0x10FFFF), XmlUnicodeData.CaseMapping(rows, 0x10FFFF));
    }
    [Fact]
    public void DataAdmissionHonorsPlanPeakAndWorkBeforePublishing()
    {
        foreach (var limits in new[] { new RegexCompileLimits(maxPlanBytes: 1), new RegexCompileLimits(maxCompileMemoryBytes: 1) })
        {
            var budget = new CompileBudget(limits);
            Assert.False(XmlUnicodeData.TryCreate(ref budget, out var data, out _));
            Assert.Null(data);
            Assert.Equal(0, budget.ReservedBytes);
        }
        var used = new CompileBudget(new(maxCompileWork: 1));
        Assert.True(used.TryCharge(1, default, out _));
        Assert.False(XmlUnicodeData.TryCreate(ref used, out _, out var d));
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, d.Code);
    }
}
