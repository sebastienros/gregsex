using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;

public class VmAllocationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PreparedSearchMatrixPreservesPreparedResults(int operationValue)
    {
        var operation = (SearchOperation)operationValue;
        var p = new Plan(EcmaScriptFlags.Unicode | EcmaScriptFlags.IgnoreCase);
        p.Seq(p.Assert(EcmaNodeKind.Lookbehind, p.Lit('x')),
            p.Seq(p.Cap(1, p.Rep(p.Alt(p.Empty(), p.Lit('a')), 1)), p.Ref(1)));
        var feature = new Prepared(p.Build(), 256);
        var q = new Plan(EcmaScriptFlags.UnicodeSets);
        q.Set([], [0x1F600, 'a'], [0x1F600], []);
        var strings = new Prepared(q.Build(), 16);
        var emptyPlan = new Plan();
        emptyPlan.Cap(1, emptyPlan.Empty());
        var empty = new Prepared(emptyPlan.Build(), 1);
        // Repeat prepared success, miss, string-set and empty paths for reuse.
        for (var i = 0; i < 4; i++)
        {
            Cycle(feature, strings, empty, operation);
        }

        long checksum = 0;
        for (var i = 0; i < 1_000; i++)
        {
            checksum += Cycle(feature, strings, empty, operation);
        }

        Assert.True(checksum > 0);
        Assert.Equal(RegexSearchStatus.Match, feature.Search("xaaaa", operation: operation));
        Assert.Equal(new(1, 4), feature.Match);
    }
    private static long Cycle(Prepared feature, Prepared strings, Prepared empty, SearchOperation operation)
    {
        var sum = (int)feature.Search("xaaaa", operation: operation) + feature.Match.Index + feature.Match.Length + feature.Work;
        sum += (int)feature.Search("xbbbbb", operation: operation) + feature.Work;
        sum += (int)strings.Search("\uD83D\uDE00a", operation: operation) + strings.Match.Length + strings.Work;
        sum += (int)strings.Search("", operation: operation) + strings.Work;
        sum += (int)empty.Search("", operation: operation) + empty.Work;
        return sum;
    }
}
