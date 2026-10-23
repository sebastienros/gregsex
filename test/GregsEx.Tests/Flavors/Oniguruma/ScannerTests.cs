using Xunit;

namespace GregsEx.Tests.Flavors.Oniguruma;

public class ScannerTests
{
    private static OnigurumaScanner Scanner(params string[] patterns) => new(patterns, RegexProfiles.OnigurumaTextMate);

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
