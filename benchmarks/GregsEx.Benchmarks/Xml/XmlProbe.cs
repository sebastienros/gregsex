using System.Text;
using GregsEx;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;

namespace XmlProbe;

internal sealed class Fixture
{
    internal const int WarmIterations = 8192;
    internal const int MeasuredIterations = 1000;
    private readonly string[] _chars = ["", "a", "Name_123", "A\u03B1\u4E2D\U00010428\U0001F600z", new string('a',4096), "a\U0001F600z", "a\0", "a\uD800"];
    private readonly byte[][] _bytes;
    private readonly int[] _charStarts = [0,0,0,0,0,2,0,0];
    private readonly int[] _byteStarts = [0,0,0,0,0,2,0,0];
    private readonly RegexSearchStatus[] _expected = [RegexSearchStatus.NoMatch,RegexSearchStatus.NoMatch,RegexSearchStatus.NoMatch,RegexSearchStatus.NoMatch,RegexSearchStatus.NoMatch,RegexSearchStatus.InvalidInput,RegexSearchStatus.InvalidInput,RegexSearchStatus.InvalidInput];
    private readonly RegexMatchLimits _limits = new();
    private readonly XmlUnicodeData _data;
    internal static readonly string[] CaseIds=["empty","one-ascii","ascii-name","mixed-supplementary","4096-ascii","interior-start","xml-domain","malformed-suffix"];
    internal Fixture()
    {
        _bytes=new byte[_chars.Length][];
        for(int i=0;i<_chars.Length-1;i++) _bytes[i]=Encoding.UTF8.GetBytes(_chars[i]);
        _bytes[^1]=[0x61,0xED,0xA0,0x80]; // exact encoded surrogate, no lossy transcoding
        var budget=new CompileBudget(new());
        if(!XmlUnicodeData.TryCreate(ref budget,out var data,out var diagnostic)) throw new InvalidOperationException(diagnostic.ToString());
        _data=data!;
    }
    internal int CharCase(int index)
    {
        var state=new RunState(new([],[],[]),_limits);
        var status=XmlCharTextReader.Validate(_chars[index],_charStarts[index],ref state);
        return (int)status+(int)state.Budget.WorkConsumed*8+(int)state.Failure.Code*65536+state.Failure.Offset;
    }
    internal int ByteCase(int index)
    {
        var state=new RunState(new([],[],[]),_limits);
        var status=XmlByteTextReader.Validate(_bytes[index],_byteStarts[index],ref state);
        return (int)status+(int)state.Budget.WorkConsumed*8+(int)state.Failure.Code*65536+state.Failure.Offset;
    }
    internal int LookupCase()
    {
        if(!_data.TryGetCategory("Cn",out var cn)||!_data.TryGetBlock("PrivateUse",out var block)) return -1;
        var names=_data.GetPredefined(XmlPredefinedClass.NameStart);
        var rows=_data.CaseMappings;
        return (XmlUnicodeData.Contains(cn,0x378)?1:0)+(XmlUnicodeData.Contains(block,0x10FFFD)?2:0)+
            (XmlUnicodeData.Contains(names,0xEFFFF)?4:0)+(XmlUnicodeData.CaseEquals(rows,0xDF,0x1E9E)?8:0)+
            (XmlCharTextReader.TryRead(_chars[3],3,out int cp,out int cw)?cp+cw:0)+
            (XmlByteTextReader.TryRead(_bytes[3],6,out int bp,out int bw)?bp+bw:0);
    }
    internal int CharMatrix() { int checksum=0; for(int i=0;i<_chars.Length;i++) checksum+=CharCase(i); return checksum+LookupCase(); }
    internal int ByteMatrix() { int checksum=0; for(int i=0;i<_bytes.Length;i++) checksum+=ByteCase(i); return checksum+LookupCase(); }
    internal void VerifyAndWarm()
    {
        for(int i=0;i<_chars.Length;i++)
        {
            var a=new RunState(new([],[],[]),_limits); var b=new RunState(new([],[],[]),_limits);
            if(XmlCharTextReader.Validate(_chars[i],_charStarts[i],ref a)!=_expected[i]||XmlByteTextReader.Validate(_bytes[i],_byteStarts[i],ref b)!=_expected[i]) throw new InvalidOperationException(CaseIds[i]);
            for(int j=0;j<WarmIterations;j++) { _=CharCase(i); _=ByteCase(i); }
        }
        for(int j=0;j<WarmIterations;j++) _=LookupCase();
    }
}
