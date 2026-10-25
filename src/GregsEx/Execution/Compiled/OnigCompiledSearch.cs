#nullable enable
using System.Diagnostics.CodeAnalysis;
using GregsEx.Contracts.Acceleration;

namespace GregsEx.Execution.Compiled;

// The Oniguruma kernels' admission and result publication, shared by OnigurumaWorkspace and OnigurumaScanner. The kernel is
// a pure match function over UTF-16 units; everything it does not model re-enters the interpreter unchanged: subjects with
// surrogates (the interpreter owns character decoding), searches with options (their budget, cancellation and deadline
// live in the matcher), exhausted step budgets and abandoned backtracking.
internal static class OnigCompiledSearch
{
    private const long StepsPerUnit = 32;
    private const long StepsBase = 16384;

    [RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
    internal static bool TryCompile(OnigurumaPattern pattern, out CoreKernel? plain, out CoreKernel? capture,
        out CompiledKernelBudget.Lease? lease, out string reason)
    {
        plain = capture = null;
        lease = null;
        if (!OnigKernelPlan.TryPlan(pattern.Source, pattern.Profile, pattern.CompileOptions.Limits, LoweringLimits.Default,
                out var plan, out reason, out _) || plan is null)
        {
            return false;
        }

        var estimate = (long)plan.Nodes.Length * 160 * 2;
        if (estimate > pattern.CompileOptions.Limits.MaxPlanBytes)
        {
            reason = "The compiled kernel exceeds the pattern plan memory limit.";
            return false;
        }

        if (!CompiledKernelBudget.TryReserve(estimate, out lease))
        {
            reason = "The process-wide runtime-compiled kernel budget is exhausted.";
            return false;
        }

        plain = CoreKernelLowering.Compile(plan, trackCaptures: false);
        capture = plan.GroupCount == 1 ? plain : CoreKernelLowering.Compile(plan, trackCaptures: true);
        if (plain is null || capture is null)
        {
            lease.Dispose();
            lease = null;
            reason = "The fast expression compiler declined the lowered program.";
            return false;
        }

        return true;
    }

    // Runs the kernel when it applies; false sends the search to the interpreter. captures holds 2 ints per group; only
    // group zero is published unless trackCaptures.
    internal static bool TryRun(OnigurumaPattern pattern, ReadOnlySpan<char> input, int startAt, Span<int> captures, bool trackCaptures,
        ref int[]? stack, out RegexSearchStatus status, out long work)
    {
        status = RegexSearchStatus.NoMatch;
        work = 0;
        // The kernel itself reports a subject with a surrogate (SurrogateEncountered), after its search-level prefilters.
        var kernel = trackCaptures ? pattern.CaptureKernel : pattern.PlainKernel;
        if (kernel is null)
        {
            return false;
        }

        var allowed = Math.Min(pattern.MatchLimits.MaxWork, StepsPerUnit * (input.Length - startAt) + StepsBase);
        if (allowed <= 0)
        {
            return false;
        }

        stack ??= new int[CompiledRegularExecutor.StackInts];
        var groups = pattern.GroupCount;
        var caps = captures[..(trackCaptures ? 2 * groups : 2)];
        var guard = KernelGuard.None;
        var code = kernel(input, startAt, caps, stack, allowed, ref work, ref guard);
        switch (code)
        {
            case CoreKernelResult.Match:
                if (trackCaptures)
                {
                    for (var g = 1; g < groups; g++)
                    {
                        if (caps[2 * g] < 0 || caps[2 * g + 1] < caps[2 * g])
                        {
                            caps[2 * g] = caps[2 * g + 1] = -1;
                        }
                    }
                }

                status = RegexSearchStatus.Match;
                return true;
            case CoreKernelResult.NoMatch:
                return true;
            default:
                return false;
        }
    }
}
