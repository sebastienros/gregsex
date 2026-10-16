using GregsEx;
namespace AllocationPolicy;
internal static class EcmaMatrix
{
 internal static Workload Create(EcmaScriptFlags mode,RegexExecutionMode policy,string scenario)
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
            var w = p.CreateWorkspace(); var m = EcmaScriptMatcher.Bind(p, frameCapacity: 32);
            var groups = new RegexCapture[p.GroupCount + 1]; groups[^1] = new(true, 999, 999);
            var status = w.FindCaptures(input, start, groups, out var range);
            Check.That(status == (yes ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch), "generic status");
            Check.That(range == (yes ? new RegexMatchRange(index, length) : default), "generic native range");
            Check.That(groups[^1] == new RegexCapture(true, 999, 999), "generic excess sentinel");
            m.LastIndex = start;
            Check.That(m.Exec(input, groups, out var typed) == status && typed == range, "typed range/status");
            Check.That(groups[^1] == new RegexCapture(true, 999, 999), "typed excess sentinel");
            long expected = Cycle(w, m, input, start, groups);
            Check.Equal(RegexExecutionBackend.Interpreted,p.ExecutionBackend);
            return new Workload(()=>Cycle(w,m,input,start,groups),()=>{m.Dispose();w.Dispose();},131072,metadata:new{source,input=InputIdentity.Chars(input),mode,policy,scenario,start,index,length,yes,expected,groups=p.GroupCount,plan_bytes=p.RetainedBytes,workspace_bytes=w.ReservedBytes,session_bytes=m.ReservedBytes,capacity=w.Capacity});
 }
    private static long Cycle(RegexWorkspace w, EcmaScriptMatcher m, string input, int start, RegexCapture[] groups)
    {
        long sum = (int)w.IsMatch(input, start);
        sum += (int)w.Find(input, start, out var range) + range.Index + 17L * range.Length;
        sum += (int)w.FindCaptures(input, start, groups, out range) + range.Index + 7L * range.Length;
        for (int i = 0; i < groups.Length - 1; i++) sum += (groups[i].Participated ? 1 : 0) + groups[i].Index + 31L * groups[i].Length;
        m.LastIndex = start; sum += (int)m.Test(input);
        m.LastIndex = start; sum += (int)m.Exec(input, groups, out range) + range.Index + 11L * range.Length;
        for (int i = 0; i < groups.Length - 1; i++) sum += (groups[i].Participated ? 1 : 0) + groups[i].Index + 13L * groups[i].Length;
        m.LastIndex = start;
        while (m.ExecNext(input, groups, out range) == RegexSearchStatus.Match) sum += range.Index + 19L * range.Length + 1;
        return sum;
    }
}
