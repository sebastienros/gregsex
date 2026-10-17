#nullable enable
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;

namespace GregsEx.Flavors.Pcre2.Runtime.Composition;

// Internal workspace for composed runtime operations.
internal sealed class Pcre2PreparedWorkspace : IDisposable
{
    internal const long MetadataBytes = 192;
    internal readonly Pcre2Executor Executor;
    internal readonly Pcre2MatchLimits Ceiling;
    private int[] _states = [], _captures = [], _scratch = [];
    private Pcre2OffsetPair[] _callbackPairs = [];
    private int _lifecycle; // idle0, operation/session1, disposed2
    private int _sessionStep, _insideNext;
    private int _validLength = -1, _validNextStart = -1;
    private ulong _validHead, _validMid, _validTail;
    private long _reservedBytes;
    internal Pcre2ExecutionCapacity Capacity { get; private set; }
    internal long ReservedBytes => _reservedBytes;
    internal long LastWork { get; private set; }
    internal RegexExecutionFailure LastFailure { get; private set; }
    internal bool IsIdle => Volatile.Read(ref _lifecycle) == 0;
    internal Span<Pcre2OffsetPair> CallbackPairs => _callbackPairs;
    private static readonly Pcre2Host Host = new();
    internal static Pcre2HostLimits DefaultHostLimits { get; } = new();

    internal Pcre2PreparedWorkspace(Pcre2Executor executor, Pcre2ExecutionCapacity capacity,
        Pcre2MatchLimits? ceiling = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        Executor = executor;
        Ceiling = ceiling ?? Pcre2Executor.DefaultLimits;
        Allocate(capacity, initial: true);
    }

    internal void Reserve(Pcre2ExecutionCapacity capacity)
    {
        Enter();
        try
        { Allocate(capacity, initial: false); }
        finally { Exit(); }
    }

    private void Allocate(Pcre2ExecutionCapacity requested, bool initial)
    {
        _ = new Pcre2ExecutionCapacity(requested.Frames, requested.CaptureEvents, requested.Windows, requested.UnicodeScratch);
        var target = initial ? requested : new Pcre2ExecutionCapacity(
            Math.Max(Capacity.Frames, requested.Frames), Math.Max(Capacity.CaptureEvents, requested.CaptureEvents),
            Math.Max(Capacity.Windows, requested.Windows), Math.Max(Capacity.UnicodeScratch, requested.UnicodeScratch));
        var required = target.RequiredStorage(Executor.Program.GroupCount);
        var final = checked(MetadataBytes + required.RequiredBytes);
        var replacement = (_states.Length < required.Integers.StateSlots ? 32L + 4L * required.Integers.StateSlots : 0) +
            (_captures.Length < required.Integers.CaptureSlots ? 32L + 4L * required.Integers.CaptureSlots : 0) +
            (_scratch.Length < required.Integers.ScratchSlots ? 32L + 4L * required.Integers.ScratchSlots : 0);
        var peak = initial ? final : checked(ReservedBytes + replacement);
        if (final > Ceiling.MaxWorkspaceBytes || peak > Ceiling.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0, peak));
        }
        // Publish only after every replacement succeeds. Failed peak admission
        // and system allocation exceptions preserve all previous partitions.
        var states = _states.Length < required.Integers.StateSlots ? new int[required.Integers.StateSlots] : _states;
        var captures = _captures.Length < required.Integers.CaptureSlots ? new int[required.Integers.CaptureSlots] : _captures;
        var scratch = _scratch.Length < required.Integers.ScratchSlots ? new int[required.Integers.ScratchSlots] : _scratch;
        var callbackPairs = initial ? new Pcre2OffsetPair[required.CallbackPairSlots] : _callbackPairs;
        _states = states;
        _captures = captures;
        _scratch = scratch;
        _callbackPairs = callbackPairs;
        Capacity = target;
        _reservedBytes = final;
    }

    internal Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null,
        Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        Enter();
        try
        {
            Validate(input.Length, startAt, Pcre2CodeUnitWidth.Sixteen, nativeMatch: true);
            options = Resolve(options);
            var state = CreateState(options);
            try
            {
                if (!Admit(input.Length, startAt, options, ref state, out var denial))
                {
                    return denial;
                }

                var bytes = MemoryMarshal.AsBytes(input);
                CheckCleanSubjectCache(bytes, input.Length, startAt, ref state);
                var result = Executor.Match(input, startAt, options, callbacks, Capacity, policy, ref state, _callbackPairs, pairs);
                UpdateCleanSubjectCache(bytes, input.Length, startAt, result, pairs, in state);
                return result;
            }
            finally { Record(ref state); }
        }
        finally { Exit(); }
    }

    internal Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Span<Pcre2OffsetPair> pairs,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null,
        Pcre2ValidationPolicy policy = Pcre2ValidationPolicy.NativeWindow)
    {
        Enter();
        try
        {
            Validate(input.Length, startAt, Pcre2CodeUnitWidth.Eight, nativeMatch: true);
            options = Resolve(options);
            var state = CreateState(options);
            try
            {
                if (!Admit(input.Length, startAt, options, ref state, out var denial))
                {
                    return denial;
                }

                CheckCleanSubjectCache(input, input.Length, startAt, ref state);
                var result = Executor.Match(input, startAt, options, callbacks, Capacity, policy, ref state, _callbackPairs, pairs);
                UpdateCleanSubjectCache(input, input.Length, startAt, result, pairs, in state);
                return result;
            }
            finally { Record(ref state); }
        }
        finally { Exit(); }
    }

    internal void CheckCleanSubjectCache(ReadOnlySpan<byte> bytes, int length, int startAt, ref RunState state)
    {
        if (startAt > 0 && bytes.Length >= 64 && length == _validLength &&
            (startAt == _validNextStart || startAt == _validNextStart + 1) && MatchesFingerprint(bytes))
        {
            state.PrevalidatedCleanSubject = true;
        }
    }

    private bool MatchesFingerprint(ReadOnlySpan<byte> bytes)
    {
        ref var r = ref MemoryMarshal.GetReference(bytes);
        return Unsafe.ReadUnaligned<ulong>(ref r) == _validHead &&
               Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, (bytes.Length >> 1) - 4)) == _validMid &&
               Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, bytes.Length - 8)) == _validTail;
    }

    internal void UpdateCleanSubjectCache(ReadOnlySpan<byte> bytes, int length, int startAt,
        Pcre2MatchResult result, ReadOnlySpan<Pcre2OffsetPair> pairs, in RunState state)
    {
        if (result.Status == Pcre2MatchStatus.Match && state.ValidatedCleanSubject && bytes.Length >= 64 && !pairs.IsEmpty)
        {
            if (startAt == 0)
            {
                ref var r = ref MemoryMarshal.GetReference(bytes);
                _validLength = length;
                _validHead = Unsafe.ReadUnaligned<ulong>(ref r);
                _validMid = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, (bytes.Length >> 1) - 4));
                _validTail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, bytes.Length - 8));
            }
            if (_validLength == length)
            {
                _validNextStart = pairs[0].End;
            }
        }
        else if (startAt == 0)
        {
            _validLength = -1;
            _validNextStart = -1;
        }
    }

    internal Pcre2MatchResult Find(ReadOnlySpan<char> input, int startAt, out Pcre2OffsetPair pair,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null)
    {
        Span<Pcre2OffsetPair> one = stackalloc Pcre2OffsetPair[1];
        one.Clear();
        var result = Match(input, startAt, one, options, callbacks);
        pair = one[0];
        return result;
    }
    internal Pcre2MatchResult Find(ReadOnlySpan<byte> input, int startAt, out Pcre2OffsetPair pair,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null)
    {
        Span<Pcre2OffsetPair> one = stackalloc Pcre2OffsetPair[1];
        one.Clear();
        var result = Match(input, startAt, one, options, callbacks);
        pair = one[0];
        return result;
    }

    internal Pcre2SubstituteResult Substitute(ReadOnlySpan<char> input, int startAt,
        ReadOnlySpan<char> replacement, Span<char> destination, Pcre2SubstituteFlags flags = default,
        Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null,
        Pcre2HostLimits? limits = null, ReadOnlySpan<Pcre2OffsetPair> precomputed = default,
        Pcre2MatchResult precomputedResult = default, int? pairCapacity = null)
    {
        Enter();
        try
        {
            Validate(input.Length, startAt, Pcre2CodeUnitWidth.Sixteen, nativeMatch: true);
            options = Resolve(options);
            var state = CreateState(options);
            try
            {
                if (!Admit(input.Length, startAt, options, ref state, out var denial))
                {
                    return new(denial.NativeReturnCode, -1, 0, denial.Failure);
                }

                return Host.Substitute(Executor, input, startAt, replacement, destination, flags, options, callbacks,
                    limits ?? DefaultHostLimits, precomputed, precomputedResult, Capacity, ref state, _callbackPairs, pairCapacity);
            }
            finally { Record(ref state); }
        }
        finally { Exit(); }
    }

    internal Pcre2SubstituteResult Substitute(ReadOnlySpan<byte> input, int startAt,
        ReadOnlySpan<byte> replacement, Span<byte> destination, Pcre2SubstituteFlags flags = default,
        Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null,
        Pcre2HostLimits? limits = null, ReadOnlySpan<Pcre2OffsetPair> precomputed = default,
        Pcre2MatchResult precomputedResult = default, int? pairCapacity = null)
    {
        Enter();
        try
        {
            Validate(input.Length, startAt, Pcre2CodeUnitWidth.Eight, nativeMatch: true);
            options = Resolve(options);
            var state = CreateState(options);
            try
            {
                if (!Admit(input.Length, startAt, options, ref state, out var denial))
                {
                    return new(denial.NativeReturnCode, -1, 0, denial.Failure);
                }

                return Host.Substitute(Executor, input, startAt, replacement, destination, flags, options, callbacks,
                    limits ?? DefaultHostLimits, precomputed, precomputedResult, Capacity, ref state, _callbackPairs, pairCapacity);
            }
            finally { Record(ref state); }
        }
        finally { Exit(); }
    }

    internal Pcre2PreparedIteration BeginIteration(int startAt = 0, Pcre2MatchOptions options = default,
        Pcre2CharCallbacks? chars = null, Pcre2ByteCallbacks? bytes = null, Pcre2HostLimits? limits = null)
    {
        var sequence = BeginSession(startAt, ref options, chars, bytes);
        return new(this, sequence, startAt, options, chars, bytes, limits ?? DefaultHostLimits);
    }
    internal int BeginSession(int startAt, ref Pcre2MatchOptions options, Pcre2CharCallbacks? chars, Pcre2ByteCallbacks? bytes)
    {
        Enter();
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegative(startAt);
            options = Resolve(options);
            if (chars is not null && Executor.Program.Profile.Width != Pcre2CodeUnitWidth.Sixteen ||
                bytes is not null && Executor.Program.Profile.Width != Pcre2CodeUnitWidth.Eight)
            {
                throw new ArgumentException("Callback native width mismatch.");
            }

            return _sessionStep = checked(_sessionStep + 1);
        }
        catch { Exit(); throw; }
    }

    internal Pcre2MatchOptions Resolve(Pcre2MatchOptions options) => options with { Limits = options.Resolve(Ceiling) };
    internal RunState CreateState(Pcre2MatchOptions options) => Pcre2KernelOperation.CreateRunState(
        new(_states, _captures, _scratch), Pcre2OperationLimits.Resolve(options.Limits!, Executor.Program.PatternLimits), options);
    internal void Validate(int length, int startAt, Pcre2CodeUnitWidth width, bool nativeMatch = false)
    {
        if (Executor.Program.Profile.Width != width)
        {
            throw new ArgumentException("Native subject width mismatch.");
        }

        if (startAt < 0 || !nativeMatch && startAt > length)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt));
        }
    }
    internal bool Admit(int length, int startAt, Pcre2MatchOptions options, ref RunState state, out Pcre2MatchResult denial)
    {
        var machine = new Pcre2VmState(Capacity, options.Limits!, length, Executor.Program.PatternLimits, Math.Min(startAt, length));
        if (!Pcre2KernelOperation.PollEntry(Executor.Program, ref machine, ref state))
        { denial = Pcre2Kernel.Failure(ref machine, ref state); return false; }
        // Include actual retained typed staging, array headers and owner metadata,
        // even for a smaller per-call/pattern heap ceiling or an empty result.
        if (ReservedBytes > machine.Limits.MaxWorkspaceBytes)
        {
            machine.SourceReturnCode = (int)Pcre2ErrorCode.Heaplimit;
            denial = Pcre2Kernel.Failure(ref machine, ref state);
            return false;
        }
        if (startAt > length)
        { denial = Pcre2KernelOperation.BadOffset(); return false; }
        denial = default;
        return true;
    }
    internal void Record(ref RunState state)
    { LastWork = state.Budget.WorkConsumed; LastFailure = state.Failure; }
    internal void Enter()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(Pcre2PreparedWorkspace));
        }

        if (previous != 0)
        {
            throw new InvalidOperationException("Workspace is already active.");
        }

        LastWork = 0;
        LastFailure = default;
    }
    internal void Exit() => Volatile.Write(ref _lifecycle, 0);

    // Sequence rejects stale value copies: two iteration values cannot fork the
    // same retained budget/cursor. Next excludes callbacks reentering a session.
    internal void EnterNext(int sequence)
    {
        if (Volatile.Read(ref _lifecycle) != 1 || sequence != _sessionStep)
        {
            throw new InvalidOperationException("Iteration is no longer the current workspace session.");
        }

        if (Interlocked.CompareExchange(ref _insideNext, 1, 0) != 0)
        {
            throw new InvalidOperationException("Iteration is already executing.");
        }

        if (sequence != _sessionStep)
        { Volatile.Write(ref _insideNext, 0); throw new InvalidOperationException("Iteration value is stale."); }
    }
    internal int ExitNext() { _sessionStep = checked(_sessionStep + 1); Volatile.Write(ref _insideNext, 0); return _sessionStep; }
    internal void EndIteration(int sequence, ref RunState state)
    {
        if (Volatile.Read(ref _insideNext) != 0)
        {
            throw new InvalidOperationException("Iteration is executing.");
        }

        if (Volatile.Read(ref _lifecycle) != 1 || sequence != _sessionStep)
        {
            throw new InvalidOperationException("Iteration is no longer the current workspace session.");
        }

        Record(ref state);
        _sessionStep = checked(_sessionStep + 1);
        Exit();
    }
    public void Dispose()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (previous == 1)
        {
            throw new InvalidOperationException("Cannot dispose an active workspace.");
        }

        if (previous == 2)
        {
            return;
        }

        _states = [];
        _captures = [];
        _scratch = [];
        _callbackPairs = [];
    }
}
