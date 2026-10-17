#nullable enable
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Host;

namespace GregsEx.Flavors.Pcre2.Runtime.Composition;

// Retains the actual cumulative RunState but no borrowed subject. The caller
// reborrows its original subject for each Next; all terminal/exception paths
// release workspace ownership. Dispose also releases an unfinished session.
internal ref struct Pcre2PreparedIteration
{
    private Pcre2PreparedWorkspace? _owner;
    private int _sequence;
    private RunState _state;
    private Pcre2HostIteration _iteration;
    private readonly Pcre2MatchOptions _options;
    private readonly Pcre2CharCallbacks? _chars;
    private readonly Pcre2ByteCallbacks? _bytes;
    private readonly Pcre2HostLimits _limits;

    internal Pcre2PreparedIteration(Pcre2PreparedWorkspace owner, int sequence, int startAt,
        Pcre2MatchOptions options, Pcre2CharCallbacks? chars, Pcre2ByteCallbacks? bytes, Pcre2HostLimits limits)
    {
        _owner = owner;
        _sequence = sequence;
        _state = owner.CreateState(options);
        _iteration = new(startAt);
        _options = options;
        _chars = chars;
        _bytes = bytes;
        _limits = limits;
    }
    internal readonly long WorkConsumed => _state.Budget.WorkConsumed;
    internal Pcre2MatchResult Next(ReadOnlySpan<char> input, Span<Pcre2OffsetPair> pairs)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(Pcre2PreparedIteration));
        owner.EnterNext(_sequence);
        var terminal = false;
        try
        {
            owner.Validate(input.Length, _iteration.StartAt, Pcre2CodeUnitWidth.Sixteen);
            if (!owner.Admit(input.Length, _iteration.StartAt, _options, ref _state, out var denial))
            { terminal = true; return denial; }
            var result = _iteration.Next(owner.Executor, input, pairs, _options, _chars, owner.Capacity,
                _limits, ref _state, owner.CallbackPairs);
            terminal = result.Status != Pcre2MatchStatus.Match;
            return result;
        }
        catch { terminal = true; throw; }
        finally
        {
            _sequence = owner.ExitNext();
            owner.Record(ref _state);
            if (terminal)
            {
                Dispose();
            }
        }
    }
    internal Pcre2MatchResult Next(ReadOnlySpan<byte> input, Span<Pcre2OffsetPair> pairs)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(Pcre2PreparedIteration));
        owner.EnterNext(_sequence);
        var terminal = false;
        try
        {
            owner.Validate(input.Length, _iteration.StartAt, Pcre2CodeUnitWidth.Eight);
            if (!owner.Admit(input.Length, _iteration.StartAt, _options, ref _state, out var denial))
            { terminal = true; return denial; }
            var result = _iteration.Next(owner.Executor, input, pairs, _options, _bytes, owner.Capacity,
                _limits, ref _state, owner.CallbackPairs);
            terminal = result.Status != Pcre2MatchStatus.Match;
            return result;
        }
        catch { terminal = true; throw; }
        finally
        {
            _sequence = owner.ExitNext();
            owner.Record(ref _state);
            if (terminal)
            {
                Dispose();
            }
        }
    }
    public void Dispose()
    {
        if (_owner is not { } owner)
        {
            return;
        }

        owner.EndIteration(_sequence, ref _state);
        _owner = null;
    }
}
