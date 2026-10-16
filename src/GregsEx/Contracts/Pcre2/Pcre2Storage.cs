namespace GregsEx.Contracts.Pcre2;

// Sole serialized storage accessor shared by independently owned modules.
// Matching logic stays in engines; this service only admits/serializes owned
// frames, windows and capture undo. It never retains borrowed caller input.
internal readonly ref struct Pcre2Storage
{
    private readonly Span<int> _frames, _windows, _captures, _undo;
    private readonly Span<Pcre2OffsetPair> _callbackPairs;
    private readonly long _requiredBytes;
    internal Span<int> UnicodeScratch { get; }
    // Kernel/advanced modules borrow integer partitions only. Composition still
    // reserves the full typed requirement and supplies staging to controls.
    internal Pcre2Storage(WorkspaceBuffers buffers, Pcre2ExecutionCapacity capacity, int groupCount)
        : this(buffers, capacity, groupCount, default, false) { }
    internal Pcre2Storage(WorkspaceBuffers buffers, Pcre2ExecutionCapacity capacity, int groupCount,
        Span<Pcre2OffsetPair> callbackPairs)
        : this(buffers, capacity, groupCount, callbackPairs, true) { }
    private Pcre2Storage(WorkspaceBuffers buffers, Pcre2ExecutionCapacity capacity, int groupCount,
        Span<Pcre2OffsetPair> callbackPairs, bool hasCallbacks)
    {
        var required = capacity.Required(groupCount);
        var typedRequired = capacity.RequiredStorage(groupCount);
        _requiredBytes = typedRequired.RequiredBytes;
        if (hasCallbacks && callbackPairs.Length < groupCount)
        {
            throw new ArgumentException("Insufficient prepared callback pairs.", nameof(callbackPairs));
        }

        _callbackPairs = hasCallbacks ? callbackPairs[..groupCount] : default;
        if (!buffers.Capacity.Contains(required))
        {
            throw new ArgumentException("Insufficient prepared PCRE storage.", nameof(buffers));
        }

        _frames = buffers.States.Slice(64, capacity.Frames * Pcre2Frame.Slots);
        _windows = buffers.States.Slice(64 + _frames.Length, capacity.Windows * Pcre2Window.Slots);
        _captures = buffers.Captures[..(groupCount * 2)];
        _undo = buffers.Scratch[..(capacity.CaptureEvents * Pcre2CaptureUndo.Slots)];
        UnicodeScratch = buffers.Scratch.Slice(_undo.Length, capacity.UnicodeScratch);
    }
    internal bool PrepareCallbackPairs(bool matchInProgress, ref Pcre2VmState machine, ref RunState state)
    {
        RequireCallbacks();
        if (!Charge(ref machine, ref state, _callbackPairs.Length, machine.Position))
        {
            return false;
        }

        for (var group = 0; group < _callbackPairs.Length; group++)
        {
            _callbackPairs[group] = group == 0 && matchInProgress ? default : Capture(group);
        }

        return true;
    }
    internal ReadOnlySpan<Pcre2OffsetPair> CallbackPairs
    {
        get { RequireCallbacks(); return _callbackPairs; }
    }
    private void RequireCallbacks()
    {
        if (_callbackPairs.IsEmpty)
        {
            throw new InvalidOperationException("This storage view has no prepared callback staging.");
        }
    }
    internal static bool Charge(ref Pcre2VmState machine, ref RunState state, long units, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(units);
        if (machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return false;
        }
        // Poll first even for resource-zero/empty paths, preserving cancel/deadline precedence.
        if (!state.TryCharge(0, offset))
        {
            return false;
        }

        if (units > machine.Limits.MaxWork - state.Budget.WorkConsumed)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Matchlimit; return false; }
        return state.TryCharge(units, offset);
    }
    internal bool InitializeCaptures(ref Pcre2VmState machine, ref RunState state)
    {
        if (!Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        if (_requiredBytes > machine.Limits.MaxWorkspaceBytes)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Heaplimit; return false; }
        if (!Charge(ref machine, ref state, _captures.Length, machine.Position))
        {
            return false;
        }

        _captures.Fill(-1);
        return true;
    }
    internal bool BeginCallout(ref Pcre2VmState machine, ref RunState state)
    {
        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if (machine.CalloutCount >= machine.Limits.MaxCallouts)
        { state.Failure = new(RegexExecutionFailureCode.WorkLimitExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        machine.CalloutCount++;
        return true;
    }
    internal Pcre2OffsetPair Capture(int group)
    {
        if ((uint)group >= (uint)(_captures.Length / 2))
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        int start = _captures[group * 2], end = _captures[group * 2 + 1];
        return start >= 0 && end >= 0 ? new(true, start, end) : default;
    }
    // Start-only open registers are intentionally possible. Callback/publication
    // views expose only completed participation; backreferences use raw registers.
    internal (int Start, int End) Registers(int group)
    {
        if ((uint)group >= (uint)(_captures.Length / 2))
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        return (_captures[group * 2], _captures[group * 2 + 1]);
    }
    internal bool WriteCapture(int group, int start, int end, ref Pcre2VmState machine, ref RunState state)
    {
        if ((uint)group >= (uint)(_captures.Length / 2) || start < -1 || end < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(group));
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if (machine.CaptureCursor >= machine.Capacity.CaptureEvents || machine.CaptureCursor >= machine.Limits.MaxCaptureEvents)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        var item = _undo.Slice(machine.CaptureCursor * Pcre2CaptureUndo.Slots, Pcre2CaptureUndo.Slots);
        item[0] = group;
        item[1] = _captures[group * 2];
        item[2] = _captures[group * 2 + 1];
        item[3] = item[1] >= 0 && item[2] >= 0 ? 1 : 0;
        machine.CaptureCursor++;
        _captures[group * 2] = start;
        _captures[group * 2 + 1] = end;
        return true;
    }
    internal bool RestoreCaptures(int checkpoint, ref Pcre2VmState machine, ref RunState state)
    {
        if (checkpoint < 0 || checkpoint > machine.CaptureCursor)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        while (machine.CaptureCursor > checkpoint)
        {
            if (!Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            var item = _undo.Slice((--machine.CaptureCursor) * Pcre2CaptureUndo.Slots, Pcre2CaptureUndo.Slots);
            _captures[item[0] * 2] = item[1];
            _captures[item[0] * 2 + 1] = item[2];
        }
        return true;
    }
    // Append inverse writes while walking only the original undo interval. The
    // old choices remain valid, including raw -1/open registers and duplicates.
    internal bool RestoreCapturesRetaining(int checkpoint, ref Pcre2VmState machine, ref RunState state)
    {
        if (checkpoint < 0 || checkpoint > machine.CaptureCursor)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        if (!Charge(ref machine, ref state, 0, machine.Position))
        {
            return false;
        }

        var original = machine.CaptureCursor;
        if ((long)original + original - checkpoint > Math.Min(machine.Capacity.CaptureEvents, machine.Limits.MaxCaptureEvents))
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        for (var cursor = original - 1; cursor >= checkpoint; cursor--)
        {
            if (!Charge(ref machine, ref state, 1, machine.Position))
            {
                return false;
            }

            var item = _undo.Slice(cursor * Pcre2CaptureUndo.Slots, Pcre2CaptureUndo.Slots);
            if (!WriteCapture(item[0], item[1], item[2], ref machine, ref state))
            {
                return false;
            }
        }
        return true;
    }
    private static void Serialize(Pcre2Frame frame, Span<int> slots)
    {
        slots[0] = (int)frame.Kind;
        slots[1] = frame.ResumeNode;
        slots[2] = frame.InputPosition;
        slots[3] = frame.CaptureCursor;
        slots[4] = frame.WindowDepth;
        slots[5] = frame.CallDepth;
        slots[6] = frame.ReportStart;
        slots[7] = frame.MarkIndex;
        slots[8] = frame.RepeatCount;
        slots[9] = frame.ScopeNode;
        slots[10] = frame.ChildCursor;
        slots[11] = frame.StartMatch;
        slots[12] = frame.LastCapture;
        slots[13] = frame.CaptureTop;
        slots[14] = (int)frame.PendingFlags;
        slots[15] = frame.Auxiliary;
        slots[16] = frame.RecurseLastUsedPosition;
        slots[17] = frame.HasSavedWindow ? 1 : 0;
        slots[18] = frame.SavedWindow.VisibleEnd;
        slots[19] = frame.SavedWindow.ScanRequiredStart;
        slots[20] = frame.SavedWindow.ReturnNode;
        slots[21] = frame.SavedWindow.SavedPosition;
        slots[22] = frame.SavedWindow.FrameCheckpoint;
        slots[23] = frame.SavedWindow.CaptureCheckpoint;
        slots[24] = frame.CallActive ? 1 : 0;
        slots[25] = frame.NoteolCleared ? 1 : 0;
    }
    private static Pcre2Frame Deserialize(ReadOnlySpan<int> s) => new(
        (Pcre2FrameKind)s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8], s[9], s[10], s[11], s[12], s[13],
        (Pcre2CalloutFlags)s[14], s[15], s[16], s[17] != 0, new(s[18], s[19], s[20], s[21], s[22], s[23]), s[24] != 0, s[25] != 0);
    // Only logical activity changes. Retained return/choice snapshots stay intact.
    internal bool SetCallActive(int index, bool active, ref Pcre2VmState machine, ref RunState state)
    {
        if ((uint)index >= (uint)machine.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        var slots = _frames.Slice(index * Pcre2Frame.Slots, Pcre2Frame.Slots);
        if ((Pcre2FrameKind)slots[0] != Pcre2FrameKind.Subroutine)
        {
            throw new ArgumentException("Not a subroutine frame.", nameof(index));
        }

        slots[24] = active ? 1 : 0;
        return true;
    }
    internal bool PushFrame(Pcre2Frame frame, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if ((long)machine.FrameCount + machine.WindowDepth >= machine.Limits.MaxDepth)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Depthlimit; return false; }
        if (machine.FrameCount >= machine.Capacity.Frames)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        var slots = _frames.Slice(machine.FrameCount++ * Pcre2Frame.Slots, Pcre2Frame.Slots);
        Serialize(frame, slots);
        if (frame.Kind == Pcre2FrameKind.Subroutine && frame.CallActive)
        {
            machine.CallDepth++;
        }

        return true;
    }
    internal bool PopFrame(ref Pcre2VmState machine, ref RunState state, out Pcre2Frame frame)
    {
        frame = default;
        if (machine.FrameCount == 0)
        {
            return false;
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        var s = _frames.Slice((--machine.FrameCount) * Pcre2Frame.Slots, Pcre2Frame.Slots);
        frame = Deserialize(s);
        if (frame.Kind == Pcre2FrameKind.Subroutine && frame.CallActive)
        {
            machine.CallDepth = Math.Max(0, machine.CallDepth - 1);
        }

        return true;
    }
    internal bool ReadFrame(int index, ref Pcre2VmState machine, ref RunState state, out Pcre2Frame frame)
    {
        frame = default;
        if ((uint)index >= (uint)machine.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        var s = _frames.Slice(index * Pcre2Frame.Slots, Pcre2Frame.Slots);
        frame = Deserialize(s);
        return true;
    }
    internal bool TruncateFrames(int checkpoint, ref Pcre2VmState machine, ref RunState state)
    {
        if (checkpoint < 0 || checkpoint > machine.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(checkpoint));
        }

        while (machine.FrameCount > checkpoint)
        {
            if (!PopFrame(ref machine, ref state, out _))
            {
                return false;
            }
        }

        return true;
    }
    internal bool PushWindow(Pcre2Window window, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        if ((long)machine.FrameCount + machine.WindowDepth >= machine.Limits.MaxDepth)
        { machine.SourceReturnCode = (int)Pcre2ErrorCode.Depthlimit; return false; }
        if (machine.WindowDepth >= machine.Capacity.Windows)
        { state.Failure = new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, machine.Position, state.Budget.WorkConsumed); return false; }
        var s = _windows.Slice(machine.WindowDepth++ * Pcre2Window.Slots, Pcre2Window.Slots);
        s[0] = window.VisibleEnd;
        s[1] = window.ScanRequiredStart;
        s[2] = window.ReturnNode;
        s[3] = window.SavedPosition;
        s[4] = window.FrameCheckpoint;
        s[5] = window.CaptureCheckpoint;
        return true;
    }
    internal bool PopWindow(ref Pcre2VmState machine, ref RunState state, out Pcre2Window window)
    {
        window = default;
        if (machine.WindowDepth == 0)
        {
            return false;
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        var s = _windows.Slice((--machine.WindowDepth) * Pcre2Window.Slots, Pcre2Window.Slots);
        window = new(s[0], s[1], s[2], s[3], s[4], s[5]);
        return true;
    }
    internal bool ReadWindow(int index, ref Pcre2VmState machine, ref RunState state, out Pcre2Window window)
    {
        window = default;
        if ((uint)index >= (uint)machine.WindowDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (!Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        var s = _windows.Slice(index * Pcre2Window.Slots, Pcre2Window.Slots);
        window = new(s[0], s[1], s[2], s[3], s[4], s[5]);
        return true;
    }
}
