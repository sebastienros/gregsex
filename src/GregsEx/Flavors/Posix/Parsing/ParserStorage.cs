#nullable enable
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.IR;

namespace GregsEx.Flavors.Posix.Parsing;

internal sealed class ParserStorage(CompileBudget budget)
{
    internal CompileBudget Budget = budget;
    internal SourceRange Source;
    private long _live;
    internal void Work(long units = 1)
    {
        if (!Budget.TryCharge(units, Source, out var d))
        {
            throw new ParseFailure(new(d));
        }
    }
    internal void Reserve(long bytes)
    {
        if (!Budget.TryReserveBytes(bytes, Source, out var d))
        {
            throw new ParseFailure(new(d));
        }

        _live = checked(_live + bytes);
    }
    internal void Release(long bytes) { Budget.ReleaseBytes(bytes); _live -= bytes; }
    internal void ReleaseAll() { Budget.ReleaseBytes(_live); _live = 0; }
    internal void Fail(RegexDiagnosticCode code) => throw new ParseFailure(new(new(code, Source.Offset, Source.Length, Source.Unit)));
    internal void Syntax(PosixErrorCode code, PosixSyntaxDisposition disposition = PosixSyntaxDisposition.UndefinedRejected,
        string capability = "PX-002", bool extension = false)
        => throw new ParseFailure(new(new(extension ? RegexDiagnosticCode.UnsupportedFeature : RegexDiagnosticCode.InvalidSyntax,
            Source.Offset, Source.Length, Source.Unit), code, PosixDiagnosticLocation.Pattern, disposition, capability));
    internal void Extension(string feature, string capability)
        => throw new ParseFailure(new(new(RegexDiagnosticCode.UnsupportedFeature, Source.Offset, Source.Length, Source.Unit,
            feature + " is excluded by the Issue 7 C-byte profile."), PosixErrorCode.BadPattern,
            PosixDiagnosticLocation.Pattern, PosixSyntaxDisposition.ExtensionRejected, capability));
    internal Buffer<T> Buffer<T>() { Reserve(64); return new(this); }
}
internal sealed class ParseFailure(PosixRegexDiagnostic diagnostic) : Exception
{ internal PosixRegexDiagnostic Diagnostic { get; } = diagnostic; }
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
            var next = new T[(int)wanted];
            Items.CopyTo(next);
            if (_items.Length != 0)
            {
                storage.Release(32L + (long)Unsafe.SizeOf<T>() * _items.Length);
            }

            _items = next;
        }
        _items[Count++] = item;
    }
    internal T Pop() { storage.Work(); var value = _items[--Count]; _items[Count] = default!; return value; }
    internal void Truncate(int count)
    { storage.Work(Count - count); _items.AsSpan(count, Count - count).Clear(); Count = count; }
}
