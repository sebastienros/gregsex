#nullable enable
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;

namespace GregsEx;

/// <summary>Exclusive mutable ECMAScript builtin session over an immutable executor.
/// Borrowed operations allocate no storage within the prepared frame reservation.
/// Allocating operations have separate finite host ceilings.</summary>
public sealed partial class EcmaScriptMatcher : IDisposable
{
    private const long MetadataBytes = 256;
    private readonly IEcmaExecutor _executor;
    private readonly EcmaExecutor? _fastExecutor;
    private readonly EcmaProgram _program;
    private readonly int _groupCount;
    private readonly bool _global;
    private readonly bool _sticky;
    private readonly bool _stateful;
    private readonly bool _unicodeAware;
    private readonly RegexMatchLimits _limits;
    private readonly EcmaScriptHostLimits _hostLimits;
    private int[] _states = [], _registers = [], _scratch = [];
    private RegexCapture[] _hostCaptures = [];
    private int _lifecycle, _frames, _admittedFrames;
    private long _reservedBytes = MetadataBytes + 32L;
    private double _lastIndex;
    /// <summary>Numeric builtin state. Access requires an idle, live session.</summary>
    public double LastIndex
    {
        get { Enter(); try { return _lastIndex; } finally { Exit(); } }
        set { Enter(); try { _lastIndex = value; } finally { Exit(); } }
    }
    /// <summary>Failure from the latest operation, distinct from no match.</summary>
    public RegexExecutionFailure LastFailure { get; private set; }
    private EcmaProgram Program => _program;
    private bool Global => _global;
    private bool Sticky => _sticky;
    private bool Stateful => _stateful;
    private RegexWorkspaceCapacity Capacity => new(_states.Length, _registers.Length, _scratch.Length);
    /// <summary>Conservatively accounted private workspace bytes, excluding immutable plans and host outputs.</summary>
    public long ReservedBytes => _reservedBytes;

    // Real construction seam for ordered integration, and independent contract-executor tests.
    // No public parser/bind placeholder is exposed before real composition exists.
    internal EcmaScriptMatcher(IEcmaExecutor executor, RegexMatchLimits? limits = null,
        RegexWorkspaceOptions? workspace = null, int frameCapacity = 4096,
        EcmaScriptHostLimits? hostLimits = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        _fastExecutor = executor as EcmaExecutor;
        _program = executor.Program;
        _groupCount = _program.GroupCount;
        var flags = _program.Profile.Flags;
        _global = (flags & EcmaScriptFlags.Global) != 0;
        _sticky = (flags & EcmaScriptFlags.Sticky) != 0;
        _stateful = _global || _sticky;
        _unicodeAware = _program.Profile.IsUnicodeAware;
        _limits = limits ?? new();
        _hostLimits = hostLimits ?? new();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCapacity);
        var initial = workspace?.InitialCapacity ?? default;
        ReserveCore(frameCapacity, initial, initial: true);
    }

    /// <summary>Transactionally grows the finite VM reservation. Peak includes old and new arrays.</summary>
    public void Reserve(int frameCapacity)
    {
        Enter();
        try
        { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCapacity); ReserveCore(frameCapacity, default); }
        finally { Exit(); }
    }
    private void ReserveCore(int frames, RegexWorkspaceCapacity extra, bool initial = false)
    {
        frames = Math.Max(frames, _frames);
        var target = extra;
        try
        {
            for (var operation = SearchOperation.Boolean; operation <= SearchOperation.Captures; operation++)
            {
                var need = _executor.GetRequiredCapacity(operation, frames);
                target = Max(target, need);
            }
        }
        catch (OverflowException) { throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, 0, 0)); }
        target = Max(target, Capacity);
        var final = MetadataBytes + target.RequiredBytes + 32L + 16L * _groupCount;
        var old = Capacity;
        var replacements = (target.StateSlots > old.StateSlots ? 32L + 4L * target.StateSlots : 0) +
            (target.CaptureSlots > old.CaptureSlots ? 32L + 4L * target.CaptureSlots : 0) +
            (target.ScratchSlots > old.ScratchSlots ? 32L + 4L * target.ScratchSlots : 0);
        if (final > _limits.MaxWorkspaceBytes || (!initial && _reservedBytes + replacements > _limits.MaxWorkspaceBytes))
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0));
        }

        var states = target.StateSlots > old.StateSlots ? new int[target.StateSlots] : _states;
        var registers = target.CaptureSlots > old.CaptureSlots ? new int[target.CaptureSlots] : _registers;
        var scratch = target.ScratchSlots > old.ScratchSlots ? new int[target.ScratchSlots] : _scratch;
        var captures = initial ? new RegexCapture[_groupCount] : _hostCaptures;
        _states = states;
        _registers = registers;
        _scratch = scratch;
        _hostCaptures = captures;
        _frames = frames;
        _reservedBytes = MetadataBytes + Capacity.RequiredBytes + 32L + 16L * _hostCaptures.Length;
        _admittedFrames = _fastExecutor?.ComputeAdmittedFrames(Capacity) ?? frames;
    }
    private static RegexWorkspaceCapacity Max(RegexWorkspaceCapacity a, RegexWorkspaceCapacity b)
        => new(Math.Max(a.StateSlots, b.StateSlots), Math.Max(a.CaptureSlots, b.CaptureSlots), Math.Max(a.ScratchSlots, b.ScratchSlots));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RunState CreateState(RegexMatchOptions options)
    {
        var limits = options.Resolve(_limits);
        return CreateStateResolved(limits, options);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RunState CreateStateResolved(RegexMatchLimits limits, RegexMatchOptions options)
    {
        LastFailure = default;
        var state = new RunState(new WorkspaceBuffers(_states, _registers, _scratch), limits, options.CancellationToken, options.Timeout);
        if (_reservedBytes > limits.MaxWorkspaceBytes)
        {
            state.Failure = new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0);
        }

        return state;
    }
    /// <summary>Raw builtin Exec. Empty success does not advance LastIndex.</summary>
    public RegexSearchStatus Exec(ReadOnlySpan<char> input, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default)
        => Borrowed(input, groups, out match, options, SearchOperation.Captures, false);
    /// <summary>Builtin Test with the same state rules as Exec.</summary>
    public RegexSearchStatus Test(ReadOnlySpan<char> input, RegexMatchOptions options = default)
        => Borrowed(input, [], out _, options, SearchOperation.Boolean, false);
    /// <summary>Iteration step requiring g or y. Advances after empty success, including past the terminal boundary.</summary>
    public RegexSearchStatus ExecNext(ReadOnlySpan<char> input, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default)
        => Borrowed(input, groups, out match, options, SearchOperation.Captures, true);
    private RegexSearchStatus Borrowed(ReadOnlySpan<char> input, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options, SearchOperation operation, bool advance)
    {
        match = default;
        Enter();
        try
        {
            var limits = options.Resolve(_limits);
            LastFailure = default;
            if (advance && !_stateful)
            {
                throw new InvalidOperationException("ExecNext requires g or y.");
            }

            if (operation == SearchOperation.Captures && groups.Length < _groupCount)
            {
                LastFailure = new(RegexExecutionFailureCode.DestinationTooSmall, 0, 0, _groupCount);
                return RegexSearchStatus.DestinationTooSmall;
            }
            var state = CreateStateResolved(limits, options);
            var status = ExecCore(input, operation, groups, out match, ref state);
            if (advance && status == RegexSearchStatus.Match && match.Length == 0)
            {
                if (!TryAdvance(input, ToLength(_lastIndex), ref state, out var next))
                { groups[.._groupCount].Clear(); match = default; status = FailureStatus(state.Failure); }
                else
                {
                    _lastIndex = next;
                }
            }
            LastFailure = state.Failure;
            return status;
        }
        finally { Exit(); }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RegexSearchStatus ExecCore(ReadOnlySpan<char> input, SearchOperation operation,
        Span<RegexCapture> captures, out RegexMatchRange match, ref RunState state, bool forceSticky = false)
    {
        match = default;
        if (!state.TryCharge(0, 0))
        {
            return ClearFailure(operation, captures, state.Failure);
        }

        var stateful = _stateful || forceSticky;
        var start = stateful ? ToLength(_lastIndex) : 0;
        if (start > input.Length)
        {
            if (stateful)
            {
                _lastIndex = 0;
            }

            if (operation == SearchOperation.Captures)
            {
                captures[.._groupCount].Clear();
            }

            return RegexSearchStatus.NoMatch;
        }
        var status = _fastExecutor is not null
            ? _fastExecutor.SearchPrevalidated(input, (int)start, _sticky || forceSticky, operation, _admittedFrames, ref state, captures, out match)
            : _executor.Search(input, (int)start, _sticky || forceSticky, EcmaStartPolicy.BuiltinLastIndex, operation, ref state, captures, out match);
        if (stateful)
        {
            if (status == RegexSearchStatus.Match)
            {
                _lastIndex = (long)match.Index + match.Length;
            }
            else if (status == RegexSearchStatus.NoMatch)
            {
                _lastIndex = 0;
            }
        }
        return status;
    }
    private RegexSearchStatus ClearFailure(SearchOperation operation, Span<RegexCapture> captures, RegexExecutionFailure failure)
    {
        if (operation == SearchOperation.Captures)
        {
            captures[.._groupCount].Clear();
        }

        return FailureStatus(failure);
    }
    internal static RegexSearchStatus FailureStatus(RegexExecutionFailure failure) => failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        RegexExecutionFailureCode.InvalidStartBoundary or RegexExecutionFailureCode.InvalidEncoding => RegexSearchStatus.InvalidInput,
        _ => RegexSearchStatus.LimitExceeded
    };
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long ToLength(double value)
    {
        if (value >= 0d && value <= int.MaxValue)
        {
            return (long)value;
        }

        return double.IsNaN(value) || value <= 0 ? 0 : value >= 9_007_199_254_740_991d ? 9_007_199_254_740_991L : (long)Math.Floor(value);
    }
    private bool TryAdvance(ReadOnlySpan<char> input, long index, ref RunState state, out long next)
    {
        next = index + 1;
        if (!state.TryCharge(1, (int)Math.Min(index, input.Length)))
        {
            return false;
        }

        if (!_unicodeAware || index >= input.Length)
        {
            return true;
        }

        if (!state.TryCharge(1, (int)index))
        {
            return false;
        }

        if (char.IsHighSurrogate(input[(int)index]) && index + 1 < input.Length)
        {
            if (!state.TryCharge(1, (int)index + 1))
            {
                return false;
            }

            if (char.IsLowSurrogate(input[(int)index + 1]))
            {
                next++;
            }
        }
        return true;
    }
    private long Advance(ReadOnlySpan<char> input, long index, ref RunState state)
    {
        if (!TryAdvance(input, index, ref state, out var next))
        {
            throw new RegexExecutionException(state.Failure);
        }

        return next;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Enter()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous != 0)
        {
            ThrowNotIdle(previous);
        }
    }
    private static void ThrowNotIdle(int previous)
    {
        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(EcmaScriptMatcher));
        }

        throw new InvalidOperationException("Session is already in use.");
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Exit() => Volatile.Write(ref _lifecycle, 0);
    /// <summary>Releases private storage. Active use is rejected; repeated disposal is harmless.</summary>
    public void Dispose()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (previous == 1)
        {
            throw new InvalidOperationException("Session is already in use.");
        }

        if (previous == 2)
        {
            return;
        }

        _states = [];
        _registers = [];
        _scratch = [];
        _hostCaptures = [];
        _reservedBytes = MetadataBytes + 32L;
    }
}
