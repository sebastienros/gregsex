using GregsEx.Contracts;

namespace GregsEx.Flavors.XPath.Host;

// Typed host operations with owned outputs and UTF-16 subjects only.
internal static partial class XPathHostFunctions
{
    internal static bool Matches(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        var context = HostContext.Create(pattern, flags, compile, match, null, unicodeVersion);
        return context.Search(input ?? "", 0, SearchOperation.Boolean, out _);
    }

    internal static string Replace(string? input, string pattern, string replacement, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var context = HostContext.Create(pattern, flags, compile, match, host, unicodeVersion);
        context.NonEmptyPattern();
        ValidateReplacement(replacement, ref context);
        input ??= "";
        context.Validate(input);
        var output = new HostBuffer<char>(2);
        var cursor = 0;
        while (cursor < input.Length && context.Search(input, cursor, SearchOperation.Captures, out var range))
        {
            context.Result();
            Append(input.AsSpan(cursor, range.Index - cursor), ref output, ref context);
            if (context.Quote)
            {
                Append(replacement, ref output, ref context);
            }
            else
            {
                ExpandReplacement(input, replacement, ref output, ref context);
            }

            cursor = checked(range.Index + range.Length);
        }
        Append(input.AsSpan(cursor), ref output, ref context);
        context.Reserve(32L + 2L * output.Count);
        context.Charge(output.Count);
        return new string(output.Items);
    }

    internal static IReadOnlyList<string> Tokenize(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        var context = HostContext.Create(pattern, flags, compile, match, host, unicodeVersion);
        context.NonEmptyPattern();
        input ??= "";
        context.Validate(input);
        var tokens = new HostBuffer<string>(8);
        var cursor = 0;
        if (input.Length != 0)
        {
            while (cursor < input.Length && context.Search(input, cursor, SearchOperation.Range, out var range))
            {
                Token(input.AsSpan(cursor, range.Index - cursor), ref tokens, ref context);
                cursor = checked(range.Index + range.Length);
            }
            Token(input.AsSpan(cursor), ref tokens, ref context);
        }
        return FinishTokens(ref tokens, ref context);
    }

    internal static IReadOnlyList<string> Tokenize(string? input,
        RegexMatchOptions match = default, XPathHostLimits? host = null)
    {
        var context = new HostContext(null, new(), match, host, 0);
        input ??= "";
        context.Validate(input);
        var tokens = new HostBuffer<string>(8);
        var start = -1;
        for (var i = 0; i < input.Length; i++)
        {
            context.Charge(1, i);
            if (input[i] is '\t' or '\r' or '\n' or ' ')
            {
                if (start >= 0)
                { Token(input.AsSpan(start, i - start), ref tokens, ref context); start = -1; }
            }
            else if (start < 0)
            {
                start = i;
            }
        }
        if (start >= 0)
        {
            Token(input.AsSpan(start), ref tokens, ref context);
        }

        return FinishTokens(ref tokens, ref context);
    }

    private static void Append(ReadOnlySpan<char> text, ref HostBuffer<char> output, ref HostContext context)
    { context.Units(text.Length); output.Append(text, ref context); }
    private static void Token(ReadOnlySpan<char> text, ref HostBuffer<string> tokens, ref HostContext context)
    {
        context.Result();
        context.Units(text.Length);
        context.Charge(text.Length);
        context.Reserve(32L + 2L * text.Length);
        tokens.Add(new string(text), ref context);
    }
    private static System.Collections.ObjectModel.ReadOnlyCollection<string> FinishTokens(ref HostBuffer<string> tokens, ref HostContext context)
    {
        context.Reserve(64L + 8L * tokens.Count);
        context.Charge(tokens.Count);
        return Array.AsReadOnly(tokens.Items.ToArray());
    }
    private static void ValidateReplacement(string replacement, ref HostContext context)
    {
        // Domain and template admission occur before subject execution. Charge
        // all code units even for literal q replacements or later misses.
        context.Validate(replacement);
        if (context.Quote)
        {
            return;
        }

        for (var i = 0; i < replacement.Length; i++)
        {
            context.Charge(1, i);
            var c = replacement[i];
            if (c is not ('$' or '\\'))
            {
                continue;
            }

            if (i + 1 == replacement.Length || (c == '$' ? replacement[i + 1] is < '0' or > '9' :
                replacement[i + 1] is not ('$' or '\\')))
            {
                throw new XPathRegexException(new(new(RegexDiagnosticCode.InvalidSyntax, i, 1,
                    RegexOffsetUnit.Utf16CodeUnits), XmlRegexDiagnosticLocation.Replacement,
                    XPathErrorCode.FORX0004, "XP-013"));
            }

            if (c == '\\')
            { context.Charge(1, ++i); }
        }
    }
    private static void ExpandReplacement(string input, string replacement,
        ref HostBuffer<char> output, ref HostContext context)
    {
        var i = 0;
        while (i < replacement.Length)
        {
            context.Charge(1, i);
            if (replacement[i] == '\\')
            { Append(replacement.AsSpan(i + 1, 1), ref output, ref context); i += 2; continue; }
            if (replacement[i] != '$')
            {
                var start = i++;
                while (i < replacement.Length && replacement[i] is not ('$' or '\\'))
                { context.Charge(1, i); i++; }
                Append(replacement.AsSpan(start, i - start), ref output, ref context);
                continue;
            }
            int begin = ++i, group = 0, selected = begin, selectedGroup = 0;
            // Grow the numeric prefix only while useful. Arbitrarily many
            // leading zeros are valid; no BigInteger or overflow-prone parse.
            while (i < replacement.Length && replacement[i] is >= '0' and <= '9')
            {
                context.Charge(1, i);
                var digit = replacement[i] - '0';
                if (group <= Math.Max(9, context.Groups.Length - 1))
                {
                    var candidate = 10L * group + digit;
                    group = candidate > Math.Max(9, context.Groups.Length - 1) ?
                        Math.Max(9, context.Groups.Length - 1) + 1 : (int)candidate;
                    if (group <= Math.Max(9, context.Groups.Length - 1))
                    { selected = i + 1; selectedGroup = group; }
                }
                i++;
            }
            if (selectedGroup < context.Groups.Length && context.Groups[selectedGroup] is { Participated: true } capture)
            {
                Append(input.AsSpan(capture.Index, capture.Length), ref output, ref context);
            }

            Append(replacement.AsSpan(selected, i - selected), ref output, ref context);
        }
    }
}
