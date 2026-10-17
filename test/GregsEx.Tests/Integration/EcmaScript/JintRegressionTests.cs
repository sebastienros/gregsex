using Xunit;

namespace GregsEx.Tests.Integration.EcmaScript;

// Defects found running every JavaScript RegExp of the Jint engine (and its test262 suite) on this flavor. Each case
// answered wrongly, threw, or exhausted a small frame reservation before the fix it pins.
public sealed class JintRegressionTests
{
    private static RegexSearchStatus Exec(string pattern, EcmaScriptFlags flags, string input, out RegexMatchRange range,
        int frameCapacity = 64, TimeSpan? timeout = null)
    {
        Assert.True(EcmaScriptMatcher.TryParse(pattern, new EcmaScriptProfile(flags), out var matcher, out var diagnostic,
            frameCapacity: frameCapacity), diagnostic.ToString());
        using (matcher)
        {
            var groups = new RegexCapture[16];
            return matcher!.Exec(input, groups, out range, new RegexMatchOptions { Timeout = timeout });
        }
    }

    [Theory]
    // An escaped lead surrogate followed by a raw trail is two lone code points under u/v, never one pair.
    [InlineData("\\uD83D\uDC38", "\U0001F438")]
    [InlineData("x\\uD83D\uDC38", "x\U0001F438")]
    [InlineData("\\uD83D\uDC38|zz", "\U0001F438")]
    // A prefilter hit on the trail half of a pair is not a place a u/v match may start.
    [InlineData("[\uDC38]", "\uD83D\uDC38")]
    [InlineData("\\s*\uDC38", "\uD83D\uDC38")]
    public void LoneSurrogatesStayLoneUnderUnicodeModes(string pattern, string input)
    {
        Assert.Equal(RegexSearchStatus.NoMatch, Exec(pattern, EcmaScriptFlags.Unicode, input, out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Exec(pattern, EcmaScriptFlags.UnicodeSets, input, out _));
    }

    [Fact]
    public void AnUnsatisfiableFixedLengthSequenceFailsInsteadOfThrowing()
    {
        Assert.Equal(RegexSearchStatus.NoMatch, Exec(".{2147483647}11", EcmaScriptFlags.None, "", out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Exec("x{2147483647,2147483648}x", EcmaScriptFlags.None, "1", out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Exec("b{9007199254740991}", EcmaScriptFlags.Unicode, "", out _));
    }

    [Theory]
    [InlineData("a.*a", "xabcabcx", 1, 4)]
    [InlineData("^(\\w*)(\\w)$", "abc", 0, 3)]
    [InlineData("(\\d{2,4})(\\d)", "123456", 0, 5)]
    [InlineData("x[\\s\\S]*?y", "x1y2y", 0, 3)]
    public void AGreedyRunGivesCharactersBackOneAtATime(string pattern, string input, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, EcmaScriptFlags.None, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Fact]
    public void AGreedyRunNeedsOneFrameWhateverItsLength()
    {
        var subject = "a" + new string('b', 100_000) + "a";
        Assert.Equal(RegexSearchStatus.Match, Exec("a.*a", EcmaScriptFlags.None, subject, out var range, frameCapacity: 4));
        Assert.Equal(new RegexMatchRange(0, subject.Length), range);
        Assert.Equal(RegexSearchStatus.Match, Exec("a[^x]*a", EcmaScriptFlags.Unicode, subject, out range, frameCapacity: 4));
        Assert.Equal(new RegexMatchRange(0, subject.Length), range);
    }

    [Theory]
    [InlineData("ZZQ", EcmaScriptFlags.IgnoreCase, "xxzZqxx", RegexSearchStatus.Match)]
    [InlineData("[k]", EcmaScriptFlags.IgnoreCase, "\u212A", RegexSearchStatus.NoMatch)] // legacy Canonicalize
    [InlineData("[k]", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "\u212A", RegexSearchStatus.Match)]
    [InlineData("\u212A", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "k", RegexSearchStatus.Match)]
    [InlineData("s", EcmaScriptFlags.IgnoreCase, "\u017F", RegexSearchStatus.NoMatch)]
    [InlineData("s", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "\u017F", RegexSearchStatus.Match)]
    [InlineData("\\s*,\\s*x", EcmaScriptFlags.None, "a ,  x", RegexSearchStatus.Match)]
    [InlineData("(?=\\d)\\w+", EcmaScriptFlags.None, "ab12", RegexSearchStatus.Match)]
    public void PrefiltersAdmitEveryCharacterThatCanStartAMatch(string pattern, EcmaScriptFlags flags, string input,
        RegexSearchStatus expected)
    {
        Assert.Equal(expected, Exec(pattern, flags, input, out _));
    }

    [Fact]
    public void ATimedSearchAnswersAsAnUntimedOne()
    {
        var subject = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 5000)) + "needle";
        foreach (var pattern in new[] { "needle", "n[aeiou]+dle", "(?:lorem )+", "NEEDLE" })
        {
            var flags = pattern == "NEEDLE" ? EcmaScriptFlags.IgnoreCase : EcmaScriptFlags.None;
            Assert.Equal(Exec(pattern, flags, subject, out var untimed), Exec(pattern, flags, subject, out var timed, timeout: TimeSpan.FromSeconds(30)));
            Assert.Equal(untimed, timed);
        }
    }
}
