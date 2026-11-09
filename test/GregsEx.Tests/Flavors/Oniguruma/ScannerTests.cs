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

    [Theory]
    // One pattern exhausts a tiny work budget, but only in the run of x after the other's match: wherever it stands
    // among the patterns, it is not tried there.
    [InlineData("a", "(x+x+)+(?=a)b")]
    [InlineData("(x+x+)+(?=a)b", "a")]
    public void NoPatternIsTriedPastTheWinningStart(string first, string second)
    {
        var scanner = Scanner(50, first, second);
        var line = "xa" + new string('x', 40) + "a";
        foreach (var memory in new[] { line.AsMemory(), line.ToCharArray().AsMemory() })
        {
            Assert.True(scanner.FindNextMatch(memory, 0, out var index));
            Assert.Equal("a", scanner.Patterns[index] == scanner.Patterns[0] ? first : second);
            Assert.Equal(1, scanner.Captures[0].Index);
        }

        Assert.Equal(0, scanner.LimitExceededCount);
    }

    [Fact]
    public void AnAttemptThatRunsOutOfWorkLeavesTheOtherPatternsTheirStarts()
    {
        // The first pattern runs out of work at 1 and counts as no match; the second still wins further on.
        var scanner = Scanner(50, "(x+x+)+(?=a)b", "a");
        var line = "." + new string('x', 40) + "a";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(1, index);
        Assert.Equal(41, scanner.Captures[0].Index);
        Assert.Equal(1, scanner.LimitExceededCount);
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

    [Theory]
    // Every grammar pattern of the corpus, in scanners of a few patterns and of more than one word of them, over the
    // corpus lines and two with surrogates: each call selects what searching every pattern to the end selects, in
    // whatever order a string's offsets are searched from.
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(130)]
    public void SelectionAgreesOverTheGrammarPatterns(int size)
    {
        var cases = CorpusTests.Read("grammar-cases.json.gz").AsArray();
        var patterns = new List<OnigurumaPattern>();
        var lines = new List<string> { "😀 let a = \"b\"; 😀.x(1) // c\n", "a\uD83Db = \uDE00 c\uD83D" };
        foreach (var source in cases.Select(c => c!["pattern"]!.GetValue<string>()).Distinct())
        {
            if (OnigurumaPattern.TryParse(source, RegexProfiles.OnigurumaTextMate, out var pattern, out _, OnigurumaCompileOptions.TextMate))
            {
                patterns.Add(pattern!);
            }
        }

        lines.AddRange(cases.SelectMany(c => c!["subjects"]!.AsArray()).Select(s => CorpusTests.Subject(s!)));
        lines = [.. lines.Distinct()];
        Assert.True(patterns.Count > 1000 && lines.Count > 30);

        var random = new Random(size);
        // Scanners over consecutive patterns; the sizes share the corpus between them.
        var skip = size * 7 % 5;
        for (var first = skip * size; first + size <= patterns.Count; first += 5 * size)
        {
            var set = patterns.GetRange(first, size);
            var workspaces = set.Select(p => p.CreateWorkspace()).ToArray();
            var ascending = new OnigurumaScanner(set);
            var walking = new OnigurumaScanner(set);
            var descending = new OnigurumaScanner(set);
            var shuffled = new OnigurumaScanner(set);
            var uncached = new OnigurumaScanner(set);
            foreach (var line in lines)
            {
                var expected = new (int Index, RegexCapture[] Captures)[line.Length + 1];
                for (var at = 0; at <= line.Length; at++)
                {
                    var best = new RegexMatchRange(int.MaxValue, 0);
                    expected[at].Index = -1;
                    for (var i = 0; i < workspaces.Length; i++)
                    {
                        if (workspaces[i].Find(line, at, out var match) == RegexSearchStatus.Match && match.Index < best.Index)
                        {
                            expected[at].Index = i;
                            best = match;
                        }
                    }

                    if (expected[at].Index >= 0)
                    {
                        expected[at].Captures = new RegexCapture[set[expected[at].Index].GroupCount];
                        Assert.Equal(RegexSearchStatus.Match, workspaces[expected[at].Index].FindCaptures(line, at, expected[at].Captures, out _));
                    }
                }

                void Check(string order, OnigurumaScanner scanner, ReadOnlyMemory<char> memory, int at)
                {
                    scanner.FindNextMatch(memory, at, out var index);
                    Assert.True(expected[at].Index == index, $"{order} from {at} of {line}: pattern {index}, expected {expected[at].Index}, of {first}..{first + size}");
                    if (index >= 0)
                    {
                        Assert.Equal(expected[at].Captures, scanner.Captures.ToArray());
                    }
                }

                var buffer = line.ToCharArray();
                var order = Enumerable.Range(0, line.Length + 1).OrderBy(_ => random.Next()).ToArray();
                for (var at = 0; at <= line.Length; at++)
                {
                    Check("ascending", ascending, line.AsMemory(), at);
                    Check("descending", descending, line.AsMemory(), line.Length - at);
                    Check("shuffled", shuffled, line.AsMemory(), order[at]);
                    Check("uncached", uncached, buffer, at);
                }

                // As a tokenizer goes, from match end to match end.
                for (var at = 0; at <= line.Length;)
                {
                    Check("walking", walking, line.AsMemory(), at);
                    var (index, captures) = expected[at];
                    at = index >= 0 ? Math.Max(at + 1, captures[0].Index + captures[0].Length) : line.Length + 1;
                }
            }

            Assert.Equal(0, ascending.LimitExceededCount + walking.LimitExceededCount + descending.LimitExceededCount + shuffled.LimitExceededCount + uncached.LimitExceededCount);
        }
    }

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
    public void APatternIsTriedPastWhereTheLastWalkStopped()
    {
        // From 0, the walk stops at the a at 1 without reaching the b; from 2 it has to go on to the b at 3.
        var scanner = Scanner("a", "b");
        const string line = ".a.b";
        Assert.True(scanner.FindNextMatch(line, 0, out var index));
        Assert.Equal(0, index);
        Assert.True(scanner.FindNextMatch(line, 2, out index));
        Assert.Equal(1, index);
        Assert.Equal(3, scanner.Captures[0].Index);
    }

    [Theory]
    // Each call has its own budget for a pattern, however many calls walk the line, so the last token is found.
    [InlineData(",", "(?:\\w+\\s*,?\\s*)+:|\\d+;", 1600, 10_000_000)]
    [InlineData("c", "(?:a|c|ac)*b|z", 3000, 10_000_000)]
    [InlineData("c", "(?:a|c|ac)*b|z", 20, 2000)]
    public void ALongWalkOfCallsKeepsItsLastToken(string separator, string pattern, int repeats, long maxWork)
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

    [Theory]
    // A walk of attempts does what one search does: the same result, captures and work, with the failure memo going
    // from one attempt to the next wherever it starts.
    [InlineData("(?:a|c|ac)*b|z", "acacacacacacacacacacacacacacacacacacXz", 64)]
    [InlineData("(?:a|c|ac)*b|z", "acacacacacacacacacacacacacacacacacacX", 64)]
    [InlineData("(?:a|c|ac)*b|z", "acacacXz", long.MaxValue)]
    [InlineData("(?:a|c|ac)*b|z", "acacacacacacXz", 0)]
    [InlineData("\\w+(?=\\()|\\d+;", "name0, name1, name2 (x), 42;", 8)]
    [InlineData("\\s*(\\w+)\\s*=", "   a b c   d = 1", 4)]
    [InlineData("😀+b", "😀😀😀a😀😀b", 2)]
    [InlineData("^b|c$", "a\nb\nc", 0)]
    public void AWalkOfAttemptsDoesWhatOneSearchDoes(string pattern, string line, long memoAfter)
    {
        var program = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate).Program;
        var whole = new OnigMatcher { MemoAfter = memoAfter };
        var expectedCaptures = new int[2 * program.GroupCount];
        var expected = whole.Search(program, line.AsSpan(), 0, expectedCaptures);

        var matcher = new OnigMatcher { MemoAfter = memoAfter };
        var captures = new int[2 * program.GroupCount];
        var status = RegexSearchStatus.NoMatch;
        long work = 0;
        var memo = 0;
        var last = Math.Min(line.Length - program.MinLength, whole.LastRequired == OnigMatcher.RequiredUnknown ? int.MaxValue : whole.LastRequired);
        for (var pos = 0; pos <= last && status == RegexSearchStatus.NoMatch; pos++)
        {
            var inside = pos > 0 && pos < line.Length && char.IsLowSurrogate(line[pos]) && char.IsHighSurrogate(line[pos - 1]);
            var lineStart = program.StartAnchor != OnigStartAnchor.LineStart || pos == 0 || (line[pos - 1] == '\n' && pos < line.Length);
            if (inside || !lineStart || !program.StartBits.CanStartWith(pos < line.Length ? line[pos] : -1))
            {
                continue;
            }

            status = matcher.MatchAt(program, line.AsSpan(), pos, 0, captures, work, memo);
            work = matcher.LastWork;
            memo = matcher.MemoStarted;
            pos = status == RegexSearchStatus.NoMatch ? matcher.NextStart(line.AsSpan(), pos) : pos;
        }

        Assert.Equal(expected, status);
        Assert.Equal(whole.LastWork, work);
        if (expected == RegexSearchStatus.Match)
        {
            Assert.Equal(expectedCaptures, captures);
        }
    }

    [Fact]
    public void AnotherSearchsMemoEndsAnAttemptsShareOfIt()
    {
        // The second walk starts its own memo over the first's, which then starts again instead of reading it.
        var program = OnigurumaPattern.Parse("(?:a|c|ac)*b|z", RegexProfiles.OnigurumaTextMate).Program;
        var matcher = new OnigMatcher { MemoAfter = 0 };
        var captures = new int[2];
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.MatchAt(program, "acacX".AsSpan(), 0, 0, captures, 0, 0));
        var first = matcher.MemoStarted;
        Assert.NotEqual(0, first);
        Assert.Equal(first, matcher.MemoVersion);
        Assert.Equal(RegexSearchStatus.NoMatch, matcher.MatchAt(program, "cacaX".AsSpan(), 0, 0, captures, 0, 0));
        Assert.NotEqual(first, matcher.MemoVersion);
        // The first memo's count no longer names the memo: this attempt starts its own instead of reading the other input's.
        Assert.Equal(RegexSearchStatus.Match, matcher.MatchAt(program, "acacb".AsSpan(), 0, 0, captures, 0, first));
    }

    [Theory]
    [InlineData("\\Gb", "ab", 1, 1, true)]
    [InlineData("\\Gb", "abb", 1, 2, false)]
    [InlineData("\\Ab", "bb", 0, 1, false)]
    [InlineData("^b", "b\nb", 0, 1, true)]
    [InlineData("b", "abab", 0, 2, true)]
    [InlineData("b", "abab", 0, 4, false)]
    public void ASearchCanGoOnFromALaterStart(string pattern, string line, int startAt, int from, bool matches)
    {
        var program = OnigurumaPattern.Parse(pattern, RegexProfiles.OnigurumaTextMate).Program;
        var captures = new int[2];
        Assert.Equal(matches, new OnigMatcher().Search(program, line.AsSpan(), startAt, captures, scanFrom: from) == RegexSearchStatus.Match);
        Assert.True(!matches || captures[0] >= from);
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

    [Theory]
    // From 5, the keep's match starts at 4, before the search does: it is the earliest start, unless an earlier
    // pattern matches at the offset, which ends the call. Two keeps: the earlier start, then the earlier pattern.
    [InlineData(new[] { "(?<=\\K..)y", "x" }, 0, 4)]
    [InlineData(new[] { "x", "(?<=\\K..)y" }, 0, 5)]
    [InlineData(new[] { "y", "(?<=\\K..)y" }, 1, 4)]
    [InlineData(new[] { "y", "(?<=\\K..)y", "(?<=\\K...)y" }, 2, 3)]
    [InlineData(new[] { "(?<=\\K..)y", "(?<=\\K..)y|q" }, 0, 4)]
    [InlineData(new[] { "q", "z|(?<=\\K...)y", "x", "(?<=\\K....)y" }, 3, 2)]
    public void AKeepThatStartsBeforeTheSearchIsSelectedAsItsStartSays(string[] patterns, int expectedIndex, int expectedStart)
    {
        const string line = "abcdexy";
        var scanner = Scanner(patterns);
        foreach (var memory in new[] { line.AsMemory(), line.ToCharArray().AsMemory() })
        {
            Assert.True(scanner.FindNextMatch(memory, 5, out var index));
            Assert.Equal(expectedIndex, index);
            Assert.Equal(expectedStart, scanner.Captures[0].Index);
        }
    }

    [Fact]
    public void AKeepIsSearchedPastTheWinningStartForLaterCalls()
    {
        // The whole search finds the match past the winning start, and its result is reused from the later offset.
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
        // The attempts in each run of x complete within the budget, so no call gives up, on a line of any length. Once
        // they have cost enough, the pattern is searched whole, which does, and past the retries the rest of the string
        // reuses the give-up without searching.
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
    public void ANewStringStartsWhatItsAttemptsCostOver()
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
    }

    [Fact]
    public void ACallThatDoesNotReachThePatternKeepsTheStringsRetries()
    {
        // Each run of a exhausts the budget, and the call for the gap after it ends before any start of the pattern.
        // The first failure is still the string's, so past the retries the pattern is not searched again.
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
        // A result from a later offset says nothing about an earlier one, but what the pattern's attempts have cost
        // is the string's. Once that sends the pattern to a whole search from the earlier offset, its match serves both.
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

    [Fact]
    public void AStartInsideACharacterKeepsNothingOfTheStringsEarlierSearches()
    {
        // From 1, the first pattern reads the lone high surrogate and the b as one character, fails, and goes on after
        // them: no match. A search from 2 reads the b on its own, whatever the string's earlier searches made of it.
        string[] patterns = ["[^\\x{00}-\\x{7F}]x|bc", "q"];
        const string line = "a\uD83Dbc";
        var workspace = OnigurumaPattern.Parse(patterns[0], RegexProfiles.OnigurumaTextMate).CreateWorkspace();
        Assert.Equal(RegexSearchStatus.NoMatch, workspace.Find(line, 1, out _));
        Assert.Equal(RegexSearchStatus.Match, workspace.Find(line, 2, out var expected));

        var scanner = Scanner(patterns);
        Assert.False(scanner.FindNextMatch(line, 1, out _));
        Assert.True(scanner.FindNextMatch(line, 2, out var index));
        Assert.Equal(0, index);
        Assert.Equal(new RegexCapture(true, expected.Index, expected.Length), scanner.Captures[0]);
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
