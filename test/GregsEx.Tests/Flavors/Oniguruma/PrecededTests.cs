using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

// The units a leading look-behind requires before a match (OnigCompiler.Preceded), which a search tests before an
// attempt and a scanner's walk applies with its start mask: a longer branch requires the unit it ends with.
public class PrecededTests
{
    private static OnigurumaPattern Pattern(string source) => OnigurumaPattern.Parse(source, RegexProfiles.OnigurumaTextMate);

#if !GREGSEX_NETSTANDARD_ASSET
    [Theory]
    [InlineData(@"(?<=\bconst\b)\s*x", "t")]
    [InlineData(@"(?:(?<=^\bfield\b)\s+(\w+))", "d")]
    [InlineData(@"(?<=[(=,]|=>|^return)\s*x", "(,=>n")]
    [InlineData(@"(?<=(?:ab)+)x", "b")]
    [InlineData(@"(?<=a|bc?)x", null)]
    [InlineData(@"(?<=ab|c*)x", null)]
    [InlineData(@"(?<=\w+)x", null)]
    [InlineData(@"(?i)(?<=ff)x", null)]
    [InlineData(@"(?<=[a-z]b)x", "b")]
    [InlineData(@"(?<=q(?:a|b))x", "ab")]
    [InlineData(@"(?<=q(?:a|b?))x", null)]
    [InlineData(@"(?<=const )foo", " ")]
    public void ALongerBranchRequiresTheUnitItEndsWith(string source, string? preceded)
    {
        var program = Pattern(source).Program;
        if (preceded is null)
        {
            Assert.Null(program.PrecededBy);
            return;
        }

        Assert.NotNull(program.PrecededBy);
        for (var c = (char)0; c < 128; c++)
        {
            Assert.Equal(preceded.Contains(c), program.PrecededBy!.Contains(c));
        }

        Assert.False(program.PrecededAtStart);
    }
#endif

    [Theory]
    [InlineData(@"(?<=\bconst\b)\s*\w+", "const a; xconst b; const")]
    [InlineData(@"(?<=^\bfield\b)\s+(\w+)", "field a\nfield b field c")]
    [InlineData(@"(?<=[(=,]|=>|^return)\s*x", "return x (x =>x ax ,x\nreturn x")]
    [InlineData(@"(?<=a|bc?)x", "ax bx bcx cx")]
    [InlineData(@"(?<=ab|c*)x", "abx x ccx")]
    [InlineData(@"(?i)(?<=ff)x", "ffx FFx ﬀx fx")]
    [InlineData(@"(?<=(?:ab)+)x", "abx ababx bx")]
    [InlineData(@"(?<=^|,)x", "x,x ,x\nx éx")]
    [InlineData(@"(?<=a|b)x", "éx ax 😀x bx")]
    [InlineData(@"(?<=q(?:a|b))x", "qax qbx qx x")]
    [InlineData(@"(?<=q(?:a|b?))x", "qax qbx qx x")]
    [InlineData(@"(?<=const )foo", "const foo xconst foo constfoo foo const  foo")]
    public void FindAgreesWithAnAttemptAtEveryStart(string source, string subject)
    {
        // \G leaves no leading look-behind, so the reference attempts every start without the filter.
        var workspace = Pattern(source).CreateWorkspace();
        var reference = Pattern(@"\G(?:" + source + ")").CreateWorkspace();
        for (var startAt = 0; startAt <= subject.Length; startAt++)
        {
            RegexMatchRange? expected = null;
            for (var pos = startAt; pos <= subject.Length && expected is null; pos++)
            {
                if (reference.Find(subject, pos, out var at) == RegexSearchStatus.Match && at.Index == pos)
                {
                    expected = at;
                }
            }

            var status = workspace.Find(subject, startAt, out var match);
            Assert.Equal(expected is not null, status == RegexSearchStatus.Match);
            if (expected is { } e)
            {
                Assert.Equal(e, match);
            }

            var scanner = new OnigurumaScanner([Pattern(source), Pattern("$x^")]);
            Assert.Equal(expected is not null, scanner.FindNextMatch(subject, startAt, out var index) && index == 0);
            if (expected is { } s)
            {
                Assert.Equal(s.Index, scanner.Captures[0].Index);
                Assert.Equal(s.Length, scanner.Captures[0].Length);
            }
        }
    }
}
