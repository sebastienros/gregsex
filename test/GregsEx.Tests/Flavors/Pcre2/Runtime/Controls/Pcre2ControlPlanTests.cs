using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using Xunit;

namespace GregsEx.Tests.Flavors.Pcre2.Runtime.Controls;

public sealed class Pcre2ControlPlanTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LexicalAssertionIdentityIsIndependentOfCallReference(bool asserted)
    {
        var p = new ControlPlan();
        var accept = p.Verb(Pcre2VerbKind.Accept);
        var definition = p.Capture(accept, 1);
        if (asserted)
        {
            definition = p.Assertion(definition);
        }

        p.Seq(p.Define(definition), p.Call(1));
        var program = p.Build();
        var budget = new CompileBudget(new());
        Assert.True(Pcre2ControlPlan.TryCreate(program, ref budget, out var plan, out var error), error.ToString());
        Assert.Same(program, plan!.Program);
        Assert.Equal(plan.RetainedBytes, budget.ReservedBytes);
        var capacity = new Pcre2ExecutionCapacity(8, 32, 8, 16);
        var required = capacity.Required(2);
        var machine = new Pcre2VmState(capacity, new(), 0);
        var state = Pcre2KernelOperation.CreateRunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]), machine.Limits);
        Assert.Equal(asserted, plan.IsAssertionAccept(accept, ref machine, ref state));
        Assert.Equal(RegexExecutionFailureCode.None, state.Failure.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void LexicalMetadataAdmitsBeforeAllocationAndReleasesFailedScratch(int denied)
    {
        var p = new ControlPlan();
        p.Seq(p.Verb(Pcre2VerbKind.Accept), p.Lit('a'));
        var program = p.Build();
        var limits = denied switch
        {
            0 => new RegexCompileLimits(maxPlanBytes: program.RetainedBytes),
            1 => new RegexCompileLimits(maxCompileMemoryBytes: 1),
            _ => new RegexCompileLimits(maxCompileWork: 1)
        };
        var budget = new CompileBudget(limits);
        Assert.False(Pcre2ControlPlan.TryCreate(program, ref budget, out var plan, out var error));
        Assert.Null(plan);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(Pcre2DiagnosticKind.CompileLimit, error.Kind);
        Assert.Equal(denied switch
        {
            0 => RegexDiagnosticCode.PlanLimitExceeded,
            1 => RegexDiagnosticCode.CompileMemoryLimitExceeded,
            _ => RegexDiagnosticCode.CompileWorkLimitExceeded
        }, error.Diagnostic.Code);
    }
}
