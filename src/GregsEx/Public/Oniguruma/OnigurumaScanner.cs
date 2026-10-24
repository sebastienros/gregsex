#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Flavors.Oniguruma;

namespace GregsEx;

/// <summary>
/// Searches several patterns from one position and selects the match that starts first, the earliest pattern on a tie,
/// as TextMate's scanners (vscode-oniguruma's OnigScanner.findNextMatchSync) do. For string-backed input, each pattern
/// keeps its last result per string and position and reuses it while it remains valid, as Onigwrap's OnigRegExp does,
/// except for patterns using \G, whose results depend on the search start.
/// One matcher and one capture buffer serve every pattern; searches allocate nothing once the stacks have grown.
/// Not thread-safe.
/// </summary>
public sealed class OnigurumaScanner
{
    private readonly OnigurumaPattern[] _patterns;
    private readonly OnigMatcher _matcher = new();
    // Per pattern: the cached subject, its range, the position searched from, and the result (captures, or null).
    private readonly string?[] _lastText;
    private readonly int[] _lastStart;
    private readonly int[] _lastLength;
    private readonly int[] _lastPosition;
    private readonly int[]?[] _lastCaptures;
    private readonly bool[] _lastMatched;
    private readonly RegexCapture[] _captures;
    private int _captureCount;

    /// <summary>Creates a scanner over parsed patterns.</summary>
    public OnigurumaScanner(IReadOnlyList<OnigurumaPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = [.. patterns];
        var n = _patterns.Length;
        _lastText = new string?[n];
        _lastStart = new int[n];
        _lastLength = new int[n];
        _lastPosition = new int[n];
        _lastCaptures = new int[n][];
        _lastMatched = new bool[n];
        var groups = 1;
        for (var i = 0; i < n; i++)
        {
            _lastCaptures[i] = new int[2 * _patterns[i].GroupCount];
            groups = Math.Max(groups, _patterns[i].GroupCount);
        }
        _captures = new RegexCapture[groups];
        if (n > 0)
        {
            var limits = _patterns[0].MatchLimits;
            _matcher.SetLimits(limits.MaxWork, limits.MaxWorkspaceBytes);
        }
    }

    /// <summary>Parses each pattern with one profile; throws <see cref="RegexParseException"/> on the first diagnostic.</summary>
    public OnigurumaScanner(IEnumerable<string> patterns, OnigurumaProfile profile, OnigurumaCompileOptions? options = null)
        : this(patterns.Select(p => OnigurumaPattern.Parse(p, profile, options)).ToArray())
    {
    }

    /// <summary>The patterns, in priority order.</summary>
    public IReadOnlyList<OnigurumaPattern> Patterns => _patterns;

    /// <summary>The captures of the last successful search, valid until the next search.</summary>
    public ReadOnlySpan<RegexCapture> Captures => _captures.AsSpan(0, _captureCount);

    /// <summary>Searches whose work ceiling was exhausted; they count as no match, as Onigwrap reports them.</summary>
    public long LimitExceededCount { get; private set; }

    /// <summary>Finds the next match at or after startAt, caching per pattern when input is string-backed.</summary>
    public bool FindNextMatch(ReadOnlyMemory<char> input, int startAt, out int patternIndex)
        => FindNextMatch(input, startAt, out patternIndex, default) == RegexSearchStatus.Match;

    /// <summary>Finds the next match at or after startAt, caching per pattern for this string.</summary>
    public bool FindNextMatch(string input, int startAt, out int patternIndex)
        => FindNextMatch(input, startAt, out patternIndex, default) == RegexSearchStatus.Match;

    /// <summary>Finds the next match at or after startAt, without caching.</summary>
    public bool FindNextMatch(ReadOnlySpan<char> input, int startAt, out int patternIndex)
        => Find(input, startAt, null, 0, 0, default, out patternIndex) == RegexSearchStatus.Match;

    /// <summary>
    /// Finds the next match at or after startAt (Match or NoMatch), caching per pattern when input is string-backed.
    /// Options bound the call: its timeout and cancellation cover every pattern, and its limits each pattern's search.
    /// A cancelled or timed-out call returns that status and caches nothing for the pattern it stopped in.
    /// </summary>
    public RegexSearchStatus FindNextMatch(ReadOnlyMemory<char> input, int startAt, out int patternIndex, RegexMatchOptions options)
    {
        if (MemoryMarshal.TryGetString(input, out var text, out var start, out var length))
        {
            return Find(input.Span, startAt, text, start, length, options, out patternIndex);
        }

        return Find(input.Span, startAt, null, 0, 0, options, out patternIndex);
    }

    /// <summary>As the ReadOnlyMemory overload, caching per pattern for this string.</summary>
    public RegexSearchStatus FindNextMatch(string input, int startAt, out int patternIndex, RegexMatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Find(input, startAt, input, 0, input.Length, options, out patternIndex);
    }

    /// <summary>As the ReadOnlyMemory overload, without caching.</summary>
    public RegexSearchStatus FindNextMatch(ReadOnlySpan<char> input, int startAt, out int patternIndex, RegexMatchOptions options)
        => Find(input, startAt, null, 0, 0, options, out patternIndex);

    private RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, string? text, int textStart, int textLength,
        in RegexMatchOptions options, out int patternIndex)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)startAt, (uint)input.Length, nameof(startAt));
        patternIndex = -1;
        if (options.Limits is null && options.Timeout is null && !options.CancellationToken.CanBeCanceled || _patterns.Length == 0)
        {
            return Find(input, startAt, text, textStart, textLength, ref patternIndex);
        }

        var limits = options.Resolve(_patterns[0].MatchLimits);
        // Polled before any result, cached ones included.
        if (options.CancellationToken.IsCancellationRequested)
        {
            _captureCount = 0;
            return RegexSearchStatus.Cancelled;
        }

        _matcher.SetBudget(limits.MaxWork, limits.MaxWorkspaceBytes, options.CancellationToken, OnigMatcher.Deadline(options.Timeout));
        try
        {
            return Find(input, startAt, text, textStart, textLength, ref patternIndex);
        }
        finally
        {
            _matcher.RestoreBudget();
        }
    }

    private RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, string? text, int textStart, int textLength, ref int patternIndex)
    {
        var bestStart = int.MaxValue;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var captures = _lastCaptures[i]!;
            bool matched;
            // A result found from an earlier offset stays the leftmost from a later one, unless \G ties it to the
            // search start (Onigwrap's cache misses that case).
            if (text is not null && !_patterns[i].Program.UsesSearchStart && ReferenceEquals(_lastText[i], text) && _lastStart[i] == textStart && _lastLength[i] == textLength
                && _lastPosition[i] <= startAt && (!_lastMatched[i] || captures[0] >= startAt))
            {
                matched = _lastMatched[i];
            }
            else
            {
                var status = _matcher.Search(_patterns[i].Program, input, startAt, captures);
                if (status == RegexSearchStatus.LimitExceeded)
                {
                    LimitExceededCount++;
                }
                else if (status is RegexSearchStatus.Cancelled or RegexSearchStatus.TimedOut)
                {
                    _lastText[i] = null;
                    patternIndex = -1;
                    _captureCount = 0;
                    return status;
                }

                // A search that ran out of work counts as no match but is not cached: a later offset gets a fresh budget
                // and may match, as it does for Oniguruma searching a new string.
                matched = status == RegexSearchStatus.Match;
                if (text is not null && status != RegexSearchStatus.LimitExceeded)
                {
                    _lastText[i] = text;
                    _lastStart[i] = textStart;
                    _lastLength[i] = textLength;
                    _lastPosition[i] = startAt;
                    _lastMatched[i] = matched;
                }
                else
                {
                    _lastText[i] = null;
                }
            }

            if (matched && captures[0] < bestStart)
            {
                bestStart = captures[0];
                patternIndex = i;
                if (bestStart == startAt)
                {
                    break;
                }
            }
        }

        if (patternIndex < 0)
        {
            _captureCount = 0;
            return RegexSearchStatus.NoMatch;
        }

        _captureCount = _patterns[patternIndex].GroupCount;
        OnigurumaWorkspace.Fill(_lastCaptures[patternIndex], _captures, _captureCount);
        return RegexSearchStatus.Match;
    }
}
