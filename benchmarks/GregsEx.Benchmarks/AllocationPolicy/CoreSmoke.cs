using GregsEx;
namespace AllocationPolicy;
internal static class CoreSmoke
{
 internal static Workload Create(RegularCoreMode mode,RegexInputKind kind,RegexExecutionMode policy,string scenario)
 {
        var profile=new RegularCoreProfile(mode);
            var source = scenario == "empty" ? "()" : scenario == "native-width" ? "(.)" : "(a+)(b)?";
            var p = RegexPattern.Parse(source,profile,new(kind,policy));
            Check.That(p.ExecutionBackend == RegexExecutionBackend.Interpreted,"RegularCore Auto selects interpreter");
            var w = p.CreateWorkspace();
            char[] chars = (scenario switch { "positive"=>"xaaab", "miss"=>"zzz", "empty"=>"x😀", _=>"x😀" }).ToCharArray();
            byte[] bytes = scenario switch { "positive"=>[120,97,97,97,98], "miss"=>[122,122,122], _=>[120,240,159,152,128] };
            if(mode==RegularCoreMode.RawBytes && scenario=="native-width")bytes=[120,255];
            int start = scenario=="native-width"?1:0;
            int expectedIndex = scenario is "positive" or "native-width"?1:0;
            int expectedLength = scenario=="positive"?4:scenario=="native-width"?(mode==RegularCoreMode.UnicodeScalars?(kind==RegexInputKind.Utf8?4:2):1):0;
            var expectedStatus = scenario=="miss"?RegexSearchStatus.NoMatch:RegexSearchStatus.Match;
            var groups = new RegexCapture[p.GroupCount+1]; groups[^1]=new(true,999,999);
            RegexMatchRange r;
            var status = kind==RegexInputKind.Utf16?w.FindCaptures(chars,start,groups,out r):w.FindCaptures(bytes,start,groups,out r);
            Check.That(status==expectedStatus,"capture status");
            Check.That(r==(scenario=="miss"?default:new RegexMatchRange(expectedIndex,expectedLength)),"native range");
            Check.That(groups[0]==(scenario=="miss"?default:new RegexCapture(true,expectedIndex,expectedLength)),"group zero");
            Check.That(groups[^1]==new RegexCapture(true,999,999),"excess output untouched");
            Check.That((kind==RegexInputKind.Utf16?p.IsMatch(chars):p.IsMatch(bytes))==(scenario!="miss"),"convenience agrees");
        return new Workload(()=>Cycle(w,kind,mode,chars,bytes,start,groups),w.Dispose,metadata:new{source,chars=InputIdentity.Chars(new string(chars)),bytes=InputIdentity.Bytes(bytes),kind,mode,policy,scenario,start,expectedStatus,expectedIndex,expectedLength,groups=p.GroupCount,plan_bytes=p.RetainedBytes,reserved_bytes=w.ReservedBytes,capacity=w.Capacity});
 }
 private static long Cycle(RegexWorkspace w,RegexInputKind kind,RegularCoreMode mode,char[] chars,byte[] bytes,int start,RegexCapture[] groups)
{
    bool utf16=kind==RegexInputKind.Utf16;
    long sum=(int)(utf16?w.IsMatch(chars,start):w.IsMatch(bytes,start));
    RegexMatchRange r;
    sum+=(int)(utf16?w.Find(chars,start,out r):w.Find(bytes,start,out r)); sum+=r.Index+17L*r.Length;
    sum+=(int)(utf16?w.FindCaptures(chars,start,groups,out r):w.FindCaptures(bytes,start,groups,out r));
    for(int i=0;i<groups.Length-1;i++)sum+=(groups[i].Participated?1:0)+groups[i].Index+31L*groups[i].Length;
    // Full-subject validation occurs at every Find; no mutable-input validity cache.
    int next=0,length=utf16?chars.Length:bytes.Length;
    while((utf16?w.Find(chars,next,out r):w.Find(bytes,next,out r))==RegexSearchStatus.Match)
    {
        sum+=r.Index+7L*r.Length+1;
        if(r.Length!=0)next=r.Index+r.Length;
        else if(r.Index==length)break;
        else if(mode!=RegularCoreMode.UnicodeScalars)next=r.Index+1;
        else if(utf16)next=r.Index+(char.IsHighSurrogate(chars[r.Index])?2:1);
        else {byte lead=bytes[r.Index];next=r.Index+(lead<128?1:lead<224?2:lead<240?3:4);}
    }
    return sum;
}

}
