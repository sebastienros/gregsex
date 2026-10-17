#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Tables;

internal readonly record struct Pcre2Alias(string Name, Pcre2PropertyKey Property);

/// <summary>Actual release-pinned PCRE2 Unicode data, independent of ambient Unicode/culture.</summary>
internal sealed class Pcre2Data : IPcre2Data
{
    internal static Pcre2Data Instance { get; } = new();
    private Pcre2Data() { }
    public string DataId => Pcre2Profile.UnicodeDataId;
    public string SourceDigest => "ffde0c6ead457c4f8edcd8f9ce490bbabd30adabdfd179ecaddd320946cf3872";
    internal const int MaximumNormalizedPropertyLength = 48;
    internal static ReadOnlySpan<int> TurkishDottedI => Pcre2GeneratedData.CaseSets.Slice(Pcre2GeneratedData.TurkishDottedIOffset, 2);
    internal static ReadOnlySpan<int> TurkishDotlessI => Pcre2GeneratedData.CaseSets.Slice(Pcre2GeneratedData.TurkishDotlessIOffset, 2);
    internal static ReadOnlySpan<int> DigitSetEnds => Pcre2GeneratedData.DigitSetEnds;
    internal static ReadOnlySpan<uint> GraphemeNoBreakMasks => Pcre2GeneratedData.GraphemeNoBreakMasks;

    public Pcre2DataView Bind(Pcre2CharacterTables tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return new(Pcre2GeneratedData.Stage1, Pcre2GeneratedData.Stage2, Pcre2GeneratedData.Records,
            Pcre2GeneratedData.CaseSets, Pcre2GeneratedData.ScriptSets, Pcre2GeneratedData.BooleanSets,
            Pcre2GeneratedData.OutputCases, tables.Bytes);
    }

    // A compile-boundary operation. Fixed48-character scratch and ordinal binary search.
    // Syntax owns braces/negation/native source errors; this accepts either split name/value
    // or an unsplit name containing the source ':'/'=' separator.
    public bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value, out Pcre2PropertyKey property)
    {
        property = default;
        Span<char> normalized = stackalloc char[MaximumNormalizedPropertyLength + 4];
        var count = 0;
        if (!Normalize(name, normalized, ref count))
        {
            return false;
        }

        var separator = normalized[..count].IndexOfAny(':', '=');
        if (!value.IsEmpty)
        {
            if (separator >= 0 || count >= MaximumNormalizedPropertyLength)
            {
                return false;
            }

            separator = count;
            normalized[count++] = '=';
            if (!Normalize(value, normalized, ref count))
            {
                return false;
            }
        }
        if (count == 0)
        {
            return false;
        }

        Pcre2PropertyKind? explicitScript = null;
        ReadOnlySpan<char> lookup = normalized[..count];
        if (separator >= 0)
        {
            ReadOnlySpan<char> prefix = normalized[..separator];
            ReadOnlySpan<char> suffix = normalized[(separator + 1)..count];
            if (prefix.SequenceEqual("sc") || prefix.SequenceEqual("script"))
            {
                explicitScript = Pcre2PropertyKind.Script;
            }
            else if (prefix.SequenceEqual("scx") || prefix.SequenceEqual("scriptextensions"))
            {
                explicitScript = Pcre2PropertyKind.ScriptExtensions;
            }
            else if (prefix.SequenceEqual("bc") || prefix.SequenceEqual("bidiclass"))
            {
                var length = suffix.Length;
                suffix.CopyTo(normalized[4..]);
                "bidi".AsSpan().CopyTo(normalized);
                lookup = normalized[..(length + 4)];
            }
            else
            {
                return false;
            }

            if (explicitScript.HasValue)
            {
                lookup = suffix;
            }
        }
        var aliases = Pcre2GeneratedData.Aliases;
        int first = 0, last = aliases.Length;
        while (first < last)
        {
            var mid = first + ((last - first) >> 1);
            var comparison = lookup.SequenceCompareTo(aliases[mid].Name.AsSpan());
            if (comparison < 0)
            {
                last = mid;
            }
            else if (comparison > 0)
            {
                first = mid + 1;
            }
            else
            {
                property = aliases[mid].Property;
                if (explicitScript.HasValue)
                {
                    if (property.Kind == Pcre2PropertyKind.ScriptExtensions)
                    {
                        property = new(explicitScript.Value, property.Id);
                    }
                    else if (property.Kind != Pcre2PropertyKind.Script)
                    {
                        property = default;
                        return false;
                    }
                    // Native PT_SC aliases remain PT_SC even with an explicit scx prefix.
                }
                return true;
            }
        }
        return false;
    }

    private static bool Normalize(ReadOnlySpan<char> source, Span<char> destination, ref int count)
    {
        foreach (var c in source)
        {
            if (c is '_' or '-' or ' ' or >= '\t' and <= '\r')
            {
                continue;
            }

            if (c < '&' || c > 'z' || count >= MaximumNormalizedPropertyLength)
            {
                return false;
            }

            destination[count++] = c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
        }
        return true;
    }

    // Canonical lexical helper only. Parser owns native-width name length and imported tables.
    public bool IsValidGroupName(ReadOnlySpan<char> name, bool utf)
    {
        if (name.IsEmpty)
        {
            return false;
        }

        if (!utf)
        {
            if (name[0] is >= '0' and <= '9')
            {
                return false;
            }

            var low = Pcre2CharacterTables.Default.Bytes;
            foreach (var c in name)
            {
                if (c > 255 || (low[Pcre2DataAbi.TypeOffset + c] & 0x10) == 0)
                {
                    return false;
                }
            }

            return true;
        }
        ReadOnlySpan<ushort> stage1 = Pcre2GeneratedData.Stage1, stage2 = Pcre2GeneratedData.Stage2;
        var records = Pcre2GeneratedData.Records;
        var first = true;
        for (var index = 0; index < name.Length; index++)
        {
            int cp = name[index];
            if (cp is >= 0xD800 and <= 0xDBFF)
            {
                if (index + 1 >= name.Length || name[index + 1] is < '\uDC00' or > '\uDFFF')
                {
                    return false;
                }

                cp = 0x10000 + ((cp - 0xD800) << 10) + name[++index] - 0xDC00;
            }
            else if (cp is >= 0xDC00 and <= 0xDFFF)
            {
                return false;
            }

            var category = records[stage2[stage1[cp / 128] * 128 + cp % 128]].ParticularCategory;
            if (category == 13 ? first : !(category is >= 5 and <= 9 || cp == '_'))
            {
                return false;
            }

            first = false;
        }
        return true;
    }
}
