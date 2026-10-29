#nullable enable
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace GregsEx.Flavors.Oniguruma;

// An immutable set of code points: sorted, merged, inclusive pairs, with the ASCII members as a bitmap. Sets with the
// same members are shared across patterns (Intern), so their SearchValues are built once per process.
internal sealed class OnigCharSet
{
    internal const int MaxCodePoint = 0x10FFFF;
    private static readonly ConcurrentDictionary<Key, OnigCharSet> s_interned = new();

    internal readonly int[] Ranges;
    internal readonly ulong Low;
    internal readonly ulong High;
    // Whether any member is at or above U+0080, and whether every code point at or above it is a member.
    internal readonly bool HasNonAscii;
    internal readonly bool AllNonAscii;
    // Whether a member is outside the BMP, so an item may take two UTF-16 units.
    internal readonly bool HasSupplementary;
    // Whether the set holds exactly one code point.
    internal readonly int Single;
    // The members' UTF-16 units when every member is a BMP non-surrogate (a run is then measured in units), or the
    // units that end a run when the set holds every unit above ASCII (a negated ASCII class): see RunLength.
    private SearchValues<char>? _units;
    private SearchValues<char>? _stops;
    // Published after _units and _stops (volatile), so a reader that sees it set also sees them.
    private volatile int _unitsState;

    private OnigCharSet(int[] ranges)
    {
        Ranges = ranges;
        Single = ranges.Length == 2 && ranges[0] == ranges[1] ? ranges[0] : -1;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            var first = ranges[i];
            var last = ranges[i + 1];
            if (last >= 0x80)
            {
                HasNonAscii = true;
            }

            if (last > 0xFFFF)
            {
                HasSupplementary = true;
            }

            for (var c = first; c <= Math.Min(last, 0x7F); c++)
            {
                if (c < 64)
                {
                    Low |= 1UL << c;
                }
                else
                {
                    High |= 1UL << (c - 64);
                }
            }
        }
        var n = ranges.Length;
        AllNonAscii = n >= 2 && ranges[n - 1] == MaxCodePoint && ranges[n - 2] <= 0x80;
    }

    internal static OnigCharSet Empty { get; } = new([]);
    internal static OnigCharSet All { get; } = Intern([0, MaxCodePoint]);

    internal bool IsEmpty => Ranges.Length == 0;

    internal static OnigCharSet Intern(int[] ranges)
    {
        if (ranges.Length == 0)
        {
            return Empty;
        }

        return s_interned.GetOrAdd(new Key(ranges), static k => new OnigCharSet(k.Ranges));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Contains(int cp)
    {
        if (cp < 128)
        {
            return ((cp < 64 ? Low : High) & (1UL << cp)) != 0;
        }

        return HasNonAscii && ContainsSlow(cp);
    }

    // Whether the set holds the UTF-16 unit c alone; a surrogate unit only matches as a lone surrogate.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool ContainsUnit(char c)
    {
        if (c < 128)
        {
            return ((c < 64 ? Low : High) & (1UL << c)) != 0;
        }

        return HasNonAscii && ContainsSlow(c);
    }

    private bool ContainsSlow(int cp)
    {
        if (AllNonAscii)
        {
            return true;
        }

        var ranges = Ranges;
        int lo = 0, hi = (ranges.Length >> 1) - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (cp < ranges[2 * mid])
            {
                hi = mid - 1;
            }
            else if (cp > ranges[2 * mid + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    // The number of units from start that belong to a run of single-unit members, at most max, or -1 when the set
    // has no vectorized form (the caller then steps code point by code point).
#if NETCOREAPP3_0_OR_GREATER
    // Compiled fully optimized on its first call, as OnigMatcher's methods are (see FullOpts there).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
    internal int RunLength(ReadOnlySpan<char> input, int start, int max)
    {
        if (_unitsState == 0)
        {
            PrepareUnits();
        }

        var span = input[start..];
        if ((uint)max < (uint)span.Length)
        {
            span = span[..max];
        }

        if (_units is not null)
        {
            var i = span.IndexOfAnyExcept(_units);
            return i < 0 ? span.Length : i;
        }

        if (_stops is not null)
        {
            var i = span.IndexOfAny(_stops);
            return i < 0 ? span.Length : i;
        }

        return -1;
    }

    // Whether every member is a single BMP unit that RunLength measures; then an item is always one unit wide.
    internal bool IsUnitRun
    {
        get
        {
            if (_unitsState == 0)
            {
                PrepareUnits();
            }

            return _units is not null;
        }
    }

    private void PrepareUnits()
    {
        lock (this)
        {
            if (_unitsState != 0)
            {
                return;
            }

            if (!HasSupplementary && !Overlaps(0xD800, 0xDFFF))
            {
                _units = SearchValues.Create(Units(Ranges));
            }
            else if (AllNonAscii)
            {
                // Members: some ASCII plus every unit above it, so a run ends only at an ASCII non-member.
                var stops = new List<char>();
                for (var c = 0; c < 128; c++)
                {
                    if (!Contains(c))
                    {
                        stops.Add((char)c);
                    }
                }
                _stops = SearchValues.Create(stops.ToArray());
            }

            _unitsState = 1;
        }
    }

    private bool Overlaps(int first, int last)
    {
        for (var i = 0; i < Ranges.Length; i += 2)
        {
            if (Ranges[i] <= last && Ranges[i + 1] >= first)
            {
                return true;
            }
        }
        return false;
    }

    private static char[] Units(int[] ranges)
    {
        var count = 0;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            count += ranges[i + 1] - ranges[i] + 1;
        }

        var units = new char[count];
        var at = 0;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            for (var c = ranges[i]; c <= ranges[i + 1]; c++)
            {
                units[at++] = (char)c;
            }
        }
        return units;
    }

    internal OnigCharSet Negate()
    {
        var result = new List<int>();
        var next = 0;
        for (var i = 0; i < Ranges.Length; i += 2)
        {
            if (Ranges[i] > next)
            {
                result.Add(next);
                result.Add(Ranges[i] - 1);
            }
            next = Ranges[i + 1] + 1;
        }
        if (next <= MaxCodePoint)
        {
            result.Add(next);
            result.Add(MaxCodePoint);
        }
        return Intern(result.ToArray());
    }

    internal OnigCharSet Intersect(OnigCharSet other)
    {
        var a = Ranges;
        var b = other.Ranges;
        var result = new List<int>();
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            var first = Math.Max(a[i], b[j]);
            var last = Math.Min(a[i + 1], b[j + 1]);
            if (first <= last)
            {
                result.Add(first);
                result.Add(last);
            }
            if (a[i + 1] < b[j + 1])
            {
                i += 2;
            }
            else
            {
                j += 2;
            }
        }
        return Intern(result.ToArray());
    }

    internal OnigCharSet Union(OnigCharSet other)
    {
        var builder = new Builder();
        builder.Add(this);
        builder.Add(other);
        return builder.Build();
    }

    internal sealed class Builder
    {
        private readonly List<(int First, int Last)> _ranges = [];

        internal bool IsEmpty => _ranges.Count == 0;

        internal void Add(int first, int last)
        {
            if (first <= last)
            {
                _ranges.Add((first, last));
            }
        }

        internal void Add(int cp) => _ranges.Add((cp, cp));

        internal void Add(OnigCharSet set)
        {
            for (var i = 0; i < set.Ranges.Length; i += 2)
            {
                _ranges.Add((set.Ranges[i], set.Ranges[i + 1]));
            }
        }

        internal void Add(ReadOnlySpan<int> pairs)
        {
            for (var i = 0; i < pairs.Length; i += 2)
            {
                _ranges.Add((pairs[i], pairs[i + 1]));
            }
        }

        internal OnigCharSet Build()
        {
            if (_ranges.Count == 0)
            {
                return Empty;
            }

            _ranges.Sort(static (x, y) => x.First.CompareTo(y.First));
            var merged = new List<int>(_ranges.Count * 2);
            var (first, last) = _ranges[0];
            for (var i = 1; i < _ranges.Count; i++)
            {
                var (f, l) = _ranges[i];
                if (f <= last + 1)
                {
                    last = Math.Max(last, l);
                }
                else
                {
                    merged.Add(first);
                    merged.Add(last);
                    (first, last) = (f, l);
                }
            }
            merged.Add(first);
            merged.Add(last);
            return Intern(merged.ToArray());
        }
    }

    private readonly struct Key(int[] ranges) : IEquatable<Key>
    {
        internal readonly int[] Ranges = ranges;

        public bool Equals(Key other) => Ranges.AsSpan().SequenceEqual(other.Ranges);

        public override bool Equals(object? obj) => obj is Key k && Equals(k);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(Ranges.AsSpan()));
            return hash.ToHashCode();
        }
    }
}
