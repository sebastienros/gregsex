using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Unicode;
using GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Composition;

// Plans are independently authored semantic recipes, not parser output. Every
// search uses the production VM and pinned data; no contract executor or BCL.
internal static class CompositionFixture
{
    internal static EcmaUnicodeData Data()
    {
        var budget = new CompileBudget(new());
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var data, out var error), error.ToString());
        Assert.Equal("Unicode-16.0.0+Emoji-16.0", data!.DataId);
        return data;
    }
    internal static IEcmaExecutor Build(Plan plan)
    {
        var program = plan.Program();
        var data = Data();
        var budget = new CompileBudget(new());
        Assert.True(new EcmaExecutorFactory().TryCreate(program, data, ref budget, out var vm, out var error), error.ToString());
        return vm!;
    }
    internal static EcmaScriptMatcher Bind(Plan plan, int frames = 128,
        RegexMatchLimits? limits = null, EcmaScriptHostLimits? host = null)
        => new(Build(plan), limits, frameCapacity: frames, hostLimits: host);
    internal static int Property(Plan plan, string name)
    {
        var data = Data();
        Assert.True(data.TryResolveProperty(name, [], true, out var property));
        var slices = data.GetStrings(property);
        var points = data.GetStringCodePoints(property);
        var strings = new int[slices.Length][];
        for (var i = 0; i < slices.Length; i++)
        {
            strings[i] = points.Slice(slices[i].Offset, slices[i].Length).ToArray();
        }

        return plan.Set(data.GetIntervals(property).ToArray(), strings);
    }
    internal static void Expect(EcmaScriptMatcher matcher, string input, int index, int length,
        params RegexCapture[] captures)
    {
        var output = new RegexCapture[captures.Length + 1];
        Assert.Equal(RegexSearchStatus.Match, matcher.Exec(input, output, out var range));
        Assert.Equal(new(index, length), range);
        Assert.Equal(new(true, index, length), output[0]);
        Assert.Equal(captures, output.Skip(1).ToArray());
    }
}
