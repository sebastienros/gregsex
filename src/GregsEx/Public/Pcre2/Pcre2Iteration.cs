using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Host;
namespace GregsEx;

/// <summary>Exclusive cumulative-budget native iteration; stores coordinates and owned state, never an input span.</summary>
public sealed class Pcre2Iteration : IDisposable
{
    private Pcre2Workspace? _owner;
    private int _sequence;
    private RunBudget _budget;
    private RegexExecutionFailure _failure;
    private Pcre2HostIteration _iteration;
    private readonly Pcre2MatchOptions _options;
    private readonly Pcre2CharCallbacks? _chars;
    private readonly Pcre2ByteCallbacks? _bytes;
    private readonly Pcre2HostLimits _limits;
    internal Pcre2Iteration(Pcre2Workspace owner, int startAt, Pcre2MatchOptions options, Pcre2CharCallbacks? chars, Pcre2ByteCallbacks? bytes, Pcre2HostLimits limits)
    {
        _sequence = owner.Prepared.BeginSession(startAt, ref options, chars, bytes);
        _owner = owner;
        _options = options;
        _chars = chars;
        _bytes = bytes;
        _limits = limits;
        _iteration = new(startAt);
        var state = owner.Prepared.CreateState(options);
        _budget = state.Budget;
    }
    /// <summary>Cumulative work across every Next and callback in this session.</summary>
    public long WorkConsumed => _budget.WorkConsumed;
    /// <summary>Find the next native16 result in the same original subject.</summary>
    public Pcre2MatchResult Next(ReadOnlySpan<char> originalInput, Span<Pcre2OffsetPair> pairs)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(Pcre2Iteration));
        owner.Prepared.EnterNext(_sequence);
        owner.ClearCopy();
        var terminal = false;
        var state = owner.Prepared.CreateState(_options);
        state.Budget = _budget;
        state.Failure = _failure;
        try
        {
            owner.Prepared.Validate(originalInput.Length, _iteration.StartAt, Pcre2CodeUnitWidth.Sixteen);
            if (!owner.Admit(originalInput.Length, _iteration.StartAt, _options, ref state, out var denial))
            { terminal = true; return denial; }
            var result = _iteration.Next(owner.Prepared.Executor, originalInput, pairs, _options, _chars, owner.Prepared.Capacity, _limits, ref state, owner.Prepared.CallbackPairs);
            if (result.Status == Pcre2MatchStatus.Match && (_options.Flags & Pcre2MatchFlags.CopyMatchedSubject) != 0 && !owner.Copy(originalInput, _options, ref state))
            {
                result = Pcre2Workspace.CopyFailure(result, ref state);
            }

            terminal = result.Status != Pcre2MatchStatus.Match;
            return result;
        }
        catch { terminal = true; throw; }
        finally
        {
            _budget = state.Budget;
            _failure = state.Failure;
            _sequence = owner.Prepared.ExitNext();
            owner.Record(ref state);
            if (terminal)
            {
                Dispose();
            }
        }
    }
    /// <summary>Find the next native8 result in the same original subject.</summary>
    public Pcre2MatchResult Next(ReadOnlySpan<byte> originalInput, Span<Pcre2OffsetPair> pairs)
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(Pcre2Iteration));
        owner.Prepared.EnterNext(_sequence);
        owner.ClearCopy();
        var terminal = false;
        var state = owner.Prepared.CreateState(_options);
        state.Budget = _budget;
        state.Failure = _failure;
        try
        {
            owner.Prepared.Validate(originalInput.Length, _iteration.StartAt, Pcre2CodeUnitWidth.Eight);
            if (!owner.Admit(originalInput.Length, _iteration.StartAt, _options, ref state, out var denial))
            { terminal = true; return denial; }
            var result = _iteration.Next(owner.Prepared.Executor, originalInput, pairs, _options, _bytes, owner.Prepared.Capacity, _limits, ref state, owner.Prepared.CallbackPairs);
            if (result.Status == Pcre2MatchStatus.Match && (_options.Flags & Pcre2MatchFlags.CopyMatchedSubject) != 0 && !owner.Copy(originalInput, _options, ref state))
            {
                result = Pcre2Workspace.CopyFailure(result, ref state);
            }

            terminal = result.Status != Pcre2MatchStatus.Match;
            return result;
        }
        catch { terminal = true; throw; }
        finally
        {
            _budget = state.Budget;
            _failure = state.Failure;
            _sequence = owner.Prepared.ExitNext();
            owner.Record(ref state);
            if (terminal)
            {
                Dispose();
            }
        }
    }
    /// <summary>Release an unfinished session; terminal Next releases automatically. Active callbacks reject disposal.</summary>
    public void Dispose()
    {
        if (_owner is not { } owner)
        {
            return;
        }

        var state = owner.Prepared.CreateState(_options);
        state.Budget = _budget;
        state.Failure = _failure;
        owner.Prepared.EndIteration(_sequence, ref state);
        owner.Record(ref state);
        owner.Exit();
        _owner = null;
    }
}
