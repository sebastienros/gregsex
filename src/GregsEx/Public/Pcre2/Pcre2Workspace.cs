using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Host;
namespace GregsEx;

/// <summary>Exclusive bounded scratch for native PCRE2 operations; never retains borrowed subjects.</summary>
public sealed class Pcre2Workspace : IDisposable
{
    internal const long MetadataBytes = 128;
    internal readonly Pcre2PreparedWorkspace Prepared;
    private readonly bool _allowGrowth;
    private bool _hasDefaultCapacity;
    private int _lifecycle, _copiedLength;
    private char[] _copiedChars = []; private byte[] _copiedBytes = [];
    private static readonly Pcre2Host Host = new();
    private Pcre2WorkspaceOptions _capacity;
    /// <summary>Prepared capacity; reserve explicitly or opt into preparation growth.</summary>
    public Pcre2WorkspaceOptions Capacity => _capacity;
    /// <summary>Conservative owned storage including copied-subject capacity and wrapper metadata.</summary>
    public long ReservedBytes => Prepared.ReservedBytes + MetadataBytes + CopyBytes;
    private long CopyBytes => (_copiedChars.Length == 0 ? 0 : 32L + 2L * _copiedChars.Length) + (_copiedBytes.Length == 0 ? 0 : 32L + _copiedBytes.Length);
    /// <summary>Cumulative charged work in the latest operation/session.</summary>
    public long LastWork { get; private set; }
    /// <summary>Latest managed safety failure; native source errors remain in the typed result.</summary>
    public RegexExecutionFailure LastFailure { get; private set; }
    /// <summary>Owned subject from a successful COPY_MATCHED_SUBJECT char operation, valid until next operation/disposal.</summary>
    public ReadOnlySpan<char> CopiedSubjectChars => _copiedChars.AsSpan(0, Prepared.Executor.Program.Profile.Width == Pcre2CodeUnitWidth.Sixteen ? _copiedLength : 0);
    /// <summary>Owned subject from a successful COPY_MATCHED_SUBJECT byte operation.</summary>
    public ReadOnlySpan<byte> CopiedSubjectBytes => _copiedBytes.AsSpan(0, Prepared.Executor.Program.Profile.Width == Pcre2CodeUnitWidth.Eight ? _copiedLength : 0);
    internal Pcre2Workspace(Pcre2Pattern pattern, Pcre2WorkspaceOptions options)
    {
        var capacity = Native(options);
        var required = checked(Pcre2PreparedWorkspace.MetadataBytes + MetadataBytes + capacity.RequiredStorage(pattern.GroupCount).RequiredBytes);
        if (required > pattern.Options.DefaultMatchLimits.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0, required));
        }

        Prepared = new(pattern.Executor, capacity, pattern.Options.DefaultMatchLimits);
        _capacity = options;
        _allowGrowth = options.AllowGrowth;
        _hasDefaultCapacity = Prepared.Capacity.Required(Prepared.Executor.Program.GroupCount).Contains(Pcre2ExecutionCapacity.Default.Required(Prepared.Executor.Program.GroupCount));
    }
    private static Pcre2ExecutionCapacity Native(Pcre2WorkspaceOptions options) => new(options.Frames, options.CaptureEvents, options.Windows, options.UnicodeScratch);
    /// <summary>Transactional explicit growth; rejected peak admission retains earlier buffers.</summary>
    public void Reserve(Pcre2WorkspaceOptions capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        Enter();
        try
        {
            var old = Prepared.Capacity;
            var target = new Pcre2ExecutionCapacity(Math.Max(old.Frames, capacity.Frames), Math.Max(old.CaptureEvents, capacity.CaptureEvents), Math.Max(old.Windows, capacity.Windows), Math.Max(old.UnicodeScratch, capacity.UnicodeScratch));
            ReserveCore(target);
            _capacity = new(target.Frames, target.CaptureEvents, target.Windows, target.UnicodeScratch, _allowGrowth);
            _hasDefaultCapacity = Prepared.Capacity.Required(Prepared.Executor.Program.GroupCount).Contains(Pcre2ExecutionCapacity.Default.Required(Prepared.Executor.Program.GroupCount));
        }
        finally { Exit(); }
    }
    /// <summary>Native16 matching; short vectors, partials, reversed group-zero and source errors remain distinct.</summary>
    public Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null) => MatchCore(input, startAt, pairs, options, callbacks);
    /// <summary>Native8 matching with original byte offsets.</summary>
    public Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null) => MatchCore(input, startAt, pairs, options, callbacks);
    private Pcre2MatchResult MatchCore<T>(ReadOnlySpan<T> input, int startAt, Span<Pcre2OffsetPair> pairs, Pcre2MatchOptions options, object? callbacks) where T : unmanaged
    {
        Enter();
        try
        {
            Prepared.Enter();
            try
            {
                Prepared.Validate(input.Length, startAt, typeof(T) == typeof(char) ? Pcre2CodeUnitWidth.Sixteen : Pcre2CodeUnitWidth.Eight, nativeMatch: true);
                options = Prepared.Resolve(options);
                var state = Prepared.CreateState(options);
                try
                {
                    if (!Admit(input.Length, startAt, options, ref state, out var denial))
                    {
                        return denial;
                    }

                    var rawBytes = MemoryMarshal.AsBytes(input);
                    Prepared.CheckCleanSubjectCache(rawBytes, input.Length, startAt, ref state);
                    var result = typeof(T) == typeof(char) ? Prepared.Executor.Match(MemoryMarshal.Cast<T, char>(input), startAt, options, (Pcre2CharCallbacks?)callbacks, Prepared.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, Prepared.CallbackPairs, pairs) : Prepared.Executor.Match(MemoryMarshal.Cast<T, byte>(input), startAt, options, (Pcre2ByteCallbacks?)callbacks, Prepared.Capacity, Pcre2ValidationPolicy.NativeWindow, ref state, Prepared.CallbackPairs, pairs);
                    Prepared.UpdateCleanSubjectCache(rawBytes, input.Length, startAt, result, pairs, in state);
                    if (result.Status == Pcre2MatchStatus.Match && (options.Flags & Pcre2MatchFlags.CopyMatchedSubject) != 0 && !Copy(input, options, ref state))
                    {
                        return CopyFailure(result, ref state);
                    }

                    return result;
                }
                finally { Record(ref state); }
            }
            finally { Prepared.Exit(); }
        }
        finally { Exit(); }
    }
    /// <summary>Native16 caller-buffer replacement, with optional exact supplied match-vector capacity.</summary>
    public Pcre2SubstituteResult Substitute(ReadOnlySpan<char> input, int startAt, ReadOnlySpan<char> replacement, Span<char> destination, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None, Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null, Pcre2HostLimits? limits = null, ReadOnlySpan<Pcre2OffsetPair> precomputedPairs = default, Pcre2MatchResult precomputedResult = default, int? pairCapacity = null) => SubstituteCore(input, startAt, replacement, destination, flags, options, callbacks, limits, precomputedPairs, precomputedResult, pairCapacity, out _);
    /// <summary>Native8 caller-buffer replacement; null capacity selects automatic full capacity.</summary>
    public Pcre2SubstituteResult Substitute(ReadOnlySpan<byte> input, int startAt, ReadOnlySpan<byte> replacement, Span<byte> destination, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None, Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null, Pcre2HostLimits? limits = null, ReadOnlySpan<Pcre2OffsetPair> precomputedPairs = default, Pcre2MatchResult precomputedResult = default, int? pairCapacity = null) => SubstituteCore(input, startAt, replacement, destination, flags, options, callbacks, limits, precomputedPairs, precomputedResult, pairCapacity, out _);
    private Pcre2SubstituteResult SubstituteCore<T>(ReadOnlySpan<T> input, int startAt, ReadOnlySpan<T> replacement, Span<T> destination, Pcre2SubstituteFlags flags, Pcre2MatchOptions options, object? callbacks, Pcre2HostLimits? limits, ReadOnlySpan<Pcre2OffsetPair> precomputed, Pcre2MatchResult result, int? pairCapacity, out object? ownedOutput, int? ownedCapacity = null) where T : unmanaged
    {
        ownedOutput = null;
        if (pairCapacity is int supplied)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(supplied, nameof(pairCapacity));
        }

        if (ownedCapacity is int requested)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(requested, nameof(ownedCapacity));
        }

        Enter();
        try
        {
            Prepared.Enter();
            try
            {
                Prepared.Validate(input.Length, startAt, typeof(T) == typeof(char) ? Pcre2CodeUnitWidth.Sixteen : Pcre2CodeUnitWidth.Eight, nativeMatch: true);
                options = Prepared.Resolve(options);
                var state = Prepared.CreateState(options);
                try
                {
                    if (!Admit(input.Length, startAt, options, ref state, out var denial))
                    {
                        return new(denial.NativeReturnCode, -1, 0, denial.Failure);
                    }

                    limits ??= Pcre2PreparedWorkspace.DefaultHostLimits;
                    var hostLimits = limits;
                    long bufferBytes = 0;
                    T[]? ownedBuffer = null;
                    if (ownedCapacity is int capacity)
                    {
                        bufferBytes = checked(32L + capacity * (typeof(T) == typeof(char) ? 2L : 1L));
                        if (capacity > limits.MaxOutputUnits || bufferBytes >= limits.MaxMemoryBytes)
                        { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, state.Budget.WorkConsumed, bufferBytes); return new((int)Pcre2ErrorCode.Heaplimit, -1, 0, state.Failure); }
                        hostLimits = new(limits.MaxResults, limits.MaxOutputUnits, limits.MaxMemoryBytes - bufferBytes);
                        ownedBuffer = new T[capacity];
                        destination = ownedBuffer;
                    }
                    var substituted = typeof(T) == typeof(char) ? Host.Substitute(Prepared.Executor, MemoryMarshal.Cast<T, char>(input), startAt, MemoryMarshal.Cast<T, char>(replacement), MemoryMarshal.Cast<T, char>(destination), flags, options, (Pcre2CharCallbacks?)callbacks, hostLimits, precomputed, result, Prepared.Capacity, ref state, Prepared.CallbackPairs, pairCapacity) : Host.Substitute(Prepared.Executor, MemoryMarshal.Cast<T, byte>(input), startAt, MemoryMarshal.Cast<T, byte>(replacement), MemoryMarshal.Cast<T, byte>(destination), flags, options, (Pcre2ByteCallbacks?)callbacks, hostLimits, precomputed, result, Prepared.Capacity, ref state, Prepared.CallbackPairs, pairCapacity);
                    if (ownedBuffer is not null && substituted.NativeReturnCode >= 0)
                    {
                        var resultBytes = checked(32L + substituted.OutputUnits * (typeof(T) == typeof(char) ? 2L : 1L));
                        if (bufferBytes + resultBytes > limits.MaxMemoryBytes)
                        { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, state.Budget.WorkConsumed, bufferBytes + resultBytes); return new((int)Pcre2ErrorCode.Heaplimit, -1, 0, state.Failure); }
                        if (!state.TryCharge(substituted.OutputUnits, substituted.OutputUnits))
                        {
                            return new((int)Pcre2ErrorCode.Matchlimit, -1, 0, state.Failure);
                        }

                        ownedOutput = typeof(T) == typeof(char) ? new string(MemoryMarshal.Cast<T, char>(destination[..substituted.OutputUnits])) : MemoryMarshal.Cast<T, byte>(destination[..substituted.OutputUnits]).ToArray();
                    }
                    return substituted;
                }
                finally { Record(ref state); }
            }
            finally { Prepared.Exit(); }
        }
        finally { Exit(); }
    }
    /// <summary>One bounded substitution into an owned string; explicit capacity avoids callback replay. Native failures throw their typed result.</summary>
    public string SubstituteString(ReadOnlySpan<char> input, int startAt, ReadOnlySpan<char> replacement, int outputCapacity, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None, Pcre2MatchOptions options = default, Pcre2CharCallbacks? callbacks = null, Pcre2HostLimits? limits = null, int? pairCapacity = null)
    { var result = SubstituteCore(input, startAt, replacement, Span<char>.Empty, flags, options, callbacks, limits, default, default, pairCapacity, out var owned, outputCapacity); if (result.NativeReturnCode < 0 || result.Failure.Code != RegexExecutionFailureCode.None) { throw new Pcre2SubstituteException(result); } return (string)owned!; }
    /// <summary>One bounded substitution into owned native8 bytes, without a second callback execution.</summary>
    public byte[] SubstituteBytes(ReadOnlySpan<byte> input, int startAt, ReadOnlySpan<byte> replacement, int outputCapacity, Pcre2SubstituteFlags flags = Pcre2SubstituteFlags.None, Pcre2MatchOptions options = default, Pcre2ByteCallbacks? callbacks = null, Pcre2HostLimits? limits = null, int? pairCapacity = null)
    { var result = SubstituteCore(input, startAt, replacement, Span<byte>.Empty, flags, options, callbacks, limits, default, default, pairCapacity, out var owned, outputCapacity); if (result.NativeReturnCode < 0 || result.Failure.Code != RegexExecutionFailureCode.None) { throw new Pcre2SubstituteException(result); } return (byte[])owned!; }
    /// <summary>Own an exclusive cumulative-budget iteration session; supply original subject on each Next.</summary>
    public Pcre2Iteration CreateIteration(int startAt = 0, Pcre2MatchOptions options = default, Pcre2CharCallbacks? charCallbacks = null, Pcre2ByteCallbacks? byteCallbacks = null, Pcre2HostLimits? limits = null)
    {
        Enter();
        try
        { return new(this, startAt, options, charCallbacks, byteCallbacks, limits ?? Pcre2PreparedWorkspace.DefaultHostLimits); }
        catch { Exit(); throw; }
    }
    internal bool Admit(int length, int startAt, Pcre2MatchOptions options, ref RunState state, out Pcre2MatchResult denial)
    {
        if (!Prepared.Admit(length, startAt, options, ref state, out denial))
        {
            return false;
        }

        var ceiling = Math.Min(options.Limits!.MaxWorkspaceBytes, Prepared.Executor.Program.PatternLimits.HeapBytes ?? long.MaxValue);
        if (ReservedBytes <= ceiling)
        {
            return true;
        }

        state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, state.Budget.WorkConsumed, ReservedBytes);
        denial = new(Pcre2MatchStatus.LimitExceeded, (int)Pcre2ErrorCode.Heaplimit, startAt, Failure: state.Failure);
        return false;
    }
    internal bool Copy<T>(ReadOnlySpan<T> input, Pcre2MatchOptions options, ref RunState state) where T : unmanaged
    {
        if (!state.TryCharge(input.Length, input.Length))
        {
            return false;
        }

        var ceiling = Math.Min(options.Limits!.MaxWorkspaceBytes, Prepared.Executor.Program.PatternLimits.HeapBytes ?? long.MaxValue);
        var old = typeof(T) == typeof(char) ? _copiedChars.Length : _copiedBytes.Length;
        if (input.Length > old)
        {
            var replacement = checked(32L + input.Length * (typeof(T) == typeof(char) ? 2L : 1L));
            var peak = checked(ReservedBytes + replacement);
            if (peak > ceiling)
            { state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, state.Budget.WorkConsumed, peak); return false; }
            if (typeof(T) == typeof(char))
            {
                _copiedChars = new char[input.Length];
            }
            else
            {
                _copiedBytes = new byte[input.Length];
            }
        }
        if (typeof(T) == typeof(char))
        {
            MemoryMarshal.Cast<T, char>(input).CopyTo(_copiedChars);
        }
        else
        {
            MemoryMarshal.Cast<T, byte>(input).CopyTo(_copiedBytes);
        }

        _copiedLength = input.Length;
        return true;
    }
    private void ReserveCore(Pcre2ExecutionCapacity target)
    {
        var old = Prepared.Capacity.Required(Prepared.Executor.Program.GroupCount);
        var next = target.Required(Prepared.Executor.Program.GroupCount);
        var replacement = (next.StateSlots > old.StateSlots ? 32L + 4L * next.StateSlots : 0) + (next.CaptureSlots > old.CaptureSlots ? 32L + 4L * next.CaptureSlots : 0) + (next.ScratchSlots > old.ScratchSlots ? 32L + 4L * next.ScratchSlots : 0);
        var peak = checked(ReservedBytes + replacement);
        if (peak > Prepared.Ceiling.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0, peak));
        }

        Prepared.Reserve(target);
    }
    internal static Pcre2MatchResult CopyFailure(Pcre2MatchResult result, ref RunState state) => new(
        state.Failure.Code == RegexExecutionFailureCode.Cancelled ? Pcre2MatchStatus.Cancelled : state.Failure.Code == RegexExecutionFailureCode.TimedOut ? Pcre2MatchStatus.TimedOut : Pcre2MatchStatus.LimitExceeded,
        state.Failure.Code == RegexExecutionFailureCode.WorkspaceLimitExceeded ? (int)Pcre2ErrorCode.Heaplimit : 0, result.StartChar, result.MarkIndex, state.Failure);
    internal void ClearCopy() => _copiedLength = 0;
    internal void Record(ref RunState state) { Prepared.Record(ref state); LastWork = state.Budget.WorkConsumed; LastFailure = state.Failure; }
    internal void Exit() => Volatile.Write(ref _lifecycle, 0);
    private void Enter()
    {
        var before = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (before == 2)
        {
            throw new ObjectDisposedException(nameof(Pcre2Workspace));
        }

        if (before != 0)
        {
            throw new InvalidOperationException("Workspace is already active.");
        }

        ClearCopy();
        LastWork = 0;
        LastFailure = default;
        if (_allowGrowth && !_hasDefaultCapacity)
        {
            try
            { ReserveCore(Pcre2ExecutionCapacity.Default); var c = Prepared.Capacity; _capacity = new(c.Frames, c.CaptureEvents, c.Windows, c.UnicodeScratch, true); _hasDefaultCapacity = true; }
            catch { Exit(); throw; }
        }
    }
    /// <summary>Release owned buffers. Active operations/sessions reject disposal; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        var before = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (before == 1)
        {
            throw new InvalidOperationException("Workspace is active.");
        }

        if (before == 2)
        {
            return;
        }

        Prepared.Dispose();
        _copiedChars = [];
        _copiedBytes = [];
        _copiedLength = 0;
    }
}
