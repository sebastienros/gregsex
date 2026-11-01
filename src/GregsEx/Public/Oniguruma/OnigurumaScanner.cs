#nullable enable
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Execution.Compiled;
using GregsEx.Flavors.Oniguruma;

namespace GregsEx;

/// <summary>
/// Searches several patterns from one position and selects the match that starts first, the earliest pattern on a tie,
/// as TextMate's scanners (vscode-oniguruma's OnigScanner.findNextMatchSync) do. A later pattern is normally searched
/// only for starts before the best so far. For string-backed input, each pattern keeps its last result per string and
/// position and reuses it while it remains valid, as Onigwrap's OnigRegExp does, except for patterns using \G, whose
/// results depend on the search start. A search that runs out of work is searched again from later offsets of the
/// string, a bounded number of times.
/// One matcher and one capture buffer serve every pattern; searches allocate nothing once the stacks have grown.
/// Not thread-safe.
/// </summary>
public sealed class OnigurumaScanner
{
    private readonly OnigurumaPattern[] _patterns;
    private readonly OnigMatcher _matcher = new();
    // Per pattern, side by side so a search visits one entry per pattern.
    private readonly Entry[] _entries;
    // Per distinct text the patterns require (Entry.RequiredKey): where it last occurs in the string it was looked up
    // in, or, for input the scanner does not cache, in the call it was looked up in.
    private readonly RequiredSlot[] _required;
    private long _call;
    private readonly RegexCapture[] _captures;
    private int _captureCount;
    // The compiled kernels' backtracking stack (allocated on first use), and whether they may run: a call with options
    // keeps its budget, cancellation and deadline in the matcher.
    private int[]? _stack;
    private bool _kernels = true;

    /// <summary>Creates a scanner over parsed patterns.</summary>
    public OnigurumaScanner(IReadOnlyList<OnigurumaPattern> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        _patterns = [.. patterns];
        var n = _patterns.Length;
        _entries = new Entry[n];
        var keys = new Dictionary<(object, int), int>();
        var groups = 1;
        for (var i = 0; i < n; i++)
        {
            var program = _patterns[i].Program;
            _entries[i].Captures = new int[2 * _patterns[i].GroupCount];
            _entries[i].Cacheable = !program.UsesSearchStart;
            _entries[i].MinLength = program.MinLength;
            // A literal by its text, a set by its instance (the compiler shares them), each with how it is searched.
            (object, int)? key = program.Required is { } literal ? (literal, 0)
                : program.RequiredUnits is { } units ? (units, 1)
                : program.NotRequiredAscii is { } except ? (except, 2) : null;
            if (key is { } k && !keys.TryGetValue(k, out _entries[i].RequiredKey))
            {
                _entries[i].RequiredKey = keys.Count;
                keys.Add(k, keys.Count);
            }
            else if (key is null)
            {
                _entries[i].RequiredKey = -1;
            }

            groups = Math.Max(groups, _patterns[i].GroupCount);
        }
        _required = new RequiredSlot[keys.Count];
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

    // How many times a pattern whose search of a string ran out of work is searched again, with a fresh budget, from a
    // later offset of that string; past them, later offsets reuse the give-up as no match without searching. A fresh
    // search can match where an earlier one ran out, as for Oniguruma searching a new string, but a line that keeps
    // running out of work would otherwise cost a whole budget at every offset a tokenizer searches it from.
    internal const int GiveUpRetries = 2;
    // The work a pattern's searches of one string stopped short by a limit may do before it goes back to whole searches,
    // which keep the failure memo. A search that ran out of work is not counted: the give-up retries bound those. With no such line, a pattern that needs the memo cost hundreds of times as much. Lower values give
    // up speed: lines of 32,000 characters ran about 4.5 times faster than with no limit at this value, 3.5 at 16,384.
    private const long BoundedWork = 65_536;

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

    [MethodImpl(OnigMatcher.FullOpts)]
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
        _kernels = false;
        try
        {
            return Find(input, startAt, text, textStart, textLength, ref patternIndex);
        }
        finally
        {
            _kernels = true;
            _matcher.RestoreBudget();
        }
    }

    [MethodImpl(OnigMatcher.FullOpts)]
    private RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, string? text, int textStart, int textLength, ref int patternIndex)
    {
        var bestStart = int.MaxValue;
        var entries = _entries;
        _call++;
        for (var i = 0; i < entries.Length; i++)
        {
            ref var e = ref entries[i];
            bool matched;
            int matchStart;
            // Only a start before the best can win: an earlier pattern wins a tie.
            var limit = bestStart - 1;
            var sameText = text is not null && ReferenceEquals(e.Text, text) && e.Start == textStart && e.Length == textLength;
            var cached = sameText && e.Cacheable && e.Position <= startAt;
            // A result found from an earlier offset stays the leftmost from a later one, unless \G ties it to the
            // search start (Onigwrap's cache misses that case). A no match says nothing past its limit.
            if (cached && e.GiveUp < 0 && (e.Matched ? e.MatchStart >= startAt : e.Covered >= limit))
            {
                matched = e.Matched;
                matchStart = e.MatchStart;
            }
            else if (cached && e.GiveUp >= 0 && e.GiveUps > GiveUpRetries)
            {
                LimitExceededCount++;
                matched = false;
                matchStart = 0;
            }
            else
            {
                // After a give-up, the starts before the attempt that ran out of work were explored without a match.
                var searchFrom = cached && e.GiveUp > startAt ? e.GiveUp : startAt;
                var pattern = _patterns[i];
                var captures = e.Captures;
                var work = sameText ? e.Work : 0;
                var required = sameText ? e.Required : text is null ? OnigMatcher.RequiredNotKept : OnigMatcher.RequiredUnknown;
                // A limit ends the search's failure memo, so past BoundedWork a string goes back to whole searches.
                // The matcher itself ignores it for \K.
                if (work > BoundedWork)
                {
                    limit = int.MaxValue;
                }

                // Patterns often require the same text: one lookup per string, or per call, serves them all. That it
                // occurs past the start (int.MaxValue) only bounds a search the limit stops short.
                if (required < -1 && e.RequiredKey >= 0)
                {
                    ref var slot = ref _required[e.RequiredKey];
                    if (text is not null && ReferenceEquals(slot.Text, text) && slot.Start == textStart && slot.Length == textLength)
                    {
                        required = slot.At;
                    }
                    else if (text is null && slot.Call == _call && (slot.At < int.MaxValue || limit < input.Length - e.MinLength))
                    {
                        required = slot.At;
                    }
                }

                // A kernel takes no limit; its result covers the whole string.
                var covered = int.MaxValue;
                RegexSearchStatus status;
                if (_kernels && required >= -1 && required < searchFrom)
                {
                    // The required text does not occur at or after the start, so no start can match. (A call with
                    // options leaves this to the matcher, which polls first.)
                    status = RegexSearchStatus.NoMatch;
                }
                else if (!(_kernels && pattern.PlainKernel is not null && OnigCompiledSearch.TryRun(pattern, input, searchFrom, captures, true, ref _stack, out status, out _)))
                {
                    status = _matcher.Search(pattern.Program, input, searchFrom, captures, limit, required);
                    covered = _matcher.LastCovered;
                    required = _matcher.LastRequired;
                    if (covered < int.MaxValue && status != RegexSearchStatus.LimitExceeded)
                    {
                        work += _matcher.LastWork;
                    }

                    if (required >= -1 && e.RequiredKey >= 0)
                    {
                        ref var slot = ref _required[e.RequiredKey];
                        slot.Text = text;
                        slot.Start = textStart;
                        slot.Length = textLength;
                        slot.Call = _call;
                        slot.At = required;
                    }
                }

                if (status == RegexSearchStatus.LimitExceeded)
                {
                    LimitExceededCount++;
                }
                else if (status is RegexSearchStatus.Cancelled or RegexSearchStatus.TimedOut)
                {
                    e.Text = null;
                    patternIndex = -1;
                    _captureCount = 0;
                    return status;
                }

                // A search that ran out of work counts as no match. It is cached with where it stopped, so a later
                // offset gets a fresh search, which may match as it does for Oniguruma searching a new string, until
                // the string has used its retries.
                matched = status == RegexSearchStatus.Match;
                matchStart = captures[0];
                if (text is not null)
                {
                    // A miss searched again stopped at its limit, which says nothing about a failure past it, so the
                    // count carries over. A match restarts it.
                    var counting = cached && (e.GiveUp >= 0 || !e.Matched);
                    e.GiveUps = (counting ? e.GiveUps : 0) + (status == RegexSearchStatus.LimitExceeded ? 1 : 0);
                    e.Text = text;
                    e.Start = textStart;
                    e.Length = textLength;
                    e.Position = startAt;
                    e.Matched = matched;
                    e.MatchStart = matchStart;
                    e.Covered = covered;
                    e.Work = work;
                    e.Required = required;
                    e.GiveUp = status == RegexSearchStatus.LimitExceeded ? _matcher.LastAttemptStart : -1;
                }
                else
                {
                    e.Text = null;
                }
            }

            if (matched && matchStart < bestStart)
            {
                bestStart = matchStart;
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
        OnigurumaWorkspace.Fill(entries[patternIndex].Captures, _captures, _captureCount);
        return RegexSearchStatus.Match;
    }

    // A pattern's last result for one string, kept as Onigwrap's OnigRegExp keeps it.
    private struct Entry
    {
        // The subject, its range, the position searched from, and the result: whether it matched, its start and its
        // captures (start and end per group).
        internal string? Text;
        internal int Start;
        internal int Length;
        internal int Position;
        internal bool Matched;
        internal int MatchStart;
        internal int[] Captures;
        // For a search that ran out of work: the start of the attempt it stopped in (every earlier start was explored
        // without a match), or -1 when the search completed, and how many searches of the string ran out of work.
        internal int GiveUp;
        internal int GiveUps;
        // The last start a no match covers (a search stops short of a better pattern's match), the work of the searches
        // of the string that a limit stopped short, and where the pattern's required text last occurs.
        internal int Covered;
        internal long Work;
        internal int Required;
        // Whether a result holds at later positions: not with \G, whose results depend on the search start.
        internal bool Cacheable;
        internal int MinLength;
        // The pattern's required text among the scanner's (_required), or -1.
        internal int RequiredKey;
    }

    // Where a required text last occurs (OnigMatcher.LastRequired) in a string, or in the input of one call.
    private struct RequiredSlot
    {
        internal string? Text;
        internal int Start;
        internal int Length;
        internal long Call;
        internal int At;
    }
}
