using System.Text.RegularExpressions;

namespace GregsEx.Benchmarks.Acceleration.Corpus;

/// <summary>Source-generated BCL variants, one per distinct pattern of the curated comparison cases.</summary>
public static partial class GeneratedPatterns
{
    public static Regex Get(string pattern) => pattern switch
    {
        "Sherlock Holmes" => SherlockHolmes(),
        "zqxjkv" => Zqxjkv(),
        "the" => The(),
        "Sherlock|Watson|Holmes|Moriarty" => Names(),
        "Holmes$" => EndHolmes(),
        "^[a-z ]+$" => FullLine(),
        "([a-z]+)=([a-z0-9]+)" => KeyValue(),
        "([0-9]{4})-([0-9]{2})-([0-9]{2})" => Date(),
        "[a-zA-Z]+ing" => IngMixed(),
        "\\p{L}+" => Letters(),
        "(a|aa)+$" => OverlapAlt(),
        ".*.*=.*" => DotStars(),
        _ => throw new ArgumentOutOfRangeException(nameof(pattern), pattern, "No source-generated variant is pinned for this pattern."),
    };

    [GeneratedRegex("Sherlock Holmes")] private static partial Regex SherlockHolmes();
    [GeneratedRegex("zqxjkv")] private static partial Regex Zqxjkv();
    [GeneratedRegex("the")] private static partial Regex The();
    [GeneratedRegex("Sherlock|Watson|Holmes|Moriarty")] private static partial Regex Names();
    [GeneratedRegex("Holmes$")] private static partial Regex EndHolmes();
    [GeneratedRegex("^[a-z ]+$")] private static partial Regex FullLine();
    [GeneratedRegex("([a-z]+)=([a-z0-9]+)")] private static partial Regex KeyValue();
    [GeneratedRegex("([0-9]{4})-([0-9]{2})-([0-9]{2})")] private static partial Regex Date();
    [GeneratedRegex("[a-zA-Z]+ing")] private static partial Regex IngMixed();
    [GeneratedRegex("\\p{L}+")] private static partial Regex Letters();
    [GeneratedRegex("(a|aa)+$", RegexOptions.None, 20000)] private static partial Regex OverlapAlt();
    [GeneratedRegex(".*.*=.*", RegexOptions.None, 20000)] private static partial Regex DotStars();
}
