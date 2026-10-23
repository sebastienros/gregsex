#nullable enable
namespace GregsEx.Flavors.Oniguruma;

internal enum OnigNodeKind
{
    Empty, Char, Set, AnyChar, Anchor, Sequence, Alternation, Repeat, Capture, Group, Atomic, Look, Backref, Call,
    CondRef, CondExpr, Absent, Keep
}

internal enum OnigAnchor
{
    LineStart, LineEnd, BufferStart, BufferEnd, SemiEnd, SearchStart, WordBoundary, NotWordBoundary,
    AsciiWordBoundary, AsciiNotWordBoundary, TextSegmentBoundary, NotTextSegmentBoundary
}

internal enum OnigRepeatMode : byte { Greedy, Lazy, Possessive }

// A parse tree node. Children are owned by their parent; Source is the pattern range that produced the node.
internal sealed class OnigNode
{
    internal OnigNodeKind Kind;
    // Char: the code point. Anchor: the OnigAnchor. Capture: the group number. Look: 1 when negative.
    // Backref/CondRef: unused (Groups). Call: the group number, or -1 until a name resolves.
    internal int Value;
    internal bool IgnoreCase;
    // AnyChar: dot also matches newline. Look: look-behind.
    internal bool Flag;
    internal OnigCharSet? Set;
    // Set: built by a negation ([^..], \W, \P{..}); such a class takes an incomplete character (a high surrogate at
    // the end of the text) as one unit, as Oniguruma's negated classes do.
    internal bool Negated;
    // Set: \w or \W outside a class, which Oniguruma keeps as a ctype node rather than a class.
    internal bool WordType;
    // Compiler analysis caches (OnigCompiler.Bits, FollowBits and Lengths); nodes no longer change once compiling.
    internal OnigStartBits? BitsCache;
    internal OnigStartBits? FollowCache;
    internal (int Min, int Max)? LengthsCache;
    internal List<OnigNode> Children = [];
    internal int Min;
    internal int Max;
    internal OnigRepeatMode Mode;
    // Capture: the name, if named. Backref/CondRef/Call: the referenced name before resolution.
    internal string? Name;
    internal int[]? Groups;
    internal string? Literal;
    internal int Offset;
    internal int Length;

    internal OnigNode(OnigNodeKind kind, int offset, int length)
    {
        Kind = kind;
        Offset = offset;
        Length = length;
    }

    internal OnigNode Child => Children[0];
}
