using GregsEx.Flavors.XPath.Host;

namespace GregsEx;

/// <summary>XSD 1.1 regex whole-value membership without schema or facet processing.</summary>
public static class XsdFunctions
{
    /// <summary>Tests the supplied lexical value. Pattern and subject are strict XML UTF-16 strings.
    /// Parsing and the temporary workspace have separately owned, allocating lifetimes.</summary>
    public static bool IsValid(string lexicalValue, string pattern,
        RegexCompileOptions? compile, RegexMatchOptions match)
        => IsValid(lexicalValue, pattern, compile, match, UnicodeVersion.Unicode16_0);

    /// <summary>Tests whole-value membership using the selected Unicode dataset and XML 1.0 Fifth Edition rules.</summary>
    public static bool IsValid(string lexicalValue, string pattern,
        RegexCompileOptions? compile = null, RegexMatchOptions match = default,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        ArgumentNullException.ThrowIfNull(lexicalValue);
        ArgumentNullException.ThrowIfNull(pattern);
        if (compile is { InputKind: not RegexInputKind.Utf16 })
        {
            throw new RegexParseException(new(RegexDiagnosticCode.UnsupportedRepresentation, 0,
                pattern.Length, RegexOffsetUnit.Utf16CodeUnits));
        }

        var parsed = RegexPattern.Parse(pattern, new XsdProfile(unicodeVersion), compile);
        var capacity = parsed.BooleanCapacity;
        var resolved = match.Resolve(parsed.MatchLimits);
        var workspaceBytes = checked(RegexWorkspace.MetadataBytes + capacity.RequiredBytes);
        if (workspaceBytes > resolved.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0, workspaceBytes));
        }

        using var workspace = parsed.CreateWorkspace(new(capacity));
        return workspace.IsMatch(lexicalValue, options: match) switch
        {
            RegexSearchStatus.Match => true,
            RegexSearchStatus.NoMatch => false,
            _ => throw new RegexExecutionException(workspace.LastFailure)
        };
    }
}

/// <summary>Typed F&amp;O 3.1 regex functions on XML UTF-16 strings. Null optional input is empty;
/// owned outputs and cold parsing/workspace costs are separate from borrowed workspace searches.</summary>
public static class XPathFunctions
{
    /// <summary>Tests for a matching substring with F&amp;O flags and errors.</summary>
    public static bool Matches(string? input, string pattern, string flags,
        RegexCompileOptions? compile, RegexMatchOptions match)
        => XPathHostFunctions.Matches(input, pattern, flags, compile, match);
    /// <summary>Tests for a matching substring using the selected Unicode dataset.</summary>
    public static bool Matches(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
        => XPathHostFunctions.Matches(input, pattern, flags, compile, match, unicodeVersion);
    /// <summary>Replaces all nonoverlapping matches using the F&amp;O replacement grammar.</summary>
    public static string Replace(string? input, string pattern, string replacement, string flags,
        RegexCompileOptions? compile, RegexMatchOptions match, XPathHostLimits? host)
        => XPathHostFunctions.Replace(input, pattern, replacement, flags, compile, match, host);
    /// <summary>Replaces matches using the selected Unicode dataset and F&amp;O replacement grammar.</summary>
    public static string Replace(string? input, string pattern, string replacement, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
        => XPathHostFunctions.Replace(input, pattern, replacement, flags, compile, match, host, unicodeVersion);
    /// <summary>Splits at nonempty separators, preserving edge and adjacent empty tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string? input, string pattern, string flags,
        RegexCompileOptions? compile, RegexMatchOptions match, XPathHostLimits? host)
        => XPathHostFunctions.Tokenize(input, pattern, flags, compile, match, host);
    /// <summary>Splits at nonempty separators using the selected Unicode dataset.</summary>
    public static IReadOnlyList<string> Tokenize(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
        => XPathHostFunctions.Tokenize(input, pattern, flags, compile, match, host, unicodeVersion);
    /// <summary>Normalizes XML whitespace and returns nonempty tokens.</summary>
    public static IReadOnlyList<string> Tokenize(string? input,
        RegexMatchOptions match = default, XPathHostLimits? host = null)
        => XPathHostFunctions.Tokenize(input, match, host);
    /// <summary>Returns the immutable input partition and final participating group tree.</summary>
    public static XPathAnalyzeResult AnalyzeString(string? input, string pattern, string flags,
        RegexCompileOptions? compile, RegexMatchOptions match, XPathHostLimits? host)
        => XPathHostFunctions.AnalyzeString(input, pattern, flags, compile, match, host);
    /// <summary>Analyzes matching substrings and participating groups using the selected Unicode dataset.</summary>
    public static XPathAnalyzeResult AnalyzeString(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null,
        UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
        => XPathHostFunctions.AnalyzeString(input, pattern, flags, compile, match, host, unicodeVersion);
}
