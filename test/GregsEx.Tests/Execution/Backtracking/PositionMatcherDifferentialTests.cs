using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
using GregsEx.Tests.Flavors.EcmaScript.Parsing;
using Xunit;

namespace GregsEx.Tests.Execution.Backtracking;

// The position matcher runs only for a host-sized search (SearchPrevalidated with hostSized: true), so nothing else in
// this suite reaches it. Each case here is searched both ways, from every start, sticky and not, for captures and for the
// range alone, and must answer exactly as the VM does.
public sealed class PositionMatcherDifferentialTests
{
    private const string Alphabet = "abcdxyAB:,[ \n\"-.@1e";

    private static readonly (string Pattern, string Flags)[] Cases =
    [
        ("\"[^\"\\\\\\n\\r]*\"|true|false|null|-?\\d+(?:\\.\\d*)?(:?[eE][+\\-]?\\d+)?", ""),
        ("(?:^|:|,)(?:\\s*\\[)+", ""),
        ("^[a-zA-Z0-9\\-\\._]+@[a-zA-Z0-9\\-_]+(\\.?[a-zA-Z0-9\\-_]*)\\.[a-zA-Z]{2,3}$", ""),
        ("(a|b)*c", ""),
        ("((a)|b)+c", ""),
        ("(a|ab)(c|bcd)(d*)", ""),
        ("a(b|c)*?d", ""),
        ("x{2,4}y?", ""),
        ("(?:ab|a)(?:bc|c)", ""),
        ("\\b(?:foo|ba)r?\\b", ""),
        ("^(?:a|b)+$", "m"),
        ("(?:A|b)+", "i"),
        ("([a-c])([a-c]?)\\2", ""),
        ("(a+)+b", ""),
        ("[^ab]+|a{2}", ""),
    ];

    public static TheoryData<string, string> Patterns()
    {
        var data = new TheoryData<string, string>();
        foreach (var (pattern, flags) in Cases)
        {
            data.Add(pattern, flags);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void HostSizedSearchesAnswerAsTheVm(string pattern, string flags)
    {
        var executor = Compile(pattern, flags);
        var capacity = executor.GetRequiredCapacity(SearchOperation.Captures, 4096);
        var states = new int[capacity.StateSlots];
        var captures = new int[capacity.CaptureSlots];
        var scratch = new int[capacity.ScratchSlots];
        var random = new Random(pattern.Length * 31 + flags.Length);
        var inputs = new List<string> { "", "a", "abc", "aac", "\"ab\", true, -1.5e3,null", "x@y.com", "a:[ [b,\n [c", "ab\nba\n", "foo bar barr" };
        for (var i = 0; i < 60; i++)
        {
            var chars = new char[random.Next(0, 12)];
            for (var c = 0; c < chars.Length; c++)
            {
                chars[c] = Alphabet[random.Next(Alphabet.Length)];
            }

            inputs.Add(new string(chars));
        }

        foreach (var input in inputs)
        {
            for (var start = 0; start <= input.Length; start++)
            {
                foreach (var sticky in new[] { false, true })
                {
                    foreach (var operation in new[] { SearchOperation.Captures, SearchOperation.Range })
                    {
                        var expected = Run(executor, input, start, sticky, operation, hostSized: false, states, captures, scratch, out var expectedMatch, out var expectedGroups);
                        var actual = Run(executor, input, start, sticky, operation, hostSized: true, states, captures, scratch, out var actualMatch, out var actualGroups);
                        var where = $"/{pattern}/{flags} on \"{input}\" from {start}, sticky {sticky}, {operation}";
                        Assert.True(expected == actual, where + $": {actual} where the VM answers {expected}");
                        Assert.True(expectedMatch == actualMatch, where + $": {actualMatch} where the VM matches {expectedMatch}");
                        Assert.True(expectedGroups.AsSpan().SequenceEqual(actualGroups), where + ": captures differ");
                    }
                }
            }
        }
    }

    [Fact]
    public void MostOfThesePatternsRunOnThePositionMatcher()
    {
        var used = 0;
        var total = 0;
        foreach (var (pattern, flags) in Cases)
        {
            total++;
            var executor = Compile(pattern, flags);
            // The matcher is built on the first host-sized search.
            var capacity = executor.GetRequiredCapacity(SearchOperation.Captures, 64);
            var state = new RunState(new(new int[capacity.StateSlots], new int[capacity.CaptureSlots], new int[capacity.ScratchSlots]), long.MaxValue, default, null);
            executor.SearchPrevalidated("a", 0, false, SearchOperation.Range, 64, ref state, [], out _, hostSized: true);
            used += executor.UsesPositionMatcher ? 1 : 0;
        }

        Assert.True(used >= 10, used + " of " + total + " patterns ran on the position matcher");
    }

    private static EcmaExecutor Compile(string pattern, string flags)
    {
        var budget = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var unicode, out var error), error.ToString());
        Assert.True(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), unicode!, ref budget, out var program, out var diagnostic),
            pattern + ": " + diagnostic);
        Assert.True(new EcmaExecutorFactory().TryCreate(program!, unicode!, ref budget, out var executor, out error), error.ToString());
        return (EcmaExecutor)executor!;
    }

    private static RegexSearchStatus Run(EcmaExecutor executor, string input, int start, bool sticky, SearchOperation operation, bool hostSized,
        int[] states, int[] captures, int[] scratch, out RegexMatchRange match, out RegexCapture[] groups)
    {
        groups = new RegexCapture[executor.Program.GroupCount];
        var state = new RunState(new(states, captures, scratch), long.MaxValue, default, null);
        return executor.SearchPrevalidated(input, start, sticky, operation, 4096, ref state,
            operation == SearchOperation.Captures ? groups : [], out match, hostSized);
    }
}
