using Xunit;

namespace GregsEx.Framework.Tests;

public sealed class OnigurumaTests
{
    [Theory]
    [InlineData(@"(?<word>a)\g<word>", "aa")]
    [InlineData(@"(?<word>a)\g<word>\g<word>", "aaa")]
    [InlineData(@"(?<word>a(?:\g<word>)?b)", "aaabbb")]
    public void Subexpression_calls_compile_and_match_on_framework(string source, string input)
    {
        var pattern = OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate);
        var status = pattern.CreateWorkspace().Find(input.AsSpan(), 0, out var match);

        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexMatchRange(0, input.Length), match);
    }
}
