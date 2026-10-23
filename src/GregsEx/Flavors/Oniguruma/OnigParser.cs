#nullable enable
using System.Globalization;
using System.Runtime.CompilerServices;

namespace GregsEx.Flavors.Oniguruma;

[Flags]
internal enum OnigInlineOptions
{
    None = 0,
    IgnoreCase = 1,
    // Oniguruma's (?m): dot also matches newline.
    DotAll = 2,
    Extended = 4,
    AsciiWord = 8,
    AsciiDigit = 16,
    AsciiSpace = 32,
    AsciiPosix = 64,
}

internal sealed class OnigSyntaxException(RegexDiagnosticCode code, int offset, int length, string message) : Exception(message)
{
    internal RegexDiagnosticCode Code { get; } = code;
    internal int Offset { get; } = offset;
    internal int Length { get; } = length;
}

// A recursive descent parser for ONIG_SYNTAX_ONIGURUMA (Oniguruma 6.9.10 doc/RE) over UTF-16 patterns.
internal sealed class OnigParser
{
    private readonly string _pattern;
    private readonly RegexCompileLimits _limits;
    private int _pos;
    private int _depth;
    private int _treeDepth;
    private int _groupCount;
    private int _behindDepth;
    // Code points of a \x{H H ...} sequence still to be read as atoms or class members.
    private readonly Queue<int> _pending = new();
    private readonly List<OnigNode> _captures = [null!];
    private readonly List<OnigNode> _references = [];
    private bool _hasNamed;

    private OnigParser(string pattern, RegexCompileLimits limits)
    {
        _pattern = pattern;
        _limits = limits;
    }

    internal int GroupCount => _groupCount + 1;

    internal IReadOnlyList<OnigNode> Captures => _captures;

    internal bool HasNamedGroups => _hasNamed;

    // An upper bound of the syntax tree's depth: two nodes per group level, one per stacked quantifier.
    internal int TreeDepth => _treeDepth;

    // Backrefs, calls and conditions, resolved by name once every group is known.
    internal IReadOnlyList<OnigNode> References => _references;

    internal static OnigParser Parse(string pattern, OnigInlineOptions options, RegexCompileLimits limits, out OnigNode root)
    {
        if (pattern.Length > limits.MaxPatternUnits)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.PatternLimitExceeded, 0, pattern.Length, "pattern too long");
        }

        var parser = new OnigParser(pattern, limits);
        root = parser.ParseAlternation(options);
        if (parser._pos < pattern.Length)
        {
            throw parser.Error("unmatched close parenthesis", parser._pos, 1);
        }

        parser.Resolve();
        return parser;
    }

    private OnigSyntaxException Error(string message, int offset, int length = 1)
        => new(RegexDiagnosticCode.InvalidSyntax, offset, length, message);

    private OnigSyntaxException Unsupported(string message, int offset, int length)
        => new(RegexDiagnosticCode.UnsupportedFeature, offset, length, message);

    private bool AtEnd => _pos >= _pattern.Length;

    private char Peek(int ahead = 0) => _pos + ahead < _pattern.Length ? _pattern[_pos + ahead] : '\0';

    private bool HasAhead(int count) => _pos + count <= _pattern.Length;

    private OnigNode ParseAlternation(OnigInlineOptions options)
    {
        var start = _pos;
        if (++_depth > _limits.MaxNestingDepth || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.NestingLimitExceeded, _pos, 1, "nesting too deep");
        }

        _treeDepth = Math.Max(_treeDepth, 2 * _depth);

        var first = ParseSequence(options);
        if (AtEnd || Peek() != '|')
        {
            _depth--;
            return first;
        }

        var alt = new OnigNode(OnigNodeKind.Alternation, start, 0);
        alt.Children.Add(first);
        while (!AtEnd && Peek() == '|')
        {
            _pos++;
            alt.Children.Add(ParseSequence(options));
        }
        alt.Length = _pos - start;
        _depth--;
        return alt;
    }

    private OnigNode ParseSequence(OnigInlineOptions options)
    {
        var start = _pos;
        var seq = new OnigNode(OnigNodeKind.Sequence, start, 0);
        while (true)
        {
            SkipExtended(options);
            if (_pending.Count == 0 && (AtEnd || Peek() is '|' or ')'))
            {
                break;
            }

            // An isolated option, (?i), covers the rest of the enclosing group, alternatives included.
            if (_pending.Count == 0 && TryIsolatedOption(ref options))
            {
                seq.Children.Add(ParseAlternation(options));
                break;
            }

            var atomStart = _pos;
            var atom = ParseAtom(options);
            if (atom is null)
            {
                continue;
            }

            atom = ParseQuantifiers(atom, atomStart, options);
            seq.Children.Add(atom);
        }
        seq.Length = _pos - start;
        if (_behindDepth == 0)
        {
            ExpandMultiFolds(seq.Children);
        }

        return seq.Children.Count == 1 ? seq.Children[0] : seq;
    }

    // Oniguruma's ignore-case strings also match through folds to several code points: (?i)ss matches ß, and (?i)ß
    // matches ss. As its expand_case_fold_string does, a run of ignore-case characters (plain (?:...) groups and {1}
    // repeats included) is read left to right: where the longest such fold starts, those characters or a character
    // folding to it; a character with such a fold, itself or the characters it folds to. Look-behind bodies keep single
    // code point folding for strings.
    private static void ExpandMultiFolds(List<OnigNode> children)
    {
        for (var i = 0; i < children.Count; i++)
        {
            if (PlainFoldChars(children[i]) is { } chars && children[i].Kind != OnigNodeKind.Char)
            {
                children.RemoveAt(i);
                children.InsertRange(i, chars);
            }
        }

        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Kind == OnigNodeKind.Repeat && child.Child.Kind == OnigNodeKind.Char && child.Child.IgnoreCase)
            {
                if (FoldOf(child.Child.Value) is { } own)
                {
                    child.Children[0] = MemberAlternatives(child.Child, own.Fold, own.Members);
                }
                continue;
            }

            if (child.Kind != OnigNodeKind.Char || !child.IgnoreCase)
            {
                continue;
            }

            var end = i;
            while (end < children.Count && children[end].Kind == OnigNodeKind.Char && children[end].IgnoreCase)
            {
                end++;
            }

            var run = children.GetRange(i, end - i);
            var segments = new List<OnigNode>();
            var changed = false;
            for (var at = 0; at < run.Count;)
            {
                var (length, members) = LongestFold(run, at);
                if (length > 0)
                {
                    var plain = new OnigNode(OnigNodeKind.Sequence, run[at].Offset, run[at].Length);
                    plain.Children.AddRange(run.GetRange(at, length));
                    var alternation = new OnigNode(OnigNodeKind.Alternation, run[at].Offset, run[at].Length);
                    alternation.Children.Add(plain);
                    alternation.Children.Add(new OnigNode(OnigNodeKind.Set, run[at].Offset, run[at].Length) { Set = CaseClosedSet(members!) });
                    segments.Add(alternation);
                    at += length;
                    changed = true;
                }
                else if (FoldOf(run[at].Value) is { } own)
                {
                    segments.Add(MemberAlternatives(run[at], own.Fold, own.Members));
                    at++;
                    changed = true;
                }
                else
                {
                    segments.Add(run[at]);
                    at++;
                }
            }

            if (changed)
            {
                children.RemoveRange(i, end - i);
                children.InsertRange(i, segments);
                i += segments.Count - 1;
            }
            else
            {
                i = end - 1;
            }
        }
    }

    // The ignore-case characters of a plain non-capturing group or {1} repeat, or null.
    private static List<OnigNode>? PlainFoldChars(OnigNode node)
    {
        switch (node.Kind)
        {
            case OnigNodeKind.Char when node.IgnoreCase:
                return [node];
            case OnigNodeKind.Group:
                return PlainFoldChars(node.Child);
            case OnigNodeKind.Repeat when node.Min == 1 && node.Max == 1:
                return PlainFoldChars(node.Child);
            case OnigNodeKind.Sequence:
                {
                    var chars = new List<OnigNode>();
                    foreach (var child in node.Children)
                    {
                        if (PlainFoldChars(child) is not { } inner)
                        {
                            return null;
                        }
                        chars.AddRange(inner);
                    }
                    return chars.Count > 0 ? chars : null;
                }
            default:
                return null;
        }
    }

    // The longest fold to several code points that run[at..] spells, by length, with its members.
    private static (int Length, int[]? Members) LongestFold(List<OnigNode> run, int at)
    {
        var best = (0, (int[]?)null);
        foreach (var (fold, members) in OnigUnicode.MultiFolds)
        {
            if (fold.Length <= best.Item1 || at + fold.Length > run.Count)
            {
                continue;
            }

            var spells = true;
            for (var j = 0; j < fold.Length && spells; j++)
            {
                spells = OnigUnicode.Fold(run[at + j].Value) == fold[j];
            }

            if (spells)
            {
                best = (fold.Length, members);
            }
        }
        return best;
    }

    private static (int[] Fold, int[] Members)? FoldOf(int cp)
    {
        foreach (var entry in OnigUnicode.MultiFolds)
        {
            if (Array.IndexOf(entry.Members, cp) >= 0)
            {
                return entry;
            }
        }
        return null;
    }

    private static OnigCharSet CaseClosedSet(int[] members)
    {
        var builder = new OnigCharSet.Builder();
        foreach (var m in members)
        {
            foreach (var e in OnigUnicode.CaseClass(m))
            {
                builder.Add(e);
            }
        }
        return builder.Build();
    }

    // ß as itself, as another character with its fold (ẞ), or spelled out (ss).
    private static OnigNode MemberAlternatives(OnigNode head, int[] fold, int[] members)
    {
        var alternation = new OnigNode(OnigNodeKind.Alternation, head.Offset, head.Length);
        alternation.Children.Add(head);
        alternation.Children.Add(new OnigNode(OnigNodeKind.Set, head.Offset, head.Length) { Set = CaseClosedSet(members) });
        var spelled = new OnigNode(OnigNodeKind.Sequence, head.Offset, head.Length);
        foreach (var cp in fold)
        {
            spelled.Children.Add(new OnigNode(OnigNodeKind.Char, head.Offset, head.Length) { Value = cp, IgnoreCase = true });
        }
        alternation.Children.Add(spelled);
        return alternation;
    }

    private void SkipExtended(OnigInlineOptions options)
    {
        if ((options & OnigInlineOptions.Extended) == 0)
        {
            return;
        }

        while (!AtEnd)
        {
            var c = Peek();
            if (c is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                _pos++;
            }
            else if (c == '#')
            {
                while (!AtEnd && Peek() != '\n')
                {
                    _pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private static bool IsSimpleQuantifier(int min, int max) => (min, max) is (0, 1) or (0, -1) or (1, -1);

    private OnigNode ParseQuantifiers(OnigNode atom, int atomStart, OnigInlineOptions options)
    {
        var stacked = 0;
        while (true)
        {
            SkipExtended(options);
            if (AtEnd)
            {
                return atom;
            }

            int min, max;
            var c = Peek();
            var interval = false;
            var qStart = _pos;
            if (c == '*')
            {
                (min, max) = (0, -1);
                _pos++;
            }
            else if (c == '+')
            {
                (min, max) = (1, -1);
                _pos++;
            }
            else if (c == '?')
            {
                (min, max) = (0, 1);
                _pos++;
            }
            else if (c == '{' && TryInterval(out min, out max))
            {
                interval = true;
            }
            else
            {
                return atom;
            }

            if (IsZeroWidthAssertion(atom))
            {
                throw Error("target of repeat operator is invalid", qStart, _pos - qStart);
            }

            var mode = OnigRepeatMode.Greedy;
            if (interval && min > max && max >= 0)
            {
                // {n,m} with n > m is possessive between m and n.
                (min, max) = (max, min);
                mode = OnigRepeatMode.Possessive;
            }
            else if (!AtEnd && Peek() == '?' && !(interval && min == max))
            {
                // {n}? is not reluctant in this syntax: it is (?:x{n})?.
                mode = OnigRepeatMode.Lazy;
                _pos++;
            }
            else if (!interval && !AtEnd && Peek() == '+')
            {
                mode = OnigRepeatMode.Possessive;
                _pos++;
            }

            if (atom.Kind == OnigNodeKind.Repeat && stacked > 0 && mode == OnigRepeatMode.Greedy && atom.Mode == OnigRepeatMode.Greedy
                && IsSimpleQuantifier(atom.Min, atom.Max) && IsSimpleQuantifier(min, max))
            {
                // Oniguruma reduces nested ?, * and + (onig_reduce_nested_quantifier): x** is x*, (x+)? is x*.
                (atom.Min, atom.Max) = atom.Min == 0 && atom.Max == 1 && min == 0 && max == 1 ? (0, 1)
                    : atom.Min == 1 && min == 1 ? (1, -1) : (0, -1);
                atom.Length = _pos - atomStart;
                continue;
            }

            if (++stacked > _limits.MaxNestingDepth)
            {
                throw new OnigSyntaxException(RegexDiagnosticCode.NestingLimitExceeded, qStart, _pos - qStart, "too many nested quantifiers");
            }

            _treeDepth = Math.Max(_treeDepth, 2 * _depth + stacked);

            var repeat = new OnigNode(OnigNodeKind.Repeat, atomStart, _pos - atomStart)
            {
                Min = min,
                Max = max,
                Mode = mode,
            };
            repeat.Children.Add(atom);
            atom = repeat;
        }
    }

    // Oniguruma's check_node_in_look_behind: no look-ahead, \z or \Z in a look-behind; no negative look-behind in a
    // positive one; no capture in a negative one.
    private void CheckLookBehind(OnigNode node, bool negative, int start)
    {
        var invalid = node.Kind switch
        {
            OnigNodeKind.Look => !node.Flag || (node.Value != 0 && !negative),
            OnigNodeKind.Anchor => (OnigAnchor)node.Value is OnigAnchor.BufferEnd or OnigAnchor.SemiEnd,
            OnigNodeKind.Capture => negative,
            _ => false,
        };
        if (invalid)
        {
            throw Error("invalid pattern in look-behind", start, _pos - start);
        }

        foreach (var child in node.Children)
        {
            CheckLookBehind(child, negative, start);
        }
    }

    // Anchors and look-arounds, also inside (?:...), which Oniguruma does not keep as a group.
    private static bool IsZeroWidthAssertion(OnigNode node)
        => node.Kind is OnigNodeKind.Anchor or OnigNodeKind.Keep or OnigNodeKind.Look
            || (node.Kind == OnigNodeKind.Group && node.Children.Count == 1 && IsZeroWidthAssertion(node.Child));

    // {n}, {n,}, {,m} and {n,m}; anything else leaves '{' a literal.
    private bool TryInterval(out int min, out int max)
    {
        min = max = 0;
        var p = _pos + 1;
        var lowStart = p;
        while (p < _pattern.Length && char.IsAsciiDigit(_pattern[p]))
        {
            p++;
        }

        var hasLow = p > lowStart;
        var hasHigh = false;
        var hasComma = false;
        var highStart = 0;
        if (p < _pattern.Length && _pattern[p] == ',')
        {
            hasComma = true;
            p++;
            highStart = p;
            while (p < _pattern.Length && char.IsAsciiDigit(_pattern[p]))
            {
                p++;
            }

            hasHigh = p > highStart;
        }
        if (p >= _pattern.Length || _pattern[p] != '}' || (!hasLow && !hasHigh))
        {
            return false;
        }

        min = hasLow ? ParseCount(lowStart, hasComma ? highStart - 1 : p) : 0;
        max = hasComma ? (hasHigh ? ParseCount(highStart, p) : -1) : min;
        _pos = p + 1;
        return true;
    }

    private int ParseCount(int start, int end)
    {
        if (!int.TryParse(_pattern.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 100000)
        {
            throw Error("too big number for repeat range", start, end - start);
        }

        return value;
    }

    private OnigNode? ParseAtom(OnigInlineOptions options)
    {
        var start = _pos;
        if (_pending.Count > 0)
        {
            return CharNode(_pending.Dequeue(), start, options);
        }

        var c = _pattern[_pos];
        switch (c)
        {
            case '(':
                return ParseGroup(options);
            case '[':
                {
                    var set = ParseClass(options, out var negated);
                    var node = new OnigNode(OnigNodeKind.Set, start, _pos - start) { Set = set, Negated = negated };
                    return (options & OnigInlineOptions.IgnoreCase) != 0 && !negated ? WithMultiFolds(node, start) : node;
                }
            case '.':
                _pos++;
                return new OnigNode(OnigNodeKind.AnyChar, start, 1) { Flag = (options & OnigInlineOptions.DotAll) != 0 };
            case '^':
                _pos++;
                return Anchor(OnigAnchor.LineStart, start);
            case '$':
                _pos++;
                return Anchor(OnigAnchor.LineEnd, start);
            case '*' or '+' or '?':
                throw Error("target of repeat operator is not specified", start);
            case '{':
                {
                    var saved = _pos;
                    if (TryInterval(out _, out _))
                    {
                        throw Error("target of repeat operator is not specified", saved, _pos - saved);
                    }

                    _pos++;
                    return CharNode('{', start, options);
                }
            case '\\':
                return ParseEscape(options);
            default:
                return CharNode(ReadCodePoint(), start, options);
        }
    }

    private int ReadCodePoint()
    {
        var c = _pattern[_pos++];
        if (char.IsHighSurrogate(c) && _pos < _pattern.Length && char.IsLowSurrogate(_pattern[_pos]))
        {
            return char.ConvertToUtf32(c, _pattern[_pos++]);
        }

        if (char.IsSurrogate(c))
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.InvalidPatternEncoding, _pos - 1, 1, "invalid code point value");
        }

        return c;
    }

    private OnigNode Anchor(OnigAnchor anchor, int start)
        => new(OnigNodeKind.Anchor, start, _pos - start) { Value = (int)anchor };

    private OnigNode CharNode(int cp, int start, OnigInlineOptions options)
        => new(OnigNodeKind.Char, start, _pos - start) { Value = cp, IgnoreCase = (options & OnigInlineOptions.IgnoreCase) != 0 };

    private OnigNode SetNode(OnigCharSet set, int start)
        => new(OnigNodeKind.Set, start, _pos - start) { Set = set };

    private OnigNode? ParseGroup(OnigInlineOptions options)
    {
        var start = _pos++;
        OnigNode node;
        if (Peek() != '?')
        {
            node = OpenCapture(start, null);
            node.Children.Add(ParseGroupBody(options, start));
            node.Length = _pos - start;
            return node;
        }

        _pos++;
        var c = Peek();
        switch (c)
        {
            case '#':
                while (!AtEnd && Peek() != ')')
                {
                    _pos++;
                }

                if (AtEnd)
                {
                    throw Error("end pattern in group", start, _pos - start);
                }

                _pos++;
                return null;
            case ':':
                _pos++;
                node = new OnigNode(OnigNodeKind.Group, start, 0);
                break;
            case '=' or '!':
                _pos++;
                node = new OnigNode(OnigNodeKind.Look, start, 0) { Value = c == '!' ? 1 : 0 };
                break;
            case '>':
                _pos++;
                node = new OnigNode(OnigNodeKind.Atomic, start, 0);
                break;
            case '~':
                return ParseAbsent(options, start);
            case '(':
                return ParseConditional(options, start);
            case '<' when Peek(1) is '=' or '!':
                {
                    node = new OnigNode(OnigNodeKind.Look, start, 0) { Value = Peek(1) == '!' ? 1 : 0, Flag = true };
                    _pos += 2;
                    _behindDepth++;
                    try
                    {
                        node.Children.Add(ParseGroupBody(options, start));
                    }
                    finally
                    {
                        _behindDepth--;
                    }

                    CheckLookBehind(node.Child, negative: node.Value != 0, start);

                    node.Length = _pos - start;
                    return node;
                }
            case '<' or '\'':
                {
                    _pos++;
                    var name = ReadGroupName(c == '<' ? '>' : '\'');
                    node = OpenCapture(start, name);
                    break;
                }
            case '{' or '*':
                throw Unsupported("callouts are not supported", start, 2);
            default:
                {
                    ParseOptionLetters(ref options, start, out var isolated);
                    if (isolated)
                    {
                        // Only reached for an isolated option at the start of a sequence: see TryIsolatedOption.
                        throw Error("invalid option", start, _pos - start);
                    }

                    node = new OnigNode(OnigNodeKind.Group, start, 0);
                    break;
                }
        }

        node.Children.Add(ParseGroupBody(options, start));
        node.Length = _pos - start;
        return node;
    }

    private OnigNode ParseGroupBody(OnigInlineOptions options, int start)
    {
        var body = ParseAlternation(options);
        if (AtEnd || Peek() != ')')
        {
            throw Error("end pattern with unmatched parenthesis", start, _pos - start);
        }

        _pos++;
        return body;
    }

    private OnigNode OpenCapture(int start, string? name)
    {
        if (_groupCount + 1 >= _limits.MaxCaptureGroups)
        {
            throw new OnigSyntaxException(RegexDiagnosticCode.CaptureLimitExceeded, start, 1, "too many capture groups");
        }

        var node = new OnigNode(OnigNodeKind.Capture, start, 0) { Value = ++_groupCount, Name = name };
        _captures.Add(node);
        if (name is not null)
        {
            _hasNamed = true;
        }

        return node;
    }

    // As Oniguruma's fetch_name: the first character is a word character other than a digit; later characters are
    // not checked (fetch_name records an error for them but returns success).
    private string ReadGroupName(char close)
    {
        var start = _pos;
        while (!AtEnd && Peek() != close && Peek() != ')')
        {
            _pos++;
        }

        if (AtEnd || Peek() != close || _pos == start)
        {
            throw Error("invalid group name", start, Math.Max(1, _pos - start));
        }

        var name = _pattern.Substring(start, _pos - start);
        var first = char.IsHighSurrogate(name[0]) && name.Length > 1 ? char.ConvertToUtf32(name[0], name[1]) : name[0];
        if (char.IsAsciiDigit(name[0]) || !OnigUnicode.Property(OnigUnicode.Word).Contains(first))
        {
            throw Error("invalid group name", start, name.Length);
        }

        _pos++;
        return name;
    }

    private bool TryIsolatedOption(ref OnigInlineOptions options)
    {
        if (!(Peek() == '(' && Peek(1) == '?' && IsOptionLetter(Peek(2))))
        {
            return false;
        }

        var saved = _pos;
        var copy = options;
        _pos += 2;
        ParseOptionLetters(ref copy, saved, out var isolated);
        if (!isolated)
        {
            _pos = saved;
            return false;
        }

        options = copy;
        return true;
    }

    private static bool IsOptionLetter(char c) => c is 'i' or 'm' or 'x' or 'W' or 'D' or 'S' or 'P' or 'y' or 'C' or 'I' or 'L' or '-';

    // Reads [imxWDSPy-imxWDSP] up to ':' (scoped, consumed) or ')' (isolated, consumed).
    private bool ParseOptionLetters(ref OnigInlineOptions options, int start, out bool isolated)
    {
        var negative = false;
        while (true)
        {
            if (AtEnd)
            {
                throw Error("end pattern in group", start, _pos - start);
            }

            var c = Peek();
            _pos++;
            OnigInlineOptions flag;
            switch (c)
            {
                case ')':
                    isolated = true;
                    return true;
                case ':':
                    isolated = false;
                    return true;
                case '-':
                    negative = true;
                    continue;
                case 'i': flag = OnigInlineOptions.IgnoreCase; break;
                case 'm': flag = OnigInlineOptions.DotAll; break;
                case 'x': flag = OnigInlineOptions.Extended; break;
                case 'W': flag = OnigInlineOptions.AsciiWord; break;
                case 'D': flag = OnigInlineOptions.AsciiDigit; break;
                case 'S': flag = OnigInlineOptions.AsciiSpace; break;
                case 'P': flag = OnigInlineOptions.AsciiPosix | OnigInlineOptions.AsciiWord | OnigInlineOptions.AsciiDigit | OnigInlineOptions.AsciiSpace; break;
                case 'y':
                    if (Peek() == '{' && Peek(1) == 'g' && Peek(2) == '}')
                    {
                        _pos += 3;
                        continue;
                    }

                    throw Unsupported("text segment word mode is not supported", start, _pos - start);
                case 'C' or 'I' or 'L':
                    throw Unsupported("whole options are not supported", _pos - 1, 1);
                default:
                    throw Error("undefined group option", _pos - 1, 1);
            }
            options = negative ? options & ~flag : options | flag;
        }
    }

    // (?~absent): the absent repeater, (?:(?!absent)\O)* (the range it matches holds no start of absent); a
    // case-sensitive literal absent gets the dedicated instruction.
    private OnigNode ParseAbsent(OnigInlineOptions options, int start)
    {
        _pos++;
        if (Peek() == '|')
        {
            throw Unsupported("absent expressions and stoppers are not supported", start, 3);
        }

        var body = ParseGroupBody(options, start);
        var literal = new System.Text.StringBuilder();
        if (TryLiteral(body, literal, out var ignoreCase) && literal.Length > 0 && !ignoreCase)
        {
            return new OnigNode(OnigNodeKind.Absent, start, _pos - start) { Literal = literal.ToString() };
        }

        var look = new OnigNode(OnigNodeKind.Look, start, _pos - start) { Value = 1 };
        look.Children.Add(body);
        var step = new OnigNode(OnigNodeKind.Sequence, start, _pos - start);
        step.Children.Add(look);
        step.Children.Add(new OnigNode(OnigNodeKind.AnyChar, start, _pos - start) { Flag = true });
        var repeat = new OnigNode(OnigNodeKind.Repeat, start, _pos - start) { Min = 0, Max = -1, Mode = OnigRepeatMode.Greedy };
        repeat.Children.Add(step);
        return repeat;
    }

    private static bool TryLiteral(OnigNode node, System.Text.StringBuilder literal, out bool ignoreCase)
    {
        ignoreCase = false;
        switch (node.Kind)
        {
            case OnigNodeKind.Char:
                literal.Append(OnigUnicode.ToUtf16(node.Value));
                ignoreCase = node.IgnoreCase;
                return true;
            case OnigNodeKind.Sequence:
                foreach (var child in node.Children)
                {
                    if (!TryLiteral(child, literal, out var ic))
                    {
                        return false;
                    }

                    ignoreCase |= ic;
                }
                return true;
            case OnigNodeKind.Group:
                return TryLiteral(node.Child, literal, out ignoreCase);
            default:
                return false;
        }
    }

    // (?(cond)yes|no): cond is a group number or name, or an expression.
    private OnigNode ParseConditional(OnigInlineOptions options, int start)
    {
        _pos++;
        OnigNode node;
        var condStart = _pos;
        if (TryConditionReference(out var reference))
        {
            node = new OnigNode(OnigNodeKind.CondRef, start, 0);
            Reference(node, reference, condStart, isCall: false);
        }
        else
        {
            node = new OnigNode(OnigNodeKind.CondExpr, start, 0);
            node.Children.Add(ParseAlternation(options));
            if (AtEnd || Peek() != ')')
            {
                throw Error("invalid conditional pattern", start, _pos - start);
            }

            _pos++;
        }

        var body = ParseAlternation(options);
        if (AtEnd || Peek() != ')')
        {
            throw Error("end pattern with unmatched parenthesis", start, _pos - start);
        }

        _pos++;
        if (body.Kind == OnigNodeKind.Alternation)
        {
            if (body.Children.Count > 2)
            {
                throw Error("invalid conditional pattern", start, _pos - start);
            }

            node.Children.Add(body.Children[0]);
            node.Children.Add(body.Children[1]);
        }
        else
        {
            node.Children.Add(body);
        }
        node.Length = _pos - start;
        return node;
    }

    private bool TryConditionReference(out string reference)
    {
        reference = "";
        var p = _pos;
        char close;
        if (Peek() is '<' or '\'')
        {
            close = Peek() == '<' ? '>' : '\'';
            var end = _pattern.IndexOf(close, p + 1);
            if (end < 0 || end + 1 >= _pattern.Length || _pattern[end + 1] != ')')
            {
                return false;
            }

            reference = _pattern.Substring(p + 1, end - p - 1);
            _pos = end + 2;
            return true;
        }

        while (p < _pattern.Length && (char.IsAsciiDigit(_pattern[p]) || (p == _pos && _pattern[p] is '+' or '-')))
        {
            p++;
        }

        if (p > _pos && p < _pattern.Length && _pattern[p] == ')' && char.IsAsciiDigit(_pattern[p - 1]))
        {
            reference = _pattern.Substring(_pos, p - _pos);
            _pos = p + 1;
            return true;
        }
        return false;
    }

    private OnigNode? ParseEscape(OnigInlineOptions options)
    {
        var start = _pos++;
        if (AtEnd)
        {
            throw Error("end pattern at escape", start);
        }

        var c = _pattern[_pos++];
        switch (c)
        {
            case 'w' or 'W' or 's' or 'S' or 'd' or 'D' or 'h' or 'H':
                {
                    var node = SetNode(TypeSet(c, options), start);
                    node.Negated = char.IsUpper(c);
                    return node;
                }
            case 'p' or 'P' when Peek() == '{':
                {
                    var negated = (c == 'P') != (Peek(1) == '^');
                    var node = SetNode(ParseProperty(c == 'P', start, options), start);
                    node.Negated = negated;
                    return node;
                }
            case 'b':
                return Anchor((options & OnigInlineOptions.AsciiWord) != 0 ? OnigAnchor.AsciiWordBoundary : OnigAnchor.WordBoundary, start);
            case 'B':
                return Anchor((options & OnigInlineOptions.AsciiWord) != 0 ? OnigAnchor.AsciiNotWordBoundary : OnigAnchor.NotWordBoundary, start);
            case 'A':
                return Anchor(OnigAnchor.BufferStart, start);
            case 'z':
                return Anchor(OnigAnchor.BufferEnd, start);
            case 'Z':
                return Anchor(OnigAnchor.SemiEnd, start);
            case 'G':
                return Anchor(OnigAnchor.SearchStart, start);
            case 'K':
                return new OnigNode(OnigNodeKind.Keep, start, 2);
            case 'N':
                return new OnigNode(OnigNodeKind.AnyChar, start, 2);
            case 'O':
                return new OnigNode(OnigNodeKind.AnyChar, start, 2) { Flag = true };
            case 'R':
                return GeneralNewline(start);
            case 'X':
                return TextSegment(start);
            case 'y':
                return Anchor(OnigAnchor.TextSegmentBoundary, start);
            case 'Y':
                return Anchor(OnigAnchor.NotTextSegmentBoundary, start);
            case 'k':
                if (Peek() is '<' or '\'')
                {
                    var node = new OnigNode(OnigNodeKind.Backref, start, 0) { IgnoreCase = (options & OnigInlineOptions.IgnoreCase) != 0 };
                    var reference = ReadReference(start);
                    Reference(node, reference, start, isCall: false);
                    node.Length = _pos - start;
                    return node;
                }
                return CharNode('k', start, options);
            case 'g':
                if (Peek() is '<' or '\'')
                {
                    var node = new OnigNode(OnigNodeKind.Call, start, 0);
                    var reference = ReadReference(start);
                    Reference(node, reference, start, isCall: true);
                    node.Length = _pos - start;
                    return node;
                }
                return CharNode('g', start, options);
            case >= '1' and <= '9':
                {
                    _pos--;
                    var numStart = _pos;
                    while (!AtEnd && char.IsAsciiDigit(Peek()))
                    {
                        _pos++;
                    }

                    if (int.TryParse(_pattern.AsSpan(numStart, _pos - numStart), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                        && (n <= _groupCount || n <= 9))
                    {
                        var node = new OnigNode(OnigNodeKind.Backref, start, _pos - start)
                        {
                            Groups = [n],
                            IgnoreCase = (options & OnigInlineOptions.IgnoreCase) != 0,
                        };
                        _references.Add(node);
                        return node;
                    }

                    _pos = numStart;
                    if (Peek() is >= '8')
                    {
                        _pos++;
                        return CharNode(_pattern[_pos - 1], start, options);
                    }

                    return CharNode(AssembleBytes(ReadOctal(3) & 0xFF, start, inClass: false), start, options);
                }
            default:
                _pos = start;
                return CharNode(ReadEscapedChar(start, inClass: false), start, options);
        }
    }

    private OnigNode GeneralNewline(int start)
    {
        // (?>\r\n|[\n\v\f\r\x85\u2028\u2029])
        var builder = new OnigCharSet.Builder();
        builder.Add(0x0A, 0x0D);
        builder.Add(0x85);
        builder.Add(0x2028, 0x2029);
        var crlf = new OnigNode(OnigNodeKind.Sequence, start, 2);
        crlf.Children.Add(new OnigNode(OnigNodeKind.Char, start, 2) { Value = '\r' });
        crlf.Children.Add(new OnigNode(OnigNodeKind.Char, start, 2) { Value = '\n' });
        var alt = new OnigNode(OnigNodeKind.Alternation, start, 2);
        alt.Children.Add(crlf);
        alt.Children.Add(SetNode(builder.Build(), start));
        var atomic = new OnigNode(OnigNodeKind.Atomic, start, 2);
        atomic.Children.Add(alt);
        return atomic;
    }

    // \X is (?>\O(?:\Y\O)*): an extended grapheme cluster, from wherever it starts.
    private OnigNode TextSegment(int start)
    {
        var step = new OnigNode(OnigNodeKind.Sequence, start, 2);
        step.Children.Add(Anchor(OnigAnchor.NotTextSegmentBoundary, start));
        step.Children.Add(new OnigNode(OnigNodeKind.AnyChar, start, 2) { Flag = true });
        var rest = new OnigNode(OnigNodeKind.Repeat, start, 2) { Min = 0, Max = -1, Mode = OnigRepeatMode.Greedy };
        rest.Children.Add(step);
        var seq = new OnigNode(OnigNodeKind.Sequence, start, 2);
        seq.Children.Add(new OnigNode(OnigNodeKind.AnyChar, start, 2) { Flag = true });
        seq.Children.Add(rest);
        var atomic = new OnigNode(OnigNodeKind.Atomic, start, 2);
        atomic.Children.Add(seq);
        return atomic;
    }

    private string ReadReference(int start)
    {
        var close = Peek() == '<' ? '>' : '\'';
        _pos++;
        var end = _pattern.IndexOf(close, _pos);
        if (end < 0 || end == _pos)
        {
            throw Error("invalid backref or call", start, _pattern.Length - start);
        }

        var reference = _pattern.Substring(_pos, end - _pos);
        _pos = end + 1;
        return reference;
    }

    private void Reference(OnigNode node, string reference, int start, bool isCall)
    {
        // A trailing +level or -level after a backreference's name or number selects a recursion level.
        var levelAt = !isCall && reference.Length > 1 ? reference.IndexOfAny(['+', '-'], 1) : -1;
        if (levelAt > 0 && levelAt + 1 < reference.Length && reference.AsSpan(levelAt + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            throw Unsupported("backreferences with a recursion level are not supported", start, _pos - start);
        }

        if (reference.Length > 0 && (char.IsAsciiDigit(reference[0]) || reference[0] is '+' or '-'))
        {
            if (!int.TryParse(reference, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                throw Error("invalid backref number", start, _pos - start);
            }

            if (reference[0] == '-')
            {
                n = _groupCount + 1 + n;
            }
            else if (reference[0] == '+')
            {
                n = _groupCount + n;
            }

            if (n < 0 || (n == 0 && !isCall))
            {
                throw Error("invalid backref number", start, _pos - start);
            }

            node.Groups = [n];
            node.Value = n;
        }
        else
        {
            node.Name = reference;
            node.Value = -1;
        }
        _references.Add(node);
    }

    private void Resolve()
    {
        foreach (var node in _references)
        {
            if (node.Name is not null)
            {
                var groups = new List<int>();
                for (var g = 1; g < _captures.Count; g++)
                {
                    if (_captures[g].Name == node.Name)
                    {
                        groups.Add(g);
                    }
                }

                if (groups.Count == 0)
                {
                    throw Error("undefined name reference", node.Offset, node.Length);
                }

                if (node.Kind == OnigNodeKind.Call && groups.Count > 1)
                {
                    throw Error("multiplex defined name call", node.Offset, node.Length);
                }

                node.Groups = groups.ToArray();
                node.Value = groups[^1];
            }
            else
            {
                foreach (var g in node.Groups!)
                {
                    if (g > _groupCount)
                    {
                        throw Error("invalid backref number", node.Offset, node.Length);
                    }
                }
            }
        }
    }

    private OnigCharSet TypeSet(char c, OnigInlineOptions options)
    {
        var lower = char.ToLowerInvariant(c);
        OnigCharSet set;
        switch (lower)
        {
            case 'w':
                set = Ascii(OnigUnicode.Property(OnigUnicode.Word), options, OnigInlineOptions.AsciiWord);
                break;
            case 's':
                set = Ascii(OnigUnicode.Property(OnigUnicode.Space), options, OnigInlineOptions.AsciiSpace);
                break;
            case 'd':
                set = Ascii(OnigUnicode.Property(OnigUnicode.Digit), options, OnigInlineOptions.AsciiDigit);
                break;
            default:
                set = OnigUnicode.Property(OnigUnicode.XDigit);
                break;
        }
        return char.IsUpper(c) ? set.Negate() : set;
    }

    private static OnigCharSet Ascii(OnigCharSet set, OnigInlineOptions options, OnigInlineOptions flag)
        => (options & flag) != 0 ? set.Intersect(OnigUnicode.Property(OnigUnicode.Ascii)) : set;

    private OnigCharSet ParseProperty(bool negative, int start, OnigInlineOptions options)
    {
        if (Peek() != '{')
        {
            throw Error("invalid character property name", start, _pos - start);
        }

        _pos++;
        if (Peek() == '^')
        {
            negative = !negative;
            _pos++;
        }

        var nameStart = _pos;
        var end = _pattern.IndexOf('}', _pos);
        if (end < 0)
        {
            throw Error("invalid character property name", start, _pattern.Length - start);
        }

        var name = _pattern.AsSpan(nameStart, end - nameStart);
        _pos = end + 1;
        if (!OnigUnicode.TryGetProperty(name, out var property))
        {
            throw Error("invalid character property name", start, _pos - start);
        }

        var set = OnigUnicode.Property(property);
        if (property <= OnigUnicode.Ascii)
        {
            set = PosixAscii(set, property, options);
        }

        return negative ? set.Negate() : set;
    }

    // Under (?W), (?D), (?S) and (?P) the corresponding POSIX classes are restricted to ASCII.
    private static OnigCharSet PosixAscii(OnigCharSet set, int property, OnigInlineOptions options)
    {
        var flag = property switch
        {
            OnigUnicode.Word => OnigInlineOptions.AsciiWord,
            OnigUnicode.Digit => OnigInlineOptions.AsciiDigit,
            OnigUnicode.Space => OnigInlineOptions.AsciiSpace,
            _ => OnigInlineOptions.AsciiPosix,
        };
        return property != OnigUnicode.Ascii && property != OnigUnicode.Newline ? Ascii(set, options, flag) : set;
    }

    private int ReadOctal(int maxDigits)
    {
        var value = 0;
        var digits = 0;
        while (digits < maxDigits && !AtEnd && Peek() is >= '0' and <= '7')
        {
            value = value * 8 + (Peek() - '0');
            _pos++;
            digits++;
        }
        return value;
    }

    // Reads a single-character escape at _pos (the backslash), outside or inside a class.
    private int ReadEscapedChar(int start, bool inClass)
    {
        _pos++;
        var c = _pattern[_pos++];
        switch (c)
        {
            case 't': return '\t';
            case 'n': return '\n';
            case 'r': return '\r';
            case 'f': return '\f';
            case 'v': return '\v';
            case 'a': return '\a';
            case 'e': return 0x1B;
            case 'b' when inClass: return '\b';
            case '0':
                return AssembleBytes(ReadOctal(2), start, inClass);
            case >= '1' and <= '7' when inClass:
                _pos--;
                return AssembleBytes(ReadOctal(3), start, inClass);
            case 'x':
                if (Peek() == '{')
                {
                    var end = _pattern.IndexOf('}', _pos);
                    if (end < 0)
                    {
                        throw Error("invalid code point value", start, _pattern.Length - start);
                    }

                    // \x{H H ...} is a sequence of code points: the first is returned, the others wait in _pending.
                    var values = _pattern.Substring(_pos + 1, end - _pos - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (values.Length == 0)
                    {
                        throw Error("invalid code point value", start, end + 1 - start);
                    }

                    var codes = new int[values.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        if (values[i].Length > 8 || !int.TryParse(values[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out codes[i])
                            || (uint)codes[i] > OnigCharSet.MaxCodePoint)
                        {
                            throw Error("invalid code point value", start, end + 1 - start);
                        }
                    }

                    if (!inClass && codes.Any(c => c is >= 0xD800 and <= 0xDFFF))
                    {
                        throw Error("invalid code point value", start, end + 1 - start);
                    }

                    _pos = end + 1;
                    for (var i = 1; i < codes.Length; i++)
                    {
                        _pending.Enqueue(codes[i]);
                    }
                    return codes[0];
                }
                else
                {
                    var value = 0;
                    var digits = 0;
                    while (digits < 2 && !AtEnd && char.IsAsciiHexDigit(Peek()))
                    {
                        value = value * 16 + HexValue(Peek());
                        _pos++;
                        digits++;
                    }

                    if (digits == 0)
                    {
                        throw Error("invalid code point value", start, _pos - start);
                    }

                    return AssembleBytes(value, start, inClass);
                }
            case 'u':
                {
                    if (!HasAhead(4))
                    {
                        throw Error("too short digits", start, _pos - start);
                    }

                    var value = 0;
                    for (var i = 0; i < 4; i++)
                    {
                        if (!char.IsAsciiHexDigit(Peek()))
                        {
                            throw Error("too short digits", start, _pos - start);
                        }

                        value = value * 16 + HexValue(Peek());
                        _pos++;
                    }

                    if (!inClass && value is >= 0xD800 and <= 0xDFFF)
                    {
                        throw Error("invalid code point value", start, _pos - start);
                    }

                    return value;
                }
            case 'o' when Peek() == '{':
                {
                    var end = _pattern.IndexOf('}', _pos);
                    if (end < 0)
                    {
                        throw Error("invalid code point value", start, _pattern.Length - start);
                    }

                    var value = 0L;
                    for (var i = _pos + 1; i < end; i++)
                    {
                        if (_pattern[i] is < '0' or > '7')
                        {
                            throw Error("invalid code point value", start, end + 1 - start);
                        }

                        value = value * 8 + (_pattern[i] - '0');
                        if (value > OnigCharSet.MaxCodePoint)
                        {
                            throw Error("invalid code point value", start, end + 1 - start);
                        }
                    }
                    _pos = end + 1;
                    return (int)value;
                }
            case 'c':
                return Control(start);
            case 'C' when Peek() == '-':
                _pos++;
                return Control(start);
            case 'M' when Peek() == '-':
                {
                    _pos++;
                    var inner = Peek() == '\\' ? ReadEscapedChar(start, inClass) : ReadCodePoint();
                    return (inner & 0xFF) | 0x80;
                }
            default:
                if (char.IsHighSurrogate(c) && !AtEnd && char.IsLowSurrogate(Peek()))
                {
                    return char.ConvertToUtf32(c, _pattern[_pos++]);
                }

                return c;
        }
    }

    // \xHH and octal escapes are encoded bytes: in UTF-16LE two make a unit (low byte first) and a high surrogate unit
    // needs two more; an incomplete character is an error, as in Oniguruma.
    private int AssembleBytes(int first, int start, bool inClass)
    {
        var low = NextByte(start, inClass);
        var unit = first | (low << 8);
        if (unit is < 0xD800 or > 0xDBFF)
        {
            return unit;
        }

        var second = NextByte(start, inClass) | (NextByte(start, inClass) << 8);
        return 0x10000 + ((unit & 0x3FF) << 10) + (second & 0x3FF);
    }

    private int NextByte(int start, bool inClass)
    {
        if (Peek() == '\\')
        {
            if (Peek(1) == 'x' && Peek(2) != '{' && char.IsAsciiHexDigit(Peek(2)))
            {
                _pos += 2;
                var value = 0;
                for (var digits = 0; digits < 2 && !AtEnd && char.IsAsciiHexDigit(Peek()); digits++)
                {
                    value = value * 16 + HexValue(Peek());
                    _pos++;
                }
                return value;
            }

            if (Peek(1) == '0' || (inClass && Peek(1) is >= '1' and <= '7'))
            {
                _pos++;
                return ReadOctal(3) & 0xFF;
            }
        }

        throw Error("too short multibyte code string", start, Math.Max(1, _pos - start));
    }

    private int Control(int start)
    {
        if (AtEnd)
        {
            throw Error("end pattern at control", start, _pos - start);
        }

        var inner = Peek() == '\\' ? ReadEscapedChar(start, inClass: false) : ReadCodePoint();
        return inner == '?' ? 0x7F : inner & 0x9F;
    }

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    // Under ignore-case a class holding ß also matches ss (Oniguruma's i_apply_case_fold alternatives).
    private OnigNode WithMultiFolds(OnigNode node, int start)
    {
        OnigNode? alternation = null;
        foreach (var (fold, members) in OnigUnicode.MultiFolds)
        {
            if (!members.Any(node.Set!.Contains))
            {
                continue;
            }

            if (alternation is null)
            {
                alternation = new OnigNode(OnigNodeKind.Alternation, start, _pos - start);
                alternation.Children.Add(node);
            }

            var spelled = new OnigNode(OnigNodeKind.Sequence, start, _pos - start);
            foreach (var cp in fold)
            {
                spelled.Children.Add(new OnigNode(OnigNodeKind.Char, start, _pos - start) { Value = cp, IgnoreCase = true });
            }
            alternation.Children.Add(spelled);
        }
        return alternation ?? node;
    }

    private OnigCharSet ParseClass(OnigInlineOptions options, out bool negated)
    {
        negated = _pattern.Length > _pos + 1 && _pattern[_pos + 1] == '^';
        return ParseClass(options);
    }

    // A bracket class at '[': nested classes union, && intersects (lowest precedence above ^), ^ negates last. As in
    // Oniguruma's prs_cc, ignore-case folds the outermost class only, before its negation, and a nested negated class
    // is merged as ranges by not_code_range_buf, which yields every code point when the class holds U+0000.
    private OnigCharSet ParseClass(OnigInlineOptions options, bool nested = false)
    {
        var start = _pos++;
        var negate = false;
        if (Peek() == '^')
        {
            negate = true;
            _pos++;
        }

        OnigCharSet? intersection = null;
        var builder = new OnigCharSet.Builder();
        var first = true;
        var afterClass = false;
        while (true)
        {
            if (AtEnd)
            {
                throw Error("premature end of char-class", start, _pos - start);
            }

            var c = Peek();
            if (c == ']' && !first)
            {
                _pos++;
                break;
            }

            if (c == '&' && Peek(1) == '&')
            {
                _pos += 2;
                var left = builder.Build();
                intersection = intersection is null ? left : intersection.Intersect(left);
                builder = new OnigCharSet.Builder();
                first = false;
                continue;
            }

            first = false;
            if (c == '-' && afterClass && Peek(1) != ']' && !(Peek(1) == '&' && Peek(2) == '&'))
            {
                // A class (\w, [:alpha:], [..]) cannot start a range.
                throw Error("unmatched range specifier in char-class", _pos, 1);
            }

            afterClass = false;
            if (c == '[')
            {
                if (Peek(1) == ':' && TryPosixBracket(builder, options))
                {
                    afterClass = true;
                    continue;
                }

                builder.Add(ParseClass(options, nested: true));
                afterClass = true;
                continue;
            }

            var itemStart = _pos;
            if (!TryClassAtom(options, builder, out var low))
            {
                afterClass = true;
                continue;
            }

            // A range: x-y, where y is a single character; otherwise '-' is literal.
            if (Peek() == '-' && Peek(1) != ']' && !(Peek(1) == '&' && Peek(2) == '&') && HasAhead(2))
            {
                var saved = _pos;
                _pos++;
                if (Peek() == '[')
                {
                    _pos = saved;
                    builder.Add(low);
                    continue;
                }

                var highBuilder = new OnigCharSet.Builder();
                if (TryClassAtom(options, highBuilder, out var high))
                {
                    if (high < low)
                    {
                        throw Error("empty range in char class", itemStart, _pos - itemStart);
                    }

                    builder.Add(low, high);
                    continue;
                }

                // A class cannot end a range either.
                throw Error("char-class value at end of range", itemStart, _pos - itemStart);
            }

            builder.Add(low);
        }

        var set = builder.Build();
        if (intersection is not null)
        {
            set = intersection.Intersect(set);
        }

        if (nested)
        {
            return !negate ? set : set.IsEmpty || set.Ranges[0] == 0 ? OnigCharSet.All : set.Negate();
        }

        if ((options & OnigInlineOptions.IgnoreCase) != 0)
        {
            set = OnigUnicode.CaseClose(set);
        }

        return negate ? set.Negate() : set;
    }

    // Reads one class member: returns true with a single code point, or false after adding a class (\w, \p{..}).
    private bool TryClassAtom(OnigInlineOptions options, OnigCharSet.Builder builder, out int cp)
    {
        var read = TryClassAtomCore(options, builder, out cp);
        while (read && _pending.Count > 0)
        {
            // The earlier code points of a \x{H H ...} sequence are members; the last may start a range.
            builder.Add(cp);
            cp = _pending.Dequeue();
        }

        return read;
    }

    private bool TryClassAtomCore(OnigInlineOptions options, OnigCharSet.Builder builder, out int cp)
    {
        cp = -1;
        var start = _pos;
        if (Peek() != '\\')
        {
            cp = ReadCodePoint();
            return true;
        }

        if (!HasAhead(2))
        {
            throw Error("end pattern at escape", start, 1);
        }

        var c = Peek(1);
        switch (c)
        {
            case 'w' or 'W' or 's' or 'S' or 'd' or 'D' or 'h' or 'H':
                _pos += 2;
                builder.Add(TypeSet(c, options));
                return false;
            case 'p' or 'P' when Peek(2) == '{':
                _pos += 2;
                builder.Add(ParseProperty(c == 'P', start, options));
                return false;
            case 'R' or 'X' or 'N' or 'O' or 'k' or 'g' or 'K' or 'G' or 'A' or 'z' or 'Z' or 'y' or 'Y':
                // Oniguruma reads these as the letter inside a class.
                _pos += 2;
                cp = c;
                return true;
            default:
                cp = ReadEscapedChar(start, inClass: true);
                return true;
        }
    }

    private bool TryPosixBracket(OnigCharSet.Builder builder, OnigInlineOptions options)
    {
        var p = _pos + 2;
        var negate = false;
        if (p < _pattern.Length && _pattern[p] == '^')
        {
            negate = true;
            p++;
        }

        var nameStart = p;
        while (p < _pattern.Length && char.IsAsciiLetter(_pattern[p]))
        {
            p++;
        }

        if (p + 1 >= _pattern.Length || _pattern[p] != ':' || _pattern[p + 1] != ']' || p == nameStart)
        {
            return false;
        }

        var property = _pattern.AsSpan(nameStart, p - nameStart) switch
        {
            "alnum" => OnigUnicode.Alnum,
            "alpha" => OnigUnicode.Alpha,
            "ascii" => OnigUnicode.Ascii,
            "blank" => OnigUnicode.Blank,
            "cntrl" => OnigUnicode.Cntrl,
            "digit" => OnigUnicode.Digit,
            "graph" => OnigUnicode.Graph,
            "lower" => OnigUnicode.Lower,
            "print" => OnigUnicode.Print,
            "punct" => OnigUnicode.Punct,
            "space" => OnigUnicode.Space,
            "upper" => OnigUnicode.Upper,
            "xdigit" => OnigUnicode.XDigit,
            "word" => OnigUnicode.Word,
            _ => throw Error("invalid POSIX bracket type", _pos, p + 2 - _pos),
        };
        var set = PosixAscii(OnigUnicode.Property(property), property, options);
        builder.Add(negate ? set.Negate() : set);
        _pos = p + 2;
        return true;
    }
}
