using System.Buffers;
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
#if !NETSTANDARD2_0
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
#endif

namespace GregsEx.Flavors.Pcre2.Runtime.Composition;

internal sealed class Pcre2Accelerator
{
    private enum FastPathKind : byte
    {
        None = 0,
        Literal = 1,
        LiteralAlternation = 2,
        SingleSetRepeat = 3,
        SetRepeatSuffixLiteral = 4,
        AtomicSequence = 5
    }

    private readonly struct AsciiLatin1Bitmap(ulong b0, ulong b1, ulong b2, ulong b3)
    {
        private readonly ulong _b0 = b0, _b1 = b1, _b2 = b2, _b3 = b3;
        internal bool IsEmpty => (_b0 | _b1 | _b2 | _b3) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(uint c)
        {
            if (c >= 256)
            {
                return false;
            }

            var mask = 1UL << (int)(c & 63);
            return (c >> 6) switch
            {
                0 => (_b0 & mask) != 0,
                1 => (_b1 & mask) != 0,
                2 => (_b2 & mask) != 0,
                _ => (_b3 & mask) != 0
            };
        }

        internal bool Overlaps(in AsciiLatin1Bitmap other) =>
            ((_b0 & other._b0) | (_b1 & other._b1) | (_b2 & other._b2) | (_b3 & other._b3)) != 0;
    }

    private sealed class BmpBitmapBox
    {
        internal ulong[]? Bits;
    }

    private readonly struct FastCharSet
    {
        internal readonly AsciiLatin1Bitmap Latin1;
        internal readonly SearchValues<char>? CharSearchValues;
        internal readonly SearchValues<byte>? ByteSearchValues;
        internal readonly Pcre2Node ClassNode;
        internal readonly bool NoneAbove255;
        internal readonly bool AllAbove255;
        internal readonly bool IsNotNewline;
        private readonly BmpBitmapBox? _bmpBox;

        internal FastCharSet(
            AsciiLatin1Bitmap latin1,
            SearchValues<char>? charSv,
            SearchValues<byte>? byteSv,
            Pcre2Node classNode,
            bool noneAbove255,
            bool allAbove255,
            bool isNotNewline = false)
        {
            Latin1 = latin1;
            CharSearchValues = charSv;
            ByteSearchValues = byteSv;
            ClassNode = classNode;
            NoneAbove255 = noneAbove255;
            AllAbove255 = allAbove255;
            IsNotNewline = isNotNewline;
            _bmpBox = !noneAbove255 && !allAbove255 ? new BmpBitmapBox() : null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(char c, Pcre2Program program, Pcre2DataView data)
        {
            if (c < 256)
            {
                return Latin1.Contains(c);
            }

            if (NoneAbove255)
            {
                return false;
            }

            if (AllAbove255)
            {
                return true;
            }

            var bits = _bmpBox?.Bits ?? EnsureBmp(program, data);
            return (bits[c >> 6] & (1UL << (c & 63))) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong[]? GetBmpBits(Pcre2Program program, Pcre2DataView data) =>
            _bmpBox is null ? null : (_bmpBox.Bits ?? EnsureBmp(program, data));

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ulong[] EnsureBmp(Pcre2Program program, Pcre2DataView data)
        {
            var bits = new ulong[1024];
            for (var i = 0; i < 256; i++)
            {
                if (Latin1.Contains((uint)i))
                {
                    bits[i >> 6] |= 1UL << (i & 63);
                }
            }
            for (var cp = 256; cp <= char.MaxValue; cp++)
            {
                if (MatchesClassUncharged(program, ClassNode, cp, data))
                {
                    bits[cp >> 6] |= 1UL << (cp & 63);
                }
            }
            if (_bmpBox is not null)
            {
                Volatile.Write(ref _bmpBox.Bits, bits);
            }

            return bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool Contains(byte b) => Latin1.Contains(b);

        internal bool DisjointFrom(in FastCharSet other)
        {
            if (Latin1.Overlaps(in other.Latin1))
            {
                return false;
            }

            if (!NoneAbove255 && !other.NoneAbove255)
            {
                return false;
            }

            return true;
        }
    }

    private readonly struct SeqAtom(
        bool isLiteral,
        string? charLiteral,
        byte[]? byteLiteral,
        FastCharSet set,
        int min,
        int max,
        int captureGroup)
    {
        internal readonly bool IsLiteral = isLiteral;
        internal readonly string? CharLiteral = charLiteral;
        internal readonly byte[]? ByteLiteral = byteLiteral;
        internal readonly FastCharSet Set = set;
        internal readonly int Min = min;
        internal readonly int Max = max;
        internal readonly int CaptureGroup = captureGroup;
    }

    private readonly FastPathKind _kind;
    private readonly bool _canAccelerate;
    private readonly int _maxLookbehind;
    private readonly bool _startAnchor;
    private readonly bool _startLineAnchor;
    private readonly bool _endAnchor;
    private readonly bool _endAnchorAllowsTrailingNewline;
    private readonly string? _charLiteral;
    private readonly byte[]? _byteLiteral;
    private readonly string[]? _charAltLiterals;
    private readonly byte[][]? _byteAltLiterals;
    private readonly SearchValues<string>? _charAltSearchValues;
    private readonly SearchValues<byte>? _byteAltFirstBytes;
    private readonly FastCharSet _primarySet;
    private readonly int _repeatMin;
    private readonly int _repeatMax;
    private readonly SeqAtom[]? _seqAtoms;
    private readonly int _seqFixedLiteralAtomIndex;
    private readonly int _seqFixedLiteralOffset;
    private readonly bool _hasEndAnchorTerminalCheck;
    private readonly FastCharSet _endAnchorTerminalSet;
    private readonly bool _endTerminalAllowsTrailingNewline;
    private readonly int _requiredCodeUnit;

    internal bool HasDirectPlan => _canAccelerate && _kind != FastPathKind.None;

    internal Pcre2Accelerator(Pcre2Program program, Pcre2DataView data)
    {
        _maxLookbehind = ComputeMaxLookbehind(program);
        _requiredCodeUnit = -1;
        _seqFixedLiteralAtomIndex = -1;

        if (!IsEligibleProgram(program))
        {
            _canAccelerate = false;
            return;
        }
        _canAccelerate = true;

        var root = UnwrapRootCapture(program, program.Root);
        if (TryBuildEndAnchorTerminalFilter(program, data, root, out _endAnchorTerminalSet, out _endTerminalAllowsTrailingNewline))
        {
            _hasEndAnchorTerminalCheck = true;
        }
        _requiredCodeUnit = ExtractRequiredCodeUnit(program, root);

        var patternAnchored = (program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0;

        if (TryBuildLiteral(program, root, out _charLiteral, out _byteLiteral, out _startAnchor, out _endAnchor, out _endAnchorAllowsTrailingNewline) &&
            (!patternAnchored || _startAnchor))
        {
            _kind = FastPathKind.Literal;
            return;
        }

        if (!patternAnchored && program.GroupCount == 1 &&
            TryBuildLiteralAlternation(program, root, out _charAltLiterals, out _byteAltLiterals, out _charAltSearchValues, out _byteAltFirstBytes))
        {
            _kind = FastPathKind.LiteralAlternation;
            return;
        }

        if (program.GroupCount == 1 &&
            TryBuildSingleSetRepeat(program, data, root, out _primarySet, out _repeatMin, out _repeatMax, out _startAnchor, out _endAnchor, out _endAnchorAllowsTrailingNewline) &&
            (!patternAnchored || _startAnchor))
        {
            _kind = FastPathKind.SingleSetRepeat;
            return;
        }

        if (!patternAnchored && program.GroupCount == 1 &&
            TryBuildSetRepeatSuffixLiteral(program, data, root, out _primarySet, out _repeatMin, out _charLiteral, out _byteLiteral))
        {
            _kind = FastPathKind.SetRepeatSuffixLiteral;
            return;
        }

        if (TryBuildAtomicSequence(
                program,
                data,
                root,
                out _seqAtoms,
                out _startAnchor,
                out _startLineAnchor,
                out _endAnchor,
                out _endAnchorAllowsTrailingNewline,
                out _seqFixedLiteralAtomIndex,
                out _seqFixedLiteralOffset) &&
            (!patternAnchored || _startAnchor))
        {
            _kind = FastPathKind.AtomicSequence;
        }
    }

#if !NETSTANDARD2_0
    internal bool TryMatch(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<char> input,
        int startAt,
        Pcre2MatchOptions options,
        Pcre2ExecutionCapacity capacity,
        Pcre2ValidationPolicy policy,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        if (!_canAccelerate ||
            options.OffsetLimit is not null ||
            capacity.Frames < Pcre2ExecutionCapacity.Default.Frames ||
            capacity.CaptureEvents < Pcre2ExecutionCapacity.Default.CaptureEvents ||
            !state.Budget.IsUnconstrained((input.Length - startAt) * 8L + 256))
        {
            return false;
        }

        var flags = Pcre2KernelOperation.EffectiveFlags(program, options.Flags);
        if ((flags & ~Pcre2MatchFlags.Notbol) != 0)
        {
            return false;
        }

        var savedState = state;
        var fragment = Pcre2TextFragments<char, Pcre2CharUnits>.Bind(
            input, startAt, _maxLookbehind, program.Profile, flags, policy, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None ||
            !fragment.Validation.Valid ||
            !fragment.Available ||
            fragment.VisibleEnd < input.Length ||
            (program.Profile.Utf && !state.ValidatedCleanSubject))
        {
            state = savedState;
            return false;
        }

        if (_hasEndAnchorTerminalCheck && !CheckEndAnchorTerminal(program, data, input))
        {
            state.TryCharge(1, startAt);
            result = new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, startAt, -1);
            return true;
        }

        if (_requiredCodeUnit >= 0 && _kind == FastPathKind.None)
        {
            if (startAt >= input.Length || input.Slice(startAt).IndexOf((char)_requiredCodeUnit) < 0)
            {
                state.TryCharge(Math.Max(1, input.Length - startAt), startAt);
                result = new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, startAt, -1);
                return true;
            }
            state = savedState;
            return false;
        }

        var notbol = (flags & Pcre2MatchFlags.Notbol) != 0;
        var handled = _kind switch
        {
            FastPathKind.Literal => ExecuteLiteral(program, input, startAt, notbol, ref state, pairs, out result),
            FastPathKind.LiteralAlternation => ExecuteLiteralAlternation(program, input, startAt, ref state, pairs, out result),
            FastPathKind.SingleSetRepeat => ExecuteSingleSetRepeat(program, data, input, startAt, notbol, ref state, pairs, out result),
            FastPathKind.SetRepeatSuffixLiteral => ExecuteSetRepeatSuffixLiteral(program, data, input, startAt, ref state, pairs, out result),
            FastPathKind.AtomicSequence => ExecuteAtomicSequence(program, data, input, startAt, notbol, ref state, pairs, out result),
            _ => false,
        };
        if (!handled)
        {
            state = savedState;
        }

        return handled;
    }

    internal bool TryMatch(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<byte> input,
        int startAt,
        Pcre2MatchOptions options,
        Pcre2ExecutionCapacity capacity,
        Pcre2ValidationPolicy policy,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        if (!_canAccelerate ||
            options.OffsetLimit is not null ||
            capacity.Frames < Pcre2ExecutionCapacity.Default.Frames ||
            capacity.CaptureEvents < Pcre2ExecutionCapacity.Default.CaptureEvents ||
            !state.Budget.IsUnconstrained((input.Length - startAt) * 8L + 256))
        {
            return false;
        }

        var flags = Pcre2KernelOperation.EffectiveFlags(program, options.Flags);
        if ((flags & ~Pcre2MatchFlags.Notbol) != 0)
        {
            return false;
        }

        var savedState = state;
        var fragment = Pcre2TextFragments<byte, Pcre2ByteUnits>.Bind(
            input, startAt, _maxLookbehind, program.Profile, flags, policy, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None ||
            !fragment.Validation.Valid ||
            !fragment.Available ||
            fragment.VisibleEnd < input.Length ||
            (program.Profile.Utf && !state.ValidatedCleanSubject))
        {
            state = savedState;
            return false;
        }

        if (_hasEndAnchorTerminalCheck && !CheckEndAnchorTerminal(input))
        {
            state.TryCharge(1, startAt);
            result = new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, startAt, -1);
            return true;
        }

        if (_requiredCodeUnit >= 0 && _kind == FastPathKind.None)
        {
            if (startAt >= input.Length || input.Slice(startAt).IndexOf((byte)_requiredCodeUnit) < 0)
            {
                state.TryCharge(Math.Max(1, input.Length - startAt), startAt);
                result = new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, startAt, -1);
                return true;
            }
            state = savedState;
            return false;
        }

        var notbol = (flags & Pcre2MatchFlags.Notbol) != 0;
        var handled = _kind switch
        {
            FastPathKind.Literal => ExecuteLiteral(program, input, startAt, notbol, ref state, pairs, out result),
            FastPathKind.LiteralAlternation => ExecuteLiteralAlternation(program, input, startAt, ref state, pairs, out result),
            FastPathKind.SingleSetRepeat => ExecuteSingleSetRepeat(program, input, startAt, notbol, ref state, pairs, out result),
            FastPathKind.SetRepeatSuffixLiteral => ExecuteSetRepeatSuffixLiteral(program, input, startAt, ref state, pairs, out result),
            FastPathKind.AtomicSequence => ExecuteAtomicSequence(program, input, startAt, notbol, ref state, pairs, out result),
            _ => false,
        };
        if (!handled)
        {
            state = savedState;
        }

        return handled;
    }
#endif

    private bool CheckEndAnchorTerminal(Pcre2Program program, Pcre2DataView data, ReadOnlySpan<char> input)
    {
        if (input.IsEmpty)
        {
            return false;
        }

        var last = input[^1];
        if (_endAnchorTerminalSet.Contains(last, program, data))
        {
            return true;
        }

        if (_endTerminalAllowsTrailingNewline && last is '\n' or '\r')
        {
            var idx = input.Length - 2;
            if (last == '\n' && idx >= 0 && input[idx] == '\r')
            {
                idx--;
            }

            return idx >= 0 && _endAnchorTerminalSet.Contains(input[idx], program, data);
        }
        return false;
    }

    private bool CheckEndAnchorTerminal(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return false;
        }

        var last = input[^1];
        if (_endAnchorTerminalSet.Contains(last))
        {
            return true;
        }

        if (_endTerminalAllowsTrailingNewline && last is (byte)'\n' or (byte)'\r')
        {
            var idx = input.Length - 2;
            if (last == (byte)'\n' && idx >= 0 && input[idx] == (byte)'\r')
            {
                idx--;
            }

            return idx >= 0 && _endAnchorTerminalSet.Contains(input[idx]);
        }
        return false;
    }

    private static Pcre2MatchResult PublishSingleMatch(
        Pcre2Program program,
        int mStart,
        int mEnd,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs)
    {
        state.TryCharge(Math.Max(1, mEnd - startAt), mStart);
        if (!pairs.IsEmpty)
        {
            pairs[0] = new(true, mStart, mEnd);
            var count = Math.Min(program.GroupCount, pairs.Length);
            for (var i = 1; i < count; i++)
            {
                pairs[i] = default;
            }
        }
        return new(Pcre2MatchStatus.Match, pairs.IsEmpty ? 0 : 1, mStart, -1);
    }

    private static Pcre2MatchResult PublishNoMatch(int startAt, int inspected, ref RunState state)
    {
        state.TryCharge(Math.Max(1, inspected), startAt);
        return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, startAt, -1);
    }

    private bool ExecuteLiteral(
        Pcre2Program program,
        ReadOnlySpan<char> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var lit = _charLiteral.AsSpan();
        if (_startAnchor)
        {
            if (startAt > 0 || notbol)
            { result = PublishNoMatch(startAt, 1, ref state); return true; }
            if (_endAnchor)
            {
                if (input.SequenceEqual(lit))
                {
                    result = PublishSingleMatch(program, 0, lit.Length, startAt, ref state, pairs);
                    return true;
                }
                if (_endAnchorAllowsTrailingNewline && input.Length > lit.Length && input[^1] is '\n' or '\r')
                {
                    return false;
                }

                result = PublishNoMatch(startAt, lit.Length, ref state);
                return true;
            }
            if (input.StartsWith(lit))
            {
                result = PublishSingleMatch(program, 0, lit.Length, startAt, ref state, pairs);
                return true;
            }
            result = PublishNoMatch(startAt, lit.Length, ref state);
            return true;
        }
        if (_endAnchor)
        {
            if (_endAnchorAllowsTrailingNewline && !input.IsEmpty && input[^1] is '\n' or '\r')
            {
                return false;
            }

            var pos = input.Length - lit.Length;
            if (pos >= startAt && input.Slice(pos).SequenceEqual(lit))
            {
                result = PublishSingleMatch(program, pos, input.Length, startAt, ref state, pairs);
                return true;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }
        var idx = input.Slice(startAt).IndexOf(lit);
        if (idx < 0)
        {
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }
        var matchStart = startAt + idx;
        result = PublishSingleMatch(program, matchStart, matchStart + lit.Length, startAt, ref state, pairs);
        return true;
    }

    private bool ExecuteLiteral(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        ReadOnlySpan<byte> lit = _byteLiteral.AsSpan();
        if (_startAnchor)
        {
            if (startAt > 0 || notbol)
            { result = PublishNoMatch(startAt, 1, ref state); return true; }
            if (_endAnchor)
            {
                if (input.SequenceEqual(lit))
                {
                    result = PublishSingleMatch(program, 0, lit.Length, startAt, ref state, pairs);
                    return true;
                }
                if (_endAnchorAllowsTrailingNewline && input.Length > lit.Length && input[^1] is (byte)'\n' or (byte)'\r')
                {
                    return false;
                }

                result = PublishNoMatch(startAt, lit.Length, ref state);
                return true;
            }
            if (input.StartsWith(lit))
            {
                result = PublishSingleMatch(program, 0, lit.Length, startAt, ref state, pairs);
                return true;
            }
            result = PublishNoMatch(startAt, lit.Length, ref state);
            return true;
        }
        if (_endAnchor)
        {
            if (_endAnchorAllowsTrailingNewline && !input.IsEmpty && input[^1] is (byte)'\n' or (byte)'\r')
            {
                return false;
            }

            var pos = input.Length - lit.Length;
            if (pos >= startAt && input.Slice(pos).SequenceEqual(lit))
            {
                result = PublishSingleMatch(program, pos, input.Length, startAt, ref state, pairs);
                return true;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }
        var idx = input.Slice(startAt).IndexOf(lit);
        if (idx < 0)
        {
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }
        var matchStart = startAt + idx;
        result = PublishSingleMatch(program, matchStart, matchStart + lit.Length, startAt, ref state, pairs);
        return true;
    }

    private bool ExecuteLiteralAlternation(
        Pcre2Program program,
        ReadOnlySpan<char> input,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var slice = input.Slice(startAt);
        var idx = slice.IndexOfAny(_charAltSearchValues!);
        if (idx < 0)
        {
            result = PublishNoMatch(startAt, slice.Length, ref state);
            return true;
        }
        var mStart = startAt + idx;
        var tail = input.Slice(mStart);
        foreach (var branch in _charAltLiterals!)
        {
            if (tail.StartsWith(branch.AsSpan()))
            {
                result = PublishSingleMatch(program, mStart, mStart + branch.Length, startAt, ref state, pairs);
                return true;
            }
        }
        return false;
    }

    private bool ExecuteLiteralAlternation(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var alts = _byteAltLiterals!;
        var firstSv = _byteAltFirstBytes!;
        var pos = startAt;
        while (pos < input.Length)
        {
            var rel = input.Slice(pos).IndexOfAny(firstSv);
            if (rel < 0)
            {
                break;
            }

            pos += rel;
            var tail = input.Slice(pos);
            for (var i = 0; i < alts.Length; i++)
            {
                var branch = alts[i];
                if (tail.StartsWith(branch))
                {
                    result = PublishSingleMatch(program, pos, pos + branch.Length, startAt, ref state, pairs);
                    return true;
                }
            }
            pos++;
        }
        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool ExecuteSingleSetRepeat(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<char> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        if (_startAnchor)
        {
            if (startAt > 0 || notbol)
            {
                result = PublishNoMatch(startAt, 1, ref state);
                return true;
            }
            if (_endAnchor && _repeatMax == int.MaxValue)
            {
                if (input.Length < _repeatMin)
                {
                    result = PublishNoMatch(startAt, input.Length, ref state);
                    return true;
                }
                if (_primarySet.CharSearchValues is { } fullSv)
                {
                    var bad = input.IndexOfAnyExcept(fullSv);
                    if (bad < 0)
                    {
                        result = PublishSingleMatch(program, 0, input.Length, startAt, ref state, pairs);
                        return true;
                    }
                    if (_endAnchorAllowsTrailingNewline && bad >= input.Length - 2 && input[bad] is '\n' or '\r')
                    {
                        return false;
                    }

                    result = PublishNoMatch(startAt, bad + 1, ref state);
                    return true;
                }
                for (var i = 0; i < input.Length; i++)
                {
                    if (!_primarySet.Contains(input[i], program, data))
                    {
                        if (_endAnchorAllowsTrailingNewline && i >= input.Length - 2 && input[i] is '\n' or '\r')
                        {
                            return false;
                        }

                        result = PublishNoMatch(startAt, i + 1, ref state);
                        return true;
                    }
                }
                result = PublishSingleMatch(program, 0, input.Length, startAt, ref state, pairs);
                return true;
            }
            var limit = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, _repeatMax);
            var cur = 0;
            while (cur < limit && _primarySet.Contains(input[cur], program, data))
            {
                cur++;
            }

            if (cur < _repeatMin)
            {
                result = PublishNoMatch(startAt, cur + 1, ref state);
                return true;
            }
            if (_endAnchor && cur != input.Length)
            {
                if (_endAnchorAllowsTrailingNewline && cur >= input.Length - 2 && input[cur] is '\n' or '\r')
                {
                    return false;
                }

                result = PublishNoMatch(startAt, cur + 1, ref state);
                return true;
            }
            result = PublishSingleMatch(program, 0, cur, startAt, ref state, pairs);
            return true;
        }

        if (_endAnchor)
        {
            return false;
        }

        var pos = startAt;
        if (_primarySet.CharSearchValues is { } sv)
        {
            while (pos <= input.Length - _repeatMin)
            {
                var rel = input.Slice(pos).IndexOfAny(sv);
                if (rel < 0)
                {
                    break;
                }

                pos += rel;
                if (pos > input.Length - _repeatMin)
                {
                    break;
                }

                var maxEnd = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, pos + _repeatMax);
                var cur = pos + 1;
                while (cur < maxEnd && _primarySet.Latin1.Contains(input[cur]))
                {
                    cur++;
                }

                if (cur - pos >= _repeatMin)
                {
                    result = PublishSingleMatch(program, pos, cur, startAt, ref state, pairs);
                    return true;
                }
                pos = cur + 1;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }

        if (_primarySet.GetBmpBits(program, data) is { } bmp)
        {
            while (pos <= input.Length - _repeatMin)
            {
                var c0 = input[pos];
                if ((bmp[c0 >> 6] & (1UL << (c0 & 63))) == 0)
                {
                    pos++;
                    continue;
                }
                var maxEnd = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, pos + _repeatMax);
                var cur = pos + 1;
                while (cur < maxEnd)
                {
                    var c = input[cur];
                    if ((bmp[c >> 6] & (1UL << (c & 63))) == 0)
                    {
                        break;
                    }

                    cur++;
                }
                if (cur - pos >= _repeatMin)
                {
                    result = PublishSingleMatch(program, pos, cur, startAt, ref state, pairs);
                    return true;
                }
                pos = cur + 1;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }

        while (pos <= input.Length - _repeatMin)
        {
            if (!_primarySet.Contains(input[pos], program, data))
            {
                pos++;
                continue;
            }
            var maxEnd = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, pos + _repeatMax);
            var cur = pos + 1;
            while (cur < maxEnd && _primarySet.Contains(input[cur], program, data))
            {
                cur++;
            }

            if (cur - pos >= _repeatMin)
            {
                result = PublishSingleMatch(program, pos, cur, startAt, ref state, pairs);
                return true;
            }
            pos = cur + 1;
        }
        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool ExecuteSingleSetRepeat(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var sv = _primarySet.ByteSearchValues!;
        if (_startAnchor)
        {
            if (startAt > 0 || notbol)
            {
                result = PublishNoMatch(startAt, 1, ref state);
                return true;
            }
            if (_endAnchor && _repeatMax == int.MaxValue)
            {
                if (input.Length < _repeatMin)
                {
                    result = PublishNoMatch(startAt, input.Length, ref state);
                    return true;
                }
                var bad = input.IndexOfAnyExcept(sv);
                if (bad < 0)
                {
                    result = PublishSingleMatch(program, 0, input.Length, startAt, ref state, pairs);
                    return true;
                }
                if (_endAnchorAllowsTrailingNewline && bad >= input.Length - 2 && input[bad] is (byte)'\n' or (byte)'\r')
                {
                    return false;
                }

                result = PublishNoMatch(startAt, bad + 1, ref state);
                return true;
            }
            var limit = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, _repeatMax);
            var cur = 0;
            while (cur < limit && _primarySet.Contains(input[cur]))
            {
                cur++;
            }

            if (cur < _repeatMin)
            {
                result = PublishNoMatch(startAt, cur + 1, ref state);
                return true;
            }
            if (_endAnchor && cur != input.Length)
            {
                if (_endAnchorAllowsTrailingNewline && cur >= input.Length - 2 && input[cur] is (byte)'\n' or (byte)'\r')
                {
                    return false;
                }

                result = PublishNoMatch(startAt, cur + 1, ref state);
                return true;
            }
            result = PublishSingleMatch(program, 0, cur, startAt, ref state, pairs);
            return true;
        }

        if (_endAnchor)
        {
            return false;
        }

        var pos = startAt;
        while (pos <= input.Length - _repeatMin)
        {
            var rel = input.Slice(pos).IndexOfAny(sv);
            if (rel < 0)
            {
                break;
            }

            pos += rel;
            if (pos > input.Length - _repeatMin)
            {
                break;
            }

            var maxEnd = _repeatMax == int.MaxValue ? input.Length : Math.Min(input.Length, pos + _repeatMax);
            var cur = pos + 1;
            while (cur < maxEnd && _primarySet.Contains(input[cur]))
            {
                cur++;
            }

            if (cur - pos >= _repeatMin)
            {
                result = PublishSingleMatch(program, pos, cur, startAt, ref state, pairs);
                return true;
            }
            pos = cur + 1;
        }
        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool ExecuteSetRepeatSuffixLiteral(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<char> input,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var suffix = _charLiteral.AsSpan();
        var minTotal = _repeatMin + suffix.Length;
        var searchFrom = startAt + _repeatMin;
        while (searchFrom <= input.Length - suffix.Length)
        {
            var rel = input.Slice(searchFrom).IndexOf(suffix);
            if (rel < 0)
            {
                break;
            }

            var suffixPos = searchFrom + rel;
            var runStart = suffixPos;
            while (runStart > startAt && _primarySet.Contains(input[runStart - 1], program, data))
            {
                runStart--;
            }

            if (suffixPos - runStart >= _repeatMin)
            {
                var matchEnd = suffixPos + suffix.Length;
                var scanPos = suffixPos + 1;
                while (scanPos <= input.Length - suffix.Length)
                {
                    var allInSet = true;
                    for (var k = suffixPos; k < scanPos; k++)
                    {
                        if (!_primarySet.Contains(input[k], program, data))
                        { allInSet = false; break; }
                    }
                    if (!allInSet)
                    {
                        break;
                    }

                    if (input.Slice(scanPos, suffix.Length).SequenceEqual(suffix))
                    {
                        matchEnd = scanPos + suffix.Length;
                    }

                    scanPos++;
                }
                if (matchEnd - runStart >= minTotal)
                {
                    result = PublishSingleMatch(program, runStart, matchEnd, startAt, ref state, pairs);
                    return true;
                }
            }
            searchFrom = suffixPos + 1;
        }
        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool ExecuteSetRepeatSuffixLiteral(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        ReadOnlySpan<byte> suffix = _byteLiteral.AsSpan();
        var minTotal = _repeatMin + suffix.Length;
        var searchFrom = startAt + _repeatMin;
        while (searchFrom <= input.Length - suffix.Length)
        {
            var rel = input.Slice(searchFrom).IndexOf(suffix);
            if (rel < 0)
            {
                break;
            }

            var suffixPos = searchFrom + rel;
            var runStart = suffixPos;
            while (runStart > startAt && _primarySet.Contains(input[runStart - 1]))
            {
                runStart--;
            }

            if (suffixPos - runStart >= _repeatMin)
            {
                var matchEnd = suffixPos + suffix.Length;
                var scanPos = suffixPos + 1;
                while (scanPos <= input.Length - suffix.Length)
                {
                    var allInSet = true;
                    for (var k = suffixPos; k < scanPos; k++)
                    {
                        if (!_primarySet.Contains(input[k]))
                        { allInSet = false; break; }
                    }
                    if (!allInSet)
                    {
                        break;
                    }

                    if (input.Slice(scanPos, suffix.Length).SequenceEqual(suffix))
                    {
                        matchEnd = scanPos + suffix.Length;
                    }

                    scanPos++;
                }
                if (matchEnd - runStart >= minTotal)
                {
                    result = PublishSingleMatch(program, runStart, matchEnd, startAt, ref state, pairs);
                    return true;
                }
            }
            searchFrom = suffixPos + 1;
        }
        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool ExecuteAtomicSequence(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<char> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var atoms = _seqAtoms!;
        if (_startAnchor && (startAt > 0 || notbol))
        {
            result = PublishNoMatch(startAt, 1, ref state);
            return true;
        }

        if (_seqFixedLiteralAtomIndex == 1 &&
            !_startAnchor &&
            !_startLineAnchor &&
            !atoms[0].IsLiteral &&
            atoms[0].Max == int.MaxValue)
        {
            var anchorLit = atoms[1].CharLiteral.AsSpan();
            var searchFrom = startAt + atoms[0].Min;
            while (searchFrom <= input.Length - anchorLit.Length)
            {
                var rel = input.Slice(searchFrom).IndexOf(anchorLit);
                if (rel < 0)
                {
                    break;
                }

                var litPos = searchFrom + rel;
                var runStart = litPos;
                while (runStart > startAt && atoms[0].Set.Contains(input[runStart - 1], program, data))
                {
                    runStart--;
                }

                if (litPos - runStart >= atoms[0].Min &&
                    TryMatchAtomicSequenceFrom(program, data, input, atoms, runStart, startAt, ref state, pairs, out result))
                {
                    return true;
                }
                searchFrom = litPos + 1;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }

        var pos = startAt;
        var maxStart = _startAnchor ? startAt : input.Length;
        while (pos <= maxStart)
        {
            if (!_startAnchor)
            {
                if (_seqFixedLiteralAtomIndex >= 0 && _seqFixedLiteralOffset >= 0)
                {
                    var litSearchPos = pos + _seqFixedLiteralOffset;
                    var anchorLit = atoms[_seqFixedLiteralAtomIndex].CharLiteral.AsSpan();
                    if (litSearchPos + anchorLit.Length > input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(litSearchPos).IndexOf(anchorLit);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos = litSearchPos + rel - _seqFixedLiteralOffset;
                }
                else if (atoms[0].IsLiteral)
                {
                    var firstLit = atoms[0].CharLiteral.AsSpan();
                    if (pos + firstLit.Length > input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(pos).IndexOf(firstLit);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos += rel;
                }
                else if (atoms[0].Min >= 1 && atoms[0].Set.CharSearchValues is { } firstSv)
                {
                    if (pos >= input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(pos).IndexOfAny(firstSv);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos += rel;
                }
            }

            if (_startLineAnchor)
            {
                if (pos == 0)
                {
                    if (notbol)
                    { pos++; continue; }
                }
                else if (input[pos - 1] != '\n')
                {
                    pos++;
                    continue;
                }
            }

            if (TryMatchAtomicSequenceFrom(program, data, input, atoms, pos, startAt, ref state, pairs, out result))
            {
                return true;
            }

            if (_startAnchor)
            {
                break;
            }

            pos++;
        }

        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool TryMatchAtomicSequenceFrom(
        Pcre2Program program,
        Pcre2DataView data,
        ReadOnlySpan<char> input,
        SeqAtom[] atoms,
        int pos,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var cur = pos;
        for (var i = 0; i < atoms.Length; i++)
        {
            ref readonly var atom = ref atoms[i];
            if (atom.IsLiteral)
            {
                var lit = atom.CharLiteral.AsSpan();
                var count = 0;
                while (count < atom.Max && cur + lit.Length <= input.Length && input.Slice(cur, lit.Length).SequenceEqual(lit))
                {
                    cur += lit.Length;
                    count++;
                }
                if (count < atom.Min)
                {
                    return false;
                }
            }
            else if (atom.Set.IsNotNewline && atom.Max == int.MaxValue)
            {
                var nl = input.Slice(cur).IndexOf('\n');
                var count = nl < 0 ? input.Length - cur : nl;
                if (count < atom.Min)
                {
                    return false;
                }

                cur += count;
            }
            else
            {
                var maxEnd = atom.Max == int.MaxValue ? input.Length : Math.Min(input.Length, cur + atom.Max);
                var startCur = cur;
                while (cur < maxEnd && atom.Set.Contains(input[cur], program, data))
                {
                    cur++;
                }

                if (cur - startCur < atom.Min)
                {
                    return false;
                }
            }
        }

        if (_endAnchor && cur != input.Length)
        {
            return false;
        }

        state.TryCharge(Math.Max(1, cur - startAt), pos);
        var captureTop = 1;
        if (!pairs.IsEmpty)
        {
            var count = Math.Min(program.GroupCount, pairs.Length);
            for (var i = 1; i < count; i++)
            {
                pairs[i] = default;
            }

            pairs[0] = new(true, pos, cur);
            var replay = pos;
            for (var i = 0; i < atoms.Length; i++)
            {
                ref readonly var atom = ref atoms[i];
                var atomStart = replay;
                if (atom.IsLiteral)
                {
                    var lit = atom.CharLiteral.AsSpan();
                    var c = 0;
                    while (c < atom.Max && replay + lit.Length <= cur && input.Slice(replay, lit.Length).SequenceEqual(lit))
                    {
                        replay += lit.Length;
                        c++;
                    }
                }
                else if (atom.Set.IsNotNewline && atom.Max == int.MaxValue)
                {
                    replay = cur;
                }
                else
                {
                    var maxEnd = atom.Max == int.MaxValue ? cur : Math.Min(cur, replay + atom.Max);
                    while (replay < maxEnd && atom.Set.Contains(input[replay], program, data))
                    {
                        replay++;
                    }
                }
                if (atom.CaptureGroup > 0)
                {
                    captureTop = Math.Max(captureTop, atom.CaptureGroup + 1);
                    if (atom.CaptureGroup < pairs.Length)
                    {
                        pairs[atom.CaptureGroup] = new(true, atomStart, replay);
                    }
                }
            }
        }
        else
        {
            for (var i = 0; i < atoms.Length; i++)
            {
                if (atoms[i].CaptureGroup > 0)
                {
                    captureTop = Math.Max(captureTop, atoms[i].CaptureGroup + 1);
                }
            }
        }

        result = new(Pcre2MatchStatus.Match, pairs.Length < captureTop ? 0 : captureTop, pos, -1);
        return true;
    }

    private bool ExecuteAtomicSequence(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        int startAt,
        bool notbol,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var atoms = _seqAtoms!;
        if (_startAnchor && (startAt > 0 || notbol))
        {
            result = PublishNoMatch(startAt, 1, ref state);
            return true;
        }

        if (_seqFixedLiteralAtomIndex == 1 &&
            !_startAnchor &&
            !_startLineAnchor &&
            !atoms[0].IsLiteral &&
            atoms[0].Max == int.MaxValue)
        {
            ReadOnlySpan<byte> anchorLit = atoms[1].ByteLiteral.AsSpan();
            var searchFrom = startAt + atoms[0].Min;
            while (searchFrom <= input.Length - anchorLit.Length)
            {
                var rel = input.Slice(searchFrom).IndexOf(anchorLit);
                if (rel < 0)
                {
                    break;
                }

                var litPos = searchFrom + rel;
                var runStart = litPos;
                while (runStart > startAt && atoms[0].Set.Contains(input[runStart - 1]))
                {
                    runStart--;
                }

                if (litPos - runStart >= atoms[0].Min &&
                    TryMatchAtomicSequenceFrom(program, input, atoms, runStart, startAt, ref state, pairs, out result))
                {
                    return true;
                }
                searchFrom = litPos + 1;
            }
            result = PublishNoMatch(startAt, input.Length - startAt, ref state);
            return true;
        }

        var pos = startAt;
        var maxStart = _startAnchor ? startAt : input.Length;
        while (pos <= maxStart)
        {
            if (!_startAnchor)
            {
                if (_seqFixedLiteralAtomIndex >= 0 && _seqFixedLiteralOffset >= 0)
                {
                    var litSearchPos = pos + _seqFixedLiteralOffset;
                    ReadOnlySpan<byte> anchorLit = atoms[_seqFixedLiteralAtomIndex].ByteLiteral.AsSpan();
                    if (litSearchPos + anchorLit.Length > input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(litSearchPos).IndexOf(anchorLit);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos = litSearchPos + rel - _seqFixedLiteralOffset;
                }
                else if (atoms[0].IsLiteral)
                {
                    ReadOnlySpan<byte> firstLit = atoms[0].ByteLiteral.AsSpan();
                    if (pos + firstLit.Length > input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(pos).IndexOf(firstLit);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos += rel;
                }
                else if (atoms[0].Min >= 1 && atoms[0].Set.ByteSearchValues is { } firstSv)
                {
                    if (pos >= input.Length)
                    {
                        break;
                    }

                    var rel = input.Slice(pos).IndexOfAny(firstSv);
                    if (rel < 0)
                    {
                        break;
                    }

                    pos += rel;
                }
            }

            if (_startLineAnchor)
            {
                if (pos == 0)
                {
                    if (notbol)
                    { pos++; continue; }
                }
                else if (input[pos - 1] != (byte)'\n')
                {
                    pos++;
                    continue;
                }
            }

            if (TryMatchAtomicSequenceFrom(program, input, atoms, pos, startAt, ref state, pairs, out result))
            {
                return true;
            }

            if (_startAnchor)
            {
                break;
            }

            pos++;
        }

        result = PublishNoMatch(startAt, input.Length - startAt, ref state);
        return true;
    }

    private bool TryMatchAtomicSequenceFrom(
        Pcre2Program program,
        ReadOnlySpan<byte> input,
        SeqAtom[] atoms,
        int pos,
        int startAt,
        ref RunState state,
        Span<Pcre2OffsetPair> pairs,
        out Pcre2MatchResult result)
    {
        result = default;
        var cur = pos;
        for (var i = 0; i < atoms.Length; i++)
        {
            ref readonly var atom = ref atoms[i];
            if (atom.IsLiteral)
            {
                ReadOnlySpan<byte> lit = atom.ByteLiteral.AsSpan();
                var count = 0;
                while (count < atom.Max && cur + lit.Length <= input.Length && input.Slice(cur, lit.Length).SequenceEqual(lit))
                {
                    cur += lit.Length;
                    count++;
                }
                if (count < atom.Min)
                {
                    return false;
                }
            }
            else if (atom.Set.IsNotNewline && atom.Max == int.MaxValue)
            {
                var nl = input.Slice(cur).IndexOf((byte)'\n');
                var count = nl < 0 ? input.Length - cur : nl;
                if (count < atom.Min)
                {
                    return false;
                }

                cur += count;
            }
            else
            {
                var maxEnd = atom.Max == int.MaxValue ? input.Length : Math.Min(input.Length, cur + atom.Max);
                var startCur = cur;
                while (cur < maxEnd && atom.Set.Contains(input[cur]))
                {
                    cur++;
                }

                if (cur - startCur < atom.Min)
                {
                    return false;
                }
            }
        }

        if (_endAnchor && cur != input.Length)
        {
            return false;
        }

        state.TryCharge(Math.Max(1, cur - startAt), pos);
        var captureTop = 1;
        if (!pairs.IsEmpty)
        {
            var count = Math.Min(program.GroupCount, pairs.Length);
            for (var i = 1; i < count; i++)
            {
                pairs[i] = default;
            }

            pairs[0] = new(true, pos, cur);
            var replay = pos;
            for (var i = 0; i < atoms.Length; i++)
            {
                ref readonly var atom = ref atoms[i];
                var atomStart = replay;
                if (atom.IsLiteral)
                {
                    ReadOnlySpan<byte> lit = atom.ByteLiteral.AsSpan();
                    var c = 0;
                    while (c < atom.Max && replay + lit.Length <= cur && input.Slice(replay, lit.Length).SequenceEqual(lit))
                    {
                        replay += lit.Length;
                        c++;
                    }
                }
                else if (atom.Set.IsNotNewline && atom.Max == int.MaxValue)
                {
                    replay = cur;
                }
                else
                {
                    var maxEnd = atom.Max == int.MaxValue ? cur : Math.Min(cur, replay + atom.Max);
                    while (replay < maxEnd && atom.Set.Contains(input[replay]))
                    {
                        replay++;
                    }
                }
                if (atom.CaptureGroup > 0)
                {
                    captureTop = Math.Max(captureTop, atom.CaptureGroup + 1);
                    if (atom.CaptureGroup < pairs.Length)
                    {
                        pairs[atom.CaptureGroup] = new(true, atomStart, replay);
                    }
                }
            }
        }
        else
        {
            for (var i = 0; i < atoms.Length; i++)
            {
                if (atoms[i].CaptureGroup > 0)
                {
                    captureTop = Math.Max(captureTop, atoms[i].CaptureGroup + 1);
                }
            }
        }

        result = new(Pcre2MatchStatus.Match, pairs.Length < captureTop ? 0 : captureTop, pos, -1);
        return true;
    }

    private static int ComputeMaxLookbehind(Pcre2Program program)
    {
        var maxLookbehind = program.SearchHints?.MaximumLookbehind ?? 0;
        foreach (var node in program.Nodes)
        {
            if (node.Kind == Pcre2NodeKind.Assertion)
            {
                maxLookbehind = Math.Max(maxLookbehind, program.Assertions[node.Operand].MaximumLength);
            }

            if (node.Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary)
            {
                maxLookbehind = Math.Max(maxLookbehind, 1);
            }
        }
        return maxLookbehind;
    }

    private static bool IsEligibleProgram(Pcre2Program program)
    {
        if (!program.IsExecutable ||
            program.HasBackslashC ||
            (program.Profile.Flags & (Pcre2CompileFlags.Endanchored | Pcre2CompileFlags.Firstline | Pcre2CompileFlags.MatchInvalidUtf | Pcre2CompileFlags.NoUtfCheck)) != 0 ||
            program.PatternMatchFlags != 0 ||
            !program.Callouts.IsEmpty ||
            !program.Controls.IsEmpty ||
            !program.Marks.IsEmpty ||
            !program.Conditions.IsEmpty ||
            !program.References.IsEmpty ||
            !program.ScanReferences.IsEmpty ||
            !program.Assertions.IsEmpty)
        {
            return false;
        }
        foreach (var node in program.Nodes)
        {
            if (node.Kind is Pcre2NodeKind.ResetStart or Pcre2NodeKind.ScriptRun or Pcre2NodeKind.Define or
                Pcre2NodeKind.Subroutine or Pcre2NodeKind.Backreference or Pcre2NodeKind.Callout or Pcre2NodeKind.Verb)
            {
                return false;
            }
        }
        return true;
    }

    private static int UnwrapRootCapture(Pcre2Program program, int nodeId)
    {
        var node = program.Nodes[nodeId];
        if (node.Kind == Pcre2NodeKind.Capture && program.Groups[node.Operand].Number == 0 && node.Children.Count == 1)
        {
            return program.Children[node.Children.Offset];
        }

        return nodeId;
    }

    private static int UnwrapAtomic(Pcre2Program program, int nodeId)
    {
        while (program.Nodes[nodeId].Kind is Pcre2NodeKind.Atomic or Pcre2NodeKind.Sequence &&
               program.Nodes[nodeId].Children.Count == 1)
        {
            nodeId = program.Children[program.Nodes[nodeId].Children.Offset];
        }
        return nodeId;
    }

    private static bool IsStartInputNode(Pcre2Node node) =>
        node.Kind == Pcre2NodeKind.AbsoluteStart ||
        (node.Kind == Pcre2NodeKind.Start && (node.Flags & Pcre2CompileFlags.Multiline) == 0);

    private static bool IsStartLineNode(Pcre2Program program, Pcre2Node node) =>
        node.Kind == Pcre2NodeKind.Start &&
        (node.Flags & Pcre2CompileFlags.Multiline) != 0 &&
        (node.Flags & Pcre2CompileFlags.AltCircumflex) == 0 &&
        program.Profile.Newline == Pcre2Newline.Lf;

    private static bool IsEndInputNode(Pcre2Node node, out bool allowsTrailingNewline)
    {
        allowsTrailingNewline = false;
        if (node.Kind == Pcre2NodeKind.AbsoluteEnd)
        {
            return true;
        }

        if (node.Kind == Pcre2NodeKind.End && (node.Flags & Pcre2CompileFlags.Multiline) == 0)
        {
            allowsTrailingNewline = (node.Flags & Pcre2CompileFlags.DollarEndonly) == 0;
            return true;
        }
        return false;
    }

    private static bool TryBuildEndAnchorTerminalFilter(
        Pcre2Program program,
        Pcre2DataView data,
        int rootId,
        out FastCharSet terminalSet,
        out bool allowsTrailingNewline)
    {
        terminalSet = default;
        allowsTrailingNewline = false;
        var root = program.Nodes[rootId];
        if (root.Kind != Pcre2NodeKind.Sequence || root.Children.Count < 2)
        {
            return false;
        }

        var children = program.Children.Slice(root.Children.Offset, root.Children.Count);
        var lastNode = program.Nodes[children[^1]];
        if (!IsEndInputNode(lastNode, out allowsTrailingNewline))
        {
            return false;
        }

        var prevId = UnwrapAtomic(program, children[^2]);
        var prev = program.Nodes[prevId];
        if (prev.Kind == Pcre2NodeKind.Capture && prev.Children.Count == 1)
        {
            prevId = UnwrapAtomic(program, program.Children[prev.Children.Offset]);
            prev = program.Nodes[prevId];
        }

        if (prev.Kind == Pcre2NodeKind.Repeat && prev.Min >= 1)
        {
            var bodyId = UnwrapAtomic(program, program.Children[prev.Children.Offset]);
            var body = program.Nodes[bodyId];
            if (body.Kind == Pcre2NodeKind.Capture && body.Children.Count == 1)
            {
                bodyId = UnwrapAtomic(program, program.Children[body.Children.Offset]);
                body = program.Nodes[bodyId];
            }

            if (TryBuildCharSet(program, data, bodyId, out terminalSet))
            {
                return true;
            }

            if (body.Kind == Pcre2NodeKind.Alternative && body.Children.Count is >= 2 and <= 16)
            {
                Span<bool> latin1 = stackalloc bool[256];
                var branches = program.Children.Slice(body.Children.Offset, body.Children.Count);
                foreach (var branchId in branches)
                {
                    if (!TryExtractLiteralBranch(program, branchId, out var s, out _) || string.IsNullOrEmpty(s))
                    {
                        return false;
                    }

                    var c = s[^1];
                    if (c >= 128)
                    {
                        return false;
                    }

                    latin1[c] = true;
                }
                terminalSet = CreateAsciiCharSet(latin1);
                return true;
            }
        }
        return false;
    }

    private static int ExtractRequiredCodeUnit(Pcre2Program program, int rootId)
    {
        var root = program.Nodes[rootId];
        if (root.Kind != Pcre2NodeKind.Sequence)
        {
            return -1;
        }

        var children = program.Children.Slice(root.Children.Offset, root.Children.Count);
        var chosen = -1;
        foreach (var childId in children)
        {
            var id = UnwrapAtomic(program, childId);
            var n = program.Nodes[id];
            if (n.Kind == Pcre2NodeKind.Capture && n.Children.Count == 1)
            {
                n = program.Nodes[UnwrapAtomic(program, program.Children[n.Children.Offset])];
            }

            if (n.Kind == Pcre2NodeKind.Literal && (n.Flags & Pcre2CompileFlags.Caseless) == 0 && n.Operand is >= 0 and < 128)
            {
                chosen = n.Operand;
            }
        }
        return chosen;
    }

    private static bool TryBuildLiteral(
        Pcre2Program program,
        int rootId,
        out string? charLiteral,
        out byte[]? byteLiteral,
        out bool startAnchor,
        out bool endAnchor,
        out bool allowsTrailingNewline)
    {
        charLiteral = null;
        byteLiteral = null;
        startAnchor = false;
        endAnchor = false;
        allowsTrailingNewline = false;

        var id = UnwrapAtomic(program, rootId);
        var node = program.Nodes[id];
        if (node.Kind == Pcre2NodeKind.Literal)
        {
            if ((node.Flags & Pcre2CompileFlags.Caseless) != 0 || node.Operand is < 0 or >= 128)
            {
                return false;
            }

            charLiteral = ((char)node.Operand).ToString();
            byteLiteral = [(byte)node.Operand];
            return true;
        }

        if (node.Kind != Pcre2NodeKind.Sequence)
        {
            return false;
        }

        var children = program.Children.Slice(node.Children.Offset, node.Children.Count);
        var idx = 0;
        var end = children.Length;
        if (idx < end && IsStartInputNode(program.Nodes[children[idx]]))
        {
            startAnchor = true;
            idx++;
        }
        if (end > idx && IsEndInputNode(program.Nodes[children[end - 1]], out allowsTrailingNewline))
        {
            endAnchor = true;
            end--;
        }
        if (idx >= end)
        {
            return false;
        }

        Span<char> chars = stackalloc char[Math.Min(128, end - idx)];
        var bytes = new byte[end - idx];
        if (end - idx > 128)
        {
            return false;
        }

        var len = 0;
        for (var i = idx; i < end; i++)
        {
            var item = program.Nodes[UnwrapAtomic(program, children[i])];
            if (item.Kind != Pcre2NodeKind.Literal || (item.Flags & Pcre2CompileFlags.Caseless) != 0 || item.Operand is < 0 or >= 128)
            {
                return false;
            }

            chars[len] = (char)item.Operand;
            bytes[len] = (byte)item.Operand;
            len++;
        }
        charLiteral = chars.Slice(0, len).ToString();
        byteLiteral = bytes;
        return true;
    }

    private static bool TryExtractLiteralBranch(Pcre2Program program, int branchId, out string? charLit, out byte[]? byteLit)
    {
        return TryBuildLiteral(program, branchId, out charLit, out byteLit, out var sa, out var ea, out _) && !sa && !ea;
    }

    private static bool TryBuildLiteralAlternation(
        Pcre2Program program,
        int rootId,
        out string[]? charLiterals,
        out byte[][]? byteLiterals,
        out SearchValues<string>? charSv,
        out SearchValues<byte>? byteFirstSv)
    {
        charLiterals = null;
        byteLiterals = null;
        charSv = null;
        byteFirstSv = null;

        var root = program.Nodes[UnwrapAtomic(program, rootId)];
        if (root.Kind != Pcre2NodeKind.Alternative || root.Children.Count is < 2 or > 16)
        {
            return false;
        }

        var branches = program.Children.Slice(root.Children.Offset, root.Children.Count);
        var cLits = new string[branches.Length];
        var bLits = new byte[branches.Length][];
        var firstBytes = new byte[branches.Length];
        for (var i = 0; i < branches.Length; i++)
        {
            if (!TryExtractLiteralBranch(program, branches[i], out var cLit, out var bLit) ||
                string.IsNullOrEmpty(cLit) || bLit is null || bLit.Length == 0)
            {
                return false;
            }
            cLits[i] = cLit;
            bLits[i] = bLit;
            firstBytes[i] = bLit[0];
        }
        charLiterals = cLits;
        byteLiterals = bLits;
        charSv = SearchValues.Create(cLits, StringComparison.Ordinal);
        byteFirstSv = SearchValues.Create(firstBytes);
        return true;
    }

    private static bool TryBuildSingleSetRepeat(
        Pcre2Program program,
        Pcre2DataView data,
        int rootId,
        out FastCharSet set,
        out int min,
        out int max,
        out bool startAnchor,
        out bool endAnchor,
        out bool allowsTrailingNewline)
    {
        set = default;
        min = 0;
        max = 0;
        startAnchor = false;
        endAnchor = false;
        allowsTrailingNewline = false;

        var id = UnwrapAtomic(program, rootId);
        var node = program.Nodes[id];
        if (node.Kind == Pcre2NodeKind.Sequence)
        {
            var children = program.Children.Slice(node.Children.Offset, node.Children.Count);
            var idx = 0;
            var end = children.Length;
            if (idx < end && IsStartInputNode(program.Nodes[children[idx]]))
            {
                startAnchor = true;
                idx++;
            }
            if (end > idx && IsEndInputNode(program.Nodes[children[end - 1]], out allowsTrailingNewline))
            {
                endAnchor = true;
                end--;
            }
            if (end - idx != 1)
            {
                return false;
            }

            id = UnwrapAtomic(program, children[idx]);
            node = program.Nodes[id];
        }

        if (node.Kind != Pcre2NodeKind.Repeat ||
            node.Quantifier == Pcre2Quantifier.Lazy ||
            node.Min < 1 ||
            node.Children.Count != 1)
        {
            return false;
        }

        var bodyId = UnwrapAtomic(program, program.Children[node.Children.Offset]);
        if (!TryBuildCharSet(program, data, bodyId, out set))
        {
            return false;
        }

        min = node.Min;
        max = node.Max < 0 ? int.MaxValue : node.Max;
        return true;
    }

    private static bool TryBuildSetRepeatSuffixLiteral(
        Pcre2Program program,
        Pcre2DataView data,
        int rootId,
        out FastCharSet set,
        out int min,
        out string? charLiteral,
        out byte[]? byteLiteral)
    {
        set = default;
        min = 0;
        charLiteral = null;
        byteLiteral = null;

        var root = program.Nodes[UnwrapAtomic(program, rootId)];
        if (root.Kind != Pcre2NodeKind.Sequence || root.Children.Count < 2)
        {
            return false;
        }

        var children = program.Children.Slice(root.Children.Offset, root.Children.Count);
        var first = program.Nodes[UnwrapAtomic(program, children[0])];
        if (first.Kind != Pcre2NodeKind.Repeat ||
            first.Quantifier != Pcre2Quantifier.Greedy ||
            first.Min < 1 ||
            first.Max != -1 ||
            first.Children.Count != 1)
        {
            return false;
        }

        var bodyId = UnwrapAtomic(program, program.Children[first.Children.Offset]);
        if (!TryBuildCharSet(program, data, bodyId, out set))
        {
            return false;
        }

        var litLen = children.Length - 1;
        if (litLen is < 1 or > 64)
        {
            return false;
        }

        Span<char> chars = stackalloc char[litLen];
        var bytes = new byte[litLen];
        for (var i = 1; i < children.Length; i++)
        {
            var item = program.Nodes[UnwrapAtomic(program, children[i])];
            if (item.Kind != Pcre2NodeKind.Literal || (item.Flags & Pcre2CompileFlags.Caseless) != 0 || item.Operand is < 0 or >= 128)
            {
                return false;
            }

            chars[i - 1] = (char)item.Operand;
            bytes[i - 1] = (byte)item.Operand;
        }

        min = first.Min;
        charLiteral = chars.ToString();
        byteLiteral = bytes;
        return true;
    }

    private static bool TryBuildAtomicSequence(
        Pcre2Program program,
        Pcre2DataView data,
        int rootId,
        out SeqAtom[]? atoms,
        out bool startAnchor,
        out bool startLineAnchor,
        out bool endAnchor,
        out bool allowsTrailingNewline,
        out int fixedLitAtomIndex,
        out int fixedLitOffset)
    {
        atoms = null;
        startAnchor = false;
        startLineAnchor = false;
        endAnchor = false;
        allowsTrailingNewline = false;
        fixedLitAtomIndex = -1;
        fixedLitOffset = -1;

        var root = program.Nodes[UnwrapAtomic(program, rootId)];
        if (root.Kind != Pcre2NodeKind.Sequence || root.Children.Count < 2)
        {
            return false;
        }

        var children = program.Children.Slice(root.Children.Offset, root.Children.Count);
        var idx = 0;
        var end = children.Length;
        if (idx < end)
        {
            var firstNode = program.Nodes[children[idx]];
            if (IsStartInputNode(firstNode))
            {
                startAnchor = true;
                idx++;
            }
            else if (IsStartLineNode(program, firstNode))
            {
                startLineAnchor = true;
                idx++;
            }
        }
        if (end > idx && IsEndInputNode(program.Nodes[children[end - 1]], out allowsTrailingNewline))
        {
            endAnchor = true;
            end--;
        }
        if (idx >= end || (endAnchor && allowsTrailingNewline))
        {
            return false;
        }

        var list = new List<SeqAtom>(8);
        Span<bool> firstBits = stackalloc bool[256];
        while (idx < end)
        {
            if (list.Count >= 8)
            {
                return false;
            }

            var childId = UnwrapAtomic(program, children[idx]);
            var child = program.Nodes[childId];
            if (child.Kind == Pcre2NodeKind.Literal && (child.Flags & Pcre2CompileFlags.Caseless) == 0 && child.Operand is >= 0 and < 128)
            {
                var litStart = idx;
                while (idx < end)
                {
                    var next = program.Nodes[UnwrapAtomic(program, children[idx])];
                    if (next.Kind != Pcre2NodeKind.Literal || (next.Flags & Pcre2CompileFlags.Caseless) != 0 || next.Operand is < 0 or >= 128)
                    {
                        break;
                    }

                    idx++;
                }
                var len = idx - litStart;
                var cArr = new char[len];
                var bArr = new byte[len];
                for (var k = 0; k < len; k++)
                {
                    var op = program.Nodes[UnwrapAtomic(program, children[litStart + k])].Operand;
                    cArr[k] = (char)op;
                    bArr[k] = (byte)op;
                }
                firstBits.Clear();
                firstBits[bArr[0]] = true;
                list.Add(new SeqAtom(true, new string(cArr), bArr, CreateAsciiCharSet(firstBits), 1, 1, -1));
                continue;
            }

            if (!TryParseSeqAtom(program, data, childId, isLast: idx == end - 1 && !endAnchor, out var atom))
            {
                return false;
            }

            list.Add(atom);
            idx++;
        }

        if (list.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Min != list[i].Max && i + 1 < list.Count)
            {
                var curSet = list[i].Set;
                var nextSet = list[i + 1].Set;
                if (!curSet.DisjointFrom(in nextSet))
                {
                    return false;
                }
            }
        }

        var bestIdx = -1;
        var bestLen = 0;
        var bestOffset = -1;
        var curOffset = 0;
        var fixedSoFar = true;
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a.IsLiteral && a.Min == 1 && a.Max == 1)
            {
                var len = a.CharLiteral!.Length;
                if (fixedSoFar && len > bestLen)
                {
                    bestLen = len;
                    bestIdx = i;
                    bestOffset = curOffset;
                }
                else if (bestIdx < 0 && i == 1 && list[0].Max == int.MaxValue)
                {
                    bestIdx = 1;
                    bestOffset = -1;
                }
                curOffset += len;
            }
            else if (!a.IsLiteral && a.Min == a.Max)
            {
                curOffset += a.Min;
            }
            else
            {
                fixedSoFar = false;
            }
        }

        atoms = list.ToArray();
        fixedLitAtomIndex = bestIdx;
        fixedLitOffset = bestOffset;
        return true;
    }

    private static bool TryParseSeqAtom(
        Pcre2Program program,
        Pcre2DataView data,
        int nodeId,
        bool isLast,
        out SeqAtom atom)
    {
        atom = default;
        var captureGroup = -1;
        var node = program.Nodes[nodeId];
        if (node.Kind == Pcre2NodeKind.Capture && node.Children.Count == 1)
        {
            captureGroup = program.Groups[node.Operand].Number;
            nodeId = UnwrapAtomic(program, program.Children[node.Children.Offset]);
            node = program.Nodes[nodeId];
        }

        var min = 1;
        var max = 1;
        if (node.Kind == Pcre2NodeKind.Repeat)
        {
            if (node.Quantifier == Pcre2Quantifier.Lazy || node.Children.Count != 1)
            {
                return false;
            }

            min = node.Min;
            max = node.Max < 0 ? int.MaxValue : node.Max;
            nodeId = UnwrapAtomic(program, program.Children[node.Children.Offset]);
            node = program.Nodes[nodeId];
        }

        if (node.Kind == Pcre2NodeKind.Dot &&
            isLast &&
            max == int.MaxValue &&
            (node.Flags & Pcre2CompileFlags.Dotall) == 0 &&
            program.Profile.Newline == Pcre2Newline.Lf)
        {
            Span<bool> notNl = stackalloc bool[256];
            notNl.Fill(true);
            notNl['\n'] = false;
            FastCharSet dotSet = new(
                CreateBitmap(notNl),
                 null,
                null,
                default,
                noneAbove255: false,
                allAbove255: true,
                isNotNewline: true);
            atom = new SeqAtom(false, null, null, dotSet, min, max, captureGroup);
            return true;
        }

        if (!TryBuildCharSet(program, data, nodeId, out var set))
        {
            return false;
        }

        atom = new SeqAtom(false, null, null, set, min, max, captureGroup);
        return true;
    }

    private static bool TryBuildCharSet(
        Pcre2Program program,
        Pcre2DataView data,
        int nodeId,
        out FastCharSet set)
    {
        set = default;
        var node = program.Nodes[nodeId];
        if (node.Kind == Pcre2NodeKind.Literal)
        {
            if ((node.Flags & Pcre2CompileFlags.Caseless) != 0 || node.Operand is < 0 or >= 256)
            {
                return false;
            }

            Span<bool> bits = stackalloc bool[256];
            bits[node.Operand] = true;
            set = CreateAsciiCharSet(bits);
            return true;
        }

        if (node.Kind != Pcre2NodeKind.Class)
        {
            return false;
        }

        var maxClassId = node.Operand;
        Span<bool> noneAbove255 = stackalloc bool[maxClassId + 1];
        Span<bool> allAbove255 = stackalloc bool[maxClassId + 1];
        var caseless = (node.Flags & Pcre2CompileFlags.Caseless) != 0;
        for (var id = 0; id <= maxClassId; id++)
        {
            var item = program.Classes[id];
            switch (item.Kind)
            {
                case Pcre2ClassKind.Intervals:
                    var allLe255 = !caseless;
                    if (allLe255)
                    {
                        foreach (var iv in program.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count))
                        {
                            if (iv.Last > 255)
                            { allLe255 = false; break; }
                        }
                    }
                    noneAbove255[id] = allLe255;
                    allAbove255[id] = false;
                    break;
                case Pcre2ClassKind.TableClass:
                    noneAbove255[id] = true;
                    allAbove255[id] = false;
                    break;
                case Pcre2ClassKind.Property:
                    noneAbove255[id] = false;
                    allAbove255[id] = false;
                    break;
                case Pcre2ClassKind.Complement:
                    noneAbove255[id] = allAbove255[item.Left];
                    allAbove255[id] = noneAbove255[item.Left];
                    break;
                case Pcre2ClassKind.Union:
                    noneAbove255[id] = noneAbove255[item.Left] && noneAbove255[item.Right];
                    allAbove255[id] = allAbove255[item.Left] || allAbove255[item.Right];
                    break;
                case Pcre2ClassKind.Intersection:
                    noneAbove255[id] = noneAbove255[item.Left] || noneAbove255[item.Right];
                    allAbove255[id] = allAbove255[item.Left] && allAbove255[item.Right];
                    break;
                case Pcre2ClassKind.Subtraction:
                    noneAbove255[id] = noneAbove255[item.Left] || allAbove255[item.Right];
                    allAbove255[id] = allAbove255[item.Left] && noneAbove255[item.Right];
                    break;
                default:
                    noneAbove255[id] = false;
                    allAbove255[id] = false;
                    break;
            }
        }

        Span<bool> latin1 = stackalloc bool[256];
        for (var cp = 0; cp < 256; cp++)
        {
            latin1[cp] = MatchesClassUncharged(program, node, cp, data);
        }

        var topNoneAbove255 = noneAbove255[maxClassId];
        var topAllAbove255 = allAbove255[maxClassId];

        SearchValues<char>? charSv = null;
        if (topNoneAbove255)
        {
            Span<char> cBuf = stackalloc char[256];
            var cCount = 0;
            for (var cp = 0; cp < 256; cp++)
            {
                if (latin1[cp])
                {
                    cBuf[cCount++] = (char)cp;
                }
            }
            if (cCount > 0)
            {
                charSv = SearchValues.Create(cBuf.Slice(0, cCount));
            }
        }

        Span<byte> bBuf = stackalloc byte[256];
        var bCount = 0;
        for (var cp = 0; cp < 256; cp++)
        {
            if (latin1[cp])
            {
                bBuf[bCount++] = (byte)cp;
            }
        }
        var byteSv = bCount > 0 ? SearchValues.Create(bBuf.Slice(0, bCount)) : null;

        set = new FastCharSet(
            CreateBitmap(latin1),
            charSv,
            byteSv,
            node,
            topNoneAbove255,
            topAllAbove255);
        return true;
    }

    private static FastCharSet CreateAsciiCharSet(ReadOnlySpan<bool> latin1)
    {
        Span<char> cBuf = stackalloc char[256];
        Span<byte> bBuf = stackalloc byte[256];
        var count = 0;
        for (var i = 0; i < 256; i++)
        {
            if (latin1[i])
            {
                cBuf[count] = (char)i;
                bBuf[count] = (byte)i;
                count++;
            }
        }
        return new FastCharSet(
            CreateBitmap(latin1),
            count > 0 ? SearchValues.Create(cBuf.Slice(0, count)) : null,
            count > 0 ? SearchValues.Create(bBuf.Slice(0, count)) : null,
            default,
            noneAbove255: true,
            allAbove255: false);
    }

    private static AsciiLatin1Bitmap CreateBitmap(ReadOnlySpan<bool> latin1)
    {
        ulong b0 = 0, b1 = 0, b2 = 0, b3 = 0;
        for (var i = 0; i < 64; i++)
        {
            if (latin1[i])
            {
                b0 |= 1UL << i;
            }
        }

        for (var i = 0; i < 64; i++)
        {
            if (latin1[64 + i])
            {
                b1 |= 1UL << i;
            }
        }

        for (var i = 0; i < 64; i++)
        {
            if (latin1[128 + i])
            {
                b2 |= 1UL << i;
            }
        }

        for (var i = 0; i < 64; i++)
        {
            if (latin1[192 + i])
            {
                b3 |= 1UL << i;
            }
        }

        return new AsciiLatin1Bitmap(b0, b1, b2, b3);
    }

    private static bool MatchesClassUncharged(Pcre2Program program, Pcre2Node node, int cp, Pcre2DataView data)
    {
        var maxId = node.Operand;
        var scratch = maxId < 128 ? stackalloc byte[maxId + 1] : new byte[maxId + 1];
        for (var id = 0; id <= maxId; id++)
        {
            var item = program.Classes[id];
            var value = item.Kind switch
            {
                Pcre2ClassKind.Intervals => IntervalsUncharged(
                    program.Intervals.Slice(item.Intervals.Offset, item.Intervals.Count),
                    cp, node.Flags, node.ExtraFlags, data),
                Pcre2ClassKind.Property => Pcre2CharacterOps.Property(cp, item.Property, data),
                Pcre2ClassKind.TableClass => TableUncharged(cp, item.TableClass, node.Flags, data),
                Pcre2ClassKind.Union => scratch[item.Left] != 0 || scratch[item.Right] != 0,
                Pcre2ClassKind.Intersection => scratch[item.Left] != 0 && scratch[item.Right] != 0,
                Pcre2ClassKind.Subtraction => scratch[item.Left] != 0 && scratch[item.Right] == 0,
                Pcre2ClassKind.SymmetricDifference => (scratch[item.Left] != 0) != (scratch[item.Right] != 0),
                Pcre2ClassKind.Complement => scratch[item.Left] == 0,
                _ => false
            };
            scratch[id] = value ? (byte)1 : (byte)0;
        }
        return scratch[maxId] != 0;
    }

    private static bool TableUncharged(int cp, int table, Pcre2CompileFlags flags, Pcre2DataView data)
    {
        if (cp > 255)
        {
            return false;
        }

        var original = (data.LowByteTables[Pcre2DataAbi.ClassOffset + table * 32 + cp / 8] & (1 << (cp % 8))) != 0;
        if (original || (flags & Pcre2CompileFlags.Caseless) == 0 || table is not (3 or 4))
        {
            return original;
        }

        var other = table == 3 ? 4 : 3;
        return (data.LowByteTables[Pcre2DataAbi.ClassOffset + other * 32 + cp / 8] & (1 << (cp % 8))) != 0;
    }

    private static bool IntervalsUncharged(
        ReadOnlySpan<Pcre2Interval> intervals,
        int cp,
        Pcre2CompileFlags flags,
        Pcre2ExtraFlags extra,
        Pcre2DataView data)
    {
        if (ContainsInterval(intervals, cp))
        {
            return true;
        }

        if ((flags & Pcre2CompileFlags.Caseless) == 0)
        {
            return false;
        }

        if ((extra & Pcre2ExtraFlags.TurkishCasing) != 0 && cp is 0x49 or 0x69 or 0x130 or 0x131)
        {
            var partner = cp switch { 0x49 => 0x131, 0x131 => 0x49, 0x69 => 0x130, _ => 0x69 };
            return ContainsInterval(intervals, partner);
        }
        if ((flags & (Pcre2CompileFlags.Utf | Pcre2CompileFlags.Ucp | Pcre2CompileFlags.MatchInvalidUtf)) == 0)
        {
            return cp <= 255 && ContainsInterval(intervals, data.LowByteTables[Pcre2DataAbi.FlipOffset + cp]);
        }

        var record = Pcre2CharacterOps.Record(cp, data);
        var other = cp + record.OtherCaseDelta;
        if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (other < 128)) && ContainsInterval(intervals, other))
        {
            return true;
        }

        foreach (var equivalent in data.CaseSets.Slice(record.CaseSetOffset, record.CaseSetCount))
        {
            if (((extra & Pcre2ExtraFlags.CaselessRestrict) == 0 || (cp < 128) == (equivalent < 128)) &&
                ContainsInterval(intervals, equivalent))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsInterval(ReadOnlySpan<Pcre2Interval> intervals, int cp)
    {
        int low = 0, high = intervals.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var item = intervals[mid];
            if (cp < item.First)
            {
                high = mid - 1;
            }
            else if (cp > item.Last)
            {
                low = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
}
