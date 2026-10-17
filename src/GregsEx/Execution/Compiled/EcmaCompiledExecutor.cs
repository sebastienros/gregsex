#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Flavors.EcmaScript.Integration;

namespace GregsEx.Execution.Compiled;

// ECMAScript executor backed by a lowered kernel. Like CompiledRegularExecutor, the kernel is a pure match function:
// admission, start-boundary policy, limits and output stay observable-equal to the interpreter, and every case the
// kernel does not model (invalid arguments, surrogate-pair boundaries, abandoned backtracking) re-enters it unchanged.
internal sealed class EcmaCompiledExecutor : IRegularExecutor
{
    private const long StepsPerUnit = 32;
    private const long StepsBase = 16384;

    private readonly EcmaWorkspaceExecutor _fallback;
    private readonly CoreKernel _plainKernel;
    private readonly CoreKernel _captureKernel;
    private readonly int _groups;
    private readonly bool _unicode;

    internal CompiledKernelBudget.Lease? Lease { get; init; }
    internal EcmaWorkspaceExecutor Interpreter => _fallback;

    internal EcmaCompiledExecutor(EcmaWorkspaceExecutor fallback, CoreKernel plainKernel, CoreKernel captureKernel)
    {
        _fallback = fallback;
        _plainKernel = plainKernel;
        _captureKernel = captureKernel;
        _groups = fallback.Executor.Program.GroupCount;
        _unicode = fallback.Executor.Program.Profile.IsUnicodeAware;
    }

    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
    {
        var interpreter = _fallback.GetRequiredCapacity(operation);
        return new(interpreter.StateSlots, Math.Max(interpreter.CaptureSlots, 2),
            Math.Max(interpreter.ScratchSlots, CompiledRegularExecutor.StackInts));
    }

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        var captures = operation == SearchOperation.Captures;
        if (typeof(TUnit) != typeof(char)
            || (uint)startAt > (uint)input.Length
            || (uint)operation > (uint)SearchOperation.Captures
            || (captures && groups.Length < _groups)
            || !state.Buffers.Capacity.Contains(GetRequiredCapacity(operation)))
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        var chars = MemoryMarshal.Cast<TUnit, char>(input);
        if (_unicode && startAt > 0 && startAt < chars.Length && char.IsLowSurrogate(chars[startAt]) && char.IsHighSurrogate(chars[startAt - 1]))
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        var outputWork = captures ? 3L * _groups : 2;
        var allowed = Math.Min(state.Budget.RemainingWork - outputWork, StepsPerUnit * (chars.Length - startAt) + StepsBase);
        if (allowed > 0 && state.TryCharge(0, startAt))
        {
            var caps = state.Buffers.Captures[..(captures ? 2 * _groups : 2)];
            long work = 0;
            var guard = new KernelGuard(state.Budget);
            var code = (captures ? _captureKernel : _plainKernel)(chars, startAt, caps, state.Buffers.Scratch, allowed, ref work, ref guard);
            if (code is CoreKernelResult.Cancelled or CoreKernelResult.TimedOut)
            {
                match = default;
                state.Failure = new(code == CoreKernelResult.Cancelled ? RegexExecutionFailureCode.Cancelled : RegexExecutionFailureCode.TimedOut,
                    startAt, state.Budget.WorkConsumed + work);
                if (captures)
                {
                    groups[.._groups].Clear();
                }

                return code == CoreKernelResult.Cancelled ? RegexSearchStatus.Cancelled : RegexSearchStatus.TimedOut;
            }
            if (code is CoreKernelResult.Match or CoreKernelResult.NoMatch && work + outputWork <= state.Budget.RemainingWork)
            {
                state.TryCharge(work + outputWork, startAt);
                if (code == CoreKernelResult.NoMatch)
                {
                    match = default;
                    if (captures)
                    {
                        groups[.._groups].Clear();
                    }

                    return RegexSearchStatus.NoMatch;
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
        return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
    }
}
