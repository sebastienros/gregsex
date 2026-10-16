using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Execution.Accelerated.Literals;
using GregsEx.IR;

namespace GregsEx.Execution.Thompson;

// Immutable bound plan. All mutable execution storage belongs to RunState.
internal sealed class ThompsonExecutor : IRegularExecutor
{
    private readonly SemanticProgram _program;
    // Optional candidate-start skipping; null in Interpreted mode, which is the differential oracle.
    private readonly StartScan? _scan;
    private readonly StartScan? _unconstrainedScan;

    internal ThompsonExecutor(SemanticProgram program, AcceleratorSwitches switches = AcceleratorSwitches.None)
    {
        ArgumentNullException.ThrowIfNull(program);
        _program = program;
        _scan = StartScan.TryCreate(program, switches);
        _unconstrainedScan = _scan ?? StartScan.TryCreate(program, AcceleratorSwitches.Semantic);
    }

    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
        => CalculateCapacity(_program.Instructions.Length, _program.GroupCount, operation);

    // Kept independently testable so overflow need not allocate a gigantic admitted program.
    // An unrepresentable partition is explicit exhaustion; Runtime must map OverflowException
    // from this admission query to WorkspaceCapacityExceeded, before renting/allocating.
    internal static RegexWorkspaceCapacity CalculateCapacity(int instructions, int groups, SearchOperation operation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instructions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(groups);
        var registers = RegisterCount(groups, operation);
        return new(checked((int)(2L * instructions)),
            checked((int)((3L * instructions + 2) * registers)),
            checked((int)(2L * instructions)));
    }

    private static int RegisterCount(int groups, SearchOperation operation) => operation switch
    {
        SearchOperation.Boolean => 0,
        SearchOperation.Range => 2,
        SearchOperation.Captures => checked((int)(2L * groups)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if (!TryAdmit<TUnit, TReader>(input, startAt, operation, ref state, groups, out var status))
        {
            match = default;
            return status;
        }
        return SearchValidated<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
    }

    // Range, destination, capacity, start-offset charge and input validation, in the order every backend must observe.
    // Returns false with the terminal status when the search ends here.
    internal bool TryAdmit<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexSearchStatus status)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if ((uint)startAt > (uint)input.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }

        if (operation == SearchOperation.Captures && groups.Length < _program.GroupCount)
        {
            state.Failure = new(RegexExecutionFailureCode.DestinationTooSmall, startAt, 0, _program.GroupCount);
            status = RegexSearchStatus.DestinationTooSmall;
            return false;
        }
        RegexWorkspaceCapacity required;
        try
        { required = GetRequiredCapacity(operation); }
        catch (OverflowException)
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, state.Budget.WorkConsumed);
            status = Finish(RegexSearchStatus.LimitExceeded, operation, groups);
            return false;
        }
        if (!state.Buffers.Capacity.Contains(required))
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, state.Budget.WorkConsumed);
            status = Finish(RegexSearchStatus.LimitExceeded, operation, groups);
            return false;
        }
        if (!state.TryCharge(0, startAt))
        {
            status = Finish(FailureStatus(state), operation, groups);
            return false;
        }
        var validation = TReader.Validate(input, startAt, _program.Profile, ref state);
        if (validation != RegexSearchStatus.NoMatch)
        {
            status = Finish(validation, operation, groups);
            return false;
        }
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    // A proven mismatch (required unit or literal absent) ends the search exactly as the interpreter would, work included.
    internal bool TryRejectByPrecondition<TUnit>(ReadOnlySpan<TUnit> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexSearchStatus status)
        where TUnit : unmanaged
    {
        var scan = _scan ?? (state.Budget.IsUnconstrained(1_000_000_000L) ? _unconstrainedScan : null);
        if (scan is null || scan.CheckRequiredPrecondition(input, startAt, ref state))
        {
            status = RegexSearchStatus.NoMatch;
            return false;
        }
        status = Finish(RegexSearchStatus.NoMatch, operation, groups);
        return true;
    }

    internal SemanticProgram Program => _program;
    internal bool HasFastPath => (_scan ?? _unconstrainedScan)?.HasFastPath == true;

    // Everything after admission, capacity and input validation; the compiled backend re-enters here on bailout
    // so validation work is never charged twice.
    internal RegexSearchStatus SearchValidated<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        match = default;
        var registers = RegisterCount(_program.GroupCount, operation);
        var activeScan = _scan ?? (state.Budget.IsUnconstrained(1_000_000_000L) ? _unconstrainedScan : null);
        if (activeScan is not null)
        {
            if (activeScan.TryExecuteFastPath(input, startAt, operation, ref state, groups, out match, out var fastStatus))
            {
                return fastStatus == RegexSearchStatus.Match ? RegexSearchStatus.Match : Finish(fastStatus, operation, groups);
            }

            if (!activeScan.CheckRequiredPrecondition(input, startAt, ref state))
            {
                return Finish(RegexSearchStatus.NoMatch, operation, groups);
            }
        }

        var n = _program.Instructions.Length;
        var current = state.Buffers.States[..n];
        var next = state.Buffers.States.Slice(n, n);
        var currentRegisters = state.Buffers.Captures[..(n * registers)];
        var nextRegisters = state.Buffers.Captures.Slice(n * registers, n * registers);
        var stackRegisters = state.Buffers.Captures.Slice(2 * n * registers, n * registers);
        var working = state.Buffers.Captures.Slice(3 * n * registers, registers);
        var saved = state.Buffers.Captures.Slice((3 * n + 1) * registers, registers);
        var visited = state.Buffers.Scratch[..n];
        var stack = state.Buffers.Scratch.Slice(n, n);
        var count = 0;
        var offset = startAt;
        if (activeScan is not null)
        {
            offset = activeScan.Next(input, offset, ref state);
            if (offset < 0)
            {
                return Finish(FailureStatus(state), operation, groups);
            }

            if (offset == input.Length)
            {
                return Finish(RegexSearchStatus.NoMatch, operation, groups);
            }
        }
        visited.Clear();
        var generation = 0;
        if (!NextGeneration(visited, ref generation, ref state, offset) || !Fill(working, -1, ref state, offset))
        {
            return Finish(FailureStatus(state), operation, groups);
        }

        var closure = AddClosure(0, offset, input.Length, registers, current, currentRegisters,
            ref count, visited, generation, stack, stackRegisters, working, saved, ref state);
        if (closure < 0)
        {
            return Finish(FailureStatus(state), operation, groups);
        }

        var hasMatch = closure == 1;
        while (true)
        {
            if (hasMatch && (operation == SearchOperation.Boolean || count == 0))
            {
                break;
            }

            if (offset == input.Length)
            {
                break;
            }

            if (!state.TryCharge(1, offset))
            {
                return Finish(FailureStatus(state), operation, groups);
            }

            if (!TReader.TryRead(input, offset, _program.Profile, out var value, out var width) ||
                width <= 0 || width > input.Length - offset)
            {
                throw new InvalidOperationException("Reader violated the validated-input contract.");
            }

            var end = offset + width;
            var nextCount = 0;
            if (!NextGeneration(visited, ref generation, ref state, end))
            {
                return Finish(FailureStatus(state), operation, groups);
            }

            for (var i = 0; i < count; i++)
            {
                var instruction = _program.Instructions[current[i]];
                if (!state.TryCharge(1, offset))
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                var accepts = instruction.Opcode switch
                {
                    CoreOpcode.Literal => value == instruction.Operand,
                    CoreOpcode.Any => value != '\n' || (_program.Profile.Flags & RegularCoreFlags.DotAll) != 0,
                    CoreOpcode.Class => ClassContains(instruction.Operand, value, offset, ref state),
                    _ => throw new InvalidOperationException("Closure published a non-consuming instruction.")
                };
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                if (!accepts)
                {
                    continue;
                }

                if (!Copy(currentRegisters.Slice(i * registers, registers), working, ref state, end))
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                closure = AddClosure(instruction.X, end, input.Length, registers, next, nextRegisters,
                    ref nextCount, visited, generation, stack, stackRegisters, working, saved, ref state);
                if (closure < 0)
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                if (closure == 1)
                { hasMatch = true; break; }
            }
            // New candidates have lower priority than every surviving earlier candidate.
            if (!hasMatch)
            {
                // No thread survived: offsets before the next candidate start would only spawn dying threads.
                if (activeScan is not null && nextCount == 0)
                {
                    var candidate = activeScan.Next(input, end, ref state);
                    if (candidate < 0)
                    {
                        return Finish(FailureStatus(state), operation, groups);
                    }

                    if (candidate == input.Length)
                    { offset = input.Length; count = 0; break; }
                    if (candidate > end)
                    {
                        end = candidate;
                        if (!NextGeneration(visited, ref generation, ref state, end))
                        {
                            return Finish(FailureStatus(state), operation, groups);
                        }
                    }
                }
                if (!state.TryCharge(1, end) || !Fill(working, -1, ref state, end))
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                closure = AddClosure(0, end, input.Length, registers, next, nextRegisters,
                    ref nextCount, visited, generation, stack, stackRegisters, working, saved, ref state);
                if (closure < 0)
                {
                    return Finish(FailureStatus(state), operation, groups);
                }

                hasMatch = closure == 1;
            }
            var statesSwap = current;
            current = next;
            next = statesSwap;
            var capturesSwap = currentRegisters;
            currentRegisters = nextRegisters;
            nextRegisters = capturesSwap;
            count = nextCount;
            offset = end;
        }
        if (!hasMatch)
        {
            return Finish(RegexSearchStatus.NoMatch, operation, groups);
        }

        if (operation == SearchOperation.Boolean)
        {
            return RegexSearchStatus.Match;
        }
        // Charge all successful output work before publishing any result.
        var outputWork = operation == SearchOperation.Captures ? 3L * _program.GroupCount : 2L;
        if (!state.TryCharge(outputWork, offset))
        {
            return Finish(FailureStatus(state), operation, groups);
        }

        match = new(saved[0], saved[1] - saved[0]);
        if (operation == SearchOperation.Captures)
        {
            for (var g = 0; g < _program.GroupCount; g++)
            {
                var begin = saved[2 * g];
                var end = saved[2 * g + 1];
                groups[g] = begin >= 0 && end >= begin ? new(true, begin, end - begin) : default;
            }
        }
        return RegexSearchStatus.Match;
    }

    // Depth-first X-before-Y traversal: mark on VISIT, not push, so a deferred Y cannot
    // suppress a higher-priority X descendant. At most N visited splits push N snapshots.
    // Return 1 on acceptance (lower paths discarded), 0 on closure exhaustion, -1 on failure.
    private int AddClosure(int pc, int offset, int length, int registers, Span<int> targets,
        Span<int> targetRegisters, ref int count, Span<int> visited, int generation, Span<int> stack,
        Span<int> stackRegisters, Span<int> working, Span<int> saved, ref RunState state)
    {
        var depth = 0;
        while (true)
        {
            if (!state.TryCharge(1, offset))
            {
                return -1; // includes dedup attempts
            }

            if (visited[pc] != generation)
            {
                if (!state.TryCharge(1, offset))
                {
                    return -1;
                }

                visited[pc] = generation;
                var instruction = _program.Instructions[pc];
                switch (instruction.Opcode)
                {
                    case CoreOpcode.Split:
                        if (!state.TryCharge(1, offset) ||
                            !Copy(working, stackRegisters.Slice(depth * registers, registers), ref state, offset))
                        {
                            return -1;
                        }

                        stack[depth++] = instruction.Y;
                        pc = instruction.X;
                        continue;
                    case CoreOpcode.Jump:
                        pc = instruction.X;
                        continue;
                    case CoreOpcode.CaptureStart:
                    case CoreOpcode.CaptureEnd:
                        var slot = 2L * instruction.Operand;
                        if (slot < registers)
                        {
                            if (!state.TryCharge(instruction.Opcode == CoreOpcode.CaptureStart ? 2 : 1, offset))
                            {
                                return -1;
                            }

                            if (instruction.Opcode == CoreOpcode.CaptureStart)
                            { working[(int)slot] = offset; working[(int)slot + 1] = -1; }
                            else
                            {
                                working[(int)slot + 1] = offset;
                            }
                        }
                        pc = instruction.X;
                        continue;
                    case CoreOpcode.ClearCaptures:
                        if (registers > 2 && !Fill(working.Slice(2 * instruction.Operand, 2 * instruction.Count),
                            -1, ref state, offset))
                        {
                            return -1;
                        }

                        pc = instruction.X;
                        continue;
                    case CoreOpcode.AssertStart:
                        if (offset == 0)
                        { pc = instruction.X; continue; }
                        break;
                    case CoreOpcode.AssertEnd:
                        if (offset == length)
                        { pc = instruction.X; continue; }
                        break;
                    case CoreOpcode.Match:
                        return Copy(working, saved, ref state, offset) ? 1 : -1;
                    default:
                        if (!state.TryCharge(1, offset) ||
                            !Copy(working, targetRegisters.Slice(count * registers, registers), ref state, offset))
                        {
                            return -1;
                        }

                        targets[count++] = pc;
                        break;
                }
            }
            if (depth == 0)
            {
                return 0;
            }

            if (!state.TryCharge(1, offset))
            {
                return -1;
            }

            pc = stack[--depth];
            if (!Copy(stackRegisters.Slice(depth * registers, registers), working, ref state, offset))
            {
                return -1;
            }
        }
    }

    private bool ClassContains(int index, int value, int offset, ref RunState state)
    {
        var @class = _program.Classes[index];
        var ranges = @class.Ranges;
        if (state.Budget.IsUnconstrained(ranges.Length))
        {
            if (ranges.Length > 0)
            {
                state.TryCharge(1, offset);
            }

            return @class.Contains(value, _program.Profile);
        }
        var found = false;
        foreach (var interval in ranges)
        {
            if (!state.TryCharge(1, offset))
            {
                return false;
            }

            if (value < interval.First)
            {
                break;
            }

            if (value <= interval.Last)
            { found = true; break; }
        }
        return _program.Profile.Contains(value) && (found != @class.Negated);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool NextGeneration(Span<int> visited, ref int generation, ref RunState state, int offset)
    {
        if (!state.TryCharge(visited.Length, offset))
        {
            return false;
        }

        if (++generation == int.MaxValue)
        {
            visited.Clear();
            generation = 1;
        }
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Fill(Span<int> destination, int value, ref RunState state, int offset)
    {
        if (!state.TryCharge(destination.Length, offset))
        {
            return false;
        }

        if (destination.Length == 0)
        {
            return true;
        }

        if (destination.Length == 2)
        {
            destination[0] = value;
            destination[1] = value;
            return true;
        }
        destination.Fill(value);
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool Copy(ReadOnlySpan<int> source, Span<int> destination, ref RunState state, int offset)
    {
        if (!state.TryCharge(source.Length, offset))
        {
            return false;
        }

        if (source.Length == 0)
        {
            return true;
        }

        if (source.Length == 2)
        {
            destination[0] = source[0];
            destination[1] = source[1];
            return true;
        }
        source.CopyTo(destination);
        return true;
    }

    internal RegexSearchStatus Finish(RegexSearchStatus status, SearchOperation operation, Span<RegexCapture> groups)
    {
        if (operation == SearchOperation.Captures)
        {
            groups[.._program.GroupCount].Clear();
        }

        return status;
    }

    internal static RegexSearchStatus FailureStatus(in RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidStartBoundary => RegexSearchStatus.InvalidInput,
        _ => RegexSearchStatus.LimitExceeded
    };
}
