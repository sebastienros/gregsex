using GregsEx;
using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
namespace AllocationPolicy;
internal static class Lifecycle
{
 internal static readonly string[] Ids=["CORE-COMPILE","CORE-PARSE","WORKSPACE-CREATE","WORKSPACE-CREATE-RESERVE-DISPOSE","CONVENIENCE","ECMA-CONTRACT-COMPILE","ECMA-EMOJI-COMPILE","ECMA-EMOJI-INTERSECTION"];
 internal static Workload Create(string id)
 {
    var profile=new RegularCoreProfile();var p=RegexPattern.Parse("(a+)(b)?",profile);
    var compiler=new GregsEx.Compilation.Core.CoreCompiler();
    Check.That(ProfileDescriptor.TryCreate(profile,RegexInputKind.Utf16,out var descriptor,out _));
    var ecma=new EcmaCompiler();var contractProfile=new EcmaScriptProfile(EcmaScriptFlags.UnicodeSets|EcmaScriptFlags.IgnoreCase);
    var emojiProfile=new EcmaScriptProfile(EcmaScriptFlags.UnicodeSets);
    var budget=new CompileBudget(new());Check.That(EcmaUnicodeData.TryCreate(ref budget,out var data,out _));
    long Cycle()
    {
       switch(id)
       {
        case "CORE-COMPILE": {var b=new CompileBudget(new());Check.That(compiler.TryCompile("(a|[bc]){2,4}",descriptor!,ref b,out var plan,out _));return plan!.RetainedBytes+b.WorkConsumed;}
        case "CORE-PARSE": return RegexPattern.Parse("(a+)(b)?",profile).RetainedBytes;
        case "WORKSPACE-CREATE": {using var w=p.CreateWorkspace(new(default));return w.ReservedBytes+1;}
        case "WORKSPACE-CREATE-RESERVE-DISPOSE": {using var w=p.CreateWorkspace(new(default));w.Reserve(p.CaptureCapacity);return w.ReservedBytes;}
        case "CONVENIENCE": return p.IsMatch("aa")?1:0;
        case "ECMA-CONTRACT-COMPILE": {var b=new CompileBudget(new());Check.That(ecma.TryCompile(@"(?<x>[a-z]+)|(?<x>[\q{ab|xyz}&&\q{AB|xyz}])\k<x>",contractProfile!,ContractUnicode.Instance,ref b,out var plan,out _));return plan!.RetainedBytes+b.WorkConsumed;}
        default: {var b=new CompileBudget(new(maxCompileWork:250_000_000));Check.That(ecma.TryCompile(id=="ECMA-EMOJI-COMPILE"?@"\p{RGI_Emoji}":@"[\p{RGI_Emoji}&&\p{RGI_Emoji}]",emojiProfile!,data!,ref b,out var plan,out _));return plan!.RetainedBytes+b.WorkConsumed;}
       }
    }
    Check.That(Cycle()>0);return new Workload(Cycle,warmCount:id.StartsWith("ECMA-EMOJI")?8:id=="ECMA-CONTRACT-COMPILE"?20:1,metadata:new{id,scope=id=="WORKSPACE-CREATE-RESERVE-DISPOSE"?"Create empty workspace + Reserve(CaptureCapacity) + Dispose, a composite; not the original reserve-only interval":"Repeated owned cost after shared/static preparation",native_memory="not measured; NativeAOT user-deferred",retained_accounting="compile budget counters are product resource accounting, not allocation measurements"});
 }
}
