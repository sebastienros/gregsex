using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Flavors.Xsd;

// All input-dependent arrays are admitted before allocation, including overlap
// during growth. Element sizes are conservative logical accounting constants.
internal struct XsdBuffer<T>(int elementBytes)
{
    internal T[] Array = [];
    internal int Count;
    internal readonly long Bytes => Array.Length == 0 ? 0 : 32L + (long)elementBytes * Array.Length;
    internal bool Add(T item, int ceiling, ref CompileBudget budget, SourceRange source,
        out RegexDiagnostic diagnostic)
    {
        diagnostic = default;
        if (Count >= ceiling)
        { diagnostic = new(RegexDiagnosticCode.ProgramLimitExceeded, source.Offset, source.Length, source.Unit); return false; }
        if (Count == Array.Length)
        {
            var capacity = (int)Math.Min(ceiling, Math.Max(8L, 2L * Array.Length));
            var bytes = 32L + (long)elementBytes * capacity;
            if (!budget.TryReserveBytes(bytes, source, out diagnostic) ||
                !budget.TryCharge(Count, source, out diagnostic))
            { if (diagnostic.Code == RegexDiagnosticCode.CompileWorkLimitExceeded) { budget.ReleaseBytes(bytes); } return false; }
            var next = new T[capacity];
            Array.AsSpan(0, Count).CopyTo(next);
            budget.ReleaseBytes(Bytes);
            Array = next;
        }
        Array[Count++] = item;
        return true;
    }
    internal void Release(ref CompileBudget budget) { budget.ReleaseBytes(Bytes); Array = []; Count = 0; }
}
