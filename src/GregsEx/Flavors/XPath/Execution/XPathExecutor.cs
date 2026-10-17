#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;

namespace GregsEx.Flavors.XPath.Execution;

// Ordered, nonrecursive interpreter. Choice frames snapshot continuations and final
// participating captures into caller-owned partitions. Repeats never expand a plan.
internal sealed class XPathExecutor : IXmlExecutor
{
    private readonly XmlCaseMapping[] _cases;
    private readonly int _taskCapacity;
    private readonly XPathFlags _flags;
    private const int TaskWidth = 4, ChoiceWidth = 2;
    public XmlProgram Program { get; }
    internal XPathExecutor(XmlProgram program, XmlCaseMapping[] cases, int taskCapacity)
    { Program = program; _cases = cases; _taskCapacity = taskCapacity; _flags = ((XPathProfile)program.Profile).Flags; }

    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, int frameCapacity)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCapacity);
        int tasks = checked(TaskWidth * _taskCapacity), registers = checked(2 * Program.GroupCount);
        return new(checked(tasks + ChoiceWidth * frameCapacity),
            checked(registers * checked(frameCapacity + 1)), checked(tasks * frameCapacity));
    }

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, int frameCapacity, ref RunState state, Span<RegexCapture> groups,
        out RegexMatchRange match) where TUnit : unmanaged where TReader : struct, IXmlTextReader<TUnit>
    {
        match = default;
        XmlTextValidation.Admit(input.Length, startAt);
        if (typeof(TUnit) == typeof(char) ? Program.InputKind != RegexInputKind.Utf16 :
            typeof(TUnit) != typeof(byte) || Program.InputKind != RegexInputKind.Utf8)
        {
            throw new ArgumentException("Subject storage must match the validated plan input kind.", nameof(input));
        }

        var required = GetRequiredCapacity(operation, frameCapacity);
        if (operation == SearchOperation.Captures && groups.Length < Program.GroupCount)
        {
            state.Failure = new(RegexExecutionFailureCode.DestinationTooSmall, startAt,
                state.Budget.WorkConsumed, Program.GroupCount);
            return RegexSearchStatus.DestinationTooSmall;
        }
        var actual = state.Buffers.Capacity;
        if (actual.StateSlots < required.StateSlots || actual.CaptureSlots < required.CaptureSlots ||
            actual.ScratchSlots < required.ScratchSlots)
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, state.Budget.WorkConsumed);
            return Finish(RegexSearchStatus.LimitExceeded, operation, groups);
        }
        var validation = TReader.Validate(input, startAt, ref state);
        if (validation != RegexSearchStatus.NoMatch)
        {
            return Finish(validation, operation, groups);
        }

        var machine = new Machine<TUnit, TReader>(this, input, state.Buffers, frameCapacity);
        var candidate = startAt;
        while (true)
        {
            var result = machine.Run(candidate, ref state);
            if (result < 0)
            {
                return Finish(XmlTextValidation.FailureStatus(ref state), operation, groups);
            }

            if (result > 0)
            {
                if (!state.TryCharge(operation == SearchOperation.Captures ? Program.GroupCount : 1, machine.Position))
                {
                    return Finish(XmlTextValidation.FailureStatus(ref state), operation, groups);
                }

                match = new(candidate, machine.Position - candidate);
                if (operation == SearchOperation.Captures)
                {
                    groups[0] = new(true, candidate, match.Length);
                    for (var g = 1; g < Program.GroupCount; g++)
                    {
                        int begin = machine.Registers[2 * g], end = machine.Registers[2 * g + 1];
                        groups[g] = begin >= 0 && end >= begin ? new(true, begin, end - begin) : default;
                    }
                }
                return RegexSearchStatus.Match;
            }
            if (candidate == input.Length)
            {
                return Finish(RegexSearchStatus.NoMatch, operation, groups);
            }

            if (!machine.Read(candidate, ref state, out _, out candidate))
            {
                return Finish(XmlTextValidation.FailureStatus(ref state), operation, groups);
            }
        }
    }
    private RegexSearchStatus Finish(RegexSearchStatus status, SearchOperation operation, Span<RegexCapture> groups)
    { if (operation == SearchOperation.Captures) { groups[..Program.GroupCount].Clear(); } return status; }

    private enum TaskKind { Evaluate, Repeat, Body, After, CaptureEnd }
    private readonly record struct Task(TaskKind Kind, int Node, int Count = 0, int Begin = 0);
    private ref struct Machine<TUnit, TReader> where TUnit : unmanaged where TReader : struct, IXmlTextReader<TUnit>
    {
        private readonly XPathExecutor _owner;
        private readonly ReadOnlySpan<TUnit> _input;
        private readonly Span<int> _tasks, _choices, _savedTasks, _savedRegisters;
        private readonly int _frames, _taskSlots;
        private int _taskCount, _choiceCount;
        internal readonly Span<int> Registers;
        internal int Position;
        internal Machine(XPathExecutor owner, ReadOnlySpan<TUnit> input, WorkspaceBuffers buffers, int frames)
        {
            _owner = owner;
            _input = input;
            _frames = frames;
            _taskSlots = TaskWidth * owner._taskCapacity;
            _tasks = buffers.States[.._taskSlots];
            _choices = buffers.States.Slice(_taskSlots, frames * ChoiceWidth);
            Registers = buffers.Captures[..(2 * owner.Program.GroupCount)];
            _savedRegisters = buffers.Captures.Slice(Registers.Length, frames * Registers.Length);
            _savedTasks = buffers.Scratch[..(frames * _taskSlots)];
            _taskCount = _choiceCount = Position = 0;
        }
        internal int Run(int candidate, ref RunState state)
        {
            Position = candidate;
            _taskCount = _choiceCount = 0;
            if (!state.TryCharge(Registers.Length, Position))
            {
                return -1;
            }

            Registers.Fill(-1);
            if (!Push(new(TaskKind.Evaluate, _owner.Program.Root), ref state))
            {
                return -1;
            }

            while (true)
            {
                if (!state.TryCharge(1, Position))
                {
                    return -1;
                }

                if (_taskCount == 0)
                {
                    return 1;
                }

                if (!state.TryCharge(TaskWidth, Position))
                {
                    return -1;
                }

                var slot = _tasks.Slice(--_taskCount * TaskWidth, TaskWidth);
                var task = new Task((TaskKind)slot[0], slot[1], slot[2], slot[3]);
                var node = _owner.Program.Nodes[task.Node];
                var succeeds = true;
                switch (task.Kind)
                {
                    case TaskKind.CaptureEnd:
                        if (!state.TryCharge(2, Position))
                        {
                            return -1;
                        }

                        Registers[2 * node.Value] = task.Begin;
                        Registers[2 * node.Value + 1] = Position;
                        break;
                    case TaskKind.Repeat:
                        if (node.Max != -1 && task.Count == node.Max)
                        {
                            break;
                        }

                        if (task.Count < node.Min)
                        { if (!Body(task, ref state)) { return -1; } break; }
                        if (node.Lazy)
                        {
                            if (!Save(new(TaskKind.Body, task.Node, task.Count), ref state))
                            {
                                return -1;
                            }
                        }
                        else if (!Save(null, ref state) || !Body(task, ref state))
                        {
                            return -1;
                        }

                        break;
                    case TaskKind.Body:
                        if (!Body(task, ref state))
                        {
                            return -1;
                        }

                        break;
                    case TaskKind.After:
                        // Required empty iterations run to Min; thereafter one empty
                        // iteration may participate, but cannot restart an infinite loop.
                        if (Position == task.Begin && task.Count >= node.Min)
                        {
                            break;
                        }

                        var count = node.Max == -1 && task.Count >= node.Min ? task.Count : checked(task.Count + 1);
                        if (!Push(new(TaskKind.Repeat, task.Node, count), ref state))
                        {
                            return -1;
                        }

                        break;
                    default:
                        switch (node.Kind)
                        {
                            case XmlNodeKind.Empty:
                                break;
                            case XmlNodeKind.Sequence:
                                if (!Push(new(TaskKind.Evaluate, node.Y), ref state) || !Push(new(TaskKind.Evaluate, node.X), ref state))
                                {
                                    return -1;
                                }

                                break;
                            case XmlNodeKind.Alternative:
                                if (!Save(new(TaskKind.Evaluate, node.Y), ref state) || !Push(new(TaskKind.Evaluate, node.X), ref state))
                                {
                                    return -1;
                                }

                                break;
                            case XmlNodeKind.Repeat:
                                if (!Push(new(TaskKind.Repeat, task.Node), ref state))
                                {
                                    return -1;
                                }

                                break;
                            case XmlNodeKind.Capture:
                                if (!Push(new(TaskKind.CaptureEnd, task.Node, Begin: Position), ref state) ||
                                    !Push(new(TaskKind.Evaluate, node.X), ref state))
                                {
                                    return -1;
                                }

                                break;
                            case XmlNodeKind.Backreference:
                                succeeds = Backreference(node.Value, ref state);
                                break;
                            case XmlNodeKind.Start:
                                succeeds = Start(ref state);
                                break;
                            case XmlNodeKind.End:
                                succeeds = End(ref state);
                                break;
                            case XmlNodeKind.Literal:
                                succeeds = Read(Position, ref state, out var cp, out var next) && Equal(cp, node.Value, ref state);
                                if (succeeds)
                                {
                                    Position = next;
                                }

                                break;
                            case XmlNodeKind.Class:
                                succeeds = Read(Position, ref state, out cp, out next) && Contains(node.Value, cp, ref state);
                                if (succeeds)
                                {
                                    Position = next;
                                }

                                break;
                        }
                        break;
                }
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return -1;
                }

                if (succeeds)
                {
                    continue;
                }

                if (_choiceCount == 0)
                {
                    return 0;
                }

                if (!Restore(--_choiceCount, ref state))
                {
                    return -1;
                }
            }
        }
        private bool Body(Task task, ref RunState state) =>
            Push(new(TaskKind.After, task.Node, task.Count, Position), ref state) &&
            Push(new(TaskKind.Evaluate, _owner.Program.Nodes[task.Node].X), ref state);
        private bool Push(Task task, ref RunState state)
        {
            if (_taskCount == _owner._taskCapacity)
            {
                return Exhaust(ref state);
            }

            if (!state.TryCharge(TaskWidth, Position))
            {
                return false;
            }

            var s = _tasks.Slice(_taskCount++ * TaskWidth, TaskWidth);
            s[0] = (int)task.Kind;
            s[1] = task.Node;
            s[2] = task.Count;
            s[3] = task.Begin;
            return true;
        }
        private bool Save(Task? append, ref RunState state)
        {
            if (_choiceCount == _frames)
            {
                return Exhaust(ref state);
            }

            if (append.HasValue && !Push(append.Value, ref state))
            {
                return false;
            }

            var index = _choiceCount++;
            if (!state.TryCharge((long)_taskCount * TaskWidth + Registers.Length + ChoiceWidth, Position))
            {
                return false;
            }

            _tasks[..(_taskCount * TaskWidth)].CopyTo(_savedTasks.Slice(index * _taskSlots));
            Registers.CopyTo(_savedRegisters.Slice(index * Registers.Length));
            var s = _choices.Slice(index * ChoiceWidth, ChoiceWidth);
            s[0] = _taskCount;
            s[1] = Position;
            if (append.HasValue)
            {
                _taskCount--;
            }

            return true;
        }
        private bool Restore(int index, ref RunState state)
        {
            var s = _choices.Slice(index * ChoiceWidth, ChoiceWidth);
            var count = s[0];
            if (!state.TryCharge((long)count * TaskWidth + Registers.Length + ChoiceWidth, Position))
            {
                return false;
            }

            _savedTasks.Slice(index * _taskSlots, count * TaskWidth).CopyTo(_tasks);
            _savedRegisters.Slice(index * Registers.Length, Registers.Length).CopyTo(Registers);
            _taskCount = count;
            Position = s[1];
            return true;
        }
        private readonly bool Exhaust(ref RunState state)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, Position, state.Budget.WorkConsumed); return false; }
        internal readonly bool Read(int offset, ref RunState state, out int scalar, out int next)
        {
            next = offset;
            if (!TReader.TryRead(_input, offset, out scalar, out var width))
            {
                return false;
            }

            if (!state.TryCharge(width, offset))
            {
                return false;
            }

            next = offset + width;
            return true;
        }
        private readonly bool Contains(int setId, int cp, ref RunState state)
        {
            var set = _owner.Program.Sets[setId];
            var intervals = _owner.Program.Intervals.Slice(set.Offset, set.Count);
            int lo = 0, hi = intervals.Length - 1;
            while (lo <= hi)
            {
                if (!state.TryCharge(1, Position))
                {
                    return false;
                }

                var mid = lo + (hi - lo) / 2;
                var item = intervals[mid];
                if (cp < item.First)
                {
                    hi = mid - 1;
                }
                else if (cp > item.Last)
                {
                    lo = mid + 1;
                }
                else
                {
                    return true;
                }
            }
            return false;
        }
        private readonly XmlCaseMapping Mapping(int cp, ref RunState state)
        {
            int lo = 0, hi = _owner._cases.Length - 1;
            while (lo <= hi)
            {
                if (!state.TryCharge(1, Position))
                {
                    break;
                }

                var mid = lo + (hi - lo) / 2;
                var item = _owner._cases[mid];
                if (cp < item.Scalar)
                {
                    hi = mid - 1;
                }
                else if (cp > item.Scalar)
                {
                    lo = mid + 1;
                }
                else
                {
                    return item;
                }
            }
            return new(cp, cp, cp);
        }
        private readonly bool Equal(int a, int b, ref RunState state)
        {
            if (a == b)
            {
                return true;
            }

            if ((_owner._flags & XPathFlags.IgnoreCase) == 0)
            {
                return false;
            }

            var x = Mapping(a, ref state);
            var y = Mapping(b, ref state);
            return x.LowerKey == y.LowerKey || x.UpperKey == y.UpperKey;
        }
        private bool Backreference(int group, ref RunState state)
        {
            if (!state.TryCharge(2, Position))
            {
                return false;
            }

            int begin = Registers[2 * group], end = Registers[2 * group + 1], target = Position;
            if (begin < 0)
            {
                return true;
            }

            while (begin < end)
            {
                if (!Read(begin, ref state, out var a, out begin) || !Read(target, ref state, out var b, out var next) ||
                    !Equal(a, b, ref state))
                {
                    return false;
                }

                target = next;
            }
            Position = target;
            return true;
        }
        private readonly bool IsLf(int offset, ref RunState state) =>
            Read(offset, ref state, out var cp, out _) && cp == 10;
        private readonly bool Start(ref RunState state)
        {
            if (Position == 0)
            {
                return true;
            }

            return (_owner._flags & XPathFlags.Multiline) != 0 && Position < _input.Length && IsLf(Position - 1, ref state);
        }
        private readonly bool End(ref RunState state)
        {
            if ((_owner._flags & XPathFlags.Multiline) == 0)
            {
                return Position == _input.Length;
            }

            if (Position < _input.Length)
            {
                return IsLf(Position, ref state);
            }

            return Position == 0 || !IsLf(Position - 1, ref state);
        }
    }
}
