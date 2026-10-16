using GregsEx;
namespace AllocationPolicy;
internal static class CallerIteration
{
 internal static Workload Create()
 {
        var p=RegexPattern.Parse("(a?)(b)?",new RegularCoreProfile()); var w=p.CreateWorkspace();
        var groups=new RegexCapture[3];
        int Cycle()
        {
            int sum=(int)w.IsMatch("ab"); sum+=(int)w.Find("zz",0,out var r);
            sum+=r.Index; sum+=(int)w.FindCaptures("",0,groups,out r);
            int start=0;
            while(w.Find("ab",start,out r)==RegexSearchStatus.Match)
            { sum+=r.Index; if(r.Length>0)start=r.Index+r.Length; else if(r.Index==2)break; else start=r.Index+1; }
            return sum;
        }
        Check.Equal(new RegexMatchRange(0,2), FindExpected());
        return new Workload(() => Cycle(),w.Dispose,metadata:new{source="(a?)(b)?",subjects=new[]{"ab","zz",""},plan_bytes=p.RetainedBytes,workspace_bytes=w.ReservedBytes,capacity=w.Capacity});
        RegexMatchRange FindExpected() { Check.Equal(RegexSearchStatus.Match,w.Find("ab",0,out var r)); return r; }
 }
}
