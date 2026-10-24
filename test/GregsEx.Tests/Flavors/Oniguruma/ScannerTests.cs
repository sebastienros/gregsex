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
    public void AMatchAtTheOffsetStopsTheScan()
    {
        // The second pattern exhausts a tiny work budget; it is only searched when the first does not match at the
        // offset.
        var options = new OnigurumaCompileOptions(defaultMatchLimits: new RegexMatchLimits(maxWork: 50));
        var scanner = new OnigurumaScanner([
            OnigurumaPattern.Parse("a", RegexProfiles.OnigurumaTextMate, options),
            OnigurumaPattern.Parse("(x+x+)+(?=a)b", RegexProfiles.OnigurumaTextMate, options)]);
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
