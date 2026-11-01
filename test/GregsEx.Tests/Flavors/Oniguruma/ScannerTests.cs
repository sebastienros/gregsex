using GregsEx.Flavors.Oniguruma;
using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

public class ScannerTests
{
    private static OnigurumaScanner Scanner(params string[] patterns) => new(patterns, RegexProfiles.OnigurumaTextMate);

    private static OnigurumaScanner Scanner(long maxWork, params string[] patterns)
        => new(patterns, RegexProfiles.OnigurumaTextMate, new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: maxWork)));

    private static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));

    [Fact]
    public void EarliestMatchWinsAndTheFirstPatternOnATie()
    {
        var scanner = Scanner("b", "a", "(a)");
        Assert.True(scanner.FindNextMatch("xab".AsSpan(), 0, out var index));
        Assert.Equal(1, index);
        Assert.Equal(1, scanner.Captures.Length);
        Assert.Equal(new RegexCapture(true, 1, 1), scanner.Captures[0]);

        Assert.True(scanner.FindNextMatch("xab".AsSpan(), 2, out index));
        Assert.Equal(0, index);
        Assert.False(scanner.FindNextMatch("xyz".AsSpan(), 0, out index));
        Assert.Equal(-1, index);
        Assert.True(scanner.Captures.IsEmpty);
    }

    [Fact]
    public void AMatchAtTheOffsetStopsTheScan()
    {
        // The second pattern exhausts a tiny work budget; it is only searched when the first does not match at the
        // offset.
        var scanner = Scanner(50, "a", "(x+x+)+(?=a)b");
        const string line = "axxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxa";
        Assert.True(scanner.FindNextMatch(line.AsSpan(), 0, out var index));
        Assert.Equal(0, index);
        Assert.Equal(0, scanner.LimitExceededCount);

        Assert.True(scanner.FindNextMatch(line.AsSpan(), 1, out index));
        Assert.Equal(0, index);
        Assert.Equal(line.Length - 1, scanner.Captures[0].Index);
        Assert.Equal(1, scanner.LimitExceededCount);
    }

    [Fact]
    public void AGiveUpIsNotReusedAsAMissAtALaterOffset()
    {
        // The search from 0 exhausts its work budget; a later offset of the same string gets a fresh search and matches.
        var options = new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: 50));
        var scanner = new OnigurumaScanner([OnigurumaPattern.Parse("(x+x+)+(?=a)b|y", RegexProfiles.OnigurumaTextMate, options)]);
        var line = new string('x', 40) + "ay";
        Assert.False(scanner.FindNextMatch(line, 0, out _));
        Assert.Equal(1, scanner.LimitExceededCount);

        Assert.True(scanner.FindNextMatch(line, 41, out var index));
        Assert.Equal(0, index);
        Assert.Equal(41, scanner.Captures[0].Index);
    }

    [Fact]
    public void AGiveUpIsSearchedAgainABoundedNumberOfTimes()
    {
        // Every offset in the x run exhausts the budget. The first searches after a give-up are fresh; past
        // GiveUpRetries, later offsets of the string reuse the give-up, even one a fresh search would match from.
        var options = new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: 50));
        var scanner = new OnigurumaScanner([OnigurumaPattern.Parse("(x+x+)+(?=a)b|y", RegexProfiles.OnigurumaTextMate, options)]);
        var line = new string('x', 40) + "ay";
        for (var offset = 0; offset <= OnigurumaScanner.GiveUpRetries + 2; offset++)
        {
            Assert.False(scanner.FindNextMatch(line, offset, out _));
        }
        Assert.False(scanner.FindNextMatch(line, 41, out _));
        Assert.Equal(OnigurumaScanner.GiveUpRetries + 4, scanner.LimitExceededCount);

        // A new string starts over.
        line = new string(line.AsSpan());
        Assert.False(scanner.FindNextMatch(line, 0, out _));
        Assert.True(scanner.FindNextMatch(line, 41, out var index));
        Assert.Equal(0, index);
        Assert.Equal(41, scanner.Captures[0].Index);
    }

    [Fact]
    public void CapturesAreThoseOfTheSelectedPattern()
    {
        var scanner = Scanner("(x)(y)?", "(a)");
        Assert.True(scanner.FindNextMatch("ax", 1, out var index));
        Assert.Equal(0, index);
        Assert.Equal(3, scanner.Captures.Length);
        Assert.False(scanner.Captures[2].Participated);
    }

    [Fact]
    public void StringInputReusesEachPatternsLastResult()
    {
        // As Onigwrap's OnigRegExp: a result at or after the offset, or no match, is valid for a later offset of the
        // same string. Results stay correct either way.
        var scanner = Scanner("c", "b");
        const string line = "abcabc";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(1, index);
        Assert.True(scanner.FindNextMatch(line, 2, out index));
        Assert.Equal(0, index);
        Assert.Equal(2, scanner.Captures[0].Index);
        Assert.True(scanner.FindNextMatch(line, 3, out index));
        Assert.Equal(1, index);
        Assert.Equal(4, scanner.Captures[0].Index);

        // Array-backed memory can change between calls, so it is searched again.
        var buffer = "abc".ToCharArray();
        Assert.True(scanner.FindNextMatch(buffer.AsMemory(), 0, out index));
        Assert.Equal(1, index);
        buffer[1] = 'z';
        Assert.True(scanner.FindNextMatch(buffer.AsMemory(), 0, out index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void SearchStartPatternsAreNotReused()
    {
        // \Gb fails from 0 but matches from 1; a cached "no match" would hide that.
        var scanner = Scanner("\\Gb");
        const string line = "abc";
        Assert.False(scanner.FindNextMatch(line, 0, out _));
        Assert.True(scanner.FindNextMatch(line, 1, out var index));
        Assert.Equal(0, index);
    }

    [Fact]
    public void ALaterPatternIsNotSearchedPastTheBestStart()
    {
        // The second pattern exhausts a tiny work budget, but only in the run of x after the first pattern's match.
        var scanner = Scanner(50, "a", "(x+x+)+(?=a)b");
        var line = "xa" + new string('x', 40) + "a";
        Assert.True(scanner.FindNextMatch(line.AsSpan(), 0, out var index));
        Assert.Equal(0, index);
        Assert.Equal(1, scanner.Captures[0].Index);
        Assert.True(scanner.FindNextMatch(line, 0, out index));
        Assert.Equal(0, index);
        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Theory]
    [InlineData("b", ".ba", 1)]
    [InlineData("bc", ".bca", 1)]
    [InlineData("b", "😀b😀a", 2)]
    [InlineData("^b", "\nba", 1)]
    public void TheLastStartBeforeTheBestStillWins(string pattern, string line, int expectedStart)
    {
        var scanner = Scanner("a", pattern);
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(1, index);
        Assert.Equal(expectedStart, scanner.Captures[0].Index);
    }

    [Theory]
    [InlineData("    const result = await fetchItems(request.id, { limit: 20 }); // load\n")]
    [InlineData("😀 let a = \"b\"; 😀.x(1) // c\n")]
    [InlineData("\n\n  x\ty\n\n")]
    public void SelectionAgreesWithSearchingEveryPatternToTheEnd(string line)
        => AssertSelectionAgrees(["\\b(const|let|var)\\b", "//.*$", "\"", "(?<=\\.)\\w+", "\\w+(?=\\()", "^\\s+", "[{}()\\[\\];,.]", "\\d+", "\\G\\s"], line);

    // Most of these patterns require ( or ;, looked up once per string (or per call for other input) for all of them.
    [Theory]
    [InlineData("call(a, b); x = (1 + 2);")]
    [InlineData("no parens at all here")]
    [InlineData("trailing (")]
    [InlineData("((((;")]
    [InlineData("a; b; c(d); e")]
    public void SelectionAgreesWhenPatternsRequireTheSameText(string line)
        => AssertSelectionAgrees(["\\w+(?=\\()", "\\(([^()]*)\\)", "[a-z]+\\s*\\(", ";.*\\(", "\\w+\\s*;", "\\d+", "[=;,]", "\\b[a-z]\\b(?=.*;)"], line);

    private static void AssertSelectionAgrees(string[] patterns, string line)
    {
        var workspaces = patterns.Select(p => OnigurumaPattern.Parse(p, RegexProfiles.OnigurumaTextMate).CreateWorkspace()).ToArray();
        var scanner = Scanner(patterns);
        // A span search clears the per-string results, so strings get a scanner of their own.
        var cachingScanner = Scanner(patterns);

        // Every offset in order, then as a tokenizer goes from match end to match end, each with its own string.
        foreach (var tokenize in new[] { false, true })
        {
            var text = new string(line.AsSpan());
            for (var at = 0; at <= text.Length;)
            {
                var expectedIndex = -1;
                var expected = new RegexMatchRange(int.MaxValue, 0);
                for (var i = 0; i < workspaces.Length; i++)
                {
                    if (workspaces[i].Find(text, at, out var match) == RegexSearchStatus.Match && match.Index < expected.Index)
                    {
                        expectedIndex = i;
                        expected = match;
                    }
                }

                Assert.Equal(expectedIndex >= 0, scanner.FindNextMatch(text.AsSpan(), at, out var index));
                Assert.Equal(expectedIndex, index);
                Assert.Equal(expectedIndex >= 0, cachingScanner.FindNextMatch(text, at, out index));
                Assert.Equal(expectedIndex, index);
                if (expectedIndex >= 0)
                {
                    var groups = new RegexCapture[scanner.Captures.Length];
                    Assert.Equal(RegexSearchStatus.Match, workspaces[expectedIndex].FindCaptures(text, at, groups, out _));
                    Assert.Equal(expected.Index, groups[0].Index);
                    Assert.Equal(expected.Length, groups[0].Length);
                    Assert.Equal(groups, scanner.Captures.ToArray());
                    Assert.Equal(groups, cachingScanner.Captures.ToArray());
                }

                at = tokenize && expectedIndex >= 0 ? Math.Max(at + 1, expected.Index + expected.Length) : at + 1;
            }
        }
    }

    [Fact]
    public void AMissIsOnlyReusedUpToWhereItsSearchStopped()
    {
        // From 0, the search for b stops before the a at 1; from 2 it has to go on to the b at 3.
        var scanner = Scanner("a", "b");
        const string line = ".a.b";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(0, index);
        Assert.True(scanner.FindNextMatch(line, 2, out index));
        Assert.Equal(1, index);
        Assert.Equal(3, scanner.Captures[0].Index);
    }

    [Theory]
    // Each search has its own budget, however many a best start divides the walk into, so the last token is found.
    [InlineData(",", "(?:\\w+\\s*,?\\s*)+:|\\d+;", 1600, 10_000_000)]
    [InlineData("c", "(?:a|c|ac)*b|z", 3000, 10_000_000)]
    [InlineData("c", "(?:a|c|ac)*b|z", 20, 2000)]
    public void ALongWalkOfStoppedSearchesKeepsItsLastToken(string separator, string pattern, int repeats, long maxWork)
    {
        var scanner = Scanner(maxWork, separator, pattern);
        var line = separator == "c"
            ? Repeat("ac", repeats) + "Xz"
            : string.Join(", ", Enumerable.Range(0, repeats).Select(i => "name" + i)) + ", 42;";
        var offset = 0;
        var last = -1;
        while (scanner.FindNextMatch(line, offset, out var index))
        {
            last = index;
            offset = scanner.Captures[0].Index + scanner.Captures[0].Length;
        }

        Assert.Equal(1, last);
        Assert.Equal(line.Length, offset);
        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Fact]
    public void AStartTiedWithTheBestIsNotAttempted()
    {
        // An attempt at the tied start would exhaust the tiny budget, and could not win.
        var scanner = Scanner(50, "a", "(a+a+)+(?=b)c");
        Assert.True(scanner.FindNextMatch(("." + new string('a', 40) + "b").AsSpan(), 0, out var index));
        Assert.Equal(0, index);
        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Fact]
    public void ASearchWhoseLimitIsBeforeItsStartAttemptsNothing()
    {
        var program = OnigurumaPattern.Parse("\\Gb", RegexProfiles.OnigurumaTextMate).Program;
        var captures = new int[2];
        Assert.Equal(RegexSearchStatus.NoMatch, new OnigMatcher().Search(program, "ab".AsSpan(), 1, captures, limit: 0));
        Assert.Equal(RegexSearchStatus.Match, new OnigMatcher().Search(program, "ab".AsSpan(), 1, captures, limit: 1));
    }

    [Theory]
    // No start past the limit could match: "class" is absent, or the last possible start is at or within the limit.
    [InlineData("class", "a b c d", 1, int.MaxValue)]
    [InlineData("bc", ".bc.", 2, int.MaxValue)]
    [InlineData("bc", ".bc.", 1, int.MaxValue)]
    [InlineData("bc", ".bc.bc", 1, 1)]
    public void ASearchSaysWhichStartsItAnswersFor(string pattern, string line, int limit, int covered)
    {
        var program = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate).Program;
        var matcher = new OnigMatcher();
        // A limited search first: each search reports for itself, even one that ends before reaching its limit.
        matcher.Search(OnigurumaPattern.Parse("bc", RegexProfiles.OnigurumaTextMate).Program, ".bc.bc".AsSpan(), 0, new int[2], 0);
        matcher.Search(program, line.AsSpan(), 0, new int[2], limit);
        Assert.Equal(covered, matcher.LastCovered);
    }

    [Theory]
    [InlineData("x", "(?<=\\K..)y", 4, 3)]
    [InlineData("e", "(?<=\\K.{4})x", 1, 5)]
    [InlineData("x", "(?=(?<=\\K..)y)y", 4, 3)]
    public void AKeepCanReportAStartBeforeItsAttempt(string first, string keeping, int expectedStart, int expectedLength)
    {
        const string line = "abcdexy";
        Assert.True(Scanner(first, keeping).FindNextMatch(line, 0, out var index));
        Assert.Equal(1, index);
        var scanner = Scanner(first, keeping);
        Assert.True(scanner.FindNextMatch(line.AsSpan(), 0, out index));
        Assert.Equal(1, index);
        Assert.Equal(new RegexCapture(true, expectedStart, expectedLength), scanner.Captures[0]);
    }

    [Fact]
    public void AKeepIsSearchedPastTheBestStartForLaterCalls()
    {
        // The whole search finds the match past the best start, and its result is reused from the later offset.
        var scanner = Scanner("a", "a\\Kb");
        const string line = "xab";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(0, index);
        Assert.True(scanner.FindNextMatch(line, 2, out index));
        Assert.Equal(1, index);
        Assert.Equal(new RegexCapture(true, 2, 1), scanner.Captures[0]);
    }

    [Fact]
    public void APatternThatKeepsMissingGoesBackToWholeSearchesAndTheirRetries()
    {
        // Each search of a run of x completes within the budget, so bounded searches alone would never give up, on a
        // line of any length. Once they have cost enough, a whole search does, and past the retries the rest of the
        // string reuses the give-up without searching.
        var scanner = Scanner(200, "a", "(x+x+)+(?=a)b");
        var line = Repeat("xxxxa", 2000);
        long exceededBeforeTheLastRuns = 0;
        for (var offset = 0; offset < line.Length; offset += 5)
        {
            if (offset == 1990 * 5)
            {
                exceededBeforeTheLastRuns = scanner.LimitExceededCount;
            }

            Assert.True(scanner.FindNextMatch(line, offset, out var index));
            Assert.Equal(0, index);
            Assert.Equal(offset + 4, scanner.Captures[0].Index);
        }

        Assert.True(exceededBeforeTheLastRuns > 0);
        Assert.Equal(10, scanner.LimitExceededCount - exceededBeforeTheLastRuns);
    }

    [Fact]
    public void ANewStringStartsItsBoundedSearchesOver()
    {
        // The work of one string does not send the next one to whole searches, which would exhaust the tiny budget.
        var scanner = Scanner(200, "a", "(x+x+)+(?=a)b");
        var line = Repeat("xxxxa", 2000);
        for (var offset = 0; offset < line.Length; offset += 5)
        {
            scanner.FindNextMatch(line, offset, out _);
        }

        var exceeded = scanner.LimitExceededCount;
        Assert.True(scanner.FindNextMatch(new string(line.AsSpan()), 0, out var index));
        Assert.Equal(0, index);
        Assert.Equal(exceeded, scanner.LimitExceededCount);
    }

    [Fact]
    public void AWholeSearchsMissIsKeptForTheWholeString()
    {
        // The search of the keep pattern is whole, so a later call reuses its miss instead of spending its one unit.
        var scanner = Scanner("a", "x\\Kz");
        const string line = ".a.azxxx";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(0, index);
        var options = new RegexMatchOptions { Limits = new RegexMatchLimits(maxWork: 1) };
        scanner.FindNextMatch(line, 2, out _, options);
        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Theory]
    [InlineData("\\A[ab].*z")]
    [InlineData("[ab]x.*z")]
    [InlineData("^[ab].*z")]
    public void ARequiredTextServesEveryTokenOfALine(string pattern)
    {
        // A string keeps where the required text last occurs; other memory looks for its next occurrence each time.
        var scanner = Scanner("c", pattern);
        var line = Repeat("xc", 1000) + "z" + Repeat("q", 2000);
        foreach (var memory in new[] { line.AsMemory(), line.ToCharArray().AsMemory() })
        {
            for (var offset = 0; offset < 2000; offset += 2)
            {
                Assert.True(scanner.FindNextMatch(memory, offset, out var index));
                Assert.Equal(0, index);
            }
        }
    }

    [Fact]
    public void ASearchTakesTheRequiredTextsPlaceFromTheCaller()
    {
        var program = OnigurumaPattern.Parse("[ab].*z", RegexProfiles.OnigurumaTextMate).Program;
        var matcher = new OnigMatcher();
        var captures = new int[2];
        Assert.Equal(RegexSearchStatus.Match, matcher.Search(program, "a.z".AsSpan(), 0, captures));
        Assert.Equal(2, matcher.LastRequired);
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.Search(program, "a.z".AsSpan(), 0, captures, requiredAt: -1));
        Assert.Equal(-1, matcher.LastRequired);
        // A caller that keeps nothing gets a limited search's answer from the next occurrence alone.
        Assert.Equal(RegexSearchStatus.Match, matcher.Search(program, "a.z".AsSpan(), 0, captures, limit: 0, requiredAt: OnigMatcher.RequiredNotKept));
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.Search(program, "a.y".AsSpan(), 0, captures, limit: 0, requiredAt: OnigMatcher.RequiredNotKept));
    }

    [Fact]
    public void ACompletedLimitedMissKeepsTheStringsRetries()
    {
        // Each run of a exhausts the budget, and the search of the gap after it completes, having stopped short of the
        // next run. The first failure is still the string's, so past the retries the pattern is not searched again.
        var scanner = Scanner(50, ";", "(?:a|aa)*b|z");
        var line = Repeat(Repeat("a", 20) + "X;.;", 300) + "z";
        var offset = 0;
        var foundZ = false;
        while (scanner.FindNextMatch(line, offset, out var index))
        {
            foundZ |= index == 1;
            offset = scanner.Captures[0].Index + scanner.Captures[0].Length;
        }

        // A fresh search from the last semicolon would find the z.
        Assert.False(foundZ);
    }

    [Fact]
    public void AMatchRestartsTheStringsRetries()
    {
        // Two failures, then a match, three times over: no stretch uses up the retries, so every z is found.
        var scanner = Scanner(50, ";", "(?:a|aa)*b|z");
        var run = Repeat("a", 20) + "X;";
        var line = Repeat(run + run + "z", 3) + ";";
        var offset = 0;
        var found = 0;
        while (scanner.FindNextMatch(line, offset, out var index))
        {
            found += index;
            offset = scanner.Captures[0].Index + scanner.Captures[0].Length;
        }

        Assert.Equal(3, found);
    }

    [Fact]
    public void GoingBackOnAStringKeepsItsWork()
    {
        // A result from a later offset says nothing about an earlier one, but what limited searches have cost is the
        // string's. Once that sends the pattern to a whole search, its match serves both offsets.
        var scanner = Scanner("c", "(?:a|c|ac)*b|z");
        var line = Repeat("ac", 40) + "Xz";
        // By then only the first pattern is searched, within a budget that one attempt of the second would exhaust.
        var options = new RegexMatchOptions { Limits = new RegexMatchLimits(maxWork: 20) };
        for (var i = 0; i < 400; i++)
        {
            Assert.Equal(RegexSearchStatus.Match, scanner.FindNextMatch(line, i % 2 == 0 ? 0 : 2, out var index, i < 200 ? default : options));
            Assert.Equal(0, index);
        }

        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Theory]
    // The windows differ by where they start, then by where they end: what one says of its text is not the other's.
    [InlineData(".c.....c...z", 0, 6, 6, 6)]
    [InlineData(".c...z", 0, 3, 0, 6)]
    public void WindowsOfOneStringKeepTheirOwnResults(string text, int firstStart, int firstLength, int secondStart, int secondLength)
    {
        var scanner = Scanner("c", "z");
        var first = text.AsMemory(firstStart, firstLength);
        var second = text.AsMemory(secondStart, secondLength);
        Assert.True(scanner.FindNextMatch(first, 0, out var index));
        Assert.Equal(0, index);
        Assert.True(scanner.FindNextMatch(second, 0, out index));
        Assert.Equal(0, index);
        Assert.True(scanner.FindNextMatch(second, 2, out index));
        Assert.Equal(1, index);
        Assert.Equal(new RegexCapture(true, 5, 1), scanner.Captures[0]);
    }

    [Fact]
    public void WarmScansDoNotAllocate()
    {
        var scanner = Scanner("\\b(const|let|var)\\b", "//.*$", "\"", "\\b([_$[:alpha:]][_$[:alnum:]]*)\\s*(?=\\()", "[{}()\\[\\];,.]");
        const string line = "    const result = await fetchItems(request.id, { limit: 20 }); // load\n";
        for (var i = 0; i < 3; i++)
        {
            for (var p = 0; p < line.Length; p += 5)
            {
                scanner.FindNextMatch(line.AsSpan(), p, out _);
            }
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var p = 0; p < line.Length; p += 5)
        {
            scanner.FindNextMatch(line.AsSpan(), p, out _);
            scanner.FindNextMatch(line, p, out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
