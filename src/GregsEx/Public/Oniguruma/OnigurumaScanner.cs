#nullable enable
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Execution.Compiled;
using GregsEx.Flavors.Oniguruma;

namespace GregsEx;

/// <summary>
/// Searches several patterns from one position and selects the match that starts first, the earliest pattern on a tie,
/// as TextMate's scanners (vscode-oniguruma's OnigScanner.findNextMatchSync) do. The scanner walks the start positions
/// once and tries at each the patterns that can start there, in order, as Oniguruma's onig_regset_search does under
/// ONIG_REGSET_POSITION_LEAD: no pattern is tried past the winning start. For string-backed input, what the walk
/// learned about a pattern (a match further on, or that none is left) is kept per string and reused from later
/// positions, as Onigwrap's OnigRegExp does, except for patterns using \G, whose results depend on the search start.
/// A search that runs out of work is searched again from later offsets of the string, a bounded number of times.
/// One matcher serves every pattern; searches allocate nothing once the stacks have grown.
/// Not thread-safe.
/// </summary>
public sealed class OnigurumaScanner
{
    // Start-unit classes: an ASCII unit is its own class, then every other unit, then the end of the input.
    private const int NonAsciiUnit = 128, EndUnit = 129, UnitClasses = 130;

    // What an entry still has to check before an attempt (Entry.Checks). The first four never change; the others
    // hold for the current input: its required text has not been looked up yet, its last search ran out of work, or
    // its attempts have cost enough for whole searches.
    private const int Anchored = 1, Preceded = 2, Prefixed = 4, Whole = 8, Unresolved = 16, GaveUp = 32, Costly = 64;

    private readonly OnigurumaPattern[] _patterns;
    private readonly OnigMatcher _matcher = new();
    // Per pattern, side by side so a search visits one entry per pattern.
    private readonly Entry[] _entries;
    // Per distinct text the patterns require (Entry.RequiredKey): where it last occurs in the current input.
    private readonly RequiredSlot[] _required;
    // Pattern sets, one bit per pattern in _words words. Per start-unit class, the patterns a match of which can start
    // with it (OnigProgram.StartBits). For the current input: the patterns still to be tried at later starts, those
    // with a known match further on (Entry.MatchStart), and those whose search ran out of work. A pattern in none of
    // the three has no match left.
    private readonly int _words;
    private readonly ulong[] _starts;
    private readonly ulong[] _alive;
    private readonly ulong[] _parked;
    private readonly ulong[] _gaveUp;
    // The patterns whose results depend on the search start (\G), which every call tries afresh.
    private readonly ulong[] _perCall;
    // The patterns searched whole before the walk, in order: those using \K, whose match can start before or after
    // its attempt, and the pattern of a scanner that has only one, which has no other pattern to stop for.
    private readonly int[] _eager;
    // The input the sets and entries describe (a string and a window of it; null for input the scanner does not keep
    // results for, which every call starts over), its generation, and the last position searched from.
    private string? _text;
    private int _textStart;
    private int _textLength;
    private long _line;
    private int _position;
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
        _words = (n + 63) >> 6;
        _starts = new ulong[UnitClasses * _words];
        _alive = new ulong[_words];
        _parked = new ulong[_words];
        _gaveUp = new ulong[_words];
        _perCall = new ulong[_words];
        var keys = new Dictionary<(object, int), int>();
        var eager = new List<int>();
        var groups = 1;
        for (var i = 0; i < n; i++)
        {
            var program = _patterns[i].Program;
            ref var e = ref _entries[i];
            e.Program = program;
            e.Captures = new int[2 * _patterns[i].GroupCount];
            e.Prefix = program.Prefix;
            e.MinLength = program.MinLength;
            // A literal by its text, a set by its instance (the compiler shares them), each with how it is searched.
            (object, int)? key = program.Required is { } literal ? (literal, 0)
                : program.RequiredUnits is { } units ? (units, 1)
                : program.NotRequiredAscii is { } except ? (except, 2) : null;
            if (key is { } k && !keys.TryGetValue(k, out e.RequiredKey))
            {
                e.RequiredKey = keys.Count;
                keys.Add(k, keys.Count);
            }
            else if (key is null)
            {
                e.RequiredKey = -1;
            }

            var bit = 1UL << i;
            var bits = program.StartBits;
            for (var unit = 0; unit < UnitClasses; unit++)
            {
                if (bits.CanStartWith(unit < NonAsciiUnit ? unit : unit == NonAsciiUnit ? char.MaxValue : -1))
                {
                    _starts[unit * _words + (i >> 6)] |= bit;
                }
            }

            // Searched whole before the walk: a pattern with no other to stop for, and one using \K. A kernel is
            // searched whole too, when the walk first reaches one of its pattern's starts.
            var whole = n == 1 || program.UsesKeep;
            if (whole)
            {
                eager.Add(i);
            }

            if (program.UsesSearchStart)
            {
                e.PerCall = true;
                _perCall[i >> 6] |= bit;
            }

            e.Fixed = (program.StartAnchor != OnigStartAnchor.None ? Anchored : 0)
                | (program.PrecededBy is not null ? Preceded : 0)
                | (program.Prefix is not null ? Prefixed : 0)
                | (whole || _patterns[i].PlainKernel is not null ? Whole : 0);
            groups = Math.Max(groups, _patterns[i].GroupCount);
        }
        _required = new RequiredSlot[keys.Count];
        _eager = [.. eager];
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
    // The work a pattern's failed attempts on one string may do before it goes back to whole searches, which keep the
    // failure memo for all their attempts and whose result serves the rest of the string. With no such line, a pattern
    // that needs the memo cost hundreds of times as much on a line a tokenizer walks token by token.
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
        _captureCount = 0;
        if (_entries.Length == 0)
        {
            return RegexSearchStatus.NoMatch;
        }

        // What a search learns holds for later starts because both read the same characters. A start just after a
        // high surrogate is inside a character as every earlier search read it, so it shares nothing with them.
        if (startAt > 0 && char.IsHighSurrogate(input[startAt - 1]))
        {
            _text = null;
        }

        Begin(text, textStart, textLength, startAt);
        var status = RegexSearchStatus.NoMatch;
        var winner = -1;
        if (_eager.Length != 0 && !SearchEager(input, startAt, out status, out winner))
        {
            return status;
        }

        if (winner < 0)
        {
            status = Walk(input, startAt, int.MaxValue, out winner);
        }

        if (status != RegexSearchStatus.Match)
        {
            return status;
        }

        patternIndex = winner;
        _captureCount = _patterns[winner].GroupCount;
        OnigurumaWorkspace.Fill(_entries[winner].Captures, _captures, _captureCount);
        return RegexSearchStatus.Match;
    }

    // Makes the sets and entries describe this input from startAt on.
    [MethodImpl(OnigMatcher.FullOpts)]
    private void Begin(string? text, int textStart, int textLength, int startAt)
    {
        _call++;
        var alive = _alive;
        var parked = _parked;
        if (text is null || !ReferenceEquals(_text, text) || _textStart != textStart || _textLength != textLength)
        {
            // Another input: every pattern is to be tried. Entries start over when they are next touched.
            _text = text;
            _textStart = textStart;
            _textLength = textLength;
            _line++;
            _position = startAt;
            alive.AsSpan().Fill(ulong.MaxValue);
            if ((_entries.Length & 63) != 0)
            {
                alive[^1] = (1UL << _entries.Length) - 1;
            }

            parked.AsSpan().Clear();
            _gaveUp.AsSpan().Clear();
            return;
        }

        if (startAt < _position)
        {
            GoBack(startAt);
        }

        _position = startAt;
        var entries = _entries;
        for (var w = 0; w < alive.Length; w++)
        {
            // A match before the start no longer answers, a result that depends on the search start never does, and
            // a search that ran out of work gets its retries (GiveUp, when the walk reaches the pattern).
            var again = _perCall[w] | _gaveUp[w];
            var p = parked[w] & ~again;
            while (p != 0)
            {
                var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(p);
                if (entries[i].MatchStart < startAt)
                {
                    again |= 1UL << i;
                }

                p &= p - 1;
            }

            parked[w] &= ~again;
            alive[w] |= again;
            _gaveUp[w] = 0;
        }
    }

    // A start before the last one: what the walks from later starts learned says nothing about the starts in between.
    // Only a match found by a search from no later than startAt still answers; every other pattern is tried again,
    // with its retries. The string keeps what its attempts cost.
    private void GoBack(int startAt)
    {
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++)
        {
            ref var e = ref entries[i];
            if (e.Line != _line)
            {
                continue;
            }

            if (e.From > startAt)
            {
                _parked[i >> 6] &= ~(1UL << i);
            }

            e.Covered = -1;
            e.GiveUps = 0;
            e.Checks &= ~GaveUp;
        }

        for (var w = 0; w < _alive.Length; w++)
        {
            _alive[w] = ~_parked[w];
            _gaveUp[w] = 0;
        }

        if ((entries.Length & 63) != 0)
        {
            _alive[^1] &= (1UL << entries.Length) - 1;
        }
    }

    // Searches the eager patterns that have no result yet. False when a search was cancelled or timed out (status).
    // A match of \K can start before startAt, where no walk finds it: winner is then the pattern the call selects.
    [MethodImpl(OnigMatcher.FullOpts)]
    private bool SearchEager(ReadOnlySpan<char> input, int startAt, out RegexSearchStatus status, out int winner)
    {
        status = RegexSearchStatus.NoMatch;
        winner = -1;
        var entries = _entries;
        var before = -1;
        foreach (var i in _eager)
        {
            ref var e = ref entries[i];
            var bit = 1UL << i;
            if (e.Call != _call)
            {
                Touch(ref e, input.Length);
            }

            // After a search that ran out of work, the starts before the attempt it stopped in are explored.
            if ((_alive[i >> 6] & bit) != 0 && Retry(i, ref e))
            {
                status = SearchWhole(i, ref e, input, startAt, Math.Max(startAt, e.Covered + 1));
                if (status is RegexSearchStatus.Cancelled or RegexSearchStatus.TimedOut)
                {
                    return false;
                }
            }

            if (before < 0 && (_parked[i >> 6] & bit) != 0 && e.MatchStart < startAt)
            {
                before = i;
            }
        }

        status = RegexSearchStatus.NoMatch;
        if (before < 0)
        {
            return true;
        }

        // The patterns before it are searched first, and a match at startAt ends the call there. Otherwise the
        // earliest start wins, and no other pattern has one before startAt.
        status = Walk(input, startAt, before, out winner);
        if (status is RegexSearchStatus.Cancelled or RegexSearchStatus.TimedOut)
        {
            return false;
        }

        if (status != RegexSearchStatus.Match)
        {
            winner = before;
            foreach (var i in _eager)
            {
                if ((_parked[i >> 6] & (1UL << i)) != 0 && entries[i].MatchStart < entries[winner].MatchStart)
                {
                    winner = i;
                }
            }

            status = RegexSearchStatus.Match;
        }

        return true;
    }

    // Walks the starts from startAt and tries at each the patterns that can start there, in order: the first match
    // is the call's. With stop, only startAt and the patterns before stop are tried, and a miss says nothing else.
    [MethodImpl(OnigMatcher.FullOpts)]
    private RegexSearchStatus Walk(ReadOnlySpan<char> input, int startAt, int stop, out int winner)
    {
        var entries = _entries;
        var starts = _starts;
        var alive = _alive;
        var parked = _parked;
        var words = alive.Length;
        var end = input.Length;
        var call = _call;
        var full = stop == int.MaxValue;
        var lastPos = full ? end : startAt;
        for (var pos = startAt; pos <= lastPos; pos++)
        {
            var unit = EndUnit;
            // A start inside a character is only tried where the search starts.
            var inside = false;
            if (pos < end)
            {
                unit = input[pos];
                if (unit >= NonAsciiUnit)
                {
                    inside = pos > startAt && char.IsLowSurrogate((char)unit) && char.IsHighSurrogate(input[pos - 1]);
                    unit = NonAsciiUnit;
                }
            }

            var row = unit * words;
            ulong anyAlive = 0, anyParked = 0;
            for (var w = 0; w < words; w++)
            {
                var a = alive[w];
                var p = parked[w];
                anyAlive |= a;
                anyParked |= p;
                var m = inside ? p : (starts[row + w] & a) | p;
                while (m != 0)
                {
                    var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(m);
                    var bit = m & (0UL - m);
                    m ^= bit;
                    if (i >= stop)
                    {
                        winner = -1;
                        return RegexSearchStatus.NoMatch;
                    }

                    ref var e = ref entries[i];
                    if ((p & bit) != 0)
                    {
                        if (e.MatchStart <= pos)
                        {
                            winner = i;
                            return RegexSearchStatus.Match;
                        }

                        continue;
                    }

                    if (e.Call != call)
                    {
                        Touch(ref e, end);
                    }

                    if (e.Covered >= pos)
                    {
                        continue;
                    }

                    if (pos > e.Last)
                    {
                        alive[w] &= ~bit;
                        continue;
                    }

                    RegexSearchStatus status;
                    if (e.Checks != 0)
                    {
                        // Most patterns that need a check need only this one: the literal every match starts with.
                        if (e.Checks == Prefixed)
                        {
                            if (!input[pos..].StartsWith(e.Prefix))
                            {
                                continue;
                            }

                            goto Attempt;
                        }

                        var check = Check(i, ref e, input, pos, startAt, out status);
                        if (check != CheckResult.Attempt)
                        {
                            if (check == CheckResult.Skip)
                            {
                                continue;
                            }

                            winner = check == CheckResult.Found ? i : -1;
                            return status;
                        }
                    }

                Attempt:
                    status = _matcher.MatchAt(e.Program, input, pos, startAt, e.Captures, e.Work, e.Memo);
                    if (status == RegexSearchStatus.Match)
                    {
                        Park(i, ref e, startAt);
                        winner = i;
                        return RegexSearchStatus.Match;
                    }

                    if (status != RegexSearchStatus.NoMatch)
                    {
                        if (status != RegexSearchStatus.LimitExceeded)
                        {
                            winner = -1;
                            return status;
                        }

                        GiveUp(i, ref e, pos);
                        continue;
                    }

                    // The attempts of a call are those of the pattern's search from startAt: its work and its failure
                    // memo go from one to the next.
                    var work = _matcher.LastWork;
                    e.LineWork += work - e.Work;
                    e.Work = work;
                    e.Memo = _matcher.MemoStarted;
                    e.Covered = _matcher.NextStart(input, pos);
                    if (e.LineWork > BoundedWork && _text is not null)
                    {
                        e.Checks |= Costly;
                    }
                }
            }

            if (anyAlive == 0 && full)
            {
                // No pattern is left to try: the earliest known match, if any, is the next one.
                winner = -1;
                if (anyParked == 0)
                {
                    break;
                }

                for (var w = 0; w < words; w++)
                {
                    for (var p = parked[w]; p != 0; p &= p - 1)
                    {
                        var i = (w << 6) + System.Numerics.BitOperations.TrailingZeroCount(p);
                        if (winner < 0 || entries[i].MatchStart < entries[winner].MatchStart)
                        {
                            winner = i;
                        }
                    }
                }

                return RegexSearchStatus.Match;
            }
        }

        if (full)
        {
            // Every start was tried: no pattern has a match left. Patterns that ran out of work keep their retries.
            alive.AsSpan().Clear();
        }

        winner = -1;
        return RegexSearchStatus.NoMatch;
    }

    private enum CheckResult { Skip, Attempt, Found, Stopped }

    // What an attempt of the pattern at pos still depends on: whether it may start there at all, or is searched whole
    // instead. Found: the pattern's whole search matched at or before pos, so it is the call's match (status Match).
    // Stopped: the search was cancelled or timed out (status).
    [MethodImpl(MethodImplOptions.NoInlining | OnigMatcher.FullOpts)]
    private CheckResult Check(int i, ref Entry e, ReadOnlySpan<char> input, int pos, int startAt, out RegexSearchStatus status)
    {
        status = RegexSearchStatus.NoMatch;
        if ((e.Checks & GaveUp) != 0 && (!Retry(i, ref e) || e.Covered >= pos))
        {
            return CheckResult.Skip;
        }

        if ((e.Checks & Unresolved) != 0 && !Resolve(i, ref e, input, pos))
        {
            return CheckResult.Skip;
        }

        var checks = e.Checks;
        var program = e.Program;
        if ((checks & (Whole | Costly)) != 0)
        {
            status = SearchWhole(i, ref e, input, startAt, pos);
            return status switch
            {
                RegexSearchStatus.Match => e.MatchStart <= pos ? CheckResult.Found : CheckResult.Skip,
                RegexSearchStatus.Cancelled or RegexSearchStatus.TimedOut => CheckResult.Stopped,
                _ => CheckResult.Skip,
            };
        }

        if ((checks & Anchored) != 0)
        {
            switch (program.StartAnchor)
            {
                case OnigStartAnchor.BufferStart when pos != 0:
                case OnigStartAnchor.SearchStart when pos != startAt:
                    _alive[i >> 6] &= ~(1UL << i);
                    return CheckResult.Skip;
                case OnigStartAnchor.LineStart when pos != 0 && !(input[pos - 1] == '\n' && pos < input.Length):
                    {
                        // No start before the next line's can match.
                        var newline = input[pos..].IndexOf('\n');
                        if (newline < 0)
                        {
                            _alive[i >> 6] &= ~(1UL << i);
                        }
                        else
                        {
                            e.Covered = pos + newline;
                        }

                        return CheckResult.Skip;
                    }
            }
        }

        if ((checks & Preceded) != 0 && !(pos == 0 ? program.PrecededAtStart : program.PrecededBy!.Contains(input[pos - 1])))
        {
            return CheckResult.Skip;
        }

        if ((checks & Prefixed) != 0 && !input[pos..].StartsWith(e.Prefix))
        {
            return CheckResult.Skip;
        }

        return CheckResult.Attempt;
    }

    // The entry's first use in a call: the call's work starts at zero, with no failure memo.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Touch(ref Entry e, int end)
    {
        e.Call = _call;
        e.Work = 0;
        e.Memo = 0;
        if (e.Line != _line || e.PerCall)
        {
            StartOver(ref e, end);
        }
    }

    // The entry's first use in a string, which has every start to try, or in a call, for a pattern whose results
    // depend on the search start: what the last call learned is forgotten.
    [MethodImpl(MethodImplOptions.NoInlining | OnigMatcher.FullOpts)]
    private void StartOver(ref Entry e, int end)
    {
        e.Covered = -1;
        e.GiveUps = 0;
        if (e.Line != _line)
        {
            e.Line = _line;
            e.Last = end - e.MinLength;
            e.LineWork = 0;
            e.Checks = e.Fixed | (e.RequiredKey >= 0 ? Unresolved : 0);
        }
        else
        {
            e.Checks &= ~GaveUp;
        }
    }

    // Looks up where the pattern's required text last occurs, once per input for the patterns that require it. False
    // when it does not occur at or after pos: no start from there on can match.
    [MethodImpl(OnigMatcher.FullOpts)]
    private bool Resolve(int i, ref Entry e, ReadOnlySpan<char> input, int pos)
    {
        ref var slot = ref _required[e.RequiredKey];
        if (slot.Line != _line || (slot.At < 0 && pos < slot.From))
        {
            var at = OnigMatcher.FindRequired(e.Program, input[pos..], fromEnd: true);
            slot.Line = _line;
            slot.From = pos;
            slot.At = at < 0 ? -1 : pos + at;
        }

        if (slot.At < pos)
        {
            _alive[i >> 6] &= ~(1UL << i);
            return false;
        }

        e.Last = Math.Min(e.Last, slot.At);
        e.Checks &= ~Unresolved;
        return true;
    }

    // A pattern whose last search of the string ran out of work is searched again from where that search stopped
    // (every earlier start was explored), until the string has used its retries: then it counts as running out of
    // work again, without searching.
    [MethodImpl(OnigMatcher.FullOpts)]
    private bool Retry(int i, ref Entry e)
    {
        if ((e.Checks & GaveUp) == 0)
        {
            return true;
        }

        if (e.GiveUps > GiveUpRetries)
        {
            LimitExceededCount++;
            _alive[i >> 6] &= ~(1UL << i);
            _gaveUp[i >> 6] |= 1UL << i;
            return false;
        }

        e.Checks &= ~GaveUp;
        return true;
    }

    // The pattern's search of the input from the first start left (from), by its kernel when it has one: a match waits
    // in the entry for the walk to reach its start, and a miss leaves nothing to try.
    [MethodImpl(OnigMatcher.FullOpts)]
    private RegexSearchStatus SearchWhole(int i, ref Entry e, ReadOnlySpan<char> input, int startAt, int from)
    {
        var bit = 1UL << i;
        if ((e.Checks & Unresolved) != 0 && !Resolve(i, ref e, input, from))
        {
            return RegexSearchStatus.NoMatch;
        }

        var pattern = _patterns[i];
        if (!(_kernels && pattern.PlainKernel is not null && OnigCompiledSearch.TryRun(pattern, input, startAt, e.Captures, true, ref _stack, out var status, out _)))
        {
            status = _matcher.Search(e.Program, input, startAt, e.Captures, e.RequiredKey >= 0 ? _required[e.RequiredKey].At : OnigMatcher.RequiredUnknown, from);
        }

        switch (status)
        {
            case RegexSearchStatus.Match:
                Park(i, ref e, startAt);
                break;
            case RegexSearchStatus.NoMatch:
                _alive[i >> 6] &= ~bit;
                break;
            case RegexSearchStatus.LimitExceeded:
                GiveUp(i, ref e, _matcher.LastAttemptStart);
                break;
        }

        return status;
    }

    // The pattern matches at Captures[0], and nowhere from startAt up to there.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Park(int i, ref Entry e, int startAt)
    {
        e.MatchStart = e.Captures[0];
        e.From = startAt;
        e.GiveUps = 0;
        _alive[i >> 6] &= ~(1UL << i);
        _parked[i >> 6] |= 1UL << i;
    }

    // A search that ran out of work counts as no match for the call. The next call searches again from the attempt
    // it stopped in, which may match as it does for Oniguruma searching a new string.
    [MethodImpl(OnigMatcher.FullOpts)]
    private void GiveUp(int i, ref Entry e, int attempt)
    {
        LimitExceededCount++;
        e.GiveUps++;
        e.Covered = Math.Max(e.Covered, attempt - 1);
        e.Checks |= GaveUp;
        _alive[i >> 6] &= ~(1UL << i);
        _gaveUp[i >> 6] |= 1UL << i;
    }

    // What the scanner knows of a pattern in the current input.
    private struct Entry
    {
        internal OnigProgram Program;
        // Start and end per group of the pattern's last match.
        internal int[] Captures;
        // The literal every match starts with (OnigProgram.Prefix), or null.
        internal string? Prefix;
        internal int MinLength;
        // The pattern's required text among the scanner's (_required), or -1.
        internal int RequiredKey;
        // The checks the pattern always needs, and those it needs now (see Anchored).
        internal int Fixed;
        internal int Checks;
        // Whether the pattern's results depend on the search start (\G): no call keeps another's.
        internal bool PerCall;
        // The input generation and the call the fields below were last brought up to date for.
        internal long Line;
        internal long Call;
        // No match starts up to Covered, as far as the walks from the current position on have tried, and none can
        // start after Last (the input's length and the required text allow no later start).
        internal int Covered;
        internal int Last;
        // For a pattern in _parked: where its match starts, and the start of the search that found it, which no
        // earlier start is known to share.
        internal int MatchStart;
        internal int From;
        // How many of the pattern's searches of the string ran out of work since its last match.
        internal int GiveUps;
        // The work of the pattern's attempts in the current call, which its budget bounds as one search, the failure
        // memo they share (OnigMatcher.MemoStarted), and the work of its failed attempts on the string.
        internal long Work;
        internal int Memo;
        internal long LineWork;
    }

    // Where a required text last occurs in the input from From on (-1: nowhere), for the input generation Line.
    private struct RequiredSlot
    {
        internal long Line;
        internal int From;
        internal int At;
    }
}
