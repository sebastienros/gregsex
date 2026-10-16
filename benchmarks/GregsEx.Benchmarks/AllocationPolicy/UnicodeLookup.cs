using GregsEx;
using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Unicode;
namespace AllocationPolicy;
internal static class UnicodeLookup
{
 internal static Workload Create()
 {
    var budget=new CompileBudget(new());Check.That(EcmaUnicodeData.TryCreate(ref budget,out var data,out _));
    Check.That(data!.IsIdentifierStart(0x10400));Check.That(data.IsIdentifierContinue(0x200d));
    Check.That(!data.IsIdentifierContinue(-1));Check.That(!data.TryResolveProperty("not_a_property","",true,out _));
    return new Workload(()=>LookupCycle(data),metadata:new{data.DataId,data.RetainedBytes,lookups=new[]{"identifier start10400","identifier continue200d","identifier invalid-1","scx=Hira","RGI_Emoji","not_a_property","case table sizes"}});
 }
    private static int LookupCycle(EcmaUnicodeData data)
    {
        int sum = data.IsIdentifierStart(0x10400) ? 1 : 0;
        sum += data.IsIdentifierContinue(0x200d) ? 1 : 0;
        sum += data.IsIdentifierContinue(-1) ? 0 : 1;
        if (data.TryResolveProperty("scx", "Hira", true, out var p)) sum += data.GetIntervals(p).Length;
        if (data.TryResolveProperty("RGI_Emoji", "", true, out p))
            sum += data.GetStrings(p).Length + data.GetStringCodePoints(p).Length;
        sum += data.TryResolveProperty("not_a_property", "", true, out _) ? 0 : 1;
        sum += data.CaseTables.UnicodeFold.Length + data.CaseTables.LegacyUppercase.Length;
        return sum;
    }
}
