using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using GregsEx.Flavors.EcmaScript.Parsing;
using GregsEx.Flavors.EcmaScript.Unicode;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

// Actual verified compiler, pinned data and VM. No test Unicode approximation.
internal sealed class Composition
{
    internal readonly EcmaProgram Program;
    internal readonly IEcmaExecutor Executor;
    internal readonly CompileBudget Budget;
    private readonly int[] _states, _captures, _scratch;
    internal readonly RegexCapture[] Groups;
    internal RegexMatchRange Match;
    internal RegexExecutionFailure Failure;
    internal long Work;

    internal Composition(string pattern, string flags, RegexCompileLimits? limits = null, int frames = 128)
    {
        var budget = new CompileBudget(limits ?? new());
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var unicode, out var error), error.ToString());
        Assert.True(new EcmaCompiler().TryCompile(pattern, GrammarTests.Profile(flags), unicode!, ref budget,
            out var program, out var diagnostic), pattern + " / " + flags + ": " + diagnostic);
        Assert.True(new EcmaExecutorFactory().TryCreate(program!, unicode!, ref budget, out var executor, out error), error.ToString());
        Program = program!;
        Executor = executor!;
        Budget = budget;
        // This direct binding accounts the child components; aggregate plan and workspace
        // admission are checked separately through the public API.
        var capacity = Executor.GetRequiredCapacity(SearchOperation.Captures, frames);
        Assert.InRange(capacity.RequiredBytes, 1, new RegexMatchLimits().MaxWorkspaceBytes);
        _states = new int[capacity.StateSlots];
        _captures = new int[capacity.CaptureSlots];
        _scratch = new int[capacity.ScratchSlots];
        Groups = new RegexCapture[Program.GroupCount];
    }
    internal RegexSearchStatus Search(string subject, int start = 0, bool sticky = false,
        EcmaStartPolicy policy = EcmaStartPolicy.LowerBound, RegexMatchLimits? limits = null)
    {
        var state = new RunState(new(_states, _captures, _scratch), limits ?? new(), default);
        var status = Executor.Search(subject, start, sticky, policy, SearchOperation.Captures, ref state, Groups, out Match);
        Failure = state.Failure;
        Work = state.Budget.WorkConsumed;
        return status;
    }
    internal void Expect(string subject, int index, int length, params RegexCapture[] captures)
    {
        Assert.Equal(RegexSearchStatus.Match, Search(subject));
        Assert.Equal(new(index, length), Match);
        Assert.Equal(new RegexCapture(true, index, length), Groups[0]);
        Assert.Equal(captures, Groups.AsSpan(1).ToArray());
    }
    internal static EcmaUnicodeData Data(ref CompileBudget budget)
    {
        Assert.True(EcmaUnicodeData.TryCreate(ref budget, out var data, out var diagnostic), diagnostic.ToString());
        return data!;
    }
}
