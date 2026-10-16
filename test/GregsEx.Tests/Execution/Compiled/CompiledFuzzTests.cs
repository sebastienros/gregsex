using System.Text;
using Xunit;

namespace GregsEx.Tests.Execution.Compiled;

// Seeded grammar fuzzing: every generated pattern that lowers must agree with the interpreter on every operation,
// including under a deterministic work ceiling so budget-exhaustion statuses are compared as well.
public class CompiledFuzzTests
{
    private static string Atom(Random r, int depth)
    {
        return r.Next(depth > 2 ? 5 : 8) switch
        {
            0 => ((char)('a' + r.Next(4))).ToString(),
            1 => "[a-c]",
            2 => "[^a]",
            3 => ".",
            4 => "é",
            5 => "(" + Alt(r, depth + 1) + ")",
            6 => "(?:" + Alt(r, depth + 1) + ")",
            _ => "[a-b]",
        };
    }

    private static string Piece(Random r, int depth)
    {
        var atom = Atom(r, depth);
        return r.Next(7) switch { 0 => atom + "*", 1 => atom + "+", 2 => atom + "?", 3 => atom + "*?", 4 => atom + "{1,3}", _ => atom };
    }

    private static string Seq(Random r, int depth)
    {
        var b = new StringBuilder();
        for (int i = 0, n = r.Next(1, 5); i < n; i++)
        {
            b.Append(Piece(r, depth));
        }

        return b.ToString();
    }

    private static string Alt(Random r, int depth)
        => r.Next(3) == 0 ? Seq(r, depth) + "|" + Seq(r, depth) : Seq(r, depth);

    private static string Subject(Random r)
    {
        var b = new StringBuilder();
        for (int i = 0, n = r.Next(0, 40); i < n; i++)
        {
            b.Append("abcdéx\n😀"[r.Next(8)]);
        }

        return b.ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomPatternsAgreeWithTheInterpreter(int seed)
    {
        var random = new Random(seed * 7919);
        var lowered = 0;
        for (var i = 0; i < 400; i++)
        {
            var pattern = Alt(random, 0);
            var limits = i % 4 == 0 ? new RegexMatchLimits(3000, 1 << 26) : (RegexMatchLimits?)null;
            RegexPattern oracle;
            RegexPattern compiled;
            try
            {
                oracle = RegexPattern.Parse(pattern, new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits),
                    new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));
                compiled = oracle.Compile();
            }
            catch (Exception e) when (e is RegexParseException or NotSupportedException) { continue; }
            lowered++;
            var a = oracle.CreateWorkspace();
            var b = compiled.CreateWorkspace();
            var ga = new RegexCapture[oracle.GroupCount];
            var gb = new RegexCapture[compiled.GroupCount];
            for (var s = 0; s < 12; s++)
            {
                var text = Subject(random);
                var start = random.Next(0, text.Length + 1);
                var expected = a.FindCaptures(text, start, ga, out var er);
                var actual = b.FindCaptures(text, start, gb, out var ar);
                if (expected == RegexSearchStatus.LimitExceeded)
                { Assert.True(actual is RegexSearchStatus.LimitExceeded or RegexSearchStatus.Match or RegexSearchStatus.NoMatch); continue; }
                Assert.True(expected == actual, $"{pattern} on '{text}'@{start}: {expected} vs {actual}");
                Assert.True(er == ar, $"{pattern} on '{text}'@{start}: {er} vs {ar}");
                Assert.True(ga.AsSpan().SequenceEqual(gb), $"{pattern} on '{text}'@{start}: captures differ");
            }
        }
        Assert.True(lowered > 20, $"only {lowered} random patterns lowered");
    }
}
