using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.XPath.Parsing;

// Cold parser scratch. Every object, capacity growth and algebra-owned result
// is admitted before allocation; old/new capacities overlap until copied.
internal sealed class ParserStorage(CompileBudget budget)
{
    internal CompileBudget Budget = budget;
    internal SourceRange Source;
    internal string Feature = "XP-001";
    private long _live;
    internal void Work(long units = 1)
    {
        if (!Budget.TryCharge(units, Source, out var diagnostic))
        {
            throw new ParseFailure(diagnostic, Feature);
        }
    }
    internal void Reserve(long bytes)
    {
        if (!Budget.TryReserveBytes(bytes, Source, out var diagnostic))
        {
            throw new ParseFailure(diagnostic, Feature);
        }

        _live = checked(_live + bytes);
    }
    internal void Adopt(XmlInterval[] intervals) => _live = checked(_live + Bytes(intervals));
    internal static long Bytes(XmlInterval[] intervals) => intervals.Length == 0 ? 0 : 32L + 8L * intervals.Length;
    internal void Release(XmlInterval[] intervals) => Release(Bytes(intervals));
    internal void Release(long bytes) { Budget.ReleaseBytes(bytes); _live -= bytes; }
    internal void ReleaseAll() { Budget.ReleaseBytes(_live); _live = 0; }
    internal void Fail(RegexDiagnosticCode code = RegexDiagnosticCode.InvalidSyntax)
        => throw new ParseFailure(new(code, Source.Offset, Source.Length, Source.Unit), Feature);
    internal Buffer<T> Buffer<T>() { Reserve(64); return new(this); }
}

internal sealed class ParseFailure(RegexDiagnostic diagnostic, string feature) : Exception
{
    internal RegexDiagnostic Diagnostic { get; } = diagnostic;
    internal string Feature { get; } = feature;
}

internal sealed class Buffer<T>(ParserStorage storage)
{
    private T[] _items = [];
    internal int Count;
    internal Span<T> Items => _items.AsSpan(0, Count);
    internal ref T this[int index] => ref _items[index];
    internal void Add(T item)
    {
        storage.Work();
        if (Count == _items.Length)
        {
            var wanted = Math.Max(4L, 2L * Count);
            if (wanted > int.MaxValue)
            {
                storage.Fail(RegexDiagnosticCode.CompileMemoryLimitExceeded);
            }

            var bytes = checked(32L + Unsafe.SizeOf<T>() * wanted);
            storage.Reserve(bytes);
            storage.Work(Count);
            var replacement = new T[(int)wanted];
            Items.CopyTo(replacement);
            if (_items.Length != 0)
            {
                storage.Release(32L + (long)Unsafe.SizeOf<T>() * _items.Length);
            }

            _items = replacement;
        }
        _items[Count++] = item;
    }
    internal T Pop() { storage.Work(); var result = _items[--Count]; _items[Count] = default!; return result; }
    internal void Truncate(int count)
    {
        storage.Work(Count - count);
        _items.AsSpan(count, Count - count).Clear();
        Count = count;
    }
}
