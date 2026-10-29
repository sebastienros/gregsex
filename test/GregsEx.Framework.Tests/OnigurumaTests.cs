using Xunit;

namespace GregsEx.Framework.Tests;

public sealed class OnigurumaTests
{
    [Theory]
    [InlineData(@"(?<word>a)\g<word>", "aa")]
    [InlineData(@"(?<word>a)\g<word>\g<word>", "aaa")]
    [InlineData(@"(?<word>a(?:\g<word>)?b)", "aaabbb")]
    // Each tail call hands its group's follow one level down per pass, so the follow sets reach no fixpoint within
    // MaxFollowPasses and the compiler keeps its anything-follows pass.
    [InlineData(@"(?<a>x)1(?<b>y\g<a>)2(?<c>y\g<b>)3(?<d>y\g<c>)4(?<e>y\g<d>)5(?<f>y\g<e>)6\g<f>~", "x1yx2yyx3yyyx4yyyyx5yyyyyx6yyyyyx~")]
    public void Subexpression_calls_compile_and_match_on_framework(string source, string input)
    {
        var pattern = OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate);
        var status = pattern.CreateWorkspace().Find(input.AsSpan(), 0, out var match);

        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new RegexMatchRange(0, input.Length), match);
    }
}
