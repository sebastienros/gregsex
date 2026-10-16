using GregsEx;
namespace AllocationPolicy;
internal sealed record CaseDefinition(string Id,string Family,Func<Workload> Factory);
internal static class Registry
{
 internal static readonly CaseDefinition[] Cases=Build().ToArray();
 private static IEnumerable<CaseDefinition> Build()
 {
    for(int rep=0;rep<4;rep++) foreach(var policy in new[]{RegexExecutionMode.Interpreted,RegexExecutionMode.Auto}) for(int fixture=0;fixture<4;fixture++)
    {
      int r=rep,f=fixture;var p=policy;
      var mode=r==2?RegularCoreMode.Utf16CodeUnits:r==3?RegularCoreMode.RawBytes:RegularCoreMode.UnicodeScalars;
      var kind=r==1?RegexInputKind.Utf8:r==3?RegexInputKind.Bytes:RegexInputKind.Utf16;
      string scenario=f==0?"positive":f==1?"miss":f==2?"empty":"native-width";
      yield return new($"CORE-{r}-{p}-{f}","core",()=>{
          var matrix=CoreMatrix.Create(r,p,f);var smoke=CoreSmoke.Create(mode,kind,p,scenario);
          return new Workload(()=>matrix.Cycle()+smoke.Cycle(),()=>{matrix.Dispose();smoke.Dispose();},metadata:new{matrix=matrix.Metadata,smoke=smoke.Metadata});
      });
    }
    foreach(var flags in new[]{EcmaScriptFlags.None,EcmaScriptFlags.Unicode,EcmaScriptFlags.UnicodeSets}) foreach(var policy in new[]{RegexExecutionMode.Interpreted,RegexExecutionMode.Auto})
    foreach(string scenario in new[]{"backreference","lookbehind","unset","empty","unicode","miss"})
    {var f=flags;var p=policy;string s=scenario;yield return new($"ECMA-{f}-{p}-{s}","ecma",()=>EcmaMatrix.Create(f,p,s));}
    for(int family=0;family<4;family++) for(int op=0;op<3;op++)
    {int f=family,o=op;yield return new($"THOMPSON-{f}-{o}","thompson",()=>ThompsonFixture.Create(f,o));}
    for(int operation=0;operation<3;operation++)
    {int o=operation;yield return new($"VM-{o}","vm",()=>VmMatrix.Create(o));}
    for(int kind=0;kind<3;kind++)
    {int k=kind;yield return new($"HOST-{k}","host",()=>HostMatrix.Create(k));}
    for(int fixture=0;fixture<10;fixture++)
    {int f=fixture;yield return new($"READER-{f}","text",()=>TextKernels.Create(f));}
    yield return new("ALGEBRA","text",TextKernels.CreateAlgebra);
    yield return new("UNICODE","unicode",UnicodeLookup.Create);
    yield return new("ITERATION","iteration",CallerIteration.Create);
 }
 internal static Workload Create(string id)=>Cases.Single(x=>x.Id==id).Factory();
}
