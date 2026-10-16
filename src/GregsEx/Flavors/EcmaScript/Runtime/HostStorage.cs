using GregsEx.Contracts;

namespace GregsEx;

// Conservative logical live accounting; no pooling, hidden builders or uncharged growth.
internal struct EcmaHostBudget
{
    private readonly EcmaScriptHostLimits _limits;
    private long _bytes, _units, _results;
    internal EcmaHostBudget(EcmaScriptHostLimits limits) => _limits = limits;
    internal void Reserve(long bytes, ref RunState state)
    {
        if (bytes > _limits.MaxMemoryBytes - _bytes)
        {
            Fail(EcmaScriptHostFailureCode.MemoryLimitExceeded, SaturatingAdd(_bytes, bytes), ref state);
        }

        _bytes += bytes;
    }
    internal void Release(long bytes) => _bytes -= bytes;
    internal void Result(ref RunState state)
    {
        if (_results == _limits.MaxResults)
        {
            Fail(EcmaScriptHostFailureCode.ResultLimitExceeded, _results + 1, ref state);
        }

        Charge(1, ref state);
        _results++;
    }
    internal void Units(int units, ref RunState state)
    {
        if (units > _limits.MaxOutputUnits - _units)
        {
            Fail(EcmaScriptHostFailureCode.OutputLimitExceeded, _units + units, ref state);
        }

        Charge(units, ref state);
        _units += units;
    }
    internal static void Charge(long units, ref RunState state)
    {
        if (!state.TryCharge(units, 0))
        {
            throw new RegexExecutionException(state.Failure);
        }
    }
    internal static void Fail(EcmaScriptHostFailureCode code, long required, ref RunState state)
        => throw new EcmaScriptHostException(new(code, required, state.Budget.WorkConsumed));
    private static long SaturatingAdd(long x, long y) => y > long.MaxValue - x ? long.MaxValue : x + y;
}

internal sealed class EcmaHostList<T>
{
    private T[] _items = [];
    private readonly int _width;
    internal int Count { get; private set; }
    internal T this[int index] => _items[index];
    private EcmaHostList(int width) => _width = width;
    internal static EcmaHostList<T> Create(int width, ref EcmaHostBudget host, ref RunState state)
    { host.Reserve(64, ref state); return new(width); }
    internal void Add(T item, ref EcmaHostBudget host, ref RunState state)
    {
        if (Count == _items.Length)
        {
            var capacity = (int)Math.Min(int.MaxValue, Math.Max(4L, 2L * Count));
            if (capacity <= Count)
            {
                EcmaHostBudget.Fail(EcmaScriptHostFailureCode.MemoryLimitExceeded, long.MaxValue, ref state);
            }

            var bytes = 32L + (long)_width * capacity;
            host.Reserve(bytes, ref state);
            EcmaHostBudget.Charge(Count, ref state);
            var next = new T[capacity];
            _items.AsSpan(0, Count).CopyTo(next);
            if (_items.Length != 0)
            {
                host.Release(32L + (long)_width * _items.Length);
            }

            _items = next;
        }
        EcmaHostBudget.Charge(1, ref state);
        _items[Count++] = item;
    }
    internal T[] Finish(ref EcmaHostBudget host, ref RunState state)
    {
        host.Reserve(32L + (long)_width * Count, ref state);
        EcmaHostBudget.Charge(Count, ref state);
        var result = new T[Count];
        _items.AsSpan(0, Count).CopyTo(result);
        return result;
    }
}

internal sealed class EcmaHostText
{
    private char[] _buffer = [];
    private int _length;
    private EcmaHostText() { }
    internal static EcmaHostText Create(ref EcmaHostBudget host, ref RunState state)
    { host.Reserve(64, ref state); return new(); }
    internal void Append(ReadOnlySpan<char> value, ref EcmaHostBudget host, ref RunState state)
    {
        host.Units(value.Length, ref state);
        var required = (long)_length + value.Length;
        if (required > int.MaxValue)
        {
            EcmaHostBudget.Fail(EcmaScriptHostFailureCode.OutputLimitExceeded, required, ref state);
        }

        if (required > _buffer.Length)
        {
            var capacity = (int)Math.Min(int.MaxValue, Math.Max(required, Math.Max(16L, 2L * _buffer.Length)));
            host.Reserve(32L + 2L * capacity, ref state);
            EcmaHostBudget.Charge(_length, ref state);
            var next = new char[capacity];
            _buffer.AsSpan(0, _length).CopyTo(next);
            if (_buffer.Length != 0)
            {
                host.Release(32L + 2L * _buffer.Length);
            }

            _buffer = next;
        }
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }
    internal string Finish(ref EcmaHostBudget host, ref RunState state)
    {
        host.Reserve(32L + 2L * _length, ref state);
        EcmaHostBudget.Charge(_length, ref state);
        return new string(_buffer, 0, _length);
    }
}
