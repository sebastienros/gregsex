#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Execution.Thompson;

namespace GregsEx.Execution.Compiled;

// RegularCore executor backed by a lowered kernel. The kernel is a pure match function; everything observable
// (admission, validation, budget, failure publication, output) stays here and in ThompsonExecutor so compiled and
// interpreted execution cannot drift. Any case the kernel does not model re-enters the interpreter after admission.
internal sealed class CompiledRegularExecutor : IRegularExecutor
{
    // Backtrack stack ints reserved in Scratch (three ints per entry).
    internal const int StackInts = 3 * 4096;
    // Kernel work allowed per input unit before backtracking is abandoned for the Pike VM; this keeps the worst-case
    // slowdown of an ambiguous pattern to a small constant factor of the interpreter's own cost.
    private const long StepsPerUnit = 32;
    private const long StepsBase = 16384;

    private readonly ThompsonExecutor _fallback;
    private readonly CoreKernel _captureKernel;
    private readonly CoreKernel _plainKernel;
    private readonly int _groups;

    // Keeps the process-wide compiled-kernel reservation alive exactly as long as the kernels.
    internal CompiledKernelBudget.Lease? Lease { get; init; }
    internal ThompsonExecutor Interpreter => _fallback;

    internal CompiledRegularExecutor(ThompsonExecutor fallback, CoreKernel plainKernel, CoreKernel captureKernel)
    {
        _fallback = fallback;
        _plainKernel = plainKernel;
        _captureKernel = captureKernel;
        _groups = fallback.Program.GroupCount;
    }

    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
    {
        var pike = _fallback.GetRequiredCapacity(operation);
        return new(pike.StateSlots, Math.Max(pike.CaptureSlots, 2), Math.Max(pike.ScratchSlots, StackInts));
    }

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if (!_fallback.TryAdmit<TUnit, TReader>(input, startAt, operation, ref state, groups, out var status))
        {
            match = default;
            return status;
        }
        if (typeof(TUnit) != typeof(char)
            || !state.Buffers.Capacity.Contains(GetRequiredCapacity(operation)))
        {
            return _fallback.SearchValidated<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        if (_fallback.TryRejectByPrecondition(input, startAt, operation, ref state, groups, out status))
        {
            match = default;
            return status;
        }
        var outputWork = operation == SearchOperation.Captures ? 3L * _groups : operation == SearchOperation.Range ? 2 : 0;
        var allowed = Math.Min(state.Budget.RemainingWork - outputWork, StepsPerUnit * (input.Length - startAt) + StepsBase);
        if (allowed > 0)
        {
            var chars = MemoryMarshal.Cast<TUnit, char>(input);
            var captures = operation == SearchOperation.Captures;
            var caps = state.Buffers.Captures[..(captures ? 2 * _groups : 2)];
            long work = 0;
            var guard = new KernelGuard(state.Budget);
            var code = (captures ? _captureKernel : _plainKernel)(chars, startAt, caps, state.Buffers.Scratch, allowed, ref work, ref guard);
            if (code is CoreKernelResult.Cancelled or CoreKernelResult.TimedOut)
            {
                match = default;
                state.Failure = new(code == CoreKernelResult.Cancelled ? RegexExecutionFailureCode.Cancelled : RegexExecutionFailureCode.TimedOut,
                    startAt, state.Budget.WorkConsumed + work);
                return _fallback.Finish(ThompsonExecutor.FailureStatus(state), operation, groups);
            }
            if (code is CoreKernelResult.Match or CoreKernelResult.NoMatch
                && work + outputWork <= state.Budget.RemainingWork)
            {
                // Kernel work stands in for the interpreter's: it never exceeds what remains, so this cannot fail.
                state.TryCharge(work + outputWork, startAt);
                if (code == CoreKernelResult.NoMatch)
                {
                    match = default;
                    return _fallback.Finish(RegexSearchStatus.NoMatch, operation, groups);
                }
                match = new(caps[0], caps[1] - caps[0]);
                if (captures)
                {
                    for (var g = 0; g < _groups; g++)
                    {
                        int begin = caps[2 * g], end = caps[2 * g + 1];
                        groups[g] = begin >= 0 && end >= begin ? new(true, begin, end - begin) : default;
                    }
                }

                return RegexSearchStatus.Match;
            }
        }
        return _fallback.SearchValidated<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
    }
}
