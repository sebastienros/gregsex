using Xunit;

namespace GregsEx.Tests.Integration.Core;

public class CoreInputIterationTests
{
    [Theory]
    [InlineData(RegexInputKind.Utf16, RegularCoreMode.UnicodeScalars)]
    [InlineData(RegexInputKind.Utf8, RegularCoreMode.UnicodeScalars)]
    [InlineData(RegexInputKind.Utf16, RegularCoreMode.Utf16CodeUnits)]
    [InlineData(RegexInputKind.Bytes, RegularCoreMode.RawBytes)]
    public void AllRepresentationsKeepOriginalOffsetsAndOperationAgreement(RegexInputKind kind, RegularCoreMode mode)
    {
        var p = RegexPattern.Parse("(.)", new RegularCoreProfile(mode), new(kind));
        using var w = p.CreateWorkspace();
        var groups = new RegexCapture[2];
        if (kind == RegexInputKind.Utf16)
        {
            Assert.Equal(RegexSearchStatus.Match, w.FindCaptures("x😀", 1, groups, out var r));
            Assert.Equal(new(1, mode == RegularCoreMode.UnicodeScalars ? 2 : 1), r);
            Assert.True(p.IsMatch("x😀"));
            Assert.Equal(new(true, r.Index, r.Length), groups[1]);
        }
        else
        {
            byte[] input = [120, 240, 159, 152, 128];
            Assert.Equal(RegexSearchStatus.Match, w.FindCaptures(input, 1, groups, out var r));
            Assert.Equal(new(1, kind == RegexInputKind.Utf8 ? 4 : 1), r);
            Assert.True(p.IsMatch(input));
            Assert.Equal(new(true, r.Index, r.Length), groups[1]);
        }
    }

    [Fact]
    public void WholeInputValidityPrecedesInteriorBoundaryAndEarlyMatches()
    {
        foreach (var kind in new[] { RegexInputKind.Utf16, RegexInputKind.Utf8 })
        {
            var p = RegexPattern.Parse("", new RegularCoreProfile(), new(kind));
            using var w = p.CreateWorkspace();
            if (kind == RegexInputKind.Utf16)
            {
                Assert.Equal(RegexSearchStatus.InvalidInput, w.Find("😀", 1, out _));
                Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, w.LastFailure.Code);
                Assert.Equal(RegexSearchStatus.InvalidInput, w.Find(['\uD83D', '\uDE00', '\uD800'], 1, out _));
                Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, w.LastFailure.Code);
                Assert.Equal(2, w.LastFailure.Offset);
                Assert.Equal(RegexSearchStatus.Match, w.Find("😀", 2, out var r));
                Assert.Equal(new(2, 0), r);
            }
            else
            {
                byte[] valid = [240, 159, 152, 128];
                for (var start = 1; start < 4; start++)
                { Assert.Equal(RegexSearchStatus.InvalidInput, w.Find(valid, start, out _)); Assert.Equal(RegexExecutionFailureCode.InvalidStartBoundary, w.LastFailure.Code); }
                Assert.Equal(RegexSearchStatus.InvalidInput, w.Find([240, 159, 152, 128, 255], 1, out _));
                Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, w.LastFailure.Code);
                Assert.Equal(4, w.LastFailure.Offset);
                Assert.Equal(RegexSearchStatus.Match, w.Find(valid, 4, out var r));
                Assert.Equal(new(4, 0), r);
            }
        }
    }

    [Theory]
    [InlineData("", "x😀", new int[] { 0, 1, 3 })]
    [InlineData("a*", "baa", new int[] { 0, 1, 3 })]
    [InlineData("a+", "baa", new int[] { 1 })]
    [InlineData("$", "x😀", new int[] { 3 })]
    [InlineData("^", "x😀", new int[] { 0 })]
    [InlineData("z", "x😀", new int[] { })]
    public void CallerLoopEmitsTerminalEmptyOnceAndAdvancesOneScalar(string source, string input, int[] expected)
    {
        var p = RegexPattern.Parse(source, new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        var indices = new List<int>();
        var start = 0;
        while (w.Find(input, start, out var r) == RegexSearchStatus.Match)
        {
            indices.Add(r.Index);
            Assert.True(indices.Count <= input.Length + 1);
            if (r.Length != 0)
            {
                start = r.Index + r.Length;
            }
            else if (r.Index == input.Length)
            {
                break;
            }
            else
            {
                start = r.Index + (char.IsHighSurrogate(input[r.Index]) ? 2 : 1);
            }
        }
        Assert.Equal(expected, indices);
    }

    [Theory]
    [InlineData(RegularCoreMode.UnicodeScalars, RegexInputKind.Utf8)]
    [InlineData(RegularCoreMode.RawBytes, RegexInputKind.Bytes)]
    public void ByteCallerLoopAdvancesLegalNativeWidths(RegularCoreMode mode, RegexInputKind kind)
    {
        byte[] input = [120, 240, 159, 152, 128];
        var p = RegexPattern.Parse("", new RegularCoreProfile(mode), new(kind));
        using var w = p.CreateWorkspace();
        var indices = new List<int>();
        var start = 0;
        while (w.Find(input, start, out var r) == RegexSearchStatus.Match)
        {
            indices.Add(r.Index);
            if (r.Index == input.Length)
            {
                break;
            }

            start = r.Index + (kind == RegexInputKind.Bytes || input[r.Index] < 128 ? 1 : 4);
        }
        Assert.Equal(kind == RegexInputKind.Bytes ? new[] { 0, 1, 2, 3, 4, 5 } : [0, 1, 5], indices);
    }

    [Fact]
    public void CodeUnitCallerLoopIncludesInteriorSurrogatesAndFailureTerminates()
    {
        var p = RegexPattern.Parse("", new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits));
        using var w = p.CreateWorkspace();
        var indices = new List<int>();
        const string input = "😀";
        for (var start = 0; ;)
        { Assert.Equal(RegexSearchStatus.Match, w.Find(input, start, out var r)); indices.Add(r.Index); if (r.Index == input.Length) { break; } start = r.Index + 1; }
        Assert.Equal(new[] { 0, 1, 2 }, indices);
        using var strict = RegexPattern.Parse("", new RegularCoreProfile()).CreateWorkspace();
        Assert.Equal(RegexSearchStatus.InvalidInput, strict.Find(['\uD800'], 0, out var failed));
        Assert.Equal(default, failed);
    }

    [Fact]
    public void WholeSubjectValidationIsChargedExactlyOnce()
    {
        var p = RegexPattern.Parse("^", new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        long Minimum(string input)
        {
            for (var budget = 1; budget < 10000; budget++)
            {
                if (w.Find(input, 0, out _, new() { Limits = new(budget) }) == RegexSearchStatus.Match)
                {
                    return budget;
                }
            }

            throw new InvalidOperationException();
        }
        Assert.Equal(100, Minimum(new string('x', 100)) - Minimum(""));
    }

    [Fact]
    public void PreparedPublicOperationsAndCallerIterationPreservePreparedResults()
    {
        var p = RegexPattern.Parse("(a?)(b)?", new RegularCoreProfile());
        using var w = p.CreateWorkspace();
        var groups = new RegexCapture[3];
        var checksum = 0;
        int Cycle()
        {
            var sum = (int)w.IsMatch("ab");
            sum += (int)w.Find("zz", 0, out var r);
            sum += r.Index;
            sum += (int)w.FindCaptures("", 0, groups, out r);
            var start = 0;
            while (w.Find("ab", start, out r) == RegexSearchStatus.Match)
            {
                sum += r.Index;
                if (r.Length > 0)
                {
                    start = r.Index + r.Length;
                }
                else if (r.Index == 2)
                {
                    break;
                }
                else
                {
                    start = r.Index + 1;
                }
            }
            return sum;
        }
        // Repeat every iteration branch with caller-owned storage.
        for (var i = 0; i < 4; i++)
        {
            checksum += Cycle();
        }

        for (var i = 0; i < 4096; i++)
        {
            checksum += Cycle();
        }

        Assert.True(checksum > 0);
    }
}
