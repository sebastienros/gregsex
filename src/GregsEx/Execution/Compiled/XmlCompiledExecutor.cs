using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlIntegration;

namespace GregsEx.Execution.Compiled;

// XSD / XPath executor backed by a lowered kernel. The kernel is a pure match function over UTF-16 text; everything it does
// not model re-enters the interpreter unchanged: UTF-8 subjects, invalid or astral text (the interpreter owns validation
// diagnostics), invalid arguments, XSD starts other than 0, exhausted work budgets and abandoned backtracking.
internal sealed class XmlCompiledExecutor : IRegularExecutor
{
    private const long StepsPerUnit = 32;
    private const long StepsBase = 16384;

    private readonly XmlWorkspaceExecutor _fallback;
    private readonly CoreKernel _plainKernel;
    private readonly CoreKernel _captureKernel;
    private readonly int _groups;
    private readonly bool _xsd;

    private readonly RegexWorkspaceCapacity[] _capacities;

    internal CompiledKernelBudget.Lease? Lease { get; init; }
    internal XmlWorkspaceExecutor Interpreter => _fallback;

    internal XmlCompiledExecutor(XmlWorkspaceExecutor fallback, CoreKernel plainKernel, CoreKernel captureKernel)
    {
        _fallback = fallback;
        _plainKernel = plainKernel;
        _captureKernel = captureKernel;
        var program = fallback.Inner.Program;
        _groups = program.GroupCount;
        _xsd = program.Dialect == XmlDialect.Xsd;
        _capacities = new RegexWorkspaceCapacity[3];
        for (var i = 0; i < _capacities.Length; i++)
        {
            _capacities[i] = Compute((SearchOperation)i);
        }
    }

    // Cached: the interpreter's capacity query validates its arguments with checked arithmetic on every call.
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
        => (uint)operation < (uint)_capacities.Length ? _capacities[(int)operation] : Compute(operation);

    private RegexWorkspaceCapacity Compute(SearchOperation operation)
    {
        var interpreter = _fallback.GetRequiredCapacity(operation);
        return new(interpreter.StateSlots, Math.Max(interpreter.CaptureSlots, 2),
            Math.Max(interpreter.ScratchSlots, CompiledRegularExecutor.StackInts));
    }

    // Text the interpreter's validator accepts without any diagnostic and that is made of single code units only.
    private static bool IsPlainXmlText(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if ((uint)(c - ' ') > '\uD7FF' - ' ' && !(c is '\t' or '\n' or '\r' or (>= '\uE000' and <= '\uFFFD')))
            {
                return false;
            }
        }

        return true;
    }

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        var captures = operation == SearchOperation.Captures;
        if (typeof(TUnit) != typeof(char)
            || (uint)startAt > (uint)input.Length
            || (uint)operation > (uint)SearchOperation.Captures
            || (_xsd && startAt != 0)
            || (captures && groups.Length < _groups)
            || !state.Buffers.Capacity.Contains(GetRequiredCapacity(operation)))
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        var chars = MemoryMarshal.Cast<TUnit, char>(input);
        if (!IsPlainXmlText(chars))
        {
            return _fallback.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
        }

        // One unit of validation work per code unit, as the interpreter's validator charges for plain text.
        var outputWork = (captures ? 3L * _groups : 2) + chars.Length;
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
