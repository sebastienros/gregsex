#nullable enable
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

    /// <summary>The pattern this workspace searches.</summary>
    public OnigurumaPattern Pattern { get; }

    /// <summary>Backtracks of the last match attempt.</summary>
    public long LastWork => _matcher.LastWork;

    /// <summary>Bytes of backtracking storage currently reserved.</summary>
    public long ReservedBytes => _matcher.ReservedBytes + 4L * _captures.Length;

    internal OnigurumaWorkspace(OnigurumaPattern pattern)
    {
        Pattern = pattern;
        _captures = new int[2 * pattern.GroupCount];
        _matcher.SetLimits(pattern.MatchLimits.MaxWork, pattern.MatchLimits.MaxWorkspaceBytes);
    }

    /// <summary>Whether a match starts in [startAt, input.Length]; lookbehind and \A still see the whole input.</summary>
    public RegexSearchStatus IsMatch(ReadOnlySpan<char> input, int startAt = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        return _matcher.Search(Pattern.Program, input, startAt, _captures);
    }

    /// <summary>The leftmost match starting in [startAt, input.Length]; \G matches at startAt.</summary>
    public RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, out RegexMatchRange match)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        var status = _matcher.Search(Pattern.Program, input, startAt, _captures);
        match = status == RegexSearchStatus.Match ? new RegexMatchRange(_captures[0], _captures[1] - _captures[0]) : default;
        return status;
    }

    /// <summary>
    /// The leftmost match and its captures; groups needs at least GroupCount entries. A group that did not participate
    /// is (false, 0, 0). With \K, group zero starts at the kept position.
    /// </summary>
    public RegexSearchStatus FindCaptures(ReadOnlySpan<char> input, int startAt, Span<RegexCapture> groups, out RegexMatchRange match)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        match = default;
        if (groups.Length < Pattern.GroupCount)
        {
            return RegexSearchStatus.DestinationTooSmall;
        }

        var status = _matcher.Search(Pattern.Program, input, startAt, _captures);
        if (status != RegexSearchStatus.Match)
        {
            return status;
        }

        Fill(_captures, groups, Pattern.GroupCount);
        match = new RegexMatchRange(_captures[0], _captures[1] - _captures[0]);
        return status;
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
