#nullable enable
using GregsEx.Execution.Compiled;
using GregsEx.Flavors.Oniguruma;

namespace GregsEx;

/// <summary>
/// Reusable search storage for one pattern. Searches allocate nothing once the backtracking stacks have grown to
/// what the pattern needs. Not thread-safe.
/// </summary>
public sealed class OnigurumaWorkspace
{
    private readonly OnigMatcher _matcher = new();
    private readonly int[] _captures;
    // The compiled kernel's backtracking stack, allocated on its first use.
    private int[]? _stack;
    private long _lastWork;

    /// <summary>The pattern this workspace searches.</summary>
    public OnigurumaPattern Pattern { get; }

    /// <summary>
    /// Work units of the last search, which RegexMatchLimits.MaxWork bounds: backtracks, loop iterations, calls,
    /// look-around entries and start positions tried, plus the units runs consume.
    /// </summary>
    public long LastWork => _lastWork;

    /// <summary>Bytes of backtracking storage currently reserved.</summary>
    public long ReservedBytes => _matcher.ReservedBytes + 4L * (_captures.Length + (_stack?.Length ?? 0));

    internal OnigMatcher Matcher => _matcher;

    internal OnigurumaWorkspace(OnigurumaPattern pattern)
    {
        Pattern = pattern;
        _captures = new int[2 * pattern.GroupCount];
        _matcher.SetLimits(pattern.MatchLimits.MaxWork, pattern.MatchLimits.MaxWorkspaceBytes);
    }

    /// <summary>
    /// Whether a match starts in [startAt, input.Length]; lookbehind and \A still see the whole input. Options bound
    /// the whole search with cancellation, a timeout and lower limits.
    /// </summary>
    public RegexSearchStatus IsMatch(ReadOnlySpan<char> input, int startAt = 0, RegexMatchOptions options = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        return Search(input, startAt, options, captures: false);
    }

    /// <summary>The leftmost match starting in [startAt, input.Length]; \G matches at startAt.</summary>
    public RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, out RegexMatchRange match, RegexMatchOptions options = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        var status = Search(input, startAt, options, captures: false);
        match = status == RegexSearchStatus.Match ? new RegexMatchRange(_captures[0], _captures[1] - _captures[0]) : default;
        return status;
    }

    /// <summary>
    /// The leftmost match and its captures; groups needs at least GroupCount entries. A group that did not participate
    /// is (false, 0, 0). With \K, group zero starts at the kept position.
    /// </summary>
    public RegexSearchStatus FindCaptures(ReadOnlySpan<char> input, int startAt, Span<RegexCapture> groups, out RegexMatchRange match,
        RegexMatchOptions options = default)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        match = default;
        if (groups.Length < Pattern.GroupCount)
        {
            return RegexSearchStatus.DestinationTooSmall;
        }

        var status = Search(input, startAt, options, captures: true);
        if (status != RegexSearchStatus.Match)
        {
            return status;
        }

        Fill(_captures, groups, Pattern.GroupCount);
        match = new RegexMatchRange(_captures[0], _captures[1] - _captures[0]);
        return status;
    }

    private RegexSearchStatus Search(ReadOnlySpan<char> input, int startAt, in RegexMatchOptions options, bool captures)
    {
        if (options.Limits is null && options.Timeout is null && !options.CancellationToken.CanBeCanceled)
        {
            if (Pattern.PlainKernel is not null && OnigCompiledSearch.TryRun(Pattern, input, startAt, _captures, captures, ref _stack, out var status, out _lastWork))
            {
                return status;
            }

            status = _matcher.Search(Pattern.Program, input, startAt, _captures);
            _lastWork = _matcher.LastWork;
            return status;
        }

        var limits = options.Resolve(Pattern.MatchLimits);
        _matcher.SetBudget(limits.MaxWork, limits.MaxWorkspaceBytes, options.CancellationToken, OnigMatcher.Deadline(options.Timeout));
        try
        {
            return _matcher.Search(Pattern.Program, input, startAt, _captures);
        }
        finally
        {
            _lastWork = _matcher.LastWork;
            _matcher.RestoreBudget();
        }
    }

    internal static void Fill(ReadOnlySpan<int> captures, Span<RegexCapture> groups, int count)
    {
        for (var g = 0; g < count; g++)
        {
            var start = captures[2 * g];
            groups[g] = start < 0 ? default : new RegexCapture(true, start, captures[2 * g + 1] - start);
        }
    }
}
