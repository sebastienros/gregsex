using System.Text;
using Xunit;

namespace GregsEx.Tests.Performance.Core;

public sealed class AllocationMatrixTests(ITestOutputHelper output)
{
    public static IEnumerable<TheoryDataRow<int, RegexExecutionMode, int>> Cases()
    {
        for (var representation = 0; representation < 4; representation++)
        {
            foreach (var mode in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
            {
                for (var fixture = 0; fixture < 4; fixture++)
                {
                    yield return new TheoryDataRow<int, RegexExecutionMode, int>(representation, mode, fixture);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PreparedBooleanRangeCapturesAndIterationPreservePreparedResults(
        int representation, RegexExecutionMode mode, int fixture)
    {
        var profile = new RegularCoreProfile(representation switch
        {
            2 => RegularCoreMode.Utf16CodeUnits,
            3 => RegularCoreMode.RawBytes,
            _ => RegularCoreMode.UnicodeScalars
        });
        var kind = representation switch { 1 => RegexInputKind.Utf8, 3 => RegexInputKind.Bytes, _ => RegexInputKind.Utf16 };
        var source = fixture switch { 2 => "(a*)", 3 => "(.)", _ => "(a+)(b)?" };
        var text = fixture switch { 0 => "xxaa!", 1 or 2 => "xxx!", _ => representation == 2 ? "\uD800" : "\U0001F600" };
        var bytes = representation == 3 && fixture == 3 ? [255] : Encoding.UTF8.GetBytes(text);
        var pattern = RegexPattern.Parse(source, profile, new(kind, mode));
        using var workspace = pattern.CreateWorkspace();
        var groups = new RegexCapture[pattern.GroupCount];
        var capacity = workspace.Capacity;
        var reserved = workspace.ReservedBytes;
        var expectedStatus = fixture == 1 ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match;
        var width = fixture == 3 ? representation switch { 0 => 2, 1 => 4, _ => 1 } : fixture == 0 ? 2 : 0;
        var expectedRange = fixture == 0 ? new RegexMatchRange(2, 2) : new RegexMatchRange(0, width);
        Assert.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
        Assert.Equal(expectedStatus, Boolean());
        Assert.Equal(expectedStatus, Find(out var range));
        Assert.Equal(expectedRange, range);
        Assert.Equal(expectedStatus, Captures(out range));
        Assert.Equal(expectedRange, range);
        for (var g = 0; g < groups.Length; g++)
        {
            Assert.Equal(fixture == 1 || g == 2 ? default : new RegexCapture(true, expectedRange.Index, expectedRange.Length), groups[g]);
        }

        var expectedCount = fixture switch { 1 => 0, 2 => 5, _ => 1 };
        Assert.Equal(expectedCount, Iterate());
        var expectedCycle = (fixture == 1 ? 0 : 3 + 2 * (expectedRange.Index + expectedRange.Length)) + expectedCount;
        foreach (var capture in groups)
        {
            if (capture.Participated)
            {
                expectedCycle += 1 + capture.Index + capture.Length;
            }
        }

        Assert.Equal(expectedCycle, Cycle());
        for (var i = 0; i < 4; i++)
        {
            _ = Cycle();
        }

        long checksum = 0;
        for (var i = 0; i < 4_096; i++)
        {
            checksum += Cycle();
        }

        Assert.Equal(4_096L * expectedCycle, checksum);
        Assert.Equal(capacity, workspace.Capacity);
        Assert.Equal(reserved, workspace.ReservedBytes);
        Assert.Equal(default, workspace.LastFailure);
        output.WriteLine($"rep={representation} mode={mode} fixture={fixture} checksum={checksum} plan={pattern.RetainedBytes} reserved={reserved} capacity={capacity}");

        RegexSearchStatus Boolean() => kind == RegexInputKind.Utf16 ? workspace.IsMatch(text) : workspace.IsMatch(bytes);
        RegexSearchStatus Find(out RegexMatchRange r, int start = 0) => kind == RegexInputKind.Utf16
            ? workspace.Find(text, start, out r) : workspace.Find(bytes, start, out r);
        RegexSearchStatus Captures(out RegexMatchRange r) => kind == RegexInputKind.Utf16
            ? workspace.FindCaptures(text, 0, groups, out r) : workspace.FindCaptures(bytes, 0, groups, out r);
        int Iterate()
        {
            int count = 0, start = 0;
            var length = kind == RegexInputKind.Utf16 ? text.Length : bytes.Length;
            while (Find(out var r, start) == RegexSearchStatus.Match)
            {
                count++;
                if (r.Length > 0)
                {
                    start = r.Index + r.Length;
                }
                else if (r.Index == length)
                {
                    break;
                }
                else if (representation == 0 && char.IsHighSurrogate(text[r.Index]))
                {
                    start = r.Index + 2;
                }
                else if (representation == 1)
                {
                    start = r.Index + (bytes[r.Index] < 128 ? 1 : bytes[r.Index] < 224 ? 2 : bytes[r.Index] < 240 ? 3 : 4);
                }
                else
                {
                    start = r.Index + 1;
                }
            }
            return count;
        }
        int Cycle()
        {
            var sum = Boolean() == RegexSearchStatus.Match ? 1 : 0;
            sum += Find(out var r) == RegexSearchStatus.Match ? 1 : 0;
            sum += r.Index + r.Length;
            sum += Captures(out r) == RegexSearchStatus.Match ? 1 : 0;
            sum += r.Index + r.Length;
            for (var g = 0; g < groups.Length; g++)
            {
                if (groups[g].Participated)
                {
                    sum += 1 + groups[g].Index + groups[g].Length;
                }
            }

            return sum + Iterate();
        }
    }

    [Fact]
    public void LifecycleAllocationScopesAreSeparateFromWarmedSearch()
    {
        var profile = new RegularCoreProfile();
        _ = RegexPattern.Parse("(a+)(b)?", profile);
        var pattern = RegexPattern.Parse("(a+)(b)?", profile);
        using var workspace = pattern.CreateWorkspace(new(default));
        workspace.Reserve(pattern.CaptureCapacity);
        _ = pattern.IsMatch("aa");
        var matched = pattern.IsMatch("aa");
        Assert.True(matched);
        Assert.Equal(pattern.CaptureCapacity, workspace.Capacity);
        Assert.True(pattern.RetainedBytes > 0);
        Assert.True(workspace.ReservedBytes > 0);
        output.WriteLine($"lifecycle semantic scopes plan={pattern.RetainedBytes} reserved={workspace.ReservedBytes}");
    }
}
