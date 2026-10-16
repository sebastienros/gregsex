using GregsEx.Contracts;
using GregsEx.Contracts.Posix;

namespace GregsEx.Execution.Posix;

// Deliberately exhaustive bounded reference verifier. Persistent continuations and
// occurrence events preserve distinct paths; there is no (node,offset) merging.
internal sealed class PosixExecutor(PosixProgram program, int[] priority) : IPosixExecutor
{
    private readonly int[] _priority = priority;
    private readonly PosixAccelerator? _accelerator = PosixAccelerator.TryCreate(program);
    public PosixProgram Program => program;
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, PosixExecutionCapacity capacity)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (!capacity.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        // Frames: seven ints/continuation. Choices: three ints/alternative.
        // Histories: six ints/event. Publication: two event IDs/group (scratch
        // active parent identity and winning close), plus reserved output triples.
        return new(checked(7 * capacity.Frames), checked(3 * program.GroupCount),
            checked(3 * capacity.Frames + 6 * capacity.HistoryEvents + 2 * program.GroupCount));
    }

    public RegexSearchStatus Search(ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        PosixExecutionFlags flags, PosixExecutionCapacity capacity, ref RunState state,
        Span<RegexCapture> groups, out RegexMatchRange match)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startAt);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(startAt, input.Length);

        if ((flags & ~(PosixExecutionFlags)3) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        var required = GetRequiredCapacity(operation, capacity);
        match = default;
        if (operation == SearchOperation.Captures && groups.Length < program.GroupCount)
        { state.Failure = new(RegexExecutionFailureCode.DestinationTooSmall, startAt, 0, program.GroupCount); return RegexSearchStatus.DestinationTooSmall; }
        var buffers = state.Buffers;
        if (buffers.States.Length < required.StateSlots || buffers.Captures.Length < required.CaptureSlots || buffers.Scratch.Length < required.ScratchSlots)
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, state.Budget.WorkConsumed,
                required.RequiredBytes);
            if (operation == SearchOperation.Captures)
            {
                groups[..program.GroupCount].Clear();
            }

            return RegexSearchStatus.LimitExceeded;
        }
        var machine = new Machine(this, input, flags, capacity, state);
        var status = machine.Search(startAt, operation, groups, out match);
        state = machine.State;
        if (status != RegexSearchStatus.Match && operation == SearchOperation.Captures)
        {
            groups[..program.GroupCount].Clear();
        }

        return status;
    }

    private ref struct Machine
    {
        private readonly PosixExecutor _executor;
        private readonly ReadOnlySpan<byte> _input;
        private readonly PosixExecutionFlags _flags;
        private readonly PosixExecutionCapacity _capacity;
        private readonly Span<int> _frames, _choices, _events, _registers;
        internal RunState State;
        private int _frameCount, _choiceCount, _eventCount, _history, _position, _next, _winner, _winningEnd;
        private readonly bool _newline, _ignoreCase;
        internal Machine(PosixExecutor executor, ReadOnlySpan<byte> input, PosixExecutionFlags flags,
            PosixExecutionCapacity capacity, RunState state)
        {
            _executor = executor;
            _input = input;
            _flags = flags;
            _capacity = capacity;
            State = state;
            _frames = state.Buffers.States[..checked(7 * capacity.Frames)];
            _choices = state.Buffers.Scratch[..checked(3 * capacity.Frames)];
            _events = state.Buffers.Scratch.Slice(checked(3 * capacity.Frames), checked(6 * capacity.HistoryEvents));
            _registers = state.Buffers.Scratch.Slice(checked(3 * capacity.Frames + 6 * capacity.HistoryEvents), 2 * executor.Program.GroupCount);
            _newline = (executor.Program.Profile.Flags & PosixCompileFlags.Newline) != 0;
            _ignoreCase = (executor.Program.Profile.Flags & PosixCompileFlags.IgnoreCase) != 0;
            _frameCount = _choiceCount = _eventCount = 0;
            _history = _next = _winner = -1;
            _position = _winningEnd = 0;
        }
        private readonly ReadOnlySpan<PosixNode> Nodes => _executor.Program.Nodes;
        private bool Charge(long units = 1) => State.TryCharge(units, _position);
        private bool Exhaust(long required)
        {
            State.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, _position, State.Budget.WorkConsumed, required);
            return false;
        }
        // Each arena record is immutable until the candidate finishes. Logical
        // ceilings bound allocated continuations + pending choices, independently
        // of physical spare storage. Earlier winner histories remain in the arena.
        private int Frame(int node, int kind, int next, int count = 0, int start = 0, int context = -1, int open = -1)
        {
            if (!Charge())
            {
                return -1;
            }

            if (_frameCount + _choiceCount >= _capacity.Frames)
            { Exhaust(_frameCount + _choiceCount + 1L); return -1; }
            int id = _frameCount++, i = 7 * id;
            _frames[i] = node;
            _frames[i + 1] = kind;
            _frames[i + 2] = next;
            _frames[i + 3] = count;
            _frames[i + 4] = start;
            _frames[i + 5] = context;
            _frames[i + 6] = open;
            return id;
        }
        private bool Choice(int next)
        {
            if (!Charge())
            {
                return false;
            }

            if (_frameCount + _choiceCount >= _capacity.Frames)
            {
                return Exhaust(_frameCount + _choiceCount + 1L);
            }

            var i = 3 * _choiceCount++;
            _choices[i] = next;
            _choices[i + 1] = _position;
            _choices[i + 2] = _history;
            return true;
        }
        private bool Restore()
        {
            if (_choiceCount == 0)
            {
                return false;
            }

            if (!Charge())
            {
                return false;
            }

            var i = 3 * --_choiceCount;
            _next = _choices[i];
            _position = _choices[i + 1];
            _history = _choices[i + 2];
            return true;
        }
        private int Event(int node, int start, int end, int parent, int open)
        {
            if (!Charge())
            {
                return -1;
            }

            if (_eventCount == _capacity.HistoryEvents)
            { Exhaust(_eventCount + 1L); return -1; }
            int id = _eventCount++, i = 6 * id;
            _events[i] = node;
            _events[i + 1] = start;
            _events[i + 2] = end;
            _events[i + 3] = parent;
            _events[i + 4] = _history;
            _events[i + 5] = open;
            _history = id;
            return id;
        }
        private readonly int E(int id, int field) => _events[6 * id + field];
        private static byte Fold(byte b) => b is >= 65 and <= 90 ? (byte)(b + 32) : b;
        internal RegexSearchStatus Search(int startAt, SearchOperation operation, Span<RegexCapture> groups, out RegexMatchRange match)
        {
            match = default;
            _position = startAt;
            if (!Charge(0))
            {
                return FailureStatus();
            }

            if (State.PrevalidatedCleanSubject)
            {
                if (!State.TryChargeRun(0, _input.Length))
                {
                    return FailureStatus();
                }

                State.ValidatedCleanSubject = true;
            }
            else
            {
                var nul = _input.IndexOf((byte)0);
                var clean = nul >= 0 ? nul : _input.Length;
                if (clean > 0 && !State.TryChargeRun(0, clean))
                {
                    return FailureStatus();
                }

                if (nul >= 0)
                {
                    _position = nul;
                    if (!Charge())
                    {
                        return FailureStatus();
                    }

                    State.Failure = new(RegexExecutionFailureCode.InvalidInputDomain, nul, State.Budget.WorkConsumed);
                    return RegexSearchStatus.InvalidInput;
                }
                State.ValidatedCleanSubject = true;
            }

            if (_executor._accelerator is { } acc &&
                _flags == PosixExecutionFlags.None &&
                _capacity == PosixExecutionCapacity.Default &&
                State.Budget.IsUnconstrained((_input.Length - startAt) * 4L + 256))
            {
                if (acc.TryExecuteFastPath(_input, startAt, operation, ref State, groups, out match, out var fastStatus))
                {
                    return fastStatus;
                }

                if (acc.RequiredEndByte >= 0 && (_input.Length <= startAt || _input[^1] != (byte)acc.RequiredEndByte))
                {
                    State.TryCharge(1, startAt);
                    return RegexSearchStatus.NoMatch;
                }
                if (acc.RequiredBytes is { } req && _input.Slice(startAt).IndexOf(req) < 0)
                {
                    State.TryCharge(_input.Length - startAt + 1, startAt);
                    return RegexSearchStatus.NoMatch;
                }
            }

            for (var start = startAt; ; start++)
            {
                _position = start;
                if (!Charge())
                {
                    return FailureStatus();
                }

                _frameCount = _choiceCount = _eventCount = 0;
                _history = _winner = -1;
                _winningEnd = -1;
                _next = Frame(_executor.Program.Root, 0, -1);
                while (State.Failure.Code == RegexExecutionFailureCode.None)
                {
                    bool viable;
                    if (_next < 0)
                    {
                        if (operation == SearchOperation.Boolean)
                        { match = new(start, _position - start); return RegexSearchStatus.Match; }
                        if (_position > _winningEnd || _position == _winningEnd && Better(_history, _winner))
                        { _winner = _history; _winningEnd = _position; }
                        viable = false;
                    }
                    else
                    {
                        viable = Step();
                    }

                    if (!viable && !Restore())
                    {
                        break;
                    }
                }
                if (State.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return FailureStatus();
                }

                if (_winningEnd >= 0)
                {
                    if (operation == SearchOperation.Captures && !Publish(start, groups))
                    {
                        return FailureStatus();
                    }

                    match = new(start, _winningEnd - start);
                    return RegexSearchStatus.Match;
                }
                if (start == _input.Length)
                {
                    return RegexSearchStatus.NoMatch;
                }
            }
        }
        private bool Step()
        {
            if (!Charge())
            {
                return false;
            }

            int i = 7 * _next, node = _frames[i], kind = _frames[i + 1], next = _frames[i + 2],
                count = _frames[i + 3], start = _frames[i + 4], context = _frames[i + 5], open = _frames[i + 6];
            var n = Nodes[node];
            _next = next;
            if (kind == 1)
            {
                return Event(node, start, _position, context, open) >= 0;
            }

            if (kind == 2)
            {
                // Do not fabricate an optional empty tail after a preceding
                // iteration. Required empty iterations and sole nullable
                // participation are finite and retained.
                if (_position == start)
                {
                    if (count > 0 && count >= n.Min)
                    {
                        return false;
                    }

                    if (count + 1 >= n.Min)
                    {
                        return true;
                    }
                }
                return Repeat(node, count + 1, next, context);
            }
            var opening = Event(node, _position, -1, context, -1);
            if (opening < 0)
            {
                return false;
            }

            var closing = Frame(node, 1, next, start: _position, context: context, open: opening);
            if (closing < 0)
            {
                return false;
            }

            _next = closing;
            switch (n.Kind)
            {
                case PosixNodeKind.Empty:
                    return true;
                case PosixNodeKind.Literal:
                    if (_position == _input.Length || !Charge())
                    {
                        return false;
                    }

                    if (_ignoreCase ? Fold(_input[_position]) != Fold((byte)n.Value) : _input[_position] != n.Value)
                    {
                        return false;
                    }

                    _position++;
                    return true;
                case PosixNodeKind.Class:
                    if (_position == _input.Length || !Charge() || !_executor.Program.Sets[n.Value].Contains(_input[_position]))
                    {
                        return false;
                    }

                    _position++;
                    return true;
                case PosixNodeKind.Any:
                    if (_position == _input.Length || !Charge() || _newline && _input[_position] == 10)
                    {
                        return false;
                    }

                    _position++;
                    return true;
                case PosixNodeKind.Start:
                    if (_position == 0 && (_flags & PosixExecutionFlags.NotBol) == 0)
                    {
                        return true;
                    }

                    return _newline && _position > 0 && Charge() && _input[_position - 1] == 10;
                case PosixNodeKind.End:
                    if (_position == _input.Length && (_flags & PosixExecutionFlags.NotEol) == 0)
                    {
                        return true;
                    }

                    return _newline && _position < _input.Length && Charge() && _input[_position] == 10;
                case PosixNodeKind.Sequence:
                    var right = Frame(n.Y, 0, closing, context: context);
                    if (right < 0)
                    {
                        return false;
                    }

                    _next = Frame(n.X, 0, right, context: context);
                    return _next >= 0;
                case PosixNodeKind.Alternative:
                    var alternative = Frame(n.Y, 0, closing, context: context);
                    if (alternative < 0 || !Choice(alternative))
                    {
                        return false;
                    }

                    _next = Frame(n.X, 0, closing, context: context);
                    return _next >= 0;
                case PosixNodeKind.Capture:
                    _next = Frame(n.X, 0, closing, context: opening);
                    return _next >= 0;
                case PosixNodeKind.Repeat:
                    return Repeat(node, 0, closing, context);
                case PosixNodeKind.Backreference:
                    return Reference(n.Value, context);
                default:
                    throw new InvalidOperationException();
            }
        }
        private bool Repeat(int node, int count, int next, int context)
        {
            if (!Charge())
            {
                return false;
            }

            var n = Nodes[node];
            _next = next;
            if (count == n.Max)
            {
                return true;
            }

            if (count >= n.Min && !Choice(next))
            {
                return false;
            }

            var again = Frame(node, 2, next, count, _position, context);
            if (again < 0)
            {
                return false;
            }

            _next = Frame(n.X, 0, again, context: context);
            return _next >= 0;
        }
        private bool Reference(int group, int context)
        {
            var parent = _executor.Program.GroupParents[group];
            // Resolve ancestors from outer to inner. An actively open enclosing
            // occurrence supersedes its previous completed repetition; otherwise
            // select the most recent completion within the selected parent.
            _registers[0] = -1;
            for (var g = 1; g <= parent; g++)
            {
                var selected = -1;
                for (var c = context; c >= 0; c = E(c, 3))
                {
                    if (!Charge())
                    {
                        return false;
                    }

                    if (Nodes[E(c, 0)].Value == g)
                    { selected = c; break; }
                }
                var enclosing = _executor.Program.GroupParents[g];
                var scope = _registers[2 * enclosing];
                if (selected < 0 && (enclosing == 0 || scope >= 0))
                {
                    for (var h = _history; h >= 0; h = E(h, 4))
                    {
                        if (!Charge())
                        {
                            return false;
                        }

                        if (E(h, 2) >= 0 && Nodes[E(h, 0)].Kind == PosixNodeKind.Capture && Nodes[E(h, 0)].Value == g && E(h, 3) == scope)
                        { selected = E(h, 5); break; }
                    }
                }

                _registers[2 * g] = selected;
            }
            var desired = _registers[2 * parent];
            if (parent != 0 && desired < 0)
            {
                return false;
            }

            for (var h = _history; h >= 0; h = E(h, 4))
            {
                if (!Charge())
                {
                    return false;
                }

                if (E(h, 2) < 0 || Nodes[E(h, 0)].Kind != PosixNodeKind.Capture || Nodes[E(h, 0)].Value != group || E(h, 3) != desired)
                {
                    continue;
                }

                var length = E(h, 2) - E(h, 1);
                if (length > _input.Length - _position)
                {
                    return false;
                }

                for (var k = 0; k < length; k++)
                {
                    if (!Charge(2))
                    {
                        return false;
                    }

                    byte a = _input[E(h, 1) + k], b = _input[_position + k];
                    if (_ignoreCase ? Fold(a) != Fold(b) : a != b)
                    {
                        return false;
                    }
                }
                _position += length;
                return true;
            }
            return false;
        }
        // Return the earliest remaining completed occurrence of one syntactic
        // node on this path. Absent is -1; event IDs are monotonically increasing.
        private int Occurrence(int tail, int node, int after)
        {
            var result = -1;
            for (var h = tail; h >= 0; h = E(h, 4))
            {
                if (!Charge())
                {
                    return -1;
                }

                if (h > after && E(h, 0) == node && E(h, 2) >= 0)
                {
                    result = h;
                }
            }
            return result;
        }
        private bool Better(int candidate, int previous)
        {
            if (previous < 0)
            {
                return true;
            }

            foreach (var node in _executor._priority)
            {
                // Binary concatenation/alternation are IR association scaffolding,
                // not additional source subexpressions with a prefix priority.
                if (Nodes[node].Kind is PosixNodeKind.Sequence or PosixNodeKind.Alternative)
                {
                    continue;
                }

                int a = -1, b = -1;
                while (true)
                {
                    a = Occurrence(candidate, node, a);
                    b = Occurrence(previous, node, b);
                    if (State.Failure.Code != RegexExecutionFailureCode.None)
                    {
                        return false;
                    }

                    if (a < 0 || b < 0)
                    { if (a != b) { return a >= 0; } break; }
                    int lengthA = E(a, 2) - E(a, 1), lengthB = E(b, 2) - E(b, 1);
                    if (lengthA != lengthB)
                    {
                        return lengthA > lengthB;
                    }
                }
            }
            return false;
        }
        private bool Publish(int start, Span<RegexCapture> groups)
        {
            var count = _executor.Program.GroupCount;
            for (var g = 0; g < 2 * count; g++)
            { if (!Charge()) { return false; } _registers[g] = -1; }
            // Each group is selected inside its already selected containing
            // occurrence. Earlier escaped descendant captures are discarded.
            for (var g = 1; g < count; g++)
            {
                int parent = _executor.Program.GroupParents[g], desired = parent == 0 ? -1 : _registers[2 * parent];
                if (parent != 0 && desired < 0)
                {
                    continue;
                }

                for (var h = _winner; h >= 0; h = E(h, 4))
                {
                    if (!Charge())
                    {
                        return false;
                    }

                    if (E(h, 2) >= 0 && Nodes[E(h, 0)].Kind == PosixNodeKind.Capture && Nodes[E(h, 0)].Value == g && E(h, 3) == desired)
                    { _registers[2 * g] = E(h, 5); _registers[2 * g + 1] = h; break; }
                }
            }
            // Charge all publication before touching caller output.
            if (!Charge(count))
            {
                return false;
            }

            groups[0] = new(true, start, _winningEnd - start);
            for (var g = 1; g < count; g++)
            {
                var h = _registers[2 * g + 1];
                groups[g] = h < 0 ? default : new(true, E(h, 1), E(h, 2) - E(h, 1));
            }
            return true;
        }
        private readonly RegexSearchStatus FailureStatus() => State.Failure.Code switch
        {
            RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
            RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
            _ => RegexSearchStatus.LimitExceeded
        };
    }
}
