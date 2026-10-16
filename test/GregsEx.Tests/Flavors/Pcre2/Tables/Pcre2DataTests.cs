using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Tables;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Tables;

public sealed class Pcre2DataTests
{
    // This canonical digest is also checked by a separate C exporter reading actual source.
    private const string NativeRowsDigest = "de670fcd3e801c37b01bc82988dcfd3fa3c425cd970af1c71f1ebc84e1ff7007";
    private static Pcre2UnicodeRecord Record(Pcre2DataView data, int cp) =>
        data.Records[data.Stage2[data.Stage1[cp / 128] * 128 + cp % 128]];

    [Fact]
    public void EveryCodePointMatchesIndependentNativeSourceFingerprint()
    {
        var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
        Assert.Equal(8704, data.Stage1.Length);
        Assert.Equal(0, data.Stage2.Length % 128);
        foreach (var block in data.Stage1)
        {
            Assert.InRange((block + 1) * 128, 128, data.Stage2.Length);
        }

        foreach (var index in data.Stage2)
        {
            Assert.InRange((int)index, 0, data.Records.Length - 1);
        }

        foreach (var r in data.Records)
        {
            Assert.InRange(r.ParticularCategory, 0, 29);
            Assert.InRange(r.Script, 0, 170);
            Assert.InRange(r.BidiClass, 0, 22);
            Assert.InRange(r.GraphemeBreak, 0, 14);
            Assert.Equal(6, r.ScriptSetCount);
            Assert.Equal(2, r.BooleanSetCount);
            Assert.InRange(r.ScriptSetOffset + 6, 6, data.ScriptSets.Length);
            Assert.InRange(r.BooleanSetOffset + 2, 2, data.BooleanSets.Length);
            Assert.InRange(r.CaseSetOffset + r.CaseSetCount, 0, data.CaseSets.Length);
            Assert.InRange(r.DigitSet, 0, 76);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> row = stackalloc byte[128];
        var caseIndex = 0;
        for (var cp = 0; cp < 0x110000; cp++)
        {
            var r = Record(data, cp);
            var count = 0;
            Add(row, ref count, r.ParticularCategory);
            Add(row, ref count, r.Script);
            Add(row, ref count, r.BidiClass);
            Add(row, ref count, r.GraphemeBreak);
            Add(row, ref count, r.OtherCaseDelta);
            Add(row, ref count, r.CaseSetCount);
            foreach (var c in data.CaseSets.Slice(r.CaseSetOffset, r.CaseSetCount))
            {
                Add(row, ref count, c);
            }

            foreach (var bits in data.ScriptSets.Slice(r.ScriptSetOffset, 6))
            {
                Add(row, ref count, unchecked((int)bits));
            }

            foreach (var bits in data.BooleanSets.Slice(r.BooleanSetOffset, 2))
            {
                Add(row, ref count, unchecked((int)bits));
            }

            Add(row, ref count, r.DigitSet);
            int lower = cp, upper = cp, title = cp;
            if (caseIndex < data.OutputCases.Length && data.OutputCases[caseIndex].CodePoint == cp)
            {
                var c = data.OutputCases[caseIndex++];
                lower = c.Lower;
                upper = c.Upper;
                title = c.Title;
            }
            Add(row, ref count, lower);
            Add(row, ref count, upper);
            Add(row, ref count, title);
            hash.AppendData(row[..count]);
        }
        Assert.Equal(data.OutputCases.Length, caseIndex);
        Assert.Equal(NativeRowsDigest, Convert.ToHexStringLower(hash.GetHashAndReset()));
        Assert.Equal(NativeRowsDigest, Pcre2GeneratedData.ExhaustiveDigest);
    }

    private static void Add(Span<byte> row, ref int length, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(row[length..], value);
        length += 4;
    }

    [Fact]
    public void All510ActualAliasesAndTheirExplicitScriptRulesResolve()
    {
        IPcre2Data provider = Pcre2Data.Instance;
        foreach (var line in Pcre2SourceAliases.Rows.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('|');
            int type = int.Parse(parts[1], CultureInfo.InvariantCulture), value = int.Parse(parts[2], CultureInfo.InvariantCulture);
            var kind = type switch
            {
                1 => Pcre2PropertyKind.MajorCategory,
                2 => Pcre2PropertyKind.ParticularCategory,
                3 => Pcre2PropertyKind.Script,
                4 => Pcre2PropertyKind.ScriptExtensions,
                11 => Pcre2PropertyKind.BidiClass,
                12 => Pcre2PropertyKind.Binary,
                _ => Pcre2PropertyKind.Special
            };
            Pcre2PropertyKey expected = new(kind, kind == Pcre2PropertyKind.Special ? type : value);
            Assert.True(provider.TryResolveProperty(parts[0], default, out var actual), line);
            Assert.Equal(expected, actual);
            Assert.True(Pcre2DataAbi.IsValidProperty(actual));
            // Source loose matching ignores precisely ASCII whitespace/hyphens/underscores.
            var loose = " \t_-" + parts[0].ToUpperInvariant() + "\r\n";
            Assert.True(provider.TryResolveProperty(loose, default, out actual));
            Assert.Equal(expected, actual);
            if (type is 3 or 4)
            {
                Assert.True(provider.TryResolveProperty("Script", parts[0], out actual));
                Assert.Equal(new(Pcre2PropertyKind.Script, value), actual);
                Assert.True(provider.TryResolveProperty("Script_Extensions", parts[0], out actual));
                Assert.Equal(new(type == 3 ? Pcre2PropertyKind.Script : Pcre2PropertyKind.ScriptExtensions, value), actual);
            }
        }
    }

    [Theory]
    [InlineData("sc", "Latin", 2, 0)]
    [InlineData("scx", "Latn", 3, 0)]
    [InlineData("scx", "Deseret", 2, 105)]
    [InlineData("Bidi_Class", "R", 5, 17)]
    [InlineData("bc", "L", 5, 9)]
    [InlineData("scx=Kirat_Rai", "", 2, 170)]
    public void SourcePropertyBoundaries(string name, string value, int kind, int id)
    {
        Assert.True(Pcre2Data.Instance.TryResolveProperty(name, value, out var actual));
        Assert.Equal(new((Pcre2PropertyKind)kind, id), actual);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("General_Category", "Lu")]
    [InlineData("Script", "Lu")]
    [InlineData("Script_Extensions", "Alphabetic")]
    [InlineData("bc", "not-a-class")]
    [InlineData("bc", "Left_To_Right")]
    [InlineData("Unknown_Property", "")]
    [InlineData("Latin", "Yes")]
    [InlineData("gc=Lu", "")]
    [InlineData("sc=Latin", "Latn")]
    [InlineData("Kirat_Rai", "")]
    [InlineData("La\u00a0tin", "")]
    [InlineData("LatinX", "")]
    public void UnknownAndNonSourceAliasesFail(string name, string value) =>
        Assert.False(Pcre2Data.Instance.TryResolveProperty(name, value, out _));

    [Fact]
    public void PropertyScratchIsBoundedAndCultureDoesNotDefineBehavior()
    {
        Assert.False(Pcre2Data.Instance.TryResolveProperty(new string('a', 49), default, out _));
        Assert.False(Pcre2Data.Instance.TryResolveProperty("sc", new string('a', 46), out _));
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            Assert.True(Pcre2Data.Instance.TryResolveProperty("LATIN", default, out var actual));
            Assert.Equal(new(Pcre2PropertyKind.ScriptExtensions, 0), actual);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData("abc_09", false, true)]
    [InlineData("9abc", false, false)]
    [InlineData("é", false, false)]
    [InlineData("é", true, true)]
    [InlineData("_١", true, true)]
    [InlineData("١a", true, false)]
    [InlineData("a\u0301", true, false)]
    [InlineData("a-", true, false)]
    [InlineData("\U00010400", true, true)]
    [InlineData("\U0001d7ce", true, false)]
    [InlineData("a\U0001d7ce", true, true)]
    [InlineData("", true, false)]
    [InlineData("a\0", false, false)]
    public void CanonicalGroupNamesUseSourceCategories(string name, bool utf, bool expected) =>
        Assert.Equal(expected, Pcre2Data.Instance.IsValidGroupName(name, utf));

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    public void CanonicalNameHelperRejectsActualMalformedSurrogateUnits(int codeUnit)
    {
        var surrogate = checked((char)codeUnit);
        foreach (var name in new[] { new string(surrogate, 1), new string([surrogate, 'a']), new string(['a', surrogate]) })
        {
            Assert.False(Pcre2Data.Instance.IsValidGroupName(name, true));
        }
    }

    [Fact]
    public void CanonicalNameHelperDoesNotApplyWidthLimits()
    {
        Assert.True(Pcre2Data.Instance.IsValidGroupName(new string('a', 129), true));
        Assert.True(Pcre2Data.Instance.IsValidGroupName(new string('a', 129), false));
    }

    [Fact]
    public void SourceCaseSetsAndDefaultOutputQuirksRemainDistinct()
    {
        var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
        var longS = Record(data, 0x17f);
        Assert.Equal(0, longS.OtherCaseDelta);
        Assert.Equal(new[] { 0x53, 0x73, 0x17f }, data.CaseSets.Slice(longS.CaseSetOffset, longS.CaseSetCount).ToArray());
        var kelvin = Record(data, 0x212a);
        Assert.Equal(0, kelvin.OtherCaseDelta);
        Assert.Equal(new[] { 0x4b, 0x6b, 0x212a }, data.CaseSets.Slice(kelvin.CaseSetOffset, kelvin.CaseSetCount).ToArray());
        Assert.Equal(new[] { 0x69, 0x130 }, Pcre2Data.TurkishDottedI.ToArray());
        Assert.Equal(new[] { 0x49, 0x131 }, Pcre2Data.TurkishDotlessI.ToArray());
        Assert.Equal(0, Record(data, 0x130).CaseSetCount);
        Assert.Equal(0, Record(data, 0x131).CaseSetCount);
        Assert.Equal(1, Record(data, 0x1c5).OtherCaseDelta);
        Assert.Contains(new Pcre2SimpleCase(0x1c5, 0x1c6, 0x1c6, 0x1c6), data.OutputCases.ToArray());
        Assert.DoesNotContain(data.OutputCases.ToArray(), c => c.CodePoint == 0x17f || c.CodePoint == 0x212a || c.CodePoint == 0x130);
    }

    [Fact]
    public void DigitSetsScriptExtensionsAndGraphemeBoundariesUseNativeIds()
    {
        var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
        Assert.Equal(76, Pcre2Data.DigitSetEnds.Length);
        for (var set = 0; set < Pcre2Data.DigitSetEnds.Length; set++)
        {
            var end = Pcre2Data.DigitSetEnds[set];
            for (var cp = end - 9; cp <= end; cp++)
            {
                Assert.Equal(13, Record(data, cp).ParticularCategory);
                Assert.Equal(set + 1, Record(data, cp).DigitSet);
            }
            Assert.NotEqual(set + 1, Record(data, end + 1).DigitSet);
            Assert.NotEqual(set + 1, Record(data, end - 10).DigitSet);
        }
        var deseret = Record(data, 0x10400);
        Assert.Equal(105, deseret.Script);
        Assert.NotEqual(0u, data.ScriptSets[deseret.ScriptSetOffset + 105 / 32] & (1u << (105 % 32)));
        Pcre2UnicodeRecord hira = Record(data, 0x3042), prolonged = Record(data, 0x30fc);
        Assert.Equal(27, hira.Script);
        foreach (var script in new[] { 27, 28 })
        {
            Assert.NotEqual(0u, data.ScriptSets[prolonged.ScriptSetOffset + script / 32] & (1u << (script % 32)));
        }

        Assert.Equal(0, Record(data, '\r').GraphemeBreak);
        Assert.Equal(1, Record(data, '\n').GraphemeBreak);
        Assert.Equal(13, Record(data, 0x200d).GraphemeBreak);
        Assert.Equal(14, Record(data, 0x1f600).GraphemeBreak);
        Assert.Equal(2u, Pcre2Data.GraphemeNoBreakMasks[0]);
        Assert.Equal(0u, Pcre2Data.GraphemeNoBreakMasks[1]);
        Assert.Equal(15, Pcre2Data.GraphemeNoBreakMasks.Length);
        Assert.Equal(4, Record(data, 0xd800).ParticularCategory);
        Assert.Equal(2, Record(data, 0x10ffff).ParticularCategory);
    }

    [Fact]
    public void BindingRetainsExactDefaultAndArbitraryCopiedPayloads()
    {
        Assert.Equal("40bfdd1a0c1e45c7c85c799dfefa098d30ea58e832d813d00f6dfc6bed167513", Pcre2CharacterTables.Default.Digest);
        var input = new byte[1088];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (byte)(i * 37 + 19);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(input));
        Pcre2CharacterTables tables = new(input);
        var expected = input.ToArray();
        Array.Clear(input);
        Pcre2DataView custom = Pcre2Data.Instance.Bind(tables), second = Pcre2Data.Instance.Bind(tables);
        Assert.Equal(digest, tables.Digest);
        Assert.True(custom.LowByteTables.SequenceEqual(expected));
        Assert.True(custom.LowByteTables.Overlaps(tables.Bytes));
        Assert.True(custom.LowByteTables.Overlaps(second.LowByteTables));
        var canonical = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
        Assert.True(custom.Records.Overlaps(canonical.Records));
        Assert.True(custom.OutputCases.Overlaps(canonical.OutputCases));
        Assert.True(custom.Stage1.Overlaps(canonical.Stage1));
        Assert.True(custom.Stage2.Overlaps(canonical.Stage2));
        Assert.False(custom.LowByteTables.SequenceEqual(canonical.LowByteTables));
        Assert.Throws<ArgumentException>(() => new Pcre2CharacterTables(new byte[1087]));
        Assert.Throws<ArgumentException>(() => new Pcre2CharacterTables(new byte[1089]));
        Assert.Throws<ArgumentNullException>(() => Pcre2Data.Instance.Bind(null!));
    }

    [Fact]
    public void TableOwnersAreStableAcrossParallelIndependentBindings()
    {
        Parallel.For(0, 32, i =>
        {
            var data = Pcre2Data.Instance.Bind(Pcre2CharacterTables.Default);
            Assert.Equal(0, Record(data, 'A').Script);
            Assert.Equal('a' - 'A', Record(data, 'A').OtherCaseDelta);
            Assert.Equal(Pcre2Profile.UnicodeDataId, Pcre2Data.Instance.DataId);
            Assert.Equal("ffde0c6ead457c4f8edcd8f9ce490bbabd30adabdfd179ecaddd320946cf3872", Pcre2Data.Instance.SourceDigest);
        });
    }
}
