#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Integration;

namespace GregsEx.Execution.Compiled;

// PCRE2 executor backed by a lowered kernel. The kernel is a pure match function over UTF-16 code units; everything it does
// not model re-enters the interpreter unchanged: byte subjects, surrogates in UTF mode (the interpreter owns validation
// diagnostics and scalar decoding), invalid arguments, match ceilings the kernel cannot honour, exhausted work budgets and
// abandoned backtracking.
internal sealed class Pcre2CompiledExecutor : IRegularExecutor
{
    private const long StepsPerUnit = 32;
    private const long StepsBase = 16384;

    private readonly Pcre2WorkspaceExecutor _fallback;
    private readonly CoreKernel _plainKernel;
    private readonly CoreKernel _captureKernel;
    private readonly int _groups;
    private readonly bool _utf;
    private readonly long _ceilingWork;
    private readonly bool _depthOk;

    internal CompiledKernelBudget.Lease? Lease { get; init; }
    internal Pcre2WorkspaceExecutor Interpreter => _fallback;

    internal Pcre2CompiledExecutor(Pcre2WorkspaceExecutor fallback, CoreKernel plainKernel, CoreKernel captureKernel)
    {
        _fallback = fallback;
        _plainKernel = plainKernel;
        _captureKernel = captureKernel;
        var program = fallback.Executor.Program;
        _groups = program.GroupCount;
        _utf = (program.Profile.Flags & Pcre2CompileFlags.Utf) != 0;
        _ceilingWork = fallback.Ceiling.MaxWork;
        _depthOk = fallback.Ceiling.MaxDepth >= CompiledRegularExecutor.StackInts / 3;
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
            || !_depthOk
            || (captures && groups.Length < _groups)
            || !state.Buffers.Capacity.Contains(GetRequiredCapacity(operation)))
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        var chars = MemoryMarshal.Cast<TUnit, char>(input);
        if (_utf && chars.IndexOfAnyInRange('\uD800', '\uDFFF') >= 0)
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        var outputWork = (captures ? 3L * _groups : 2) + (_utf ? chars.Length : 0);
        var allowed = Math.Min(Math.Min(state.Budget.RemainingWork, _ceilingWork) - outputWork, StepsPerUnit * (chars.Length - startAt) + StepsBase);
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
            if (code is CoreKernelResult.Match or CoreKernelResult.NoMatch && work + outputWork <= Math.Min(state.Budget.RemainingWork, _ceilingWork))
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
