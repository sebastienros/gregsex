#nullable enable
using GregsEx.Flavors.Oniguruma.Tables;

namespace GregsEx.Flavors.Oniguruma;

// Oniguruma's own Unicode 16.0 property ranges, names and simple case folds (OnigurumaUnicodeData.g.cs).
internal static class OnigUnicode
{
    // ONIGENC_CTYPE_* values, which are also the first CodeRanges slots.
    internal const int Newline = 0, Alpha = 1, Blank = 2, Cntrl = 3, Digit = 4, Graph = 5, Lower = 6, Print = 7,
        Punct = 8, Space = 9, Upper = 10, XDigit = 11, Word = 12, Alnum = 13, Ascii = 14;

    private static readonly OnigCharSet?[] s_properties = new OnigCharSet?[OnigurumaUnicodeData.PropertyCount];
    private static Dictionary<int, int[]>? s_classes;

    // The UTF-16 units of a code point; a surrogate code point (from a lone surrogate unit) is that unit.
    internal static string ToUtf16(int cp) => cp is >= 0xD800 and <= 0xDFFF ? ((char)cp).ToString() : char.ConvertFromUtf32(cp);

    // Oniguruma's name lookup: ASCII only, ' ', '-' and '_' ignored, case-insensitive.
    internal static bool TryGetProperty(ReadOnlySpan<char> name, out int property)
    {
        property = -1;
        Span<char> buffer = stackalloc char[64];
        var length = 0;
        foreach (var c in name)
        {
            if (c >= 0x80)
            {
                return false;
            }

            if (c is ' ' or '-' or '_')
            {
                continue;
            }

            if (length == buffer.Length)
            {
                return false;
            }

            buffer[length++] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }

        var key = buffer[..length];
        var offsets = OnigurumaUnicodeData.NameOffsets;
        var blob = OnigurumaUnicodeData.NameBlob.AsSpan();
        int lo = 0, hi = offsets.Length - 2;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var candidate = blob[offsets[mid]..offsets[mid + 1]];
            var order = candidate.SequenceCompareTo(key);
            if (order == 0)
            {
                property = OnigurumaUnicodeData.NameProperties[mid];
                return true;
            }

            if (order < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return false;
    }

    private static OnigCharSet? s_standaloneWord;

    // The word characters of \w, \W, \p{Word}, \P{Word}, \b and \B outside a bracket class. Oniguruma answers these
    // ctype tests below U+0100 from EncUNICODE_ISO_8859_1_CtypeTable, which also marks U+00B2, U+00B3, U+00B9 and
    // U+00BC-U+00BE (General_Category No) as word characters; bracket classes use CR_Word, which does not.
    internal static OnigCharSet StandaloneWord
        => s_standaloneWord ??= Property(Word).Union(OnigCharSet.Intern([0xB2, 0xB3, 0xB9, 0xB9, 0xBC, 0xBE]));

    internal static OnigCharSet Property(int property)
    {
        var set = Volatile.Read(ref s_properties[property]);
        if (set is not null)
        {
            return set;
        }

        var table = OnigurumaUnicodeData.PropertyTables[property];
        var offset = OnigurumaUnicodeData.Tables[2 * table];
        var count = OnigurumaUnicodeData.Tables[2 * table + 1];
        var builder = new OnigCharSet.Builder();
        builder.Add(OnigurumaUnicodeData.Ranges.Slice(2 * offset, 2 * count));
        set = builder.Build();
        Volatile.Write(ref s_properties[property], set);
        return set;
    }

    // The simple case fold of cp, or cp.
    internal static int Fold(int cp)
    {
        var folds = OnigurumaUnicodeData.Folds;
        int lo = 0, hi = (folds.Length >> 1) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var key = folds[2 * mid];
            if (key == cp)
            {
                return folds[2 * mid + 1];
            }

            if (key < cp)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return cp;
    }

    // Grapheme_Cluster_Break values as Oniguruma numbers them (unicode.c EGCB_TYPE).
    internal const int GbOther = 0, GbCR = 1, GbLF = 2, GbControl = 3, GbExtend = 4, GbPrepend = 5,
        GbRegionalIndicator = 6, GbSpacingMark = 7, GbZWJ = 8, GbL = 13, GbLV = 14, GbLVT = 15, GbT = 16, GbV = 17;

    private static OnigCharSet? s_pictographic;

    internal static int GraphemeBreak(int cp)
    {
        var table = OnigurumaUnicodeData.GraphemeBreaks;
        int lo = 0, hi = table.Length / 3 - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < table[3 * mid])
            {
                hi = mid - 1;
            }
            else if (cp > table[3 * mid + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return table[3 * mid + 2];
            }
        }
        return GbOther;
    }

    internal static bool IsExtendedPictographic(int cp)
    {
        if (s_pictographic is null)
        {
            TryGetProperty("ExtendedPictographic", out var property);
            s_pictographic = Property(property);
        }

        return s_pictographic.Contains(cp);
    }

    // Folds to several code points (ß -> ss): each with the code points that fold to it.
    internal static IReadOnlyList<(int[] Fold, int[] Members)> MultiFolds => s_multiFolds ??= LoadMultiFolds();

    private static IReadOnlyList<(int[] Fold, int[] Members)>? s_multiFolds;

    private static IReadOnlyList<(int[] Fold, int[] Members)> LoadMultiFolds()
    {
        var data = OnigurumaUnicodeData.MultiFolds;
        var list = new List<(int[], int[])>();
        var i = 0;
        while (i < data.Length)
        {
            var width = data[i];
            var fold = data.Slice(i + 1, width).ToArray();
            var count = data[i + 1 + width];
            var members = data.Slice(i + 2 + width, count).ToArray();
            list.Add((fold, members));
            i += 2 + width + count;
        }
        return list;
    }

    // Every code point that folds as cp does, cp included, in ascending order.
    internal static int[] CaseClass(int cp)
    {
        var classes = s_classes ?? BuildClasses();
        return classes.TryGetValue(Fold(cp), out var members) ? members : [cp];
    }

    // Adds the case variants of every member: Oniguruma folds a class under ignore-case before negating it. Code points
    // that fold to the same several code points are variants too (ß and ẞ both fold to ss, ᾀ and ᾈ to ἀι).
    internal static OnigCharSet CaseClose(OnigCharSet set)
    {
        var classes = s_classes ?? BuildClasses();
        var builder = new OnigCharSet.Builder();
        builder.Add(set);
        var added = false;
        foreach (var members in classes.Values)
        {
            added |= AddVariants(set, members, builder);
        }
        foreach (var (_, members) in MultiFolds)
        {
            added |= AddVariants(set, members, builder);
        }
        return added ? builder.Build() : set;
    }

    // Adds all of members when set has some but not all of them.
    private static bool AddVariants(OnigCharSet set, int[] members, OnigCharSet.Builder builder)
    {
        var any = false;
        var all = true;
        foreach (var m in members)
        {
            if (set.Contains(m))
            {
                any = true;
            }
            else
            {
                all = false;
            }
        }
        if (!any || all)
        {
            return false;
        }

        foreach (var m in members)
        {
            builder.Add(m);
        }
        return true;
    }

    private static Dictionary<int, int[]> BuildClasses()
    {
        var lists = new Dictionary<int, List<int>>();
        var folds = OnigurumaUnicodeData.Folds;
        for (var i = 0; i < folds.Length; i += 2)
        {
            var target = folds[i + 1];
            if (!lists.TryGetValue(target, out var list))
            {
                lists[target] = list = [target];
            }
            list.Add(folds[i]);
        }

        var classes = new Dictionary<int, int[]>(lists.Count);
        foreach (var (target, list) in lists)
        {
            list.Sort();
            classes[target] = list.Distinct().ToArray();
        }
        Interlocked.CompareExchange(ref s_classes, classes, null);
        return s_classes!;
    }
}
