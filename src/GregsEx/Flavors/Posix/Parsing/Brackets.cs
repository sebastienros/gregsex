#nullable enable
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Locale;

namespace GregsEx.Flavors.Posix.Parsing;

internal struct ByteSetBuilder
{
    private ulong _a, _b, _c, _d;
    internal void Add(int value)
    {
        var bit = 1UL << (value & 63);
        switch (value >> 6)
        {
            case 0:
                _a |= bit;
                break;
            case 1:
                _b |= bit;
                break;
            case 2:
                _c |= bit;
                break;
            default:
                _d |= bit;
                break;
        }
    }
    internal readonly bool Contains(int value) => Build().Contains((byte)value);
    internal readonly PosixByteSet Build() => new(_a & ~1UL, _b, _c, _d);
    internal void Complement() { _a = ~_a & ~1UL; _b = ~_b; _c = ~_c; _d = ~_d; }
    internal void Remove(int value)
    {
        var bit = ~(1UL << (value & 63));
        switch (value >> 6)
        {
            case 0:
                _a &= bit;
                break;
            case 1:
                _b &= bit;
                break;
            case 2:
                _c &= bit;
                break;
            default:
                _d &= bit;
                break;
        }
    }
}
internal readonly record struct BracketToken(int Value, int Kind); // 0=character/symbol, 1=equivalence, 2=class
internal ref partial struct PosixParser
{
    private int Bracket()
    {
        var first = _at;
        Advance();
        var negative = Peek() == '^';
        if (negative)
        {
            Advance();
        }

        int tokens = 0, firstElement = -1, lastElement = -1;
        var set = new ByteSetBuilder();
        while (true)
        {
            Here(first, Math.Min(Length, _at + 1));
            _storage.Work();
            if (_at == Length)
            { Here(first, Length); _storage.Syntax(PosixErrorCode.UnbalancedBracket, capability: "PX-006"); }
            if (Peek() == ']' && tokens > 0)
            {
                break;
            }

            var left = BracketItem(first);
            var element = left.Kind == 0 ? left.Value : -1;
            if (Peek() == '-' && At(_at + 1) != ']' && _at + 1 < Length)
            {
                Advance();
                var right = BracketItem(first);
                element = -1;
                if (left.Kind != 0 || right.Kind != 0)
                {
                    RangeError(first, left.Kind == 1 || right.Kind == 1 ? PosixSyntaxDisposition.UnspecifiedRejected : PosixSyntaxDisposition.UndefinedRejected);
                }

                if (left.Value > right.Value)
                {
                    RangeError(first, PosixSyntaxDisposition.UnspecifiedRejected);
                }

                for (var b = left.Value; b <= right.Value; b++)
                { _storage.Work(); set.Add(b); }
                if (Peek() == '-' && At(_at + 1) != ']')
                {
                    RangeError(first, PosixSyntaxDisposition.UndefinedRejected);
                }
            }
            else
            {
                AddToken(ref set, left);
            }

            if (tokens == 0)
            {
                firstElement = element;
            }

            lastElement = element;
            tokens++;
        }
        Advance();
        if (tokens >= 3 && firstElement is '.' or '=' or ':' && lastElement == firstElement)
        { Here(first, _at); _storage.Syntax(PosixErrorCode.BadPattern, PosixSyntaxDisposition.UnspecifiedRejected, "PX-006"); }
        // Expand the positive set first, THEN complement. Opaque bytes have no counterpart.
        if (IgnoreCase)
        {
            for (int b = 'A'; b <= 'Z'; b++)
            { _storage.Work(); if (set.Contains(b) || set.Contains(b + 32)) { set.Add(b); set.Add(b + 32); } }
        }

        if (negative)
        {
            set.Complement();
            if ((_profile.Flags & PosixCompileFlags.Newline) != 0)
            {
                set.Remove(10);
            }
        }
        return Class(set.Build(), first, _at);
    }
    private readonly void AddToken(ref ByteSetBuilder set, BracketToken token)
    {
        if (token.Kind != 2)
        { set.Add(token.Value); return; }
        var masks = CByteLocale.Instance.ClassMasks;
        for (var b = 1; b <= 255; b++)
        {
            _storage.Work();
            if ((masks[b] & (1 << token.Value)) != 0)
            {
                set.Add(b);
            }
        }
    }
    private BracketToken BracketItem(int bracketStart)
    {
        int first = _at, c = Peek();
        if (c == '[' && Peek(1) is '.' or '=' or ':')
        {
            var marker = Peek(1);
            Advance(2);
            var start = _at;
            while (_at < Length && !(Peek() == marker && Peek(1) == ']'))
            {
                Advance();
            }

            if (_at == Length)
            { Here(bracketStart, Length); _storage.Syntax(PosixErrorCode.UnbalancedBracket, capability: "PX-006"); }
            var count = _at - start;
            Advance(2);
            Here(first, _at);
            if (marker == ':')
            {
                // Class vocabulary is bounded; unknown longer names need no buffer or allocation.
                Span<byte> name = stackalloc byte[6];
                if (count <= name.Length)
                {
                    for (var i = 0; i < count; i++)
                    { _storage.Work(); name[i] = (byte)At(start + i); }
                    if (CByteLocale.Instance.TryGetClass(name[..count], out var value))
                    {
                        return new((int)value, 2);
                    }
                }
                _storage.Syntax(PosixErrorCode.InvalidCharacterClass, PosixSyntaxDisposition.Invalid, "PX-004");
            }
            if (count != 1)
            {
                _storage.Syntax(PosixErrorCode.InvalidCollatingElement, PosixSyntaxDisposition.Invalid, "PX-005");
            }

            _storage.Work();
            return new(At(start), marker == '=' ? 1 : 0);
        }
        Advance();
        return new(c, 0);
    }
    private readonly void RangeError(int first, PosixSyntaxDisposition disposition)
    {
        var end = _at;
        while (end < Length && At(end) != ']')
        { _storage.Work(); end++; }
        Here(first, Math.Min(Length, end + 1));
        _storage.Syntax(PosixErrorCode.InvalidRange, disposition, "PX-006");
    }
}
