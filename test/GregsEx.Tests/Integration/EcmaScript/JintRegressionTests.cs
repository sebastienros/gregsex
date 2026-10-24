using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
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

    [Fact]
    public void AQuotedStringLiteralReservesItsTrailNotACopyOfTheStackPerFrame()
    {
        // A frame used to copy the whole task stack and every register: this pattern needed about 157 ints of workspace
        // per character of a string literal, so Jint refused a 1 MB literal. A frame now keeps what it must undo.
        const string pattern = "\"(?:[^\"\\\\]|\\\\.)*\"";
        var body = string.Concat(Enumerable.Repeat("abc\\\"def\\\\gh ", 64 * 1024 / 14));
        var subject = "\"" + body + "\"";
        var frames = subject.Length * 8 / 5;
        Assert.True(EcmaScriptMatcher.TryParse(pattern, new EcmaScriptProfile(EcmaScriptFlags.None, UnicodeVersion.Unicode17_0),
            out var matcher, out var diagnostic, frameCapacity: frames), diagnostic.ToString());
        using (matcher)
        {
            Assert.True(matcher!.ReservedBytes < 4L * 32 * subject.Length, $"{matcher.ReservedBytes / (4.0 * subject.Length):F1} ints per character");
            Assert.Equal(RegexSearchStatus.Match, matcher.Exec(subject, new RegexCapture[1], out var range));
            Assert.Equal(new RegexMatchRange(0, subject.Length), range);
        }
    }

    [Theory]
    // Captures a frame restores from its register trail, including ones set inside a positive lookahead whose own
    // choices were dropped when it succeeded. Expected results from ECMAScript semantics (checked against Node 24.4, V8 13.6).
    [InlineData("(?:(?=(a+))a)*b|(a)c", "aaac", "2,2|-|2,1")]
    [InlineData("(?=(\\w+))(\\w)*!", "abc!", "0,4|0,3|2,1")]
    [InlineData("((a)|b)+?c|(.)", "ababx", "0,1|-|-|0,1")]
    [InlineData("(?!(a)b)(a)c", "ac", "0,2|-|0,1")]
    [InlineData("^(?:(a)|(?=(b))b|c)+$", "abca", "0,4|3,1|-")]
    [InlineData("(?:(a)|b)*(?=(a))ab", "bab", "0,3|-|1,1")]
    [InlineData("(?:(?=(x|(y)))\\w)+z", "xyxz", "0,4|2,1|-")]
    [InlineData("^(?:(?=(a)(b)?)\\w)+?c", "abac", null)]
    [InlineData("(?=(a)(?=(b)))?a(c)?|(q)", "abq", "0,1|-|-|-|-")]
    [InlineData("((?=(a)))+a(?!(b))", "aab", "0,1|0,0|0,1|-")]
    public void TrailedCapturesRollBack(string pattern, string input, string? expected)
    {
        Assert.True(EcmaScriptMatcher.TryParse(pattern, new EcmaScriptProfile(EcmaScriptFlags.None, UnicodeVersion.Unicode17_0),
            out var matcher, out var diagnostic, frameCapacity: 64), diagnostic.ToString());
        using (matcher)
        {
            var groups = new RegexCapture[8];
            var status = matcher!.Exec(input, groups, out _);
            if (expected is null)
            {
                Assert.Equal(RegexSearchStatus.NoMatch, status);
                return;
            }

            Assert.Equal(RegexSearchStatus.Match, status);
            var actual = string.Join("|", groups.Take(expected.Split('|').Length)
                .Select(g => g.Participated ? $"{g.Index},{g.Length}" : "-"));
            Assert.Equal(expected, actual);
        }
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

    [Theory]
    // The string prefilter searches OrdinalIgnoreCase, which agrees with Canonicalize on ASCII only, and not on k/s
    // under u/v (KELVIN SIGN, LONG S): those patterns must stay findable.
    [InlineData("agggtaaa|tttaccct", EcmaScriptFlags.IgnoreCase, "xxAgGgTaAaxx", 2, 8)]
    [InlineData("[cgt]gggtaaa|tttaccc[acg]", EcmaScriptFlags.IgnoreCase, "xxTTTACCCgx", 2, 8)]
    [InlineData("kk", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "x\u212A\u212A", 1, 2)]
    [InlineData("ss", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "x\u017Fs", 1, 2)]
    [InlineData("(?:ab|cd)\\b", EcmaScriptFlags.IgnoreCase, "xx CD yy", 3, 2)]
    public void StringPrefiltersFindEveryMatch(string pattern, EcmaScriptFlags flags, string input, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, flags, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Fact]
    public void AUnicodeGreedyRunGivesBackWholeCodePoints()
    {
        Assert.Equal(RegexSearchStatus.Match, Exec("a.*a", EcmaScriptFlags.Unicode, "xa\U0001F600a\U0001F600b", out var range));
        Assert.Equal(new RegexMatchRange(1, 4), range);
        Assert.Equal(RegexSearchStatus.NoMatch, Exec(".*\\uDE00", EcmaScriptFlags.Unicode, "a\U0001F600b", out _));
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

    [Theory]
    // A nullable repeat runs on the atomic-sequence plan: last, or followed by a literal it cannot match.
    [InlineData("\\s*,\\s*", "x  ,  y", 1, 5)]
    [InlineData("\\s*,\\s*", "x y, z", 3, 2)]
    [InlineData("\\s*,", "a , b", 1, 2)]
    [InlineData("[a-c]*d", "xxabcabdz", 2, 6)]
    [InlineData("x?y", "aaxyz", 2, 2)]
    [InlineData("a\\s*,", "xa  ,", 1, 4)]
    [InlineData(",\\s*", "a,", 1, 1)]
    public void NullableRepeatsMatchAsTheyDidOnTheVm(string pattern, string input, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, EcmaScriptFlags.None, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Theory]
    // Under legacy i a class has a folded BMP bitmap and a literal without case variants is planned as a plain one;
    // Canonicalize's rules must survive both.
    [InlineData("[a-z]", "K", RegexSearchStatus.Match)]
    [InlineData("[a-z]", "\u212A", RegexSearchStatus.NoMatch)]
    [InlineData("[\\u212A]", "k", RegexSearchStatus.NoMatch)]
    [InlineData("[\\u017F]", "S", RegexSearchStatus.NoMatch)]
    [InlineData("[s]", "\u017F", RegexSearchStatus.NoMatch)]
    [InlineData("[\\u03c3]", "\u03c2", RegexSearchStatus.Match)]
    [InlineData("[\\u0131]", "I", RegexSearchStatus.NoMatch)]
    [InlineData("[i]", "\u0130", RegexSearchStatus.NoMatch)]
    [InlineData("[\\u00df]", "\u1e9e", RegexSearchStatus.NoMatch)]
    [InlineData("[a-z]+@[a-z0-9.]+\\.[a-z]{2,}", "sample-123-text", RegexSearchStatus.NoMatch)]
    [InlineData("[a-z]+@[a-z0-9.]+\\.[a-z]{2,}", "mail Bob@Example.COM now", RegexSearchStatus.Match)]
    [InlineData("[^a-z]+-", "abc-12Z", RegexSearchStatus.NoMatch)]
    [InlineData("[^a-z]+", "abc-12Z", RegexSearchStatus.Match)]
    public void CaseInsensitiveClassesKeepCanonicalize(string pattern, string input, RegexSearchStatus expected)
    {
        Assert.Equal(expected, Exec(pattern, EcmaScriptFlags.IgnoreCase, input, out _));
    }

    [Theory]
    // The FIRST-character prefilter looks through groups, alternations and inner sequences.
    [InlineData("(?:abc|def|ghi)+\\d*", "xx-defabc12-", 3, 8)]
    [InlineData("(https?):\\/\\/([^\\/\\s]+)", "see https://host/x", 4, 12)]
    [InlineData("(?:(?=b)|a)c", "xbac", 2, 2)]
    [InlineData("(?:x?)+y", "aay", 2, 1)]
    [InlineData("(?<=a)b", "cbab", 3, 1)]
    [InlineData("(a)\\1|z", "qaa", 1, 2)]
    public void FirstCharactersSeeThroughGroups(string pattern, string input, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, EcmaScriptFlags.None, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Theory]
    // A dot run is measured by a range search for its line terminators, which falls back after a few false hits
    // (tabs, CJK) to the exact search.
    [InlineData("a.*a", "a\tb\ta\u2028a", EcmaScriptFlags.None, 0, 5)]
    [InlineData("a.*a", "a\u4e2d\u4e2d\u4e2d\u4e2d\u4e2d\u4e2d\u4e2d\u4e2d\u4e2d\u4e2da\rza", EcmaScriptFlags.None, 0, 12)]
    [InlineData("^.*$", "x\t\t\t\t\t\t\t\t\t\ty\u2029z", EcmaScriptFlags.Multiline, 0, 12)]
    [InlineData(".*", "\U0001F600\t\U0001F600\nz", EcmaScriptFlags.Unicode, 0, 5)]
    public void DotRunsStopAtEveryLineTerminator(string pattern, string input, EcmaScriptFlags flags, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, flags, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Fact]
    public void AnAbsoluteDeadlineIsReadOncePerStrideEvenAtCheckpoints()
    {
        // The caller read the clock to compute the deadline, so the first stride, zero-unit checkpoints included, reads
        // nothing; the first charge past it reads the clock and sees the deadline has passed.
        var budget = new RunBudget(long.MaxValue, default, System.Diagnostics.Stopwatch.GetTimestamp() - 1);
        Assert.True(budget.TryCharge(0, 0, out _));
        Assert.True(budget.TryCharge(RunBudget.ClockStride - 1, 0, out _));
        Assert.True(budget.TryCharge(0, 0, out _));
        Assert.True(budget.TryCharge(1, 0, out _));
        Assert.False(budget.TryCharge(1, 0, out var failure));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, failure.Code);
    }

    [Fact]
    public void CaseVariantPrefiltersFollowTheProgramsUnicodeVersion()
    {
        // The inverse case folds behind the FIRST-character prefilter were cached per mode, not per Unicode version:
        // after a Unicode 16 program built them, a Unicode 17 program's prefilter missed U+A7CF (new in 17.0).
        foreach (var flags in new[] { EcmaScriptFlags.IgnoreCase, EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode })
        {
            Assert.False(Matches("(\uA7CE|a)+x", new EcmaScriptProfile(flags, UnicodeVersion.Unicode16_0), "\uA7CFx"));
            Assert.True(Matches("(\uA7CE|a)+x", new EcmaScriptProfile(flags, UnicodeVersion.Unicode17_0), "\uA7CFx"));
        }

        static bool Matches(string pattern, EcmaScriptProfile profile, string input)
        {
            Assert.True(EcmaScriptMatcher.TryParse(pattern, profile, out var matcher, out var diagnostic), diagnostic.ToString());
            using (matcher)
            {
                return matcher!.Exec(input, new RegexCapture[4], out _) == RegexSearchStatus.Match;
            }
        }
    }

    private static EcmaProgram CompileProgram(string pattern, EcmaScriptProfile profile, out EcmaUnicodeData unicode,
        out long work, RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new RegexCompileLimits());
        Assert.True(EcmaUnicodeData.TryCreate(profile.UnicodeVersion, ref budget, out var data, out _));
        var before = budget.WorkConsumed;
        Assert.True(new EcmaCompiler().TryCompile(pattern, profile, data!, ref budget, out var program, out var diagnostic),
            diagnostic.ToString());
        unicode = data!;
        work = budget.WorkConsumed - before;
        return program!;
    }

    [Theory]
    // Compiling an i-flag class built its SearchValues by testing all 65,536 units and corrected its folded bitmap by
    // testing each of ~1,400 legacy mappings; both now walk only what the class touches. Every unit must still be
    // a member exactly when Canonicalize puts it in the class, in the bitmap, the ASCII masks and the SearchValues.
    [InlineData("[a-z]")]
    [InlineData("[À-ɏ]")]
    [InlineData("[a-zͰ-Ͽ]")]
    [InlineData("[^x]")]
    [InlineData("[^k]")]
    [InlineData("\\w")]
    [InlineData("\\W")]
    [InlineData("[\\u0130\\u0131]")]
    [InlineData("[\\u212A\\u017F]")]
    [InlineData("[^\\u03a3]")]
    [InlineData("[\\u1e9e\\u00df]")]
    [InlineData("[\\uA7CE]")]
    [InlineData("[\\s\\S]")]
    [InlineData("[\\uFF41-\\uFFFF]")]
    public void FoldedClassMembershipFollowsCanonicalizeOnEveryCodeUnit(string pattern)
    {
        foreach (var version in new[] { UnicodeVersion.Unicode16_0, UnicodeVersion.Unicode17_0 })
        {
            foreach (var flags in new[] { EcmaScriptFlags.IgnoreCase, EcmaScriptFlags.None, EcmaScriptFlags.Unicode })
            {
                var program = CompileProgram(pattern, new EcmaScriptProfile(flags, version), out var unicode, out _);
                var ignoreCase = (flags & EcmaScriptFlags.IgnoreCase) != 0;
                var slice = program.Sets[0];
                var intervals = program.Intervals.Slice(slice.IntervalOffset, slice.IntervalCount);
                var set = new EcmaFastSet(program, slice, flags, unicode.CaseTables);
                Assert.NotNull(set.BmpBits);
                for (var c = 0; c <= 0xFFFF; c++)
                {
                    if (flags == EcmaScriptFlags.Unicode && c is >= 0xD800 and <= 0xDFFF)
                    {
                        continue;
                    }

                    var expected = EcmaFastSet.ContainsInterval(intervals,
                        EcmaFastSet.Fold(c, unicodeAware: false, ignoreCase, unicode.CaseTables));
                    Assert.True(expected == set.MatchesSingleChar((char)c), $"{pattern} {flags} {version} U+{c:X4}");
#if !GREGSEX_NETSTANDARD_ASSET
                    // The netstandard2.0 asset holds its own polyfilled SearchValues type.
                    if (set.BmpBitsIsExact && set.SearchValues is { } values)
                    {
                        Assert.True(expected == (values.Contains((char)c) != set.IsNegatedSearchValues),
                            $"{pattern} {flags} {version} SearchValues U+{c:X4}");
                    }
#endif
                }
            }
        }
    }

    [Fact]
    public void ClassesWithTheSameMembersAndCaseModeShareOneFastSet()
    {
        // Each Set node built its own 8 KiB bitmap and SearchValues, though patterns repeat their classes.
        var profile = new EcmaScriptProfile(EcmaScriptFlags.IgnoreCase, UnicodeVersion.Unicode17_0);
        var program = CompileProgram("[a-z]+@[a-z]+\\.[a-z]{2,}|[0-9]", profile, out var unicode, out _);
        var sets = new EcmaAccelerator(program, unicode.CaseTables).FastSets.OfType<EcmaFastSet>().Distinct().ToArray();
        Assert.Equal(2, sets.Length);

        // The same members under another case mode are another set.
        profile = new EcmaScriptProfile(EcmaScriptFlags.None, UnicodeVersion.Unicode17_0);
        program = CompileProgram("[a-z](?i:[a-z])[a-z]", profile, out unicode, out _);
        sets = new EcmaAccelerator(program, unicode.CaseTables).FastSets.OfType<EcmaFastSet>().Distinct().ToArray();
        Assert.Equal(2, sets.Length);
        Assert.Equal(RegexSearchStatus.Match, Exec("[a-z](?i:[a-z])[a-z]", EcmaScriptFlags.None, "aBc", out _));
        Assert.Equal(RegexSearchStatus.NoMatch, Exec("[a-z](?i:[a-z])[a-z]", EcmaScriptFlags.None, "abC", out _));
    }

    [Fact]
    public void CasedCodePointsAreBuiltOncePerCaseTables()
    {
        // The set of points with a case mapping, which decides whether an i-flag literal can be planned as a plain one,
        // was rebuilt on every compile. It is cached per case-table instance, so each Unicode version keeps its own.
        foreach (var version in new[] { UnicodeVersion.Unicode16_0, UnicodeVersion.Unicode17_0, UnicodeVersion.Unicode16_0 })
        {
            var budget = new CompileBudget(new RegexCompileLimits());
            Assert.True(EcmaUnicodeData.TryCreate(version, ref budget, out var unicode, out _));
            foreach (var unicodeAware in new[] { false, true })
            {
                var cased = EcmaAccelerator.GetCasedPoints(unicode!.CaseTables, unicodeAware);
                Assert.Same(cased, EcmaAccelerator.GetCasedPoints(unicode.CaseTables, unicodeAware));
                Assert.Equal(version == UnicodeVersion.Unicode17_0, cased.Contains(0xA7CF));
            }

            var flags = EcmaScriptFlags.IgnoreCase;
            Assert.True(EcmaScriptMatcher.TryParse("꟎@", new EcmaScriptProfile(flags, version), out var matcher, out _));
            using (matcher)
            {
                Assert.Equal(version == UnicodeVersion.Unicode17_0 ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
                    matcher!.Exec("x꟏@", new RegexCapture[1], out _));
            }
        }
    }

    [Theory]
    // Each range added to a class was placed by a scan from its first range, so loading a property's sorted ranges was
    // quadratic: \p{L} charged ~250,000 work units and took ~600 µs per compile.
    [InlineData("\\p{L}", "General_Category", "L")]
    [InlineData("\\p{Lu}", "General_Category", "Lu")]
    [InlineData("\\p{Script=Greek}", "Script", "Greek")]
    [InlineData("\\p{Alphabetic}", null, "Alphabetic")]
    public void PropertyClassesLoadInLinearWork(string pattern, string? name, string value)
    {
        var profile = new EcmaScriptProfile(EcmaScriptFlags.Unicode, UnicodeVersion.Unicode17_0);
        var program = CompileProgram(pattern, profile, out var unicode, out var work);
        Assert.True(name is null
            ? unicode.TryResolveProperty(value, default, false, out var property)
            : unicode.TryResolveProperty(name, value, false, out property));
        var expected = unicode.GetIntervals(property);
        var slice = program.Sets[0];
        Assert.True(expected.SequenceEqual(program.Intervals.Slice(slice.IntervalOffset, slice.IntervalCount)));
        Assert.True(work < 16L * expected.Length + 1_000, $"{pattern}: {work} work units for {expected.Length} ranges");
    }

    [Theory]
    // Ranges added out of order still merge with every neighbour they touch.
    [InlineData("[zb-da-cA]", "Aa-dz")]
    [InlineData("[x-zf-ha-c\\u0100d-ei]", "a-ix-zĀ")]
    [InlineData("[a-cx-z\\u0100d-w]", "a-zĀ")]
    public void OutOfOrderClassRangesMerge(string pattern, string members)
    {
        var program = CompileProgram(pattern, new EcmaScriptProfile(EcmaScriptFlags.Unicode), out _, out _);
        var slice = program.Sets[0];
        var actual = program.Intervals.Slice(slice.IntervalOffset, slice.IntervalCount).ToArray();
        var expected = new List<EcmaInterval>();
        for (var i = 0; i < members.Length; i++)
        {
            var last = i + 2 < members.Length && members[i + 1] == '-' ? members[i + 2] : members[i];
            expected.Add(new(members[i], last));
            i += last == members[i] ? 0 : 2;
        }

        Assert.Equal(expected.OrderBy(iv => iv.First), actual);
    }

    [Theory]
    // A non-multiline ^ inside an alternation no longer widens the FIRST set; the input's start is still tried.
    [InlineData("(?:^|:|,)(?:\\s*\\[)+", EcmaScriptFlags.None, " [x", 0, 2)]
    [InlineData("(?:^|:|,)(?:\\s*\\[)+", EcmaScriptFlags.None, "a:  [x", 1, 4)]
    [InlineData("(?:^|:)\\[", EcmaScriptFlags.Multiline, "a\n[x", 2, 1)]
    [InlineData("(?:^)?x", EcmaScriptFlags.None, "ax", 1, 1)]
    // An alternative skips a branch whose start excludes the current character, without saving a choice for it.
    [InlineData("\"[^\"\\\\\\n\\r]*\"|true|false|null|-?\\d+(?:\\.\\d*)?(:?[eE][+\\-]?\\d+)?", EcmaScriptFlags.None, " -1.5e3,", 1, 6)]
    [InlineData("(?:x|\\b)y", EcmaScriptFlags.None, "a y", 2, 1)]
    [InlineData("(?:a|$)", EcmaScriptFlags.None, "b", 1, 0)]
    // \b around a literal, and \b\w+\b, run on their plans.
    [InlineData("\\bab\\b", EcmaScriptFlags.None, "abc cab ab_ ab", 12, 2)]
    [InlineData("\\b-", EcmaScriptFlags.None, " -a-", 3, 1)]
    [InlineData("\\bk\\b", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "Kk k", 3, 1)]
    [InlineData("\\b\\w+\\b", EcmaScriptFlags.None, "-- cd_e f", 3, 4)]
    [InlineData("\\b\\w+\\b", EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Unicode, "-aſb", 1, 3)]
    public void GatedShapesMatchAsBefore(string pattern, EcmaScriptFlags flags, string input, int index, int length)
    {
        Assert.Equal(RegexSearchStatus.Match, Exec(pattern, flags, input, out var range));
        Assert.Equal(new RegexMatchRange(index, length), range);
    }

    [Theory]
    [InlineData("(?:^|:)\\[", EcmaScriptFlags.None, "a\n[x")]
    [InlineData("\\b\\w{1,2}\\b", EcmaScriptFlags.None, "abcd")]
    [InlineData("\\bab\\b", EcmaScriptFlags.None, "abc cab ab_")]
    public void GatedShapesFailAsBefore(string pattern, EcmaScriptFlags flags, string input)
    {
        Assert.Equal(RegexSearchStatus.NoMatch, Exec(pattern, flags, input, out _));
    }
}
