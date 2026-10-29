#nullable enable
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace GregsEx.Flavors.Oniguruma;

// The backtracking machine. A matcher owns its stacks and registers and reuses them across searches and programs;
// it is not thread-safe. Choice frames are five ints (kind, a, b, c, undo height); register writes made while a frame
// exists are logged (register, old value) so popping the frame restores them. Atomic groups and lookarounds push a
// barrier frame and cut the stack back to it, which keeps the log intact for frames below.
internal sealed class OnigMatcher
{
    private const int FrameWidth = 5;
    // The interpreter's hot methods are compiled fully optimized on their first call instead of starting in tier 0:
    // tier-0 code runs them several times slower, and a host that keeps compiling new methods (an editor) postpones
    // their promotion indefinitely (the runtime restarts its call-counting delay at every new tier-0 method). Run
    // itself stays tiered: it is the one method whose dynamic profile pays (about 7% of a TextMate tokenizer's steady
    // state, from the layout of its instruction switch), and its loop is long enough for on-stack replacement to
    // optimize it without waiting for call counting (docs/specs/flavors.md, FL-ONIG-004).
#if NETCOREAPP3_0_OR_GREATER
    internal const MethodImplOptions FullOpts = MethodImplOptions.AggressiveOptimization;
#else
    internal const MethodImplOptions FullOpts = 0;
#endif
    // Frame kinds; barriers last.
    private const int Choice = 0, RunBackUnit = 1, RunBackCodePoint = 2, LazyRun = 3, BehindNext = 4, AbsentBack = 5,
        BarrierAtomic = 10, BarrierLook = 11, BarrierNotLook = 12, BarrierBehind = 13, BarrierNotBehind = 14,
        BarrierCond = 15;

    private static readonly OnigCharSet s_word = OnigUnicode.StandaloneWord;

    private int[] _stack = new int[FrameWidth * 32];
    private int[] _undo = new int[64];
    private int[] _regs = new int[32];
    // The pattern's limits, and those of the current search.
    private long _maxWork = 10_000_000;
    private long _defaultBytes = 64L << 20;
    private long _maxBytes = 64L << 20;

    // Work units of the last search (see Run): backtracks, loop iterations, calls, look-around entries and starts,
    // plus the units runs consume.
    internal long LastWork => _work;

    // Where the last attempt started: after a LimitExceeded search, every start before it was explored without a match.
    internal int LastAttemptStart { get; private set; }

    // The current search's budget, cancellation and deadline (a Stopwatch timestamp, long.MaxValue for none).
    // Work is checked against _stopAt alone: the budget, or the next poll of the cancellation and the clock.
    private const long PollStride = 4096;
    private long _work;
    private long _budget = 10_000_000;
    private long _stopAt;
    private CancellationToken _cancellation;
    private long _deadline = long.MaxValue;
    private bool _polls;

    // Where the leading run of the last attempt ended (OnigProgram.LeadingRun), or -1.
    private int _leadingEnd;

    // Failed backtrack resumes of the current search, one bit per (OnigProgram.MemoSlots slot, position), kept across
    // its attempts: what follows a resume depends only on that pair, so a pair that failed once fails again. Started
    // once a search has done MemoAfter work, so ordinary searches never clear it (tests lower it to cover every search).
    internal long MemoAfter { get; set; } = 1024;
    private const long MaxMemoBits = 1L << 25;
    private ulong[] _memo = [];
    private bool _memoOn;

    // The pattern's limits, which RestoreBudget returns to after a search with options.
    internal void SetLimits(long maxWork, long maxBytes)
    {
        _maxWork = maxWork;
        _defaultBytes = maxBytes;
        RestoreBudget();
    }

    internal void RestoreBudget() => SetBudget(_maxWork, _defaultBytes, default, long.MaxValue);

    // The Stopwatch timestamp a timeout from now ends at, or long.MaxValue for none.
    internal static long Deadline(TimeSpan? timeout)
    {
        if (timeout is not { } t)
        {
            return long.MaxValue;
        }

        var now = Stopwatch.GetTimestamp();
        var ticks = t.TotalSeconds * Stopwatch.Frequency;
        return ticks >= long.MaxValue - now ? long.MaxValue - 1 : now + (long)ticks;
    }

    internal long ReservedBytes => 4L * (_stack.Length + _undo.Length + _regs.Length) + 8L * _memo.Length;

    // Budget, cancellation and deadline for the searches that follow, until the next call; SetLimits restores the
    // plain budget.
    internal void SetBudget(long maxWork, long maxBytes, CancellationToken cancellation, long deadline)
    {
        _budget = maxWork;
        _maxBytes = maxBytes;
        _polls = cancellation.CanBeCanceled || deadline != long.MaxValue;
        // Stored only when polled: a token field write costs a GC barrier.
        _cancellation = _polls ? cancellation : default;
        _deadline = deadline;
    }

    // Searches for the leftmost match starting in [startAt, input.Length], under the budget for the whole search
    // and polling cancellation and the deadline throughout (before any result, even an immediate one). On Match,
    // captures holds start and end per group (group zero first), -1 for groups that did not participate.
    [MethodImpl(FullOpts)]
    internal RegexSearchStatus Search(OnigProgram program, ReadOnlySpan<char> input, int startAt, Span<int> captures)
    {
        _work = 0;
        _memoOn = false;
        if (_polls && !Poll(out var stopped))
        {
            return stopped;
        }

        _stopAt = NextStop();
        if (_regs.Length < program.RegisterCount)
        {
            _regs = new int[Math.Max(program.RegisterCount, 2 * _regs.Length)];
        }

        var end = input.Length;
        var last = end - program.MinLength;
        if (last < startAt)
        {
            return RegexSearchStatus.NoMatch;
        }

        // What every match contains lies at or after its start: no start after its last occurrence can match.
        if (program.Required is { } required)
        {
            var at = input.LastIndexOf(required, StringComparison.Ordinal);
            if (at < startAt)
            {
                return RegexSearchStatus.NoMatch;
            }

            last = Math.Min(last, at);
        }
        else if (program.RequiredUnits is { } units || program.NotRequiredAscii is not null)
        {
            var at = program.RequiredUnits is not null ? input.LastIndexOfAny(program.RequiredUnits) : input.LastIndexOfAnyExcept(program.NotRequiredAscii!);
            if (at < startAt)
            {
                return RegexSearchStatus.NoMatch;
            }

            last = Math.Min(last, at);
        }

        switch (program.StartAnchor)
        {
            case OnigStartAnchor.BufferStart:
                return startAt == 0 ? Attempt(program, input, 0, startAt, captures) : RegexSearchStatus.NoMatch;
            case OnigStartAnchor.SearchStart:
                return Attempt(program, input, startAt, startAt, captures);
            case OnigStartAnchor.LineStart:
                {
                    var s = startAt;
                    while (s <= last)
                    {
                        if (s == 0 || (input[s - 1] == '\n' && s < end))
                        {
                            var status = Attempt(program, input, s, startAt, captures);
                            if (status != RegexSearchStatus.NoMatch)
                            {
                                return status;
                            }
                        }

                        var nl = input[s..].IndexOf('\n');
                        if (nl < 0)
                        {
                            break;
                        }

                        s += nl + 1;
                    }
                    return RegexSearchStatus.NoMatch;
                }
        }

        var prefix = program.Prefix;
        var first = program.FirstUnits;
        var notFirst = program.NotFirstAscii;
        var preceded = program.PrecededBy;
        var bits = program.StartBits;
        var atEnd = bits.End;
        var pos = startAt;
        while (pos <= last)
        {
            if (preceded is not null)
            {
                // The units that must precede a start drive the scan; each candidate then checks its own unit.
                if (!(pos == 0 ? program.PrecededAtStart : preceded.Contains(input[pos - 1])))
                {
                    var from = Math.Max(pos - 1, 0);
                    var i = input[from..(last < end ? last : end)].IndexOfAny(preceded);
                    if (i < 0)
                    {
                        break;
                    }

                    pos = from + i + 1;
                }

                if (!bits.CanStartWith(pos < end ? input[pos] : -1) || (prefix is not null && !input[pos..].StartsWith(prefix)))
                {
                    pos++;
                    continue;
                }
            }
            else if (prefix is not null)
            {
                var i = input[pos..].IndexOf(prefix, StringComparison.Ordinal);
                if (i < 0 || (pos += i) > last)
                {
                    break;
                }
            }
            else if (first is not null || notFirst is not null)
            {
                var window = input[pos..(last < end ? last + 1 : end)];
                var i = first is not null ? window.IndexOfAny(first) : window.IndexOfAnyExcept(notFirst!);
                if (i < 0)
                {
                    // Only the end of the input is left, where an end anchor can still match.
                    if (!atEnd || last < end || pos > end)
                    {
                        break;
                    }

                    pos = end;
                }
                else
                {
                    pos += i;
                }
            }

            if (pos > startAt && pos < end && char.IsLowSurrogate(input[pos]) && char.IsHighSurrogate(input[pos - 1]))
            {
                // A filter landed inside a character.
                pos++;
                continue;
            }

            _leadingEnd = -1;
            var status = Attempt(program, input, pos, startAt, captures);
            if (status != RegexSearchStatus.NoMatch)
            {
                return status;
            }

            // The next start is a character later (a high surrogate starts two units), or past the leading run.
            pos = _leadingEnd > pos ? _leadingEnd : pos;
            pos += pos < end && char.IsHighSurrogate(input[pos]) ? 2 : 1;
        }
        return RegexSearchStatus.NoMatch;
    }

    private long NextStop() => _polls ? Math.Min(_budget, _work + PollStride) : _budget;

    private bool Poll(out RegexSearchStatus stop)
    {
        if (_cancellation.IsCancellationRequested)
        {
            stop = RegexSearchStatus.Cancelled;
            return false;
        }

        if (Stopwatch.GetTimestamp() >= _deadline)
        {
            stop = RegexSearchStatus.TimedOut;
            return false;
        }

        stop = default;
        return true;
    }

    // Whether the search may go on at this much work; otherwise why it stops.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool Checkpoint(long work, out RegexSearchStatus stop)
    {
        _work = work;
        if (work > _budget)
        {
            stop = RegexSearchStatus.LimitExceeded;
            return false;
        }

        if (_polls && !Poll(out stop))
        {
            return false;
        }

        _stopAt = NextStop();
        stop = default;
        return true;
    }

    // Oniguruma's UTF-16LE decoding: a high surrogate always starts a two-unit character (whatever the next unit is;
    // at the last unit the character is incomplete, so its width passes the end and nothing matches it), any other
    // unit is a character of its own.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CodePointAt(ReadOnlySpan<char> input, int pos, out int width)
    {
        var c = input[pos];
        if (!char.IsHighSurrogate(c))
        {
            width = 1;
            return c;
        }

        width = 2;
        return pos + 1 < input.Length ? 0x10000 + ((c & 0x3FF) << 10) + (input[pos + 1] & 0x3FF) : c;
    }

    // The character before pos as Oniguruma's left_adjust_char_head finds it: a low surrogate with a unit before it
    // belongs to that unit.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CodePointBefore(ReadOnlySpan<char> input, int pos)
    {
        var c = input[pos - 1];
        if (char.IsLowSurrogate(c) && pos >= 2)
        {
            var h = input[pos - 2];
            return char.IsHighSurrogate(h) ? 0x10000 + ((h & 0x3FF) << 10) + (c & 0x3FF) : h;
        }

        return c;
    }

    [MethodImpl(FullOpts)]
    private static bool IsWordAt(ReadOnlySpan<char> input, int pos, bool ascii)
    {
        if (pos >= input.Length)
        {
            return false;
        }

        var c = input[pos];
        if (c < 128 || ascii)
        {
            return c < 128 && s_word.Contains(c);
        }

        return s_word.Contains(CodePointAt(input, pos, out _));
    }

    [MethodImpl(FullOpts)]
    private static bool IsWordBefore(ReadOnlySpan<char> input, int pos, bool ascii)
    {
        if (pos <= 0)
        {
            return false;
        }

        var c = input[pos - 1];
        if (c < 128 || ascii)
        {
            return c < 128 && s_word.Contains(c);
        }

        return s_word.Contains(CodePointBefore(input, pos));
    }

    [MethodImpl(FullOpts)]
    private static bool Anchor(ReadOnlySpan<char> input, int pos, int searchStart, OnigAnchor anchor)
    {
        var end = input.Length;
        switch (anchor)
        {
            case OnigAnchor.LineStart:
                return pos == 0 || (pos < end && input[pos - 1] == '\n');
            case OnigAnchor.LineEnd:
                return pos == end || input[pos] == '\n';
            case OnigAnchor.BufferStart:
                return pos == 0;
            case OnigAnchor.BufferEnd:
                return pos == end;
            case OnigAnchor.SemiEnd:
                return pos == end || (pos == end - 1 && input[pos] == '\n');
            case OnigAnchor.SearchStart:
                return pos == searchStart;
            case OnigAnchor.WordBoundary:
                return IsWordBefore(input, pos, false) != IsWordAt(input, pos, false);
            case OnigAnchor.NotWordBoundary:
                return IsWordBefore(input, pos, false) == IsWordAt(input, pos, false);
            case OnigAnchor.AsciiWordBoundary:
                return IsWordBefore(input, pos, true) != IsWordAt(input, pos, true);
            case OnigAnchor.AsciiNotWordBoundary:
                return IsWordBefore(input, pos, true) == IsWordAt(input, pos, true);
            case OnigAnchor.TextSegmentBoundary:
                return IsGraphemeBreak(input, pos);
            default:
                return !IsGraphemeBreak(input, pos);
        }
    }

    // Where the character before pos starts (Oniguruma's prev char head), or -1.
    [MethodImpl(FullOpts)]
    private static int PreviousHead(ReadOnlySpan<char> input, int pos)
    {
        if (pos <= 0)
        {
            return -1;
        }

        return char.IsLowSurrogate(input[pos - 1]) && pos >= 2 ? pos - 2 : pos - 1;
    }

    // Oniguruma's onigenc_egcb_is_break_position: UAX #29 extended grapheme cluster boundaries.
    [MethodImpl(FullOpts)]
    private static bool IsGraphemeBreak(ReadOnlySpan<char> input, int pos)
    {
        if (pos == 0 || pos >= input.Length)
        {
            return true;
        }

        var prev = PreviousHead(input, pos);
        var fromCode = CodePointAt(input, prev, out _);
        var toCode = CodePointAt(input, pos, out _);
        var from = OnigUnicode.GraphemeBreak(fromCode);
        var to = OnigUnicode.GraphemeBreak(toCode);
        if (from == 0 && to == 0)
        {
            return true;
        }

        if (from == OnigUnicode.GbCR && to == OnigUnicode.GbLF)
        {
            return false;
        }

        if (from is >= OnigUnicode.GbCR and <= OnigUnicode.GbControl || to is >= OnigUnicode.GbCR and <= OnigUnicode.GbControl)
        {
            return true;
        }

        if (from >= OnigUnicode.GbL && to >= OnigUnicode.GbL)
        {
            return !((from == OnigUnicode.GbL && to != OnigUnicode.GbT)
                || ((from == OnigUnicode.GbLV || from == OnigUnicode.GbV) && (to == OnigUnicode.GbV || to == OnigUnicode.GbT))
                || (to == OnigUnicode.GbT && (from == OnigUnicode.GbLVT || from == OnigUnicode.GbT)));
        }

        if (to is OnigUnicode.GbExtend or OnigUnicode.GbZWJ or OnigUnicode.GbSpacingMark || from == OnigUnicode.GbPrepend)
        {
            return false;
        }

        if (from == OnigUnicode.GbZWJ)
        {
            if (!OnigUnicode.IsExtendedPictographic(toCode))
            {
                return true;
            }

            // GB11: \p{ExtPict} Extend* ZWJ x \p{ExtPict}.
            while ((prev = PreviousHead(input, prev)) >= 0)
            {
                var code = CodePointAt(input, prev, out _);
                if (OnigUnicode.IsExtendedPictographic(code))
                {
                    return false;
                }

                if (OnigUnicode.GraphemeBreak(code) != OnigUnicode.GbExtend)
                {
                    break;
                }
            }
            return true;
        }

        if (from == OnigUnicode.GbRegionalIndicator && to == OnigUnicode.GbRegionalIndicator)
        {
            // GB12, GB13: break between pairs of regional indicators.
            var n = 0;
            while ((prev = PreviousHead(input, prev)) >= 0 && OnigUnicode.GraphemeBreak(CodePointAt(input, prev, out _)) == OnigUnicode.GbRegionalIndicator)
            {
                n++;
            }
            return n % 2 != 0;
        }

        return true;
    }

    // Matches one run item at pos: the width in units, or 0 when it does not match.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ItemWidth(OnigProgram program, ReadOnlySpan<char> input, int pos, int kind, int arg)
    {
        if (pos >= input.Length)
        {
            return 0;
        }

        switch (kind)
        {
            case RunItem.Char:
                if (arg <= 0xFFFF)
                {
                    return input[pos] == arg ? 1 : 0;
                }
                else
                {
                    var cp = CodePointAt(input, pos, out var w);
                    return cp == arg && pos + w <= input.Length ? w : 0;
                }
            case RunItem.SetUnit:
                return program.Sets[arg].ContainsUnit(input[pos]) ? 1 : 0;
            case RunItem.Set:
                return SetWidth(program, input, pos, arg);
            case RunItem.AnyNL:
                {
                    if (input[pos] == '\n')
                    {
                        return 0;
                    }

                    CodePointAt(input, pos, out var w);
                    return pos + w <= input.Length ? w : 0;
                }
            default:
                {
                    CodePointAt(input, pos, out var w);
                    return pos + w <= input.Length ? w : 0;
                }
        }
    }

    // The width of the set's character at pos, or 0. A high surrogate at the end is an incomplete character that only a
    // negated class takes, as one unit (Oniguruma's CCLASS_NOT and negated ctypes).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SetWidth(OnigProgram program, ReadOnlySpan<char> input, int pos, int set)
    {
        var cp = CodePointAt(input, pos, out var w);
        if (pos + w <= input.Length)
        {
            return program.Sets[set].Contains(cp) ? w : 0;
        }

        return program.IncompleteSets[set] && program.Sets[set].Contains(cp) ? 1 : 0;
    }

    private void Grow(ref int[] array, int needed)
    {
        var size = Math.Max(needed, array.Length * 2);
        if (4L * (size + (_stack.Length + _undo.Length + _regs.Length - array.Length)) + 8L * _memo.Length > _maxBytes)
        {
            throw new OnigLimitException();
        }

        Array.Resize(ref array, size);
    }

    private sealed class OnigLimitException : Exception;

    // The cleared memo for a search of this program over length units, or null when it would not fit the limits.
    [MethodImpl(FullOpts)]
    private ulong[]? StartMemo(OnigProgram program, int length)
    {
        var words = (int)Math.Min(int.MaxValue, ((long)program.MemoSlotCount * (length + 1) + 63) >> 6);
        if (64L * words > MaxMemoBits)
        {
            return null;
        }

        if (_memo.Length < words)
        {
            if (4L * (_stack.Length + _undo.Length + _regs.Length) + 8L * words > _maxBytes)
            {
                return null;
            }

            _memo = new ulong[words];
        }
        else
        {
            Array.Clear(_memo, 0, words);
        }

        _memoOn = true;
        return _memo;
    }

    // Whether the resume at key already failed; marks it as visited.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Seen(ulong[] memo, int key)
    {
        ref var word = ref memo[key >> 6];
        var bit = 1UL << key;
        if ((word & bit) != 0)
        {
            return true;
        }

        word |= bit;
        return false;
    }

    [MethodImpl(FullOpts)]
    private RegexSearchStatus Attempt(OnigProgram program, ReadOnlySpan<char> input, int start, int searchStart, Span<int> captures)
    {
        LastAttemptStart = start;
        if (++_work > _stopAt && !Checkpoint(_work, out var stop))
        {
            return stop;
        }

        try
        {
            return Run(program, input, start, searchStart, captures);
        }
        catch (OnigLimitException)
        {
            return RegexSearchStatus.LimitExceeded;
        }
    }

    private RegexSearchStatus Run(OnigProgram program, ReadOnlySpan<char> input, int start, int searchStart, Span<int> captures)
    {
        var code = program.Code;
        var regs = _regs;
        var stack = _stack;
        var groups = program.GroupCount;
        var openBase = program.OpenBase;
        // Only ends mark participation: starts and open starts are written before they are read.
        for (var g = 1; g < groups; g++)
        {
            regs[2 * g + 1] = -1;
        }

        regs[program.KeepRegister] = -1;

        regs[program.DepthRegister] = 0;
        regs[program.BankRegister] = program.FirstBank;
        regs[program.LimitRegister] = input.Length;
        // What consuming instructions see: the input, or its part before the position of an enclosing look-behind
        // (Oniguruma's right range). Anchors and boundaries always see the whole input.
        var text = input;
        var end = input.Length;
        int pc = 0, pos = start, sp = 0, up = 0;
        // Work counts what can repeat without consuming input: back edges (JmpBack, LoopTail), calls, look-around entries,
        // backtracks and attempts (charged by Attempt), plus the units runs consume; code between them is
        // straight-line, bounded by the program's length. It is counted per attempt against the room the search has
        // left before its next checkpoint, and added to the search's total when the attempt ends.
        long work = 0;
        var room = _stopAt - _work;
        var slots = program.MemoSlots;
        var memo = _memoOn ? _memo : null;
        var stride = input.Length + 1;

        while (true)
        {
            switch (code[pc])
            {
                case Op.Char:
                    if (pos < end && text[pos] == code[pc + 1])
                    {
                        pos++;
                        pc += 2;
                        continue;
                    }
                    goto Fail;
                case Op.CharIC2:
                    if (pos < end)
                    {
                        var c = text[pos];
                        if (c == code[pc + 1] || c == code[pc + 2])
                        {
                            pos++;
                            pc += 3;
                            continue;
                        }
                    }
                    goto Fail;
                case Op.Str:
                    {
                        var length = code[pc + 2];
                        if (end - pos >= length && text.Slice(pos, length).SequenceEqual(program.Literals.AsSpan(code[pc + 1], length)))
                        {
                            pos += length;
                            pc += 3;
                            continue;
                        }
                        goto Fail;
                    }
                case Op.SetUnit:
                    if (pos < end && program.Sets[code[pc + 1]].ContainsUnit(text[pos]))
                    {
                        pos++;
                        pc += 2;
                        continue;
                    }
                    goto Fail;
                case Op.Set:
                    if (pos < end)
                    {
                        var w = SetWidth(program, text, pos, code[pc + 1]);
                        if (w > 0)
                        {
                            pos += w;
                            pc += 2;
                            continue;
                        }
                    }
                    goto Fail;
                case Op.AnyNL:
                    if (pos < end && text[pos] != '\n')
                    {
                        CodePointAt(text, pos, out var w);
                        if (pos + w <= end)
                        {
                            pos += w;
                            pc++;
                            continue;
                        }
                    }
                    goto Fail;
                case Op.Any:
                    if (pos < end)
                    {
                        CodePointAt(text, pos, out var w);
                        if (pos + w <= end)
                        {
                            pos += w;
                            pc++;
                            continue;
                        }
                    }
                    goto Fail;
                case Op.Anchor:
                    if (Anchor(input, pos, searchStart, (OnigAnchor)code[pc + 1]))
                    {
                        pc += 2;
                        continue;
                    }
                    goto Fail;
                case Op.Keep:
                    Set(ref regs, program.KeepRegister, pos, sp, ref up);
                    pc++;
                    continue;
                case Op.Jmp:
                    pc = code[pc + 1];
                    continue;
                case Op.JmpBack:
                    if (++work > room)
                    {
                        goto Budget;
                    }

                    pc = code[pc + 1];
                    continue;
                case Op.Split:
                    Push(ref sp, Choice, code[pc + 2], pos, 0, up);
                    pc = code[pc + 1];
                    continue;
                case Op.SplitLazy:
                    Push(ref sp, Choice, code[pc + 1], pos, 0, up);
                    pc = code[pc + 2];
                    continue;
                case Op.Alt:
                    {
                        var c = pos < end ? text[pos] : -1;
                        var a = code[pc + 3];
                        var b = code[pc + 4];
                        var okFirst = a < 0 || program.Bits[a].CanStartWith(c);
                        var okSecond = b < 0 || program.Bits[b].CanStartWith(c);
                        if (okFirst && memo is not null && Seen(memo, slots![pc + 5] * stride + pos))
                        {
                            okFirst = false;
                        }

                        if (okFirst)
                        {
                            if (okSecond)
                            {
                                Push(ref sp, Choice, code[pc + 2], pos, 0, up);
                            }

                            pc = code[pc + 1];
                            continue;
                        }

                        if (okSecond)
                        {
                            pc = code[pc + 2];
                            continue;
                        }
                        goto Fail;
                    }
                case Op.Run:
                    {
                        var kind = code[pc + 1];
                        var arg = code[pc + 2];
                        var min = code[pc + 3];
                        var max = code[pc + 4];
                        var mode = (OnigRepeatMode)code[pc + 5];
                        if (mode == OnigRepeatMode.Lazy)
                        {
                            var count = 0;
                            while (count < min)
                            {
                                var w = ItemWidth(program, text, pos, kind, arg);
                                if (w == 0)
                                {
                                    goto Fail;
                                }

                                pos += w;
                                count++;
                            }
                            if (max < 0 || count < max)
                            {
                                Push(ref sp, LazyRun, pc, pos, count, up);
                            }

                            pc += 7;
                            continue;
                        }

                        int runStart = pos, minPos, n;
                        var unitItems = kind is RunItem.SetUnit || (kind == RunItem.Char && arg <= 0xFFFF);
                        if (unitItems)
                        {
                            var span = text[pos..];
                            if ((uint)max < (uint)span.Length)
                            {
                                span = span[..max];
                            }

                            n = kind == RunItem.Char ? span.IndexOfAnyExcept((char)arg) : program.Sets[arg].RunLength(text, pos, max < 0 ? int.MaxValue : max);
                            if (n < 0 && kind == RunItem.Char)
                            {
                                n = span.Length;
                            }
                            else if (n < 0)
                            {
                                // No vectorized form: measure unit by unit.
                                n = 0;
                                var units = program.Sets[arg];
                                while (n < span.Length && units.ContainsUnit(span[n]))
                                {
                                    n++;
                                }
                            }

                            minPos = runStart + min;
                            pos += n;
                            work += n;
                            if (pc == program.LeadingRun)
                            {
                                _leadingEnd = pos;
                            }

                            if (n < min)
                            {
                                goto Fail;
                            }
                        }
                        else
                        {
                            var count = 0;
                            minPos = pos;
                            while (max < 0 || count < max)
                            {
                                var w = ItemWidth(program, text, pos, kind, arg);
                                if (w == 0)
                                {
                                    break;
                                }

                                pos += w;
                                count++;
                                work++;
                                if (count == min)
                                {
                                    minPos = pos;
                                }
                            }
                            if (pc == program.LeadingRun)
                            {
                                _leadingEnd = pos;
                            }

                            if (count < min)
                            {
                                goto Fail;
                            }
                        }

                        pc += 7;
                        if (mode == OnigRepeatMode.Greedy && pos > minPos)
                        {
                            var follow = code[pc - 1];
                            if (follow >= 0 && unitItems)
                            {
                                // Give units back up front to where what follows can start.
                                pos = GiveBack(program, text, pos, minPos, follow);
                                if (pos < minPos)
                                {
                                    goto Fail;
                                }
                            }
                            else if (follow >= 0)
                            {
                                var bits = program.Bits[follow];
                                while (pos > minPos && !bits.CanStartWith(pos < end ? text[pos] : -1))
                                {
                                    pos = StepBack(text, pos, unitItems, minPos);
                                }

                                if (!bits.CanStartWith(pos < end ? text[pos] : -1))
                                {
                                    goto Fail;
                                }
                            }

                            if (pos > minPos)
                            {
                                Push(ref sp, unitItems ? RunBackUnit : RunBackCodePoint, pc, pos, minPos, up);
                            }
                        }

                        continue;
                    }
                case Op.SaveOpen:
                    Set(ref regs, openBase + code[pc + 1], pos, sp, ref up);
                    pc += 2;
                    continue;
                case Op.SaveClose:
                    {
                        var g = code[pc + 1];
                        Set(ref regs, 2 * g, regs[openBase + g], sp, ref up);
                        Set(ref regs, 2 * g + 1, pos, sp, ref up);
                        pc += 2;
                        continue;
                    }
                case Op.RetIfCalled:
                    {
                        var depth = regs[program.DepthRegister];
                        if (depth > 0 && regs[program.FrameBase + program.FrameSize * (depth - 1)] == code[pc + 1])
                        {
                            pc = Return(program, ref regs, depth, sp, ref up);
                            continue;
                        }

                        pc += 2;
                        continue;
                    }
                case Op.Call:
                    if (++work > room)
                    {
                        goto Budget;
                    }

                    {
                        var depth = regs[program.DepthRegister];
                        if (depth >= OnigProgram.MaxCallDepth - 1)
                        {
                            // As Oniguruma at SubexpCallMaxNestLevel: this path fails, the search goes on.
                            goto Fail;
                        }

                        // The frame keeps the group, the return and the open starts the callee may move.
                        var group = code[pc + 2];
                        var frame = program.FrameBase + program.FrameSize * depth;
                        Set(ref regs, frame, group, sp, ref up);
                        Set(ref regs, frame + 1, pc + 3, sp, ref up);
                        var saves = program.CallSaves[group];
                        for (var i = 0; i < saves.Length; i++)
                        {
                            Set(ref regs, frame + 2 + i, regs[openBase + saves[i]], sp, ref up);
                        }

                        Set(ref regs, program.DepthRegister, depth + 1, sp, ref up);
                        Set(ref regs, program.BankRegister, regs[program.BankRegister] + program.LoopBankSize, sp, ref up);
                        pc = code[pc + 1];
                        continue;
                    }
                case Op.LoopInit:
                    Set(ref regs, regs[program.BankRegister] + 2 * code[pc + 1], 0, sp, ref up);
                    pc += 2;
                    continue;
                case Op.LoopHead:
                    {
                        var count = regs[regs[program.BankRegister] + 2 * code[pc + 1]];
                        var max = code[pc + 3];
                        var enter = pc + 6;
                        if (count < code[pc + 2])
                        {
                            pc = enter;
                        }
                        else if (max < 0 || count < max)
                        {
                            var (first, second) = code[pc + 4] != 0 ? (code[pc + 5], enter) : (enter, code[pc + 5]);
                            if (memo is not null && Seen(memo, slots![first] * stride + pos))
                            {
                                pc = second;
                            }
                            else
                            {
                                Push(ref sp, Choice, second, pos, 0, up);
                                pc = first;
                            }
                        }
                        else
                        {
                            pc = code[pc + 5];
                        }
                        continue;
                    }
                case OnigCompiler.LoopEnter:
                    {
                        var bank = regs[program.BankRegister];
                        Set(ref regs, bank + 2 * code[pc + 1] + 1, pos, sp, ref up);
                        if (code[pc + 3] >= 0)
                        {
                            // Snapshot the captures the body may change, for the empty-iteration check.
                            var snapshot = bank + 2 * program.LoopCount + code[pc + 2];
                            var loopGroups = program.LoopGroups[code[pc + 3]];
                            for (var i = 0; i < loopGroups.Length; i++)
                            {
                                var g = loopGroups[i];
                                var gEnd = regs[2 * g + 1];
                                Set(ref regs, snapshot + 2 * i, gEnd < 0 ? -1 : regs[2 * g], sp, ref up);
                                Set(ref regs, snapshot + 2 * i + 1, gEnd, sp, ref up);
                            }
                        }

                        pc += 4;
                        continue;
                    }
                case Op.LoopTail:
                    if (++work > room)
                    {
                        goto Budget;
                    }

                    {
                        var bank = regs[program.BankRegister];
                        var r = bank + 2 * code[pc + 1];
                        Set(ref regs, r, regs[r] + 1, sp, ref up);
                        var empty = pos == regs[r + 1];
                        if (empty && code[pc + 5] >= 0)
                        {
                            // An empty iteration that changed a capture does not end the loop.
                            var snapshot = bank + 2 * program.LoopCount + code[pc + 4];
                            var loopGroups = program.LoopGroups[code[pc + 5]];
                            for (var i = 0; i < loopGroups.Length && empty; i++)
                            {
                                // Oniguruma: a change is a group set for the first time, or a new span unless the
                                // old and new spans are both empty.
                                var g = loopGroups[i];
                                var gEnd = regs[2 * g + 1];
                                var gStart = gEnd < 0 ? -1 : regs[2 * g];
                                var oldStart = regs[snapshot + 2 * i];
                                var oldEnd = regs[snapshot + 2 * i + 1];
                                var changed = gEnd >= 0 && (oldEnd < 0 || ((oldStart != gStart || oldEnd != gEnd) && !(oldStart == oldEnd && gStart == gEnd)));
                                empty = !changed;
                            }
                        }

                        pc = empty ? code[pc + 3] : code[pc + 2];
                        continue;
                    }
                case Op.AtomicStart:
                    Push(ref sp, BarrierAtomic, code[pc + 1], pos, 0, up);
                    pc += 2;
                    continue;
                case Op.AtomicEnd:
                case Op.CondEnd:
                    sp = FindBarrier(sp, code[pc + 1]);
                    pc += 2;
                    continue;
                case Op.LookStart:
                    if (++work > room)
                    {
                        goto Budget;
                    }

                    Push(ref sp, code[pc + 1] != 0 ? BarrierNotLook : BarrierLook, code[pc + 2], pos, code[pc + 3], up);
                    pc += 4;
                    continue;
                case Op.LookEnd:
                case Op.BehindEnd:
                    {
                        var at = FindBarrier(sp, code[pc + 2]);
                        stack = _stack;
                        if (code[pc] == Op.BehindEnd && pos != stack[at + 2])
                        {
                            goto Fail;
                        }

                        if (code[pc + 1] != 0)
                        {
                            Restore(regs, ref up, stack[at + 4]);
                            sp = at;
                            goto Fail;
                        }

                        if (code[pc] == Op.BehindEnd)
                        {
                            // The limit BehindStart replaced is the first write it logged after its barrier.
                            var outer = _undo[stack[at + 4] + 1];
                            sp = at;
                            Set(ref regs, program.LimitRegister, outer, sp, ref up);
                            text = input[..outer];
                            end = outer;
                        }
                        else
                        {
                            sp = at;
                        }

                        pos = stack[at + 2];
                        pc += 3;
                        continue;
                    }
                case Op.BehindStart:
                    if (++work > room)
                    {
                        goto Budget;
                    }

                    {
                        var negative = code[pc + 1] != 0;
                        var min = code[pc + 4];
                        var max = code[pc + 5];
                        var high = pos - min;
                        if (high < 0)
                        {
                            if (negative)
                            {
                                pc = code[pc + 3];
                                continue;
                            }
                            goto Fail;
                        }

                        var low = max < 0 ? 0 : Math.Max(0, pos - max);
                        Push(ref sp, negative ? BarrierNotBehind : BarrierBehind, code[pc + 2], pos, code[pc + 3], up);
                        // The body cannot consume past this position.
                        Set(ref regs, program.LimitRegister, pos, sp, ref up);
                        text = input[..pos];
                        end = pos;
                        // Candidate starts are character heads: never between a high and a low surrogate.
                        if (IsMidPair(input, high) && high - 1 >= low)
                        {
                            high--;
                        }

                        var next = PreviousCandidate(input, high, low);
                        if (next >= low)
                        {
                            Push(ref sp, BehindNext, pc + 6, next, low, up);
                        }

                        pos = high;
                        pc += 6;
                        continue;
                    }
                case Op.AssertSet:
                    {
                        var set = program.Sets[code[pc + 3]];
                        var edges = code[pc + 4];
                        bool ok;
                        if (code[pc + 2] != 0)
                        {
                            ok = pos > 0
                                ? set.Contains(CodePointBefore(input, pos)) || ((edges & 2) != 0 && pos < input.Length && input[pos - 1] == '\n')
                                : (edges & 1) != 0;
                        }
                        else
                        {
                            // The unit test respects an enclosing look-behind's limit; the end edge is the input's.
                            ok = pos < end ? set.Contains(CodePointAt(text, pos, out _)) : pos >= input.Length && (edges & 1) != 0;
                        }

                        if (ok == (code[pc + 1] == 0))
                        {
                            pc += 5;
                            continue;
                        }
                        goto Fail;
                    }
                case Op.AssertStr:
                    {
                        var literal = program.Literals.AsSpan(code[pc + 3], code[pc + 4]);
                        var ok = code[pc + 2] != 0
                            ? pos >= literal.Length && input.Slice(pos - literal.Length, literal.Length).SequenceEqual(literal)
                            : text[pos..].StartsWith(literal);
                        if (ok == (code[pc + 1] == 0))
                        {
                            pc += 5;
                            continue;
                        }
                        goto Fail;
                    }
                case Op.AssertLiterals:
                    if (program.Tables[code[pc + 3]].Matches(input, pos, code[pc + 2] != 0) == (code[pc + 1] == 0))
                    {
                        pc += 4;
                        continue;
                    }
                    goto Fail;
                case Op.Backref:
                    {
                        var ignoreCase = code[pc + 1] != 0;
                        var count = code[pc + 2];
                        var matched = -1;
                        for (var i = 0; i < count; i++)
                        {
                            var g = code[pc + 3 + i];
                            var gEnd = regs[2 * g + 1];
                            if (gEnd < 0)
                            {
                                continue;
                            }

                            var gStart = regs[2 * g];
                            var length = BackrefLength(input, text, gStart, gEnd, pos, ignoreCase);
                            if (length >= 0)
                            {
                                matched = length;
                                break;
                            }
                        }
                        if (matched < 0)
                        {
                            goto Fail;
                        }

                        pos += matched;
                        pc += 3 + count;
                        continue;
                    }
                case Op.CondRef:
                    {
                        var count = code[pc + 2];
                        var any = false;
                        for (var i = 0; i < count; i++)
                        {
                            if (regs[2 * code[pc + 3 + i] + 1] >= 0)
                            {
                                any = true;
                                break;
                            }
                        }
                        pc = any ? pc + 3 + count : code[pc + 1];
                        continue;
                    }
                case Op.CondStart:
                    Push(ref sp, BarrierCond, code[pc + 1], pos, code[pc + 2], up);
                    pc += 3;
                    continue;
                case Op.Absent:
                    {
                        var literal = program.Literals.AsSpan(code[pc + 1], code[pc + 2]);
                        // The longest range from pos holding no start of the literal.
                        var q = text[pos..].IndexOf(literal, StringComparison.Ordinal);
                        var limit = q < 0 ? end : pos + q;
                        if (limit > pos)
                        {
                            Push(ref sp, AbsentBack, pc + 3, limit, pos, up);
                        }

                        pos = limit;
                        pc += 3;
                        continue;
                    }
                case Op.Match:
                    {
                        if (program.UsesCalls)
                        {
                            var depth = regs[program.DepthRegister];
                            if (depth > 0 && regs[program.FrameBase + program.FrameSize * (depth - 1)] == 0)
                            {
                                pc = Return(program, ref regs, depth, sp, ref up);
                                continue;
                            }
                        }

                        var keep = regs[program.KeepRegister];
                        // \K inside a look-ahead may lie past the end: Oniguruma clamps it.
                        captures[0] = keep >= 0 ? Math.Min(keep, pos) : start;
                        captures[1] = pos;
                        for (var g = 1; g < groups && 2 * g + 1 < captures.Length; g++)
                        {
                            var gEnd = regs[2 * g + 1];
                            captures[2 * g] = gEnd < 0 ? -1 : regs[2 * g];
                            captures[2 * g + 1] = gEnd;
                        }
                        _work += work;
                        return RegexSearchStatus.Match;
                    }
                case Op.Fail:
                    goto Fail;
                default:
                    throw new InvalidOperationException($"opcode {code[pc]}");
            }

        Budget:
            // A checkpoint: stop, or run the same instruction again, which counts itself again.
            if (!Checkpoint(_work + work, out var stopped))
            {
                return stopped;
            }

            work = -1;
            room = _stopAt - _work;
            continue;

        Fail:
            stack = _stack;
            while (true)
            {
                if (sp == 0)
                {
                    _work += work;
                    return RegexSearchStatus.NoMatch;
                }

                if (++work > room)
                {
                    if (!Checkpoint(_work + work, out var stop))
                    {
                        return stop;
                    }

                    work = 0;
                    room = _stopAt - _work;
                }

                if (memo is null && slots is not null && _work + work > MemoAfter)
                {
                    memo = StartMemo(program, input.Length);
                    if (memo is null)
                    {
                        slots = null;
                    }
                }

                sp -= FrameWidth;
                Restore(regs, ref up, stack[sp + 4]);
                if (regs[program.LimitRegister] != end)
                {
                    end = regs[program.LimitRegister];
                    text = input[..end];
                }

                switch (stack[sp])
                {
                    case Choice:
                        pc = stack[sp + 1];
                        pos = stack[sp + 2];
                        if (memo is not null && slots![pc] >= 0 && Seen(memo, slots[pc] * stride + pos))
                        {
                            continue;
                        }
                        break;
                    case RunBackUnit:
                    case RunBackCodePoint:
                    case AbsentBack:
                        {
                            var unit = stack[sp] != RunBackCodePoint;
                            var minPos = stack[sp + 3];
                            pc = stack[sp + 1];
                            var p = StepBack(text, stack[sp + 2], unit, minPos);
                            var follow = stack[sp] == AbsentBack ? -1 : code[pc - 1];
                            if (follow >= 0 && unit)
                            {
                                p = GiveBack(program, text, p, minPos, follow);
                                if (p < minPos)
                                {
                                    continue;
                                }
                            }
                            else if (follow >= 0)
                            {
                                var bits = program.Bits[follow];
                                while (p > minPos && !bits.CanStartWith(text[p]))
                                {
                                    p = StepBack(text, p, unit, minPos);
                                }

                                if (!bits.CanStartWith(text[p]))
                                {
                                    continue;
                                }
                            }

                            if (p > minPos)
                            {
                                stack[sp + 2] = p;
                                sp += FrameWidth;
                            }

                            if (memo is not null && slots![pc] >= 0 && Seen(memo, slots[pc] * stride + p))
                            {
                                continue;
                            }

                            pos = p;
                            break;
                        }
                    case LazyRun:
                        {
                            var runPc = stack[sp + 1];
                            var p = stack[sp + 2];
                            var w = ItemWidth(program, text, p, code[runPc + 1], code[runPc + 2]);
                            if (w == 0)
                            {
                                continue;
                            }

                            var count = stack[sp + 3] + 1;
                            var max = code[runPc + 4];
                            if (max < 0 || count < max)
                            {
                                stack[sp + 2] = p + w;
                                stack[sp + 3] = count;
                                sp += FrameWidth;
                            }

                            if (memo is not null && slots![runPc + 7] >= 0 && Seen(memo, slots[runPc + 7] * stride + p + w))
                            {
                                continue;
                            }

                            pos = p + w;
                            pc = runPc + 7;
                            break;
                        }
                    case BehindNext:
                        {
                            var s = stack[sp + 2];
                            pc = stack[sp + 1];
                            var next = PreviousCandidate(input, s, stack[sp + 3]);
                            if (next >= stack[sp + 3])
                            {
                                stack[sp + 2] = next;
                                sp += FrameWidth;
                            }

                            pos = s;
                            break;
                        }
                    case BarrierNotLook:
                    case BarrierNotBehind:
                    case BarrierCond:
                        pos = stack[sp + 2];
                        pc = stack[sp + 3];
                        break;
                    default:
                        continue;
                }
                break;
            }
        }
    }

    [MethodImpl(FullOpts)]
    private static bool IsMidPair(ReadOnlySpan<char> input, int pos)
        => pos > 0 && pos < input.Length && char.IsLowSurrogate(input[pos]) && char.IsHighSurrogate(input[pos - 1]);

    // The look-behind candidate before s, a character head no lower than low, or low - 1 when none is left.
    [MethodImpl(FullOpts)]
    private static int PreviousCandidate(ReadOnlySpan<char> input, int s, int low)
    {
        var next = s - 1;
        if (IsMidPair(input, next) && next - 1 >= low)
        {
            next--;
        }

        return next;
    }

    // The last position in [minPos, pos] of a unit run where the follow can start, or minPos - 1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GiveBack(OnigProgram program, ReadOnlySpan<char> text, int pos, int minPos, int follow)
    {
        if (program.Bits[follow].CanStartWith(pos < text.Length ? text[pos] : -1))
        {
            return pos;
        }

        var window = text[minPos..pos];
        var values = program.BitsBack[follow]!;
        return minPos + (program.BitsBackExcept[follow] ? window.LastIndexOfAnyExcept(values) : window.LastIndexOfAny(values));
    }

    // One item back from pos in a run: a unit, or a code point when items may be surrogate pairs.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int StepBack(ReadOnlySpan<char> input, int pos, bool unit, int minPos)
    {
        pos--;
        if (!unit && pos > minPos && char.IsLowSurrogate(input[pos]) && char.IsHighSurrogate(input[pos - 1]))
        {
            pos--;
        }

        return pos;
    }

    // The length of text at pos matching the capture [start, end) of input, or -1.
    [MethodImpl(FullOpts)]
    private static int BackrefLength(ReadOnlySpan<char> input, ReadOnlySpan<char> text, int start, int end, int pos, bool ignoreCase)
    {
        var length = end - start;
        if (!ignoreCase)
        {
            return text.Length - pos >= length && text.Slice(pos, length).SequenceEqual(input.Slice(start, length)) ? length : -1;
        }

        var i = start;
        var j = pos;
        while (i < end)
        {
            if (j >= text.Length)
            {
                return -1;
            }

            var a = CodePointAt(input, i, out var wa);
            var b = CodePointAt(text, j, out var wb);
            if (a != b && OnigUnicode.Fold(a) != OnigUnicode.Fold(b))
            {
                return -1;
            }

            i += wa;
            j += wb;
        }
        return j - pos;
    }

    // Returns from the call at depth: restores the open starts it saved and the caller's loop bank; the return pc.
    [MethodImpl(FullOpts)]
    private int Return(OnigProgram program, ref int[] regs, int depth, int sp, ref int up)
    {
        var frame = program.FrameBase + program.FrameSize * (depth - 1);
        var saves = program.CallSaves[regs[frame]];
        for (var i = 0; i < saves.Length; i++)
        {
            Set(ref regs, program.OpenBase + saves[i], regs[frame + 2 + i], sp, ref up);
        }

        Set(ref regs, program.DepthRegister, depth - 1, sp, ref up);
        Set(ref regs, program.BankRegister, regs[program.BankRegister] - program.LoopBankSize, sp, ref up);
        return regs[frame + 1];
    }

    [MethodImpl(FullOpts)]
    private int FindBarrier(int sp, int id)
    {
        var stack = _stack;
        for (var i = sp - FrameWidth; i >= 0; i -= FrameWidth)
        {
            if (stack[i] >= BarrierAtomic && stack[i + 1] == id)
            {
                return i;
            }
        }
        throw new InvalidOperationException("barrier not found");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Restore(int[] regs, ref int up, int height)
    {
        var undo = _undo;
        while (up > height)
        {
            up -= 2;
            regs[undo[up]] = undo[up + 1];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Set(ref int[] regs, int index, int value, int sp, ref int up)
    {
        if (sp != 0)
        {
            if (up + 2 > _undo.Length)
            {
                Grow(ref _undo, up + 2);
            }

            _undo[up] = index;
            _undo[up + 1] = regs[index];
            up += 2;
        }
        regs[index] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(ref int sp, int kind, int a, int b, int c, int up)
    {
        if (sp + FrameWidth > _stack.Length)
        {
            Grow(ref _stack, sp + FrameWidth);
        }

        var stack = _stack;
        stack[sp] = kind;
        stack[sp + 1] = a;
        stack[sp + 2] = b;
        stack[sp + 3] = c;
        stack[sp + 4] = up;
        sp += FrameWidth;
    }
}
