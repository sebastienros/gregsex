#nullable enable
#if NETSTANDARD2_0
// netstandard2.0 stand-ins for BCL APIs that neither netstandard2.0, System.Memory nor Meziantou.Polyfill supply.
// They are correct scalar loops: the net8.0/net10.0 builds bind the vectorized BCL members instead.
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    internal static class GregsExPolyfills
    {
        extension(Array)
        {
            public static int MaxLength => 0x7FFFFFC7;
            public static void Clear(Array array) => Array.Clear(array, 0, array.Length);
        }

        extension(double)
        {
            public static bool IsFinite(double d) => !double.IsNaN(d) && !double.IsInfinity(d);
        }
#if !GREGSEX_GENERATOR

        extension(ref HashCode hash)
        {
            public void AddBytes(ReadOnlySpan<byte> value)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    hash.Add(value[i]);
                }
            }
        }
#endif

        public static int IndexOfAnyInRange(this ReadOnlySpan<char> span, char low, char high)
        {
            var width = (uint)(high - low);
            for (var i = 0; i < span.Length; i++)
            {
                if ((uint)(span[i] - low) <= width)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int IndexOfAnyInRange(this ReadOnlySpan<byte> span, byte low, byte high)
        {
            var width = (uint)(high - low);
            for (var i = 0; i < span.Length; i++)
            {
                if ((uint)(span[i] - low) <= width)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int IndexOfAnyExceptInRange(this ReadOnlySpan<char> span, char low, char high)
        {
            var width = (uint)(high - low);
            for (var i = 0; i < span.Length; i++)
            {
                if ((uint)(span[i] - low) > width)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int IndexOfAnyExceptInRange(this ReadOnlySpan<byte> span, byte low, byte high)
        {
            var width = (uint)(high - low);
            for (var i = 0; i < span.Length; i++)
            {
                if ((uint)(span[i] - low) > width)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int LastIndexOfAny<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            for (var i = span.Length - 1; i >= 0; i--)
            {
                if (values.Contains(span[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int LastIndexOfAnyExcept<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            for (var i = span.Length - 1; i >= 0; i--)
            {
                if (!values.Contains(span[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public static int LastIndexOf(this ReadOnlySpan<char> span, ReadOnlySpan<char> value, StringComparison comparisonType)
        {
            if (comparisonType != StringComparison.Ordinal)
            {
                throw new NotSupportedException();
            }

            return span.LastIndexOf(value);
        }

        public static bool TryEnsureSufficientExecutionStack()
        {
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                return true;
            }
            catch (InsufficientExecutionStackException)
            {
                return false;
            }
        }
    }
}

namespace System.Numerics
{
    internal static class BitOperations
    {
        public static int PopCount(uint value)
        {
            value -= (value >> 1) & 0x55555555u;
            value = (value & 0x33333333u) + ((value >> 2) & 0x33333333u);
            return (int)((((value + (value >> 4)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24);
        }

        public static int PopCount(ulong value) => PopCount((uint)value) + PopCount((uint)(value >> 32));

        public static int TrailingZeroCount(uint value) => value == 0 ? 32 : PopCount((value & (0u - value)) - 1);

        public static int TrailingZeroCount(ulong value) => value == 0 ? 64 : PopCount((value & (0ul - value)) - 1);

        public static int TrailingZeroCount(int value) => TrailingZeroCount((uint)value);

        public static int TrailingZeroCount(long value) => TrailingZeroCount((ulong)value);

        public static int Log2(uint value)
        {
            var result = 0;
            while ((value >>= 1) != 0)
            {
                result++;
            }

            return result;
        }

        public static int Log2(ulong value) => (value >> 32) != 0 ? 32 + Log2((uint)(value >> 32)) : Log2((uint)value);
    }
}
#endif
