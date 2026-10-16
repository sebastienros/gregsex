using System.Text;
using System.Text.Json;
using GregsEx.Contracts.Pcre2;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Recursion;

public sealed class Pcre2NativeRecursionTests
{
    private static string Report(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test/corpora/pcre2/recursion", file)))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir!.FullName, "test/corpora/pcre2/recursion", file);
    }
    public static IEnumerable<TheoryDataRow<string, int, bool>> Cases()
    {
        foreach (var width in new[] { 8, 16 })
        {
            foreach (var line in File.ReadLines(Report($"native-{width}.jsonl")))
            {
                using var doc = JsonDocument.Parse(line);
                var c = doc.RootElement;
                yield return new TheoryDataRow<string, int, bool>(c.GetProperty("id").GetString()!, width, c.GetProperty("utf").GetBoolean());
            }
        }
    }
    [Theory, MemberData(nameof(Cases))]
    public void IndependentNative1046(string id, int width, bool utf)
    {
        using var native = JsonDocument.Parse(File.ReadLines(Report($"native-{width}.jsonl")).Single(line =>
        {
            using var d = JsonDocument.Parse(line);
            return d.RootElement.GetProperty("id").GetString() == id && d.RootElement.GetProperty("utf").GetBoolean() == utf;
        }));
        var p = new RecursionPlan(new((Pcre2CodeUnitWidth)width, utf ? Pcre2CompileFlags.Utf : 0));
        string subject;
        var predicate = false;
        var flags = Pcre2MatchFlags.Anchored;
        switch (id)
        {
            case "whole-positive":
            case "whole-negative":
            case "whole-empty":
                p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1), p.Lit('b'));
                subject = id == "whole-positive" ? "aaabbb" : id == "whole-negative" ? "aab" : "";
                break;
            case "non-atomic":
            case "atomic":
                int g = p.Capture(p.Alt(p.Lit('a'), p.Seq(p.Lit('a'), p.Lit('b'))), 1), call = p.Call(1);
                if (id == "atomic")
                {
                    call = p.Atomic(call);
                }

                p.Seq(g, call, p.Lit('c'), p.Leaf(Pcre2NodeKind.End));
                subject = "aabc";
                break;
            case "define":
            case "define-negative":
                p.Seq(p.Define(p.Capture(p.Seq(p.Lit('a'), p.Lit('b')), 1)), p.Call(1));
                subject = id == "define" ? "ab" : "ac";
                break;
            case "inner-capture-retry":
                p.Seq(p.Define(p.Capture(p.Alt(p.Capture(p.Lit('a'), 2), p.Capture(p.Seq(p.Lit('a'), p.Lit('b')), 3)), 1)), p.Call(1), p.Lit('c'), p.Leaf(Pcre2NodeKind.End));
                subject = "abc";
                break;
            case "recursive-captures":
                p.Seq(p.Capture(p.Seq(p.Lit('a'), p.Repeat(p.Call(1), 0, 1), p.Capture(p.Lit('b'), 2)), 1), p.Leaf(Pcre2NodeKind.End));
                subject = "aabb";
                break;
            case "most-recent":
                var outer = p.Capture(p.Seq(p.Lit('a'), p.Call(2)), 1);
                var inner = p.Capture(p.Condition(Pcre2ConditionKind.Recursion, p.Lit('x'), p.Lit('b'), p.Reference(1)), 2);
                p.Seq(p.Define(p.Seq(outer, inner)), p.Call(1));
                subject = "ab";
                break;
            case "inactive":
                p.Seq(p.Define(p.Capture(p.Alt(p.Lit('a'), p.Seq(p.Lit('a'), p.Lit('a'))), 1)), p.Call(1),
                    p.Condition(Pcre2ConditionKind.AnyRecursion, p.Lit('x'), p.Lit('b')), p.Leaf(Pcre2NodeKind.End));
                subject = "aab";
                break;
            case "any-recursion":
            case "r0":
                p.Condition(id == "any-recursion" ? Pcre2ConditionKind.AnyRecursion : Pcre2ConditionKind.Recursion,
                    p.Lit('a'), p.Seq(p.Lit('a'), p.Repeat(p.Call(), 0, 1)), id == "r0" ? p.Reference(0, Pcre2ReferenceKind.WholePattern) : -1);
                subject = "aa";
                break;
            case "capture-false":
            case "capture-true":
                p.Seq(p.Repeat(p.Capture(p.Lit('a'), 1), 0, 1), p.Condition(Pcre2ConditionKind.CaptureSet, p.Lit('b'), p.Lit('c'), p.Reference(1)));
                subject = id == "capture-false" ? "c" : "ab";
                break;
            case "open-repeated":
                p.Repeat(p.Capture(p.Seq(p.Lit('a'), p.Condition(Pcre2ConditionKind.CaptureSet, p.Lit('b'), p.Lit('c'), p.Reference(1))), 1), 1);
                subject = "acab";
                break;
            case "version-equal":
            case "version-greater":
                p.Condition(Pcre2ConditionKind.Version, p.Lit('a'), p.Lit('b'), minor: id == "version-equal" ? 46 : 47, atLeast: id == "version-greater");
                subject = id == "version-equal" ? "a" : "b";
                break;
            case "loop":
            case "disabled-loop":
                p.Call();
                subject = "";
                if (id == "disabled-loop")
                {
                    flags |= Pcre2MatchFlags.DisableRecurseloopCheck;
                }

                break;
            case "positive-predicate":
            case "false-negative-capture":
                var assertion = p.Assertion(p.Capture(p.Lit('a'), 1), negative: id == "false-negative-capture");
                p.Predicate(assertion, p.Lit(id == "positive-predicate" ? 'a' : 'b'), p.Lit(id == "positive-predicate" ? 'b' : 'a'));
                subject = "a";
                predicate = true;
                break;
            case "duplicate-name-capture":
                p.Seq(p.Alt(p.Capture(p.Lit('a'), 1), p.Capture(p.Lit('b'), 2)), p.Condition(Pcre2ConditionKind.CaptureSet, p.Lit('c'), p.Lit('d'), p.Reference(1, Pcre2ReferenceKind.Named, 1, 2)));
                subject = "bc";
                break;
            case "absent-else":
                p.Condition(Pcre2ConditionKind.Version, p.Lit('a'), major: 99, minor: 0);
                subject = "";
                break;
            default:
                throw new InvalidOperationException("Unmapped independent input.");
        }
        var fixture = new RecursionFixture(p.Build()) { PredicateDriver = predicate };
        var pairs = Enumerable.Repeat(new Pcre2OffsetPair(true, 999, 999), fixture.Program.GroupCount + 2).ToArray();
        var options = new Pcre2MatchOptions { Flags = flags };
        var limits = new Pcre2MatchLimits(maxWork: 10000, maxDepth: 64);
        var result = width == 8 ? fixture.Match(Encoding.ASCII.GetBytes(subject), pairs, options, limits) : fixture.Match(subject, pairs, options, limits);
        Assert.Equal(native.RootElement.GetProperty("returnCode").GetInt32(), result.NativeReturnCode);
        var index = 0;
        foreach (var pair in native.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var endpoints = pair.EnumerateArray().ToArray();
            var set = endpoints[0].ValueKind != JsonValueKind.Null;
            Assert.Equal(set, pairs[index].Participated);
            if (set)
            { Assert.Equal(endpoints[0].GetInt32(), pairs[index].Start); Assert.Equal(endpoints[1].GetInt32(), pairs[index].End); }
            index++;
        }
        if (result.NativeReturnCode < 0)
        {
            Assert.All(pairs, x => Assert.Equal(new(true, 999, 999), x));
        }

        Assert.Equal(new(true, 999, 999), pairs[^1]);
        Assert.Equal(new(true, 999, 999), pairs[^2]);
        if (result.NativeReturnCode >= 0)
        {
            Assert.Equal(0, fixture.ReturnDepth);
        }
    }
}
