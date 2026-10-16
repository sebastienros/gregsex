using GregsEx;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
namespace AllocationPolicy;
internal static class VmMatrix
{
 internal static Workload Create(int operationValue)
 {
        var operation = (SearchOperation)operationValue;
        var p = new Plan(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase);
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Lit('x')),
            p.Seq(p.Cap(1, p.Rep(p.Alt(p.Empty(), p.Lit('a')), 1)), p.Ref(1)));
        var feature = new Prepared(p.Build(), 256);
        var q = new Plan(EcmaScriptFlags.UnicodeSets); q.Set([], [0x1F600, 'a'], [0x1F600], []);
        var strings = new Prepared(q.Build(), 16);
        var emptyPlan = new Plan(); emptyPlan.Cap(1, emptyPlan.Empty()); var empty = new Prepared(emptyPlan.Build(), 1);
        Check.Equal(RegexSearchStatus.Match,feature.Search("xaaaa",operation:operation));
        Check.Equal(new RegexMatchRange(1,4),feature.Match);
        // Nullable repeat + empty backreference accepts zero-width after x.
        Check.Equal(RegexSearchStatus.Match,feature.Search("xbbbbb",operation:operation));
        Check.Equal(new RegexMatchRange(1,0),feature.Match);
        Check.Equal(RegexSearchStatus.NoMatch,feature.Search("zbbbb",operation:operation));
        Check.Equal(RegexSearchStatus.Match,empty.Search("",operation:operation));
        return new Workload(()=>Cycle(feature,strings,empty,operation),warmCount:131072,metadata:new{operation,feature="(?<=x)((?:|a)+)\\1",subjects=new[]{"xaaaa","xbbbbb","zbbbb","😀a","",""},frames=new[]{256,16,1}});
 }
    private static long Cycle(Prepared feature, Prepared strings, Prepared empty, SearchOperation operation)
    {
        var sum = (int)feature.Search("xaaaa", operation: operation) + feature.Match.Index + feature.Match.Length + feature.Work;
        sum += (int)feature.Search("xbbbbb", operation: operation) + feature.Work;
        sum += (int)feature.Search("zbbbb", operation: operation) + feature.Work;
        sum += (int)strings.Search("\uD83D\uDE00a", operation: operation) + strings.Match.Length + strings.Work;
        sum += (int)strings.Search("", operation: operation) + strings.Work;
        sum += (int)empty.Search("", operation: operation) + empty.Work;
        return sum;
    }
}
