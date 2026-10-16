using System.Diagnostics.CodeAnalysis;
using GregsEx.Flavors.EcmaScript.Integration;

namespace GregsEx.Execution.Compiled;

internal static class EcmaCompiledBackend
{
    [RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
    internal static bool TryCreate(EcmaWorkspaceExecutor interpreter, out EcmaCompiledExecutor? executor, out string reason, long maxAdditionalBytes = long.MaxValue)
    {
        executor = null;
        if (!TryPlan(interpreter, out var plan, out reason))
        {
            return false;
        }

        var estimate = (long)plan!.Nodes.Length * 160 * 2;
        if (estimate > maxAdditionalBytes)
        {
            reason = "The compiled kernel exceeds the pattern plan memory limit.";
            return false;
        }
        if (!CompiledKernelBudget.TryReserve(estimate, out var lease))
        {
            reason = "The process-wide runtime-compiled kernel budget is exhausted.";
            return false;
        }
        var plain = CoreKernelLowering.Compile(plan, trackCaptures: false);
        var capture = plan.GroupCount == 1 ? plain : CoreKernelLowering.Compile(plan, trackCaptures: true);
        if (plain is null || capture is null)
        {
            lease.Dispose();
            reason = "The fast expression compiler declined the lowered program.";
            return false;
        }
        executor = new(interpreter, plain, capture) { Lease = lease };
        return true;
    }

    // Analysis only: no code is generated, so this is usable under NativeAOT and by the build-time emitter.
    internal static bool TryPlan(EcmaWorkspaceExecutor interpreter, out CoreKernelPlan? plan, out string reason)
        => SourcePlanner.PlanEcma(interpreter.Executor.Program, out plan, out reason);
}
