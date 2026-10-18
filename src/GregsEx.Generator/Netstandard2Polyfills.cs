// netstandard2.0 stand-ins for BCL APIs the linked engine sources use. Build-time speed is irrelevant here:
// the generator runs the parser and planner once per [GeneratedPattern], never at match time.

namespace GregsEx.Execution.Backtracking
{
    // Mirrors EcmaExecutor.TaskWidth (the executor itself is not linked into the generator).
    internal static class EcmaExecutor { internal const int TaskWidth = 6; }
}

namespace System
{
    internal static class ArrayPolyfills
    {
        extension(Array)
        {
            public static int MaxLength => 0x7FFFFFC7;
            public static void Clear(Array array) => Array.Clear(array, 0, array.Length);
        }
    }
}

namespace System.Numerics
{
    // EcmaFastSet walks its bitmap words; netstandard2.0's BitOperations is internal to System.Memory.
    internal static class BitOperations
    {
        public static int PopCount(ulong value)
        {
            var count = 0;
            for (; value != 0; value &= value - 1)
            {
                count++;
            }

            return count;
        }

        public static int TrailingZeroCount(ulong value)
        {
            if (value == 0)
            {
                return 64;
            }

            var count = 0;
            for (; (value & 1) == 0; value >>= 1)
            {
                count++;
            }

            return count;
        }
    }
}
