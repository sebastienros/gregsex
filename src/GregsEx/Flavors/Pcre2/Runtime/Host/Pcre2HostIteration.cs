#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

// An outer session owns workspace exclusion and one RunState. This value retains
// positions only; the original subject must be supplied and revalidated each time.
internal struct Pcre2HostIteration
{
    internal int StartAt;
    private int _subjectLength, _results;
    private bool _bound, _retry, _complete;
    private Pcre2HostCallbackBudget? _calloutBudget;
    internal Pcre2HostIteration(int startAt) { ArgumentOutOfRangeException.ThrowIfNegative(startAt); this = default; StartAt = startAt; }
    internal Pcre2MatchResult Next(IPcre2Executor executor, ReadOnlySpan<char> input, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options, Pcre2CharCallbacks? callbacks, Pcre2ExecutionCapacity capacity,
        Pcre2HostLimits limits, ref RunState state, Span<Pcre2OffsetPair> callbackPairs) =>
        Next<char, Pcre2CharCallbacks, Pcre2HostChars>(executor, input, pairs, options, callbacks, capacity, limits, ref state, callbackPairs);
    internal Pcre2MatchResult Next(IPcre2Executor executor, ReadOnlySpan<byte> input, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options, Pcre2ByteCallbacks? callbacks, Pcre2ExecutionCapacity capacity,
        Pcre2HostLimits limits, ref RunState state, Span<Pcre2OffsetPair> callbackPairs) =>
        Next<byte, Pcre2ByteCallbacks, Pcre2HostBytes>(executor, input, pairs, options, callbacks, capacity, limits, ref state, callbackPairs);
    private Pcre2MatchResult Next<T, C, U>(IPcre2Executor executor, ReadOnlySpan<T> input, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options, C? callbacks, Pcre2ExecutionCapacity capacity,
        Pcre2HostLimits limits, ref RunState state, Span<Pcre2OffsetPair> callbackPairs)
        where T : unmanaged where C : class where U : struct, IPcre2HostUnits<T, C>
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(limits);
        if (executor.Program.Profile.Width != default(U).Width)
        {
            throw new ArgumentException("Native iterator width mismatch.", nameof(input));
        }

        if (pairs.IsEmpty)
        {
            throw new ArgumentException("Iteration requires a group-zero output pair.", nameof(pairs));
        }

        if (!_bound)
        { if (StartAt > input.Length) { throw new ArgumentOutOfRangeException(nameof(StartAt)); } _bound = true; _subjectLength = input.Length; }
        if (input.Length != _subjectLength)
        {
            throw new ArgumentException("The original subject length must remain constant for a session.", nameof(input));
        }

        if (!state.TryCharge(0, StartAt))
        { _complete = true; return Failure(ref state); }
        if (_complete)
        {
            return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, 0);
        }

        if (_results >= limits.MaxResults)
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, StartAt, state.Budget.WorkConsumed, _results + 1L);
            _complete = true;
            return Failure(ref state);
        }
        if (_calloutBudget is null)
        {
            if (limits.MaxMemoryBytes < 256)
            { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, StartAt, state.Budget.WorkConsumed, 256); _complete = true; return Failure(ref state); }
            _calloutBudget = new((options.Limits ?? new()).MaxCallouts);
        }
        callbacks = default(U).Wrap(callbacks, _calloutBudget);
        while (true)
        {
            var attempt = options with { Flags = options.Flags | (_retry ? Pcre2MatchFlags.Anchored | Pcre2MatchFlags.NotemptyAtstart : 0) };
            var result = default(U).Match(executor, input, StartAt, attempt, callbacks, capacity, ref state, callbackPairs, pairs);
            if (_calloutBudget.Denied)
            { state.Failure = new(RegexExecutionFailureCode.WorkLimitExceeded, StartAt, state.Budget.WorkConsumed); _complete = true; return Failure(ref state); }
            var pair = result.Status == Pcre2MatchStatus.Match ? pairs[0] : default;
            if (result.Status == Pcre2MatchStatus.Match && (!pair.Participated || pair.Start > input.Length || pair.End > input.Length))
            {
                throw new InvalidOperationException("Executor returned an invalid iteration endpoint.");
            }
            // A native NOTEMPTY_ATSTART retry can still report a reset-start
            // nonprogress pair. Emit the original result once, then advance
            // after this bounded retry rather than publishing it repeatedly.
            var retryNonprogress = result.Status == Pcre2MatchStatus.Match && (pair.Start > pair.End || pair.End <= StartAt);
            if (_retry && (result.Status == Pcre2MatchStatus.NoMatch || retryNonprogress))
            {
                if (StartAt == input.Length)
                { _complete = true; return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, 0); }
                var next = default(U).Advance(input, StartAt, executor.Program.Profile, ref state);
                if (!state.TryCharge(0, StartAt))
                { _complete = true; return Failure(ref state); }
                if (next <= StartAt || next > input.Length)
                {
                    throw new InvalidOperationException("Checked host advancement must make progress.");
                }

                StartAt = next;
                _retry = false;
                continue;
            }
            if (result.Status != Pcre2MatchStatus.Match)
            { _complete = true; return result; }
            _retry = pair.Start >= pair.End || pair.End <= StartAt;
            StartAt = Math.Max(StartAt, pair.End);
            _results++;
            return result;
        }
    }
    private static Pcre2MatchResult Failure(ref RunState state) => new(state.Failure.Code == RegexExecutionFailureCode.Cancelled ? Pcre2MatchStatus.Cancelled : state.Failure.Code == RegexExecutionFailureCode.TimedOut ? Pcre2MatchStatus.TimedOut : Pcre2MatchStatus.LimitExceeded, (int)Pcre2ErrorCode.Matchlimit, 0, Failure: state.Failure);
}
