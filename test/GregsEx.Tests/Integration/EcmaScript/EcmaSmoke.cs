namespace GregsEx.Tests.Integration.EcmaScript;

internal static class EcmaSmoke
{
    internal static int Run(bool measure, Action<string> observe)
    {
        var count = 0;
        foreach (var mode in new[] { EcmaScriptFlags.None, EcmaScriptFlags.Unicode, EcmaScriptFlags.UnicodeSets })
        {
            foreach (var policy in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
            {
                foreach (var scenario in new[] { "backreference", "lookbehind", "unset", "empty", "unicode", "miss" })
                {
                    var (source, input, start, index, length, yes) = scenario switch
                    {
                        "backreference" => ("(?<x>a+)\\k<x>", "xaaaa", 0, 1, 4, true),
                        "lookbehind" => ("(?<=x)(a)", "xa", 1, 1, 1, true),
                        "unset" => ("(a|(b))\\2", "xa", 0, 1, 1, true),
                        "empty" => ("()", "x😀", 0, 0, 0, true),
                        "unicode" => ("(.)", "x😀", 1, 1, mode == EcmaScriptFlags.None ? 1 : 2, true),
                        _ => ("(a+)\\1", "zzz", 0, 0, 0, false)
                    };
                    var profile = new EcmaScriptProfile(mode | EcmaScriptFlags.Global | EcmaScriptFlags.HasIndices);
                    var p = RegexPattern.Parse(source, profile, new(executionMode: policy));
                    using var w = p.CreateWorkspace();
                    using var m = EcmaScriptMatcher.Bind(p, frameCapacity: 32);
                    var groups = new RegexCapture[p.GroupCount + 1];
                    groups[^1] = new(true, 999, 999);
                    var status = w.FindCaptures(input, start, groups, out var range);
                    Check(status == (yes ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch), "generic status");
                    Check(range == (yes ? new RegexMatchRange(index, length) : default), "generic native range");
                    Check(groups[^1] == new RegexCapture(true, 999, 999), "generic excess sentinel");
                    m.LastIndex = start;
                    Check(m.Exec(input, groups, out var typed) == status && typed == range, "typed range/status");
                    Check(groups[^1] == new RegexCapture(true, 999, 999), "typed excess sentinel");
                    var expected = Cycle(w, m, input, start, groups);
                    // The legacy boolean is retained for existing callers; both values are semantic-only.
                    Check(Cycle(w, m, input, start, groups) == expected, "repeat checksum");
                    observe($"ECMA semantic {mode}/{policy}/{scenario} checksum={expected}");
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    Check(w.IsMatch("", options: new() { CancellationToken = cancellation.Token }) == RegexSearchStatus.Cancelled, "generic empty cancellation");
                    Check(m.Test("", new() { CancellationToken = cancellation.Token }) == RegexSearchStatus.Cancelled, "typed empty cancellation");
                    Check(w.Find(input, start, out _) == status, "generic reuse");
                    count++;
                }
            }
        }

        return count;
    }

    private static long Cycle(RegexWorkspace w, EcmaScriptMatcher m, string input, int start, RegexCapture[] groups)
    {
        long sum = (int)w.IsMatch(input, start);
        sum += (int)w.Find(input, start, out var range) + range.Index + 17L * range.Length;
        sum += (int)w.FindCaptures(input, start, groups, out range) + range.Index + 7L * range.Length;
        for (var i = 0; i < groups.Length - 1; i++)
        {
            sum += (groups[i].Participated ? 1 : 0) + groups[i].Index + 31L * groups[i].Length;
        }

        m.LastIndex = start;
        sum += (int)m.Test(input);
        m.LastIndex = start;
        sum += (int)m.Exec(input, groups, out range) + range.Index + 11L * range.Length;
        for (var i = 0; i < groups.Length - 1; i++)
        {
            sum += (groups[i].Participated ? 1 : 0) + groups[i].Index + 13L * groups[i].Length;
        }

        m.LastIndex = start;
        while (m.ExecNext(input, groups, out range) == RegexSearchStatus.Match)
        {
            sum += range.Index + 19L * range.Length + 1;
        }

        return sum;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
