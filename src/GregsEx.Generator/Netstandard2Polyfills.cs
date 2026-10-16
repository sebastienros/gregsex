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
