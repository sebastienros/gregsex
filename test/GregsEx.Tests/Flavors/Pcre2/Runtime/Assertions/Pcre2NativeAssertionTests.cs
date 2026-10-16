using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Assertions;

public sealed class Pcre2NativeAssertionTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public void IndependentlyExecutedPinnedNativeObservations(int width)
    {
        var root = FindRoot();
        var compared = 0;
        foreach (var line in File.ReadLines(Path.Combine(root, $"test/corpora/pcre2/assertions/native-{width}.jsonl")))
        {
            using var json = JsonDocument.Parse(line);
            var observation = json.RootElement;
            var id = observation.GetProperty("id").GetString()!;
            // Predicate capture lifetime is exercised by the concrete scoped-result
            // tests; production conditional branch selection belongs to recursion.
            if (id == "negative-predicate")
            {
                continue;
            }

            var (plan, input, start, flags) = Build(id, width);
            var fixture = new AssertionFixture(plan);
            var pairs = new Pcre2OffsetPair[plan.GroupCount];
            var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.Anchored | flags };
            var result = width == 8 ? fixture.Match(System.Text.Encoding.UTF8.GetBytes(input), pairs, start, options) : fixture.Match(input, pairs, start, options);
            var rc = observation.GetProperty("rc").GetInt32();
            Assert.True(rc == result.NativeReturnCode, $"{id}/{width}: native {rc}, managed {result}");
            if (rc >= 0 || rc == -2)
            {
                var expected = observation.GetProperty("pairs");
                for (var group = 0; group < expected.GetArrayLength(); group++)
                {
                    var pair = expected[group];
                    var value = pair.ValueKind == JsonValueKind.Null ? default : new Pcre2OffsetPair(true, pair[0].GetInt32(), pair[1].GetInt32());
                    Assert.True(value == pairs[group], $"{id}/{width} group{group}: expected {value}, actual {pairs[group]}");
                }
            }
            compared++;
        }
        Assert.Equal(width == 8 ? 29 : 31, compared);
    }
    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GregsEx.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Missing owned native observation report.");
    }
    private static (Pcre2Program, string, int, Pcre2MatchFlags) Build(string id, int width)
    {
        var profile = new Pcre2Profile(width: width == 8 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen);
        if (id.StartsWith("case-", StringComparison.Ordinal))
        {
            profile = new(width: profile.Width, flags: id == "case-utf" ? Pcre2CompileFlags.Utf : id == "case-ucp" ? Pcre2CompileFlags.Ucp : 0);
        }

        if (id.StartsWith("table-", StringComparison.Ordinal))
        {
            var tables = Pcre2CharacterTables.Default.Bytes.ToArray();
            tables['a'] = tables['b'] = 1;
            profile = new(width: profile.Width, flags: id == "table-ucp" ? Pcre2CompileFlags.Ucp : 0, characterTables: new(tables));
        }
        if (id.StartsWith("duplicate-", StringComparison.Ordinal))
        {
            profile = new(width: profile.Width, flags: Pcre2CompileFlags.Dupnames);
        }

        if (id == "unset-enabled")
        {
            profile = new(width: profile.Width, flags: Pcre2CompileFlags.MatchUnsetBackref);
        }

        var p = new AssertionPlan(profile);
        string subject;
        var start = 0;
        Pcre2MatchFlags flags = 0;
        switch (id)
        {
            case "ahead":
                p.Seq(p.Assertion(p.Capture(p.Literal('a'))), p.Literal('a'));
                subject = "a";
                break;
            case "negative":
                p.Seq(p.Assertion(p.Capture(p.Literal('a')), Pcre2AssertionKind.NegativeAhead), p.Literal('b'));
                subject = "b";
                break;
            case "atomic-ahead":
            case "nonatomic-ahead":
                p.Seq(p.Assertion(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), id == "atomic-ahead" ? Pcre2AssertionKind.PositiveAhead : Pcre2AssertionKind.NonAtomicAhead), p.Backref([1]), p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
                subject = "aa";
                break;
            case "atomic-behind":
            case "nonatomic-behind":
                p.Seq(p.Assertion(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), id == "atomic-behind" ? Pcre2AssertionKind.PositiveBehind : Pcre2AssertionKind.NonAtomicBehind, 1, 2, true), p.Backref([1]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
                subject = "aaa";
                start = 2;
                break;
            case "variable-behind":
                p.Seq(p.Assertion(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), Pcre2AssertionKind.PositiveBehind, 1, 2, true), p.Literal('b'));
                subject = "aab";
                start = 2;
                break;
            case "fixed-branches":
                p.Seq(p.Assertion(p.Alt(p.Capture(p.Literal('a'), 1), p.Capture(p.Seq(p.Literal('a'), p.Literal('a')), 2)), Pcre2AssertionKind.PositiveBehind, 1, 2, branchMaxima: [1, 2]), p.Literal('b'));
                subject = "aab";
                start = 2;
                break;
            case "negative-behind":
                p.Seq(p.Assertion(p.Seq(p.Capture(p.Repeat(p.Literal('a'), 1, 2)), p.Literal('x')), Pcre2AssertionKind.NegativeBehind, 2, 3, true), p.Literal('b'));
                subject = "aab";
                start = 2;
                break;
            case "nested-ahead":
                p.Seq(p.Assertion(p.Seq(p.Literal('a'), p.Assertion(p.Literal('b'))), Pcre2AssertionKind.PositiveBehind, 1, 1), p.Literal('b'));
                subject = "ab";
                start = 1;
                break;
            case "behind-true-end":
                p.Seq(p.Assertion(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), Pcre2AssertionKind.PositiveBehind, 1, 1), p.Literal('b'));
                subject = "ab";
                start = 1;
                break;
            case "scan-capture":
            case "scan-no-bump":
                p.Seq(p.Capture(p.Repeat(p.Leaf(Pcre2NodeKind.Dot), 1, 2)), p.Scan(p.Seq(p.Capture(p.Literal('a'), 2), p.Literal('b'), p.Leaf(Pcre2NodeKind.AbsoluteEnd))));
                subject = id == "scan-capture" ? "ab" : "ba";
                break;
            case "scan-retry":
                p.Seq(p.Capture(p.Seq(p.Literal('a'), p.Literal('a'))), p.Scan(p.Capture(p.Repeat(p.Literal('a'), 1, 2), 2)), p.Backref([2]), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
                subject = "aaa";
                break;
            case "scan-end":
                p.Seq(p.Literal('a'), p.Capture(p.Literal('b')), p.Scan(p.Seq(p.Assertion(p.Literal('a'), Pcre2AssertionKind.PositiveBehind, 1, 1), p.Literal('b'), p.Leaf(Pcre2NodeKind.End), p.Leaf(Pcre2NodeKind.AbsoluteEnd))), p.Literal('c'));
                subject = "abc";
                flags = Pcre2MatchFlags.Noteol;
                break;
            case "scan-beginning":
                p.Seq(p.Literal('a'), p.Capture(p.Literal('b')), p.Scan(p.Seq(p.Leaf(Pcre2NodeKind.AbsoluteStart), p.Literal('b'))));
                subject = "ab";
                break;
            case "scan-overwrite":
                p.Seq(p.Capture(p.Seq(p.Literal('a'), p.Literal('a'))), p.Scan(p.Capture(p.Repeat(p.Literal('a'), 1, 2), 2)), p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), 2), p.Backref([2]));
                subject = "aaa";
                break;
            case "scan-nesting":
                p.Seq(p.Capture(p.Seq(p.Literal('a'), p.Literal('b'))), p.Scan(p.Seq(p.Capture(p.Literal('a'), 2), p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.AbsoluteEnd)), 2), p.Literal('b'), p.Leaf(Pcre2NodeKind.AbsoluteEnd))), p.Literal('c'), p.Leaf(Pcre2NodeKind.AbsoluteEnd));
                subject = "abc";
                break;
            case "scan-noteol-restore":
                p.Seq(p.Capture(p.Literal('a')), p.Scan(p.Seq(p.Literal('a'), p.Leaf(Pcre2NodeKind.End))), p.Leaf(Pcre2NodeKind.End));
                subject = "a";
                flags = Pcre2MatchFlags.Noteol;
                break;
            case "duplicate-first":
            case "duplicate-not-second":
                p.Seq(p.Repeat(p.Capture(p.Literal('a'), 1), 0, 1), p.Capture(p.Literal('b'), 2), p.Backref([1, 2], Pcre2ReferenceKind.Named));
                subject = id == "duplicate-first" ? "aba" : "abb";
                break;
            case "unset-default":
            case "unset-enabled":
                p.Seq(p.Repeat(p.Capture(p.Literal('a')), 0, 1), p.Backref([1]), p.Literal('b'));
                subject = "b";
                break;
            case "open-prior-capture":
                p.Repeat(p.Capture(p.Seq(p.Literal('a'), p.Repeat(p.Backref([1]), 0, 1))), 2, 2);
                subject = "aaa";
                break;
            case "partial-prefix":
            case "partial-mismatch":
                p.Seq(p.Capture(p.Seq(p.Literal('a'), p.Literal('b'))), p.Backref([1]));
                subject = id == "partial-prefix" ? "aba" : "abx";
                flags = Pcre2MatchFlags.PartialHard;
                break;
            case "case-utf":
            case "case-raw":
            case "case-ucp":
                p.Seq(p.Capture(p.Literal(0x23A)), p.Backref([1], flags: profile.Flags | Pcre2CompileFlags.Caseless));
                subject = "Ⱥⱥ";
                break;
            case "table-raw":
            case "table-ucp":
                p.Seq(p.Capture(p.Literal('a')), p.Backref([1], flags: profile.Flags | Pcre2CompileFlags.Caseless));
                subject = "ab";
                break;
            default:
                throw new InvalidOperationException(id);
        }
        return (p.Build(), subject, start, flags);
    }
}
