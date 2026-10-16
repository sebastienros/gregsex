using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

// Per-invocation compiler storage. Replacement capacity is admitted while the old
// array is still live, and is released only after the copy. No pools or borrowed
// buffers enter the immutable result.
internal sealed class SyntaxBuffer<T>(int elementBytes)
{
    private T[] _items = [];
    internal int Count { get; private set; }
    internal Span<T> Span => _items.AsSpan(0, Count);
    internal ref T this[int index] => ref _items[index];
    internal long Bytes => _items.Length == 0 ? 0 : 32L + (long)elementBytes * _items.Length;
    internal void Add(T value, ref CompileBudget budget, SourceRange source)
    {
        SyntaxFailure.Charge(ref budget, 1, source);
        if (Count == _items.Length)
        {
            var size = _items.Length == 0 ? 4 : checked(_items.Length * 2);
            var bytes = checked(32L + (long)elementBytes * size);
            SyntaxFailure.Reserve(ref budget, bytes, source);
            SyntaxFailure.Charge(ref budget, Count, source);
            var replacement = new T[size];
            _items.AsSpan(0, Count).CopyTo(replacement);
            budget.ReleaseBytes(Bytes);
            _items = replacement;
        }
        _items[Count++] = value;
    }
    internal void Truncate(int count) { _items.AsSpan(count, Count - count).Clear(); Count = count; }
    internal T Pop() { var item = _items[--Count]; _items[Count] = default!; return item; }
}

internal sealed class SyntaxFailure(Pcre2Diagnostic diagnostic) : Exception
{
    internal Pcre2Diagnostic Diagnostic { get; } = diagnostic;
    internal static void Charge(ref CompileBudget budget, long amount, SourceRange source)
    {
        if (!budget.TryCharge(amount, source, out var d))
        {
            throw new SyntaxFailure(new(d, Pcre2DiagnosticKind.CompileLimit));
        }
    }
    internal static void Reserve(ref CompileBudget budget, long amount, SourceRange source)
    {
        if (!budget.TryReserveBytes(amount, source, out var d))
        {
            throw new SyntaxFailure(new(d, Pcre2DiagnosticKind.CompileLimit));
        }
    }
}
