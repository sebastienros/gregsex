#nullable enable
using System.Diagnostics.CodeAnalysis;
using GregsEx.Contracts.Acceleration;
using GregsEx.Execution.Thompson;

namespace GregsEx.Execution.Compiled;

// Plan analysis plus lowering for the RegularCore interpreter, shared by the public Compile() operation.
internal static class CompiledBackend
{
    // Never falls back to Expression.Compile: a null result from the fast compiler is an ineligible pattern.
    [RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
    internal static bool TryCreate(ThompsonExecutor interpreter, out CompiledRegularExecutor? executor, out string reason, long maxAdditionalBytes = long.MaxValue)
    {
        executor = null;
        if (!TryPlan(interpreter, out var plan, out reason))
        {
            return false;
        }

        var program = interpreter.Program;
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
        var capture = program.GroupCount == 1 ? plain : CoreKernelLowering.Compile(plan, trackCaptures: true);
        if (plain is null || capture is null)
        {
            lease.Dispose();
            reason = "The fast expression compiler declined the lowered program.";
            return false;
        }
        executor = new(new ThompsonExecutor(program, AcceleratorSwitches.Semantic), plain, capture) { Lease = lease };
        return true;
    }

    // Analysis only: no code is generated, so this is usable under NativeAOT and by the build-time source generator.
    internal static bool TryPlan(ThompsonExecutor interpreter, out CoreKernelPlan? plan, out string reason)
        => SourcePlanner.PlanCore(interpreter.Program, interpreter.HasFastPath, out plan, out reason);
}
