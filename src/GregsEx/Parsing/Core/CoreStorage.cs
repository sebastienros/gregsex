using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Parsing.Core;

internal enum NodeKind { Empty, Literal, Class, Any, Start, End, Concat, Alternate, Group, Repeat }

// Children precede parents; group IDs within any subtree form a contiguous opening-order range.
internal struct CoreNode
{
    internal NodeKind Kind;
    internal int Left, Right, Value, Minimum, Maximum, FirstGroup, GroupCount;
    internal bool Lazy;
    internal SourceRange Source;
    internal long Instructions;
}

internal struct ParseFrame
{
    internal int Concat, Alternate, Group, Offset, FirstGroup;
}

// Explicitly owned compile storage. Replacement admission counts both old and new live arrays.
// Element sizes are conservative ABI sizes supplied at each concrete use, never user input.
internal struct CoreBuffer<T>
{
    internal T[] Items = [];
    internal int Count;
    private readonly int _elementBytes;
    private long _bytes;

    internal CoreBuffer(int elementBytes) => _elementBytes = elementBytes;

    internal bool Ensure(int required, ref CompileBudget budget, SourceRange source, out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        if (required <= Items.Length)
        {
            return true;
        }

        var capacity = Math.Max(required, Math.Max(4L, (long)Items.Length * 2));
        if (capacity > Array.MaxLength)
        {
            diagnostic = new(RegexDiagnosticCode.CompileMemoryLimitExceeded, source.Offset, source.Length, source.Unit);
            return false;
        }
        var bytes = 32 + capacity * _elementBytes;
        if (!budget.TryCharge(Count, source, out diagnostic) ||
            !budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        var replacement = new T[(int)capacity];
        Items.AsSpan(0, Count).CopyTo(replacement);
        Items = replacement;
        budget.ReleaseBytes(_bytes);
        _bytes = bytes;
        return true;
    }

    internal void Release(ref CompileBudget budget)
    {
        Items = [];
        Count = 0;
        budget.ReleaseBytes(_bytes);
        _bytes = 0;
    }
}
