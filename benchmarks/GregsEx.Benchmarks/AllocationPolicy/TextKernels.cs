using GregsEx;
using GregsEx.Contracts;
using GregsEx.IR;
using GregsEx.Text;
using GregsEx.Unicode.Core;
namespace AllocationPolicy;
internal static class TextKernels
{
 internal static Workload Create(int fixture)
 {
    var mode=fixture==4?RegularCoreMode.Utf16CodeUnits:fixture==9?RegularCoreMode.RawBytes:RegularCoreMode.UnicodeScalars;
    var kind=fixture<5?RegexInputKind.Utf16:fixture==9?RegexInputKind.Bytes:RegexInputKind.Utf8;
    Check.That(ProfileDescriptor.TryCreate(new RegularCoreProfile(mode),kind,out var profile,out _));
    var limits=new RegexMatchLimits();var budget=new CompileBudget(new());
    Check.That(ProfileDescriptor.TryCreate(new RegularCoreProfile(),RegexInputKind.Utf16,out var classProfile,out _));
    Check.That(CoreCharacterClasses.TryCreateAscii(AsciiClass.Word,true,classProfile!,ref budget,default,out var set,out _));
    string chars=fixture switch { 0=>"abc\0😀€",1=>"",2=>"😀",3=>"a\uD800",4=>"a\uD800\uDC00\uDFFF",_=>"" };
    byte[] bytes=fixture switch { 5=>"abc\0😀€"u8.ToArray(),6=>[],7=>"😀"u8.ToArray(),8=>[97,0xE0,0x80,0x80],9=>[0,97,0x80,0xFF],_=>[] };
    int start=fixture is 2 or 7?1:fixture is 4 or 9?2:0;
    var expected=fixture is 2 or 3 or 7 or 8?RegexSearchStatus.InvalidInput:RegexSearchStatus.NoMatch;
    long Cycle()=>kind==RegexInputKind.Utf16?Execute<char,CharTextReader>(chars,start,profile!,limits,set!,expected):Execute<byte,ByteTextReader>(bytes,start,profile!,limits,set!,expected);
    Check.That(Cycle()>0,"Reader/classification domain expectation");return new Workload(Cycle,metadata:new{fixture,mode,kind,chars=InputIdentity.Chars(chars),bytes=InputIdentity.Bytes(bytes),start,expected});
 }
 internal static Workload CreateAlgebra()
 {
    Check.That(ProfileDescriptor.TryCreate(new RegularCoreProfile(),RegexInputKind.Utf16,out var profile,out _));
    var limits=new RegexCompileLimits();CharacterRange[] left=[new(1,10),new(100,110)],right=[new(5,15),new(105,115)],output=new CharacterRange[8];
    Check.That(Algebra(left,right,output,profile!,limits)>0,"Caller-capacity algebra");
    return new Workload(()=>Algebra(left,right,output,profile!,limits),metadata:new{left,right,capacity=output.Length});
 }
    private static long Execute<TUnit, TReader>(ReadOnlySpan<TUnit> input, int start, ProfileDescriptor profile,
        RegexMatchLimits limits, CharacterClass set, RegexSearchStatus expected)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        var state = new RunState(new([], [], []), limits);
        var status = TReader.Validate(input, start, profile, ref state);
        if (status != expected) return long.MinValue / 10000;
        long checksum = 1 + state.Budget.WorkConsumed;
        if (status != RegexSearchStatus.NoMatch) return checksum;
        for (int offset = 0; TReader.TryRead(input, offset, profile, out int value, out int width); offset += width)
        {
            if (!state.TryCharge(1, offset) ||
                !CoreCharacterClasses.TryContains(set, value, profile, ref state, offset, out bool contains)) return long.MinValue / 10000;
            checksum += value + width + (contains ? 1 : 0);
        }
        return checksum;
    }

    private static long Algebra(ReadOnlySpan<CharacterRange> left, ReadOnlySpan<CharacterRange> right,
        Span<CharacterRange> output, ProfileDescriptor profile, RegexCompileLimits limits)
    {
        var budget = new CompileBudget(limits);
        if (!CoreCharacterClasses.TryUnion(left, right, output, profile, ref budget, default, out int union, out _) ||
            !CoreCharacterClasses.TryIntersect(left, right, output, profile, ref budget, default, out int intersection, out _) ||
            !CoreCharacterClasses.TryComplement(left, output, profile, ref budget, default, out int complement, out _))
            return long.MinValue / 10000;
        return union + intersection + complement + budget.WorkConsumed;
    }
}
