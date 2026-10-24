#nullable enable
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

internal static class Pcre2Substrings
{
    // Program-aware form preserves unknown / unavailable / unset distinctions.
    internal static int Length(ReadOnlySpan<Pcre2OffsetPair> pairs, int number, int groupCount = int.MaxValue,
        Pcre2MatchResult? matchResult = null)
    {
        // pcre2_substring.c320–327: partial group zero remains accessible;
        // other failed/partial queries propagate rc. Pair-only callers retain their
        // existing checked-vector semantics; typed callers can supply the rc.
        if (matchResult is { NativeReturnCode: < 0 } failed &&
            (failed.NativeReturnCode != (int)Pcre2ErrorCode.Partial || number != 0))
        {
            return failed.NativeReturnCode;
        }

        if (groupCount == int.MaxValue)
        {
            groupCount = pairs.Length;
        }

        if (number < 0 || number >= groupCount)
        {
            return (int)Pcre2ErrorCode.Nosubstring;
        }

        if (number >= pairs.Length)
        {
            return (int)Pcre2ErrorCode.Unavailable;
        }

        var pair = pairs[number];
        return !pair.Participated ? (int)Pcre2ErrorCode.Unset : Math.Max(0, pair.End - pair.Start);
    }
    internal static int Number(Pcre2Program program, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name)
    {
        foreach (var entry in program.Names)
        {
            if (!name.SequenceEqual(entry.Name))
            {
                continue;
            }

            var numbers = program.Numbers.Slice(entry.Numbers.Offset, entry.Numbers.Count);
            var firstAvailable = -1;
            foreach (var n in numbers)
            {
                if (n < pairs.Length)
                {
                    if (firstAvailable < 0)
                    {
                        firstAvailable = n;
                    }

                    if (pairs[n].Participated)
                    {
                        return n;
                    }
                }
            }

            return firstAvailable >= 0 ? firstAvailable : numbers[0];
        }
        return program.GroupCount;
    }
    internal static Pcre2SubstringResult Copy<T>(ReadOnlySpan<T> subject, ReadOnlySpan<Pcre2OffsetPair> pairs,
        int number, Span<T> destination, int groupCount = int.MaxValue, Pcre2MatchResult? matchResult = null) where T : unmanaged
    {
        var length = Length(pairs, number, groupCount, matchResult);
        if (length < 0)
        {
            return new(length, 0, 0);
        }

        var pair = pairs[number];
        if (pair.Start > subject.Length || pair.End > subject.Length)
        {
            return new((int)Pcre2ErrorCode.Invalidoffset, 0, 0);
        }

        var required = (long)length + 1;
        if (destination.Length < required)
        {
            return new((int)Pcre2ErrorCode.Nomemory, 0, required);
        }

        subject.Slice(pair.Start, length).CopyTo(destination);
        destination[length] = default;
        return new(0, length, required);
    }
    internal static T[] Materialize<T>(ReadOnlySpan<T> subject, ReadOnlySpan<Pcre2OffsetPair> pairs,
        int number, Pcre2HostLimits limits, int groupCount = int.MaxValue) where T : unmanaged
    {
        var length = Length(pairs, number, groupCount);
        if (length < 0)
        {
            throw new ArgumentException($"PCRE2 substring error {length}.", nameof(number));
        }

        var pair = pairs[number];
        if (pair.Start > subject.Length || pair.End > subject.Length)
        {
            throw new ArgumentException("Invalid native capture offsets.", nameof(pairs));
        }

        if ((long)length + 1 > limits.MaxOutputUnits || (long)length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>() + 32 > limits.MaxMemoryBytes)
        {
            throw new InvalidOperationException("PCRE2 host materialization limit exceeded.");
        }

        return subject.Slice(pair.Start, length).ToArray();
    }
    internal static string MaterializeChars(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs,
        int number, Pcre2HostLimits limits, int groupCount = int.MaxValue)
    {
        var length = Length(pairs, number, groupCount);
        if (length < 0)
        {
            throw new ArgumentException($"PCRE2 substring error {length}.", nameof(number));
        }

        var pair = pairs[number];
        if (pair.Start > subject.Length || pair.End > subject.Length)
        {
            throw new ArgumentException("Invalid native capture offsets.", nameof(pairs));
        }

        if ((long)length + 1 > limits.MaxOutputUnits || (long)length * sizeof(char) + 32 > limits.MaxMemoryBytes)
        {
            throw new InvalidOperationException("PCRE2 host materialization limit exceeded.");
        }

        return subject.Slice(pair.Start, length).ToString();
    }

}
