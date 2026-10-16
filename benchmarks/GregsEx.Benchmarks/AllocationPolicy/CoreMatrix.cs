using GregsEx;
using System.Text;
namespace AllocationPolicy;
internal static class CoreMatrix
{
    internal static Workload Create(int representation, RegexExecutionMode mode, int fixture)
    {
        var profile = new RegularCoreProfile(representation switch
        {
            2 => RegularCoreMode.Utf16CodeUnits,
            3 => RegularCoreMode.RawBytes,
            _ => RegularCoreMode.UnicodeScalars
        });
        var kind = representation switch { 1 => RegexInputKind.Utf8, 3 => RegexInputKind.Bytes, _ => RegexInputKind.Utf16 };
        string source = fixture switch { 2 => "(a*)", 3 => "(.)", _ => "(a+)(b)?" };
        string text = fixture switch { 0 => "xxaa!", 1 or 2 => "xxx!", _ => representation == 2 ? "\uD800" : "\U0001F600" };
        byte[] bytes = representation == 3 && fixture == 3 ? [255] : Encoding.UTF8.GetBytes(text);
        var pattern = RegexPattern.Parse(source, profile, new(kind, mode));
        var workspace = pattern.CreateWorkspace();
        var groups = new RegexCapture[pattern.GroupCount];
        var capacity = workspace.Capacity;
        long reserved = workspace.ReservedBytes;
        var expectedStatus = fixture == 1 ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match;
        int width = fixture == 3 ? representation switch { 0 => 2, 1 => 4, _ => 1 } : fixture == 0 ? 2 : 0;
        var expectedRange = fixture == 0 ? new RegexMatchRange(2, 2) : new RegexMatchRange(0, width);
        Check.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
        Check.Equal(expectedStatus, Boolean());
        Check.Equal(expectedStatus, Find(out var range));
        Check.Equal(expectedRange, range);
        Check.Equal(expectedStatus, Captures(out range));
        Check.Equal(expectedRange, range);
        for (int g = 0; g < groups.Length; g++)
            Check.Equal(fixture == 1 || g == 2 ? default : new RegexCapture(true, expectedRange.Index, expectedRange.Length), groups[g]);
        int expectedCount = fixture switch { 1 => 0, 2 => 5, _ => 1 };
        Check.Equal(expectedCount, Iterate());
        int expectedCycle = (fixture == 1 ? 0 : 3 + 2 * (expectedRange.Index + expectedRange.Length)) + expectedCount;
        foreach (var capture in groups)
            if (capture.Participated) expectedCycle += 1 + capture.Index + capture.Length;
        Check.Equal(expectedCycle, Cycle());
        return new Workload(() => Cycle(), workspace.Dispose,metadata:new{source,text=InputIdentity.Chars(text),bytes=InputIdentity.Bytes(bytes),kind,mode,representation,fixture,expectedStatus,expectedRange,expectedCycle,expectedCount,groups=pattern.GroupCount,plan_bytes=pattern.RetainedBytes,reserved_bytes=reserved,capacity});
        RegexSearchStatus Boolean() => kind == RegexInputKind.Utf16 ? workspace.IsMatch(text) : workspace.IsMatch(bytes);
        RegexSearchStatus Find(out RegexMatchRange r, int start = 0) => kind == RegexInputKind.Utf16
            ? workspace.Find(text, start, out r) : workspace.Find(bytes, start, out r);
        RegexSearchStatus Captures(out RegexMatchRange r) => kind == RegexInputKind.Utf16
            ? workspace.FindCaptures(text, 0, groups, out r) : workspace.FindCaptures(bytes, 0, groups, out r);
        int Iterate()
        {
            int count = 0, start = 0;
            int length = kind == RegexInputKind.Utf16 ? text.Length : bytes.Length;
            while (Find(out var r, start) == RegexSearchStatus.Match)
            {
                count++;
                if (r.Length > 0) start = r.Index + r.Length;
                else if (r.Index == length) break;
                else if (representation == 0 && char.IsHighSurrogate(text[r.Index])) start = r.Index + 2;
                else if (representation == 1)
                    start = r.Index + (bytes[r.Index] < 128 ? 1 : bytes[r.Index] < 224 ? 2 : bytes[r.Index] < 240 ? 3 : 4);
                else start = r.Index + 1;
            }
            return count;
        }
        int Cycle()
        {
            int sum = Boolean() == RegexSearchStatus.Match ? 1 : 0;
            sum += Find(out var r) == RegexSearchStatus.Match ? 1 : 0;
            sum += r.Index + r.Length;
            sum += Captures(out r) == RegexSearchStatus.Match ? 1 : 0;
            sum += r.Index + r.Length;
            for (int g = 0; g < groups.Length; g++)
                if (groups[g].Participated) sum += 1 + groups[g].Index + groups[g].Length;
            return sum + Iterate();
        }
    }
}
