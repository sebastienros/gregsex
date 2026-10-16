using System.Collections;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;

namespace GregsEx;

public sealed partial class EcmaScriptMatcher
{
    /// <summary>Captures an independent global session seed. Each iterator owns scratch and one lifetime budget.</summary>
    public EcmaScriptMatchAll MatchAll(string input, RegexMatchOptions options = default)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            var state = CreateState(options);
            var host = new EcmaHostBudget(_hostLimits);
            try
            {
                RequireGlobal(ref state);
                EcmaHostBudget.Charge(0, ref state);
                host.Reserve(256, ref state);
                return new(_executor, _limits, _hostLimits, _frames, ToLength(_lastIndex), input, options, state.Budget, host);
            }
            finally { LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
    internal bool IteratorStep(string input, RegexMatchOptions options, ref RunBudget budget,
        ref EcmaHostBudget host, out EcmaScriptExecResult? result, out RegexSearchStatus status)
    {
        result = null;
        status = RegexSearchStatus.NoMatch;
        Enter();
        try
        {
            var state = CreateState(options);
            state.Budget = budget;
            try
            {
                status = ExecCore(input, SearchOperation.Captures, _hostCaptures, out var range, ref state);
                RequireSuccess(status, ref state);
                if (status == RegexSearchStatus.NoMatch)
                {
                    return false;
                }

                result = Materialize(range, ref host, ref state);
                if (range.Length == 0)
                {
                    _lastIndex = Advance(input, ToLength(_lastIndex), ref state);
                }

                return true;
            }
            finally { budget = state.Budget; LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
}

/// <summary>Immutable MatchAll snapshot. Iterators are independent of the source session.</summary>
public sealed class EcmaScriptMatchAll : IEnumerable<EcmaScriptExecResult>
{
    private readonly IEcmaExecutor _executor;
    private readonly RegexMatchLimits _limits;
    private readonly EcmaScriptHostLimits _hostLimits;
    private readonly int _frames;
    private readonly long _seed;
    private readonly string _input;
    private readonly RegexMatchOptions _options;
    private readonly RunBudget _budget;
    private readonly EcmaHostBudget _host;
    internal EcmaScriptMatchAll(IEcmaExecutor executor, RegexMatchLimits limits, EcmaScriptHostLimits hostLimits,
        int frames, long seed, string input, RegexMatchOptions options, RunBudget budget, EcmaHostBudget host)
    {
        _executor = executor;
        _limits = limits;
        _hostLimits = hostLimits;
        _frames = frames;
        _seed = seed;
        _input = input;
        _options = options;
        _budget = budget;
        _host = host;
    }
    /// <summary>Creates a finite independent iterator; its deadline starts at MatchAll invocation.</summary>
    public EcmaScriptMatchAllIterator GetEnumerator()
    {
        var state = new RunState(default, _limits, _options) { Budget = _budget };
        var host = _host;
        EcmaHostBudget.Charge(0, ref state);
        host.Reserve(320, ref state);
        var clone = new EcmaScriptMatcher(_executor, _limits, frameCapacity: _frames, hostLimits: _hostLimits);
        try
        {
            clone.LastIndex = _seed;
            return new(clone, _input, _options, state.Budget, host);
        }
        catch { clone.Dispose(); throw; }
    }
    IEnumerator<EcmaScriptExecResult> IEnumerable<EcmaScriptExecResult>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Owned MatchAll iterator. Failures throw and remain inspectable; disposal releases its clone.</summary>
public sealed class EcmaScriptMatchAllIterator : IEnumerator<EcmaScriptExecResult>
{
    private EcmaScriptMatcher? _clone;
    private readonly string _input;
    private readonly RegexMatchOptions _options;
    private RunBudget _budget;
    private EcmaHostBudget _host;
    private EcmaScriptExecResult? _current;
    private int _lifecycle;
    private bool _completed;
    /// <summary>Terminal search outcome; null before termination or early disposal.</summary>
    public RegexSearchStatus? TerminalStatus { get; private set; }
    /// <summary>Terminal execution failure.</summary>
    public RegexExecutionFailure Failure { get; private set; }
    /// <summary>Terminal materialization failure.</summary>
    public EcmaScriptHostFailure HostFailure { get; private set; }
    /// <summary>Cumulative match, advancement and output work.</summary>
    public long WorkConsumed => _budget.WorkConsumed;
    /// <summary>The immutable current result. Invalid before/after a successful step.</summary>
    public EcmaScriptExecResult Current => _current ?? throw new InvalidOperationException("No current result.");
    object IEnumerator.Current => Current;
    internal EcmaScriptMatchAllIterator(EcmaScriptMatcher clone, string input, RegexMatchOptions options,
        RunBudget budget, EcmaHostBudget host)
    { _clone = clone; _input = input; _options = options; _budget = budget; _host = host; }
    /// <summary>Polls and consumes the same budget on every step; never converts failure into normal completion.</summary>
    public bool MoveNext()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(EcmaScriptMatchAllIterator));
        }

        if (previous != 0)
        {
            throw new InvalidOperationException("Iterator is already in use.");
        }

        try
        {
            _current = null;
            if (!_budget.TryCharge(0, 0, out var failure))
            {
                throw new RegexExecutionException(failure);
            }

            if (_completed)
            {
                return false;
            }

            var found = _clone!.IteratorStep(_input, _options, ref _budget, ref _host, out _current, out var status);
            if (!found)
            { TerminalStatus = status; Complete(); }
            return found;
        }
        catch (RegexExecutionException exception)
        { Failure = exception.Failure; TerminalStatus = EcmaScriptMatcher.FailureStatus(Failure); Complete(); throw; }
        catch (EcmaScriptHostException exception)
        { HostFailure = exception.Failure; TerminalStatus = RegexSearchStatus.LimitExceeded; Complete(); throw; }
        catch { Complete(); throw; }
        finally { Volatile.Write(ref _lifecycle, 0); }
    }
    private void Complete() { _completed = true; _current = null; _clone?.Dispose(); _clone = null; }
    /// <summary>Reset is unsupported; create another iterator from the snapshot.</summary>
    public void Reset() => throw new NotSupportedException();
    /// <summary>Releases the independent clone. Active use is rejected; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (previous == 1)
        {
            throw new InvalidOperationException("Iterator is already in use.");
        }

        if (previous == 2)
        {
            return;
        }

        Complete();
    }
}
