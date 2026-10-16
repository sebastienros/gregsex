namespace GregsEx;

/// <summary>Element or text identity in the typed analyze-string tree.</summary>
public enum XPathAnalyzeNodeKind
{
    /// <summary>A matching substring, containing text and groups.</summary>
    Match,
    /// <summary>A nonmatching substring, represented by its owned input range.</summary>
    NonMatch,
    /// <summary>A participating numbered group, containing text and nested groups.</summary>
    Group,
    /// <summary>Text within a match or group.</summary>
    Text
}

/// <summary>A preorder tree node whose range refers to the result's owned input.</summary>
/// <param name="Kind">Element or text identity.</param>
/// <param name="ParentIndex">Earlier ancestor index, or -1 for a top-level segment.</param>
/// <param name="GroupNumber">Positive capture number for Group; zero otherwise.</param>
/// <param name="Range">Native UTF-16 input range, including empty participating groups.</param>
public readonly record struct XPathAnalyzeNode(XPathAnalyzeNodeKind Kind, int ParentIndex,
    int GroupNumber, RegexMatchRange Range);

/// <summary>Owned immutable ordered analyze-string tree. Final participating groups retain their
/// syntactic parent when it contains them; otherwise their nearest participating containing
/// syntactic ancestor (ending at the match) is their parent. Siblings follow input position,
/// including empty groups, and text partitions the original input without duplication.</summary>
public sealed class XPathAnalyzeResult
{
    /// <summary>F&amp;O namespace for the result and all element descendants.</summary>
    public const string Namespace = "http://www.w3.org/2005/xpath-functions";
    /// <summary>Result element local name.</summary>
    public const string LocalName = "analyze-string-result";
    /// <summary>Owned immutable source string; node ranges slice this string.</summary>
    public string Input { get; }
    /// <summary>Read-only preorder tree. Top-level nodes are Match/NonMatch.</summary>
    public IReadOnlyList<XPathAnalyzeNode> Nodes { get; }

    // Host owner validates a complete partition, scalar boundaries, subtree
    // containment/ordering, group participation and one occurrence per group
    // per match, then admits the copy/view bytes BEFORE invoking this constructor.
    // Empty input has no nodes. ParentIndex uses the containing ancestor rule above;
    // unmatched groups are absent, matched-empty groups have zero length.
    internal XPathAnalyzeResult(string input, ReadOnlySpan<XPathAnalyzeNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(input);
        Input = input;
        Nodes = Array.AsReadOnly(nodes.ToArray());
    }
}
