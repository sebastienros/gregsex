#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Composition;

namespace GregsEx.Flavors.Pcre2.Integration;

internal sealed class Pcre2WorkspaceExecutor(Pcre2Executor executor, Pcre2MatchLimits ceiling) : IRegularExecutor
{
    internal Pcre2Executor Executor => executor;
    internal Pcre2MatchLimits Ceiling => ceiling;
    internal static Pcre2ExecutionCapacity Capacity => Pcre2ExecutionCapacity.Default;
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation) => Capacity.Required(executor.Program.GroupCount);

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        match = default;
        if ((uint)startAt > (uint)input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }

        var pairs = state.Buffers.Pcre2Pairs;
        if (pairs.Length < executor.Program.GroupCount)
        {
            throw new ArgumentException("Prepared typed PCRE2 pair storage is required.");
        }

        Pcre2MatchResult result;
        var options = new Pcre2MatchOptions { Limits = ceiling };
        if (typeof(TUnit) == typeof(char))
        {
            result = executor.Match(MemoryMarshal.Cast<TUnit, char>(input), startAt, options, null, Capacity, Pcre2ValidationPolicy.WholeSubjectProjection, ref state, pairs, pairs);
        }
        else if (typeof(TUnit) == typeof(byte))
        {
            result = executor.Match(MemoryMarshal.Cast<TUnit, byte>(input), startAt, options, null, Capacity, Pcre2ValidationPolicy.WholeSubjectProjection, ref state, pairs, pairs);
        }
        else
        {
            throw new ArgumentException("Unsupported subject unit.");
        }

        if (result.Status == Pcre2MatchStatus.Match)
        {
            if (!pairs[0].TryGetRange(out match))
            {
                throw new InvalidOperationException("Validated generic projection produced an unordered range.");
            }

            if (operation == SearchOperation.Captures)
            {
                for (var i = 0; i < executor.Program.GroupCount; i++)
                { var pair = pairs[i]; groups[i] = pair.Participated ? new(true, pair.Start, pair.End - pair.Start) : default; }
            }

            return RegexSearchStatus.Match;
        }
        if (operation == SearchOperation.Captures)
        {
            groups[..executor.Program.GroupCount].Clear();
        }

        if (result.Status == Pcre2MatchStatus.NoMatch)
        {
            return RegexSearchStatus.NoMatch;
        }

        state.Failure = result.Failure;
        if (state.Failure.Code == RegexExecutionFailureCode.None)
        {
            var code = result.NativeReturnCode switch
            {
                (int)Pcre2ErrorCode.Matchlimit => RegexExecutionFailureCode.WorkLimitExceeded,
                (int)Pcre2ErrorCode.Depthlimit => RegexExecutionFailureCode.WorkspaceCapacityExceeded,
                (int)Pcre2ErrorCode.Heaplimit => RegexExecutionFailureCode.WorkspaceLimitExceeded,
                (int)Pcre2ErrorCode.Badutfoffset => RegexExecutionFailureCode.InvalidStartBoundary,
                _ when result.NativeReturnCode is <= -3 and >= -26 => RegexExecutionFailureCode.InvalidEncoding,
                _ => throw new InvalidOperationException("Validated generic projection produced an inadmissible native outcome.")
            };
            state.Failure = new(code, result.StartChar, state.Budget.WorkConsumed);
        }
        return state.Failure.Code switch
        {
            RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
            RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
            RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidStartBoundary => RegexSearchStatus.InvalidInput,
            _ => RegexSearchStatus.LimitExceeded
        };
    }
}
