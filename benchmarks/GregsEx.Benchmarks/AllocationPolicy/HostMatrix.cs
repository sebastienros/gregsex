using GregsEx;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Unicode;
namespace AllocationPolicy;
internal static class HostMatrix
{
 internal static Workload Create(int kind)
 {
        var executor = kind == 0 ? EmptyProgram() : LiteralProgram(kind == 2);
        var compile = new CompileBudget(new());
        Check.That(EcmaUnicodeData.TryCreate(ref compile, out var data, out _));
        Check.That(new EcmaExecutorFactory().TryCreate(executor, data!, ref compile, out var vm, out _));
        var matcher = new EcmaScriptMatcher(vm!, frameCapacity: 128);
        var groups = new RegexCapture[executor.GroupCount];
        Check.Equal(kind==0?9:8,kind==0?Cycle(matcher,groups):LiteralCycle(matcher,groups));
        return new Workload(()=>kind==0?Cycle(matcher,groups):LiteralCycle(matcher,groups),matcher.Dispose,131072,metadata:new{kind,flags="gu",source=kind==0?"":kind==1?"a":"(a)",frames=128,expected=kind==0?9:8});
 }
 private static EcmaProgram EmptyProgram() { var p=new Plan(EcmaScriptFlags.Global|EcmaScriptFlags.Unicode);p.Empty();return p.Program(); }
    private static EcmaProgram LiteralProgram(bool capture)
    {
        var nodes = new EcmaNode[capture ? 2 : 1];
        nodes[0] = new(EcmaNodeKind.Literal, -1, -1, 'a', -1, 0, 0, false, 0, 0, 0, new(0, 0, RegexOffsetUnit.Utf16CodeUnits));
        if (capture) nodes[1] = new(EcmaNodeKind.Capture, 0, -1, 1, -1, 0, 0, false, 0, 1, 1, new(0, 0, RegexOffsetUnit.Utf16CodeUnits));
        var budget = new CompileBudget(new());
        Check.That(EcmaProgram.TryCreate(new(EcmaScriptFlags.Global | EcmaScriptFlags.Unicode), nodes.Length - 1,
            capture ? 2 : 1, 0, nodes, [], [], [], [], [], [], ref budget, out var program, out _));
        return program!;
    }
    private static int LiteralCycle(EcmaScriptMatcher matcher, RegexCapture[] groups)
    {
        matcher.LastIndex = 0; int sum = (int)matcher.Test("ba");
        matcher.LastIndex = 0;
        sum += (int)matcher.Exec("ba", groups, out var hit) + hit.Index + hit.Length;
        matcher.LastIndex = 0; sum += (int)matcher.Exec("z", groups, out _);
        sum += (int)matcher.ExecNext("ba", groups, out var next) + next.Index + next.Length + (int)matcher.LastIndex;
        sum += (int)matcher.ExecNext("ba", groups, out _);
        return sum;
    }
    private static int Cycle(EcmaScriptMatcher matcher, RegexCapture[] groups)
    {
        matcher.LastIndex = 0;
        int sum = (int)matcher.Test("\ud83d\ude00");
        sum += (int)matcher.Exec("\ud83d\ude00", groups, out var raw) + raw.Index + raw.Length;
        sum += (int)matcher.ExecNext("\ud83d\ude00", groups, out var first) + first.Index + (int)matcher.LastIndex;
        sum += (int)matcher.ExecNext("\ud83d\ude00", groups, out var last) + last.Index + (int)matcher.LastIndex;
        sum += (int)matcher.ExecNext("\ud83d\ude00", groups, out _) + (int)matcher.LastIndex;
        matcher.LastIndex = 2;
        sum += (int)matcher.ExecNext("", groups, out _) + (int)matcher.LastIndex;
        return sum;
    }
}
