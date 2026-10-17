#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Controls;

internal static class Pcre2Marks
{
    // match.c6656 uses the source zero-terminated strcmp, even though callback
    // and getter payloads retain an explicit length and every embedded NUL.
    internal static bool SameSkipName(Pcre2Program program, int leftMark, int rightMark,
        ref Pcre2VmState machine, ref RunState state)
    {
        var left = program.Marks[leftMark];
        var right = program.Marks[rightMark];
        var cursor = 0;
        while (true)
        {
            if (!Pcre2Storage.Charge(ref machine, ref state, 2, machine.Position))
            {
                return false;
            }

            var a = cursor < left.Count ? program.Units[left.Offset + cursor] : 0;
            var b = cursor < right.Count ? program.Units[right.Offset + cursor] : 0;
            if (a != b)
            {
                return false;
            }

            if (a == 0)
            {
                return true;
            }

            cursor++;
        }
    }
}
