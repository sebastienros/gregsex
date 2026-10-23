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

    // Every code point that folds as cp does, cp included, in ascending order.
    internal static int[] CaseClass(int cp)
    {
        var classes = s_classes ?? BuildClasses();
        return classes.TryGetValue(Fold(cp), out var members) ? members : [cp];
    }

    // Adds the case variants of every member: Oniguruma folds a class under ignore-case before negating it.
    internal static OnigCharSet CaseClose(OnigCharSet set)
    {
        var classes = s_classes ?? BuildClasses();
        var builder = new OnigCharSet.Builder();
        builder.Add(set);
        var added = false;
        foreach (var members in classes.Values)
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
            if (any && !all)
            {
                foreach (var m in members)
                {
                    builder.Add(m);
                }
                added = true;
            }
        }
        return added ? builder.Build() : set;
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
