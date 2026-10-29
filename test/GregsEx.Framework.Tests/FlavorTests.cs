using System.Text;
using Xunit;

namespace GregsEx.Framework.Tests;

// One compile-and-find per flavor through the netstandard2.0 asset, on the .NET Framework runtime itself: the net10.0
// host bound to that asset accepts collection behavior (such as updating a dictionary while enumerating it) that
// Framework rejects.
public sealed class FlavorTests
{
    [Theory]
    [InlineData("RegularCore", "(a+)(b)?", "xaaab", 1, 4)]
    [InlineData("EcmaScript", "(?<=a)b+", "abbb", 1, 3)]
    [InlineData("EcmaScriptUnicodeSets", @"[\p{L}--[a-z]]+", "abCD", 2, 2)]
    [InlineData("Xsd", "[a-z-[aeiou]]+", "bcdf", 0, 4)]
    [InlineData("XPath", "([a-z])([0-9])", "x a1", 2, 2)]
    [InlineData("Pcre2Utf16", "^(a(?1)?z)$", "aaazzz", 0, 6)]
    public void Patterns_compile_and_match_on_framework(string flavor, string source, string input, int index, int length)
    {
        var pattern = flavor switch
        {
            "RegularCore" => RegexPattern.Parse(source, RegexProfiles.RegularCoreUnicodeScalars),
            "EcmaScript" => RegexPattern.Parse(source, RegexProfiles.EcmaScript),
            "EcmaScriptUnicodeSets" => RegexPattern.Parse(source, RegexProfiles.EcmaScriptUnicodeSets),
            "Xsd" => RegexPattern.Parse(source, RegexProfiles.Xsd),
            "XPath" => RegexPattern.Parse(source, RegexProfiles.XPath),
            "Pcre2Utf16" => RegexPattern.Parse(source, RegexProfiles.Pcre2Utf16),
            _ => throw new ArgumentOutOfRangeException(nameof(flavor), flavor, null),
        };
        using var workspace = pattern.CreateWorkspace();
        var status = workspace.Find(input.AsSpan(), 0, out var match);

        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexMatchRange(index, length), match);
    }

    // POSIX patterns and subjects are C-locale bytes.
    [Theory]
    [InlineData(PosixDialect.Basic, @"a\{2\}", "baa", 1, 2)]
    [InlineData(PosixDialect.Extended, "(ab|a)(bc|c)", "abc", 0, 3)]
    public void Posix_patterns_compile_and_match_on_framework(PosixDialect dialect, string source, string input, int index, int length)
    {
        var pattern = PosixFunctions.Parse(Encoding.ASCII.GetBytes(source), new PosixProfile(dialect), new RegexCompileOptions(RegexInputKind.Bytes));
        using var workspace = pattern.CreateWorkspace();
        var status = workspace.Find(Encoding.ASCII.GetBytes(input), 0, out var match);

        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexMatchRange(index, length), match);
    }
}
