using GregsEx.Flavors.Oniguruma;
using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

// A failed attempt that began with a greedy run rules out the starts inside that run (OnigProgram.LeadingRun), also in
// a pattern with subexpression calls, where a call into the group holding the run runs it again further on.
public class LeadingRunTests
{
    private static OnigurumaPattern Pattern(string source) => OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate);

    // Built here rather than in the test data, which does not carry lone surrogates: H a lone high surrogate, L a lone
    // low one, P a pair. The oracle rows leave lone high surrogates out: a start filter can still try the start after
    // one, which NextStart rules out with the character a high surrogate starts.
    private static string Units(string tokens) => tokens.Replace("P", "😀").Replace("H", "\uD83D").Replace("L", "\uDE00");

    [Theory]
    [InlineData(@"(?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))\s*(?!=>|==)(?==)", " |\t|(|)|=")]
    [InlineData(@"(?<t> *\((?: |x|\g<t>)*\))y", " |(|)|x|y")]
    [InlineData(@"(?<t>a*b\g<t>?)c", "a|b|c|L")]
    [InlineData(@"(?<t>\w*(?:\.\g<t>)?)\(", "a|b|.|(")]
    [InlineData(@"(\w* +x*+(?:)*+)(?!\b\g<1>)", "a|b| ")]
    [InlineData(@"(?<t>[\x{1F600}a]*)b\g<t>c", "a|b|c|P")]
    [InlineData(@"a*(?:b\g<0>c|d)", "a|b|c|d")]
    [InlineData(@"(?<t>a{2,}b\g<t>?)c", "a|b|c")]
    [InlineData(@"(?<t>a*+b\g<t>?)c", "a|b|c")]
    [InlineData(@"a*d", "a|d|L|P")]
    public void FindAgreesWithAnAttemptAtEveryStart(string source, string pieces)
    {
        // The reference attempts every start with the matcher itself, so nothing is skipped.
        var parts = pieces.Split('|').Select(Units).ToArray();
        var pattern = Pattern(source);
        var program = pattern.Program;
        var workspace = pattern.CreateWorkspace();
        var matcher = new OnigMatcher();
        var raw = new int[2 * pattern.GroupCount];
        var groups = new RegexCapture[pattern.GroupCount];
        var random = new Random(source.Length);
        for (var trial = 0; trial < 400; trial++)
        {
            var subject = string.Concat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => parts[random.Next(parts.Length)]));
            for (var startAt = 0; startAt <= subject.Length; startAt++)
            {
                RegexCapture[]? expected = null;
                for (var pos = startAt; pos <= subject.Length && expected is null; pos++)
                {
                    if (pos > startAt && pos < subject.Length && char.IsHighSurrogate(subject[pos - 1]) && char.IsLowSurrogate(subject[pos]))
                    {
                        // Inside a character of two units: a search starts there only at its own start.
                        continue;
                    }

                    if (matcher.MatchAt(program, subject, pos, startAt, raw, 0, 0) == RegexSearchStatus.Match)
                    {
                        expected = [.. Enumerable.Range(0, pattern.GroupCount).Select(g => raw[2 * g] < 0
                            ? default
                            : new RegexCapture(true, raw[2 * g], raw[2 * g + 1] - raw[2 * g]))];
                    }
                }

                var status = workspace.FindCaptures(subject, startAt, groups, out _);
                Assert.True(expected is not null == (status == RegexSearchStatus.Match), $"\"{pieces}\" subject {Escape(subject)} from {startAt}: {status}");
                if (expected is not null)
                {
                    Assert.Equal(expected, groups);
                }

                var scanner = new OnigurumaScanner([pattern, Pattern("$x^")]);
                Assert.Equal(expected is not null, scanner.FindNextMatch(subject, startAt, out var index) && index == 0);
                if (expected is not null)
                {
                    Assert.Equal(expected, scanner.Captures.ToArray());
                }
            }
        }
    }

    private static string Escape(string s) => string.Concat(s.Select(c => c is >= ' ' and < (char)127 ? c.ToString() : $"\\u{(int)c:X4}"));

#if !GREGSEX_NETSTANDARD_ASSET
    [Theory]
    [InlineData(@"(?<tuple>\s*\((?:[^\(\)]|\g<tuple>)+\))", true)]
    [InlineData(@"(?<t>a*b\g<t>?)c", true)]
    [InlineData(@"(?<t>a*)b\k<t>", false)]
    public void APatternWithCallsKeepsItsLeadingRun(string source, bool leading)
        => Assert.Equal(leading, Pattern(source).Program.LeadingRun >= 0);

    [Theory]
    [InlineData(@"(?<t> *\((?: |\g<t>)*\))", "    x", 4)]
    [InlineData(@"(?<t>[\x{1F600}]*\()\g<t>?", "PPx", 4)]
    [InlineData(@" *\(", "   Hx", 4)]
    [InlineData(@" *\(", "   Px", 4)]
    [InlineData(@"a*d", "aaHd", 3)]
    public void AFailedAttemptRulesOutItsRun(string source, string tokens, int lastRuledOut)
    {
        // The search goes on from NextStart + 1; a high surrogate and the unit after it are read as one character.
        var subject = Units(tokens);
        var program = Pattern(source).Program;
        var matcher = new OnigMatcher();
        var captures = new int[2 * program.GroupCount];
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.MatchAt(program, subject, 0, 0, captures, 0, 0));
        Assert.Equal(lastRuledOut, matcher.NextStart(subject, 0));
    }
#endif
}
