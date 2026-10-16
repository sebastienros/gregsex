using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Text;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Integration;
using GregsEx.Execution.Compiled;
using GregsEx.Flavors.Pcre2.Integration;

namespace GregsEx;

/// <summary>Owned, exclusive mutable scratch for one immutable pattern. Prepared no-growth calls allocate no storage.</summary>
public sealed class RegexWorkspace : IDisposable
{
    // Conservative object header, references, status/failure fields and reservation metadata.
    internal const long MetadataBytes = 128;
    private readonly RegexPattern _pattern;
    private readonly bool _allowGrowth;
    private int[] _states = [];
    private int[] _captures = [];
    private int[] _scratch = [];
    private Pcre2OffsetPair[] _pcrePairs = [];
    private int _validLength = -1;
    private int _validNextStart = -1;
    private ulong _validHead, _validMid, _validTail;
    private long PairStorageBytes => _pattern.Executor is Pcre2WorkspaceExecutor or Pcre2CompiledExecutor ? 32L + 16L * _pattern.GroupCount : 0;
    private int _lifecycle; // 0 idle, 1 active (including Reserve), 2 disposed
    /// <summary>Structured failure from the latest admitted operation; None for Match and NoMatch.</summary>
    public RegexExecutionFailure LastFailure { get; private set; }
    /// <summary>Current prepared partitions. Inspect only while the workspace is idle.</summary>
    public RegexWorkspaceCapacity Capacity => new(_states.Length, _captures.Length, _scratch.Length);
    /// <summary>Conservative owned scratch and workspace metadata bytes, excluding the shared pattern.</summary>
    public long ReservedBytes => MetadataBytes + Capacity.RequiredBytes + PairStorageBytes;

    internal RegexWorkspace(RegexPattern pattern, RegexWorkspaceOptions options)
    {
        _pattern = pattern;
        _allowGrowth = options.AllowGrowth;
        if (!TryReserve(options.InitialCapacity, pattern.MatchLimits.MaxWorkspaceBytes, initial: true))
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0));
        }
    }

    /// <summary>Grows each requested partition transactionally. A memory failure throws and retains prior buffers.
    /// Peak admission includes old and replacement arrays; this method may allocate regardless of AllowGrowth.</summary>
    public void Reserve(RegexWorkspaceCapacity capacity)
    {
        Enter();
        try
        {
            if (!TryReserve(capacity, _pattern.MatchLimits.MaxWorkspaceBytes))
            {
                throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0));
            }
        }
        finally { Exit(); }
    }

    private bool TryReserve(RegexWorkspaceCapacity requested, long ceiling, bool initial = false)
    {
        var old = Capacity;
        var target = new RegexWorkspaceCapacity(Math.Max(old.StateSlots, requested.StateSlots),
            Math.Max(old.CaptureSlots, requested.CaptureSlots), Math.Max(old.ScratchSlots, requested.ScratchSlots));
        var finalBytes = MetadataBytes + target.RequiredBytes + PairStorageBytes;
        var replacements = (target.StateSlots > old.StateSlots ? 32L + 4L * target.StateSlots : 0) +
            (target.CaptureSlots > old.CaptureSlots ? 32L + 4L * target.CaptureSlots : 0) +
            (target.ScratchSlots > old.ScratchSlots ? 32L + 4L * target.ScratchSlots : 0);
        var peakBytes = initial ? finalBytes : ReservedBytes + replacements;
        if (finalBytes > ceiling || peakBytes > ceiling)
        {
            return false;
        }
        // All allocations complete before any field is replaced. A system allocation failure
        // propagates; it cannot partially publish the reservation or hide as NoMatch.
        var states = target.StateSlots > old.StateSlots ? new int[target.StateSlots] : _states;
        var captures = target.CaptureSlots > old.CaptureSlots ? new int[target.CaptureSlots] : _captures;
        var scratch = target.ScratchSlots > old.ScratchSlots ? new int[target.ScratchSlots] : _scratch;
        var pairs = initial && PairStorageBytes != 0 ? new Pcre2OffsetPair[_pattern.GroupCount] : _pcrePairs;
        _states = states;
        _captures = captures;
        _scratch = scratch;
        _pcrePairs = pairs;
        return true;
    }

    /// <summary>Searches for existence using native UTF-16 offsets.</summary>
    public RegexSearchStatus IsMatch(ReadOnlySpan<char> input, int startAt = 0, RegexMatchOptions options = default)
        => Search<char, CharTextReader>(input, startAt, SearchOperation.Boolean, [], out _, options);
    /// <summary>Searches for existence using native byte offsets.</summary>
    public RegexSearchStatus IsMatch(ReadOnlySpan<byte> input, int startAt = 0, RegexMatchOptions options = default)
        => Search<byte, ByteTextReader>(input, startAt, SearchOperation.Boolean, [], out _, options);
    /// <summary>Finds the first selected native UTF-16 range in the original subject.</summary>
    public RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt, out RegexMatchRange match, RegexMatchOptions options = default)
        => Search<char, CharTextReader>(input, startAt, SearchOperation.Range, [], out match, options);
    /// <summary>Finds the first selected native byte range in the original subject.</summary>
    public RegexSearchStatus Find(ReadOnlySpan<byte> input, int startAt, out RegexMatchRange match, RegexMatchOptions options = default)
        => Search<byte, ByteTextReader>(input, startAt, SearchOperation.Range, [], out match, options);
    /// <summary>Finds final captures. Short destinations are untouched; excess slots are always untouched.</summary>
    public RegexSearchStatus FindCaptures(ReadOnlySpan<char> input, int startAt, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default)
        => Search<char, CharTextReader>(input, startAt, SearchOperation.Captures, groups, out match, options);
    /// <summary>Finds final captures with native byte coordinates.</summary>
    public RegexSearchStatus FindCaptures(ReadOnlySpan<byte> input, int startAt, Span<RegexCapture> groups,
        out RegexMatchRange match, RegexMatchOptions options = default)
        => Search<byte, ByteTextReader>(input, startAt, SearchOperation.Captures, groups, out match, options);

    private RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, Span<RegexCapture> groups, out RegexMatchRange match, RegexMatchOptions options)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        match = default;
        Enter();
        try
        {
            if ((typeof(TUnit) == typeof(char)) != (_pattern.InputKind == RegexInputKind.Utf16))
            {
                throw new ArgumentException("Subject overload does not match the pattern representation.", nameof(input));
            }

            if ((uint)startAt > (uint)input.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(startAt));
            }

            if (_pattern.Profile is XsdProfile && startAt != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startAt), "XSD operations require whole-value membership at zero.");
            }

            if (operation == SearchOperation.Captures && _pattern.Executor is PosixWorkspaceExecutor { NoSub: true })
            {
                throw new NotSupportedException("NoSub disables generic capture retrieval.");
            }

            var limits = options.Resolve(_pattern.MatchLimits);
            var maxWork = limits.MaxWork;
            var maxWorkspaceBytes = limits.MaxWorkspaceBytes;
            var timeout = options.Timeout;
            LastFailure = default;
            if (operation == SearchOperation.Captures && groups.Length < _pattern.GroupCount)
            {
                LastFailure = new(RegexExecutionFailureCode.DestinationTooSmall, startAt, 0, _pattern.GroupCount);
                return RegexSearchStatus.DestinationTooSmall;
            }
            RegexWorkspaceCapacity required;
            try
            { required = _pattern.Executor.GetRequiredCapacity(operation); }
            catch (OverflowException)
            { return AdmissionFailure(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, operation, groups); }
            if (ReservedBytes > maxWorkspaceBytes)
            {
                return AdmissionFailure(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, operation, groups);
            }

            if (!Capacity.Contains(required))
            {
                if (!_allowGrowth)
                {
                    return AdmissionFailure(RegexExecutionFailureCode.WorkspaceCapacityExceeded, startAt, operation, groups);
                }

                if (MetadataBytes + required.RequiredBytes > maxWorkspaceBytes)
                {
                    return AdmissionFailure(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, operation, groups);
                }

                if (!TryReserve(required, maxWorkspaceBytes))
                {
                    return AdmissionFailure(RegexExecutionFailureCode.WorkspaceLimitExceeded, startAt, operation, groups);
                }
            }
            var state = new RunState(new WorkspaceBuffers(_states, _captures, _scratch, _pcrePairs), maxWork, options.CancellationToken, timeout);
            if (startAt > 0 && input.Length >= 64 && input.Length == _validLength &&
                (startAt == _validNextStart || startAt == _validNextStart + 1) &&
                MatchesFingerprint(input))
            {
                state.PrevalidatedCleanSubject = true;
            }
            var status = _pattern.Executor.Search<TUnit, TReader>(input, startAt, operation, ref state, groups, out match);
            if (status == RegexSearchStatus.Match && state.ValidatedCleanSubject && input.Length >= 64)
            {
                if (!state.PrevalidatedCleanSubject)
                {
                    SaveFingerprint(input);
                }

                _validNextStart = match.Index + match.Length;
            }
            else
            {
                _validLength = -1;
            }
            LastFailure = state.Failure;
            return status;
        }
        finally { Exit(); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MatchesFingerprint<TUnit>(ReadOnlySpan<TUnit> input) where TUnit : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(input);
        ref var b0 = ref MemoryMarshal.GetReference(bytes);
        var mid = (bytes.Length - 8) >> 1;
        var tail = bytes.Length - 8;
        return Unsafe.ReadUnaligned<ulong>(ref b0) == _validHead &&
               Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, mid)) == _validMid &&
               Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, tail)) == _validTail;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SaveFingerprint<TUnit>(ReadOnlySpan<TUnit> input) where TUnit : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(input);
        ref var b0 = ref MemoryMarshal.GetReference(bytes);
        var mid = (bytes.Length - 8) >> 1;
        var tail = bytes.Length - 8;
        _validLength = input.Length;
        _validHead = Unsafe.ReadUnaligned<ulong>(ref b0);
        _validMid = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, mid));
        _validTail = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, tail));
    }

    internal RegexSearchStatus PosixRegExec(ReadOnlySpan<byte> input, Span<PosixMatch> matches,
        PosixExecutionFlags flags, RegexMatchOptions options)
    {
        Enter();
        try
        {
            if (_pattern.Executor is not PosixWorkspaceExecutor adapter)
            {
                throw new ArgumentException("RegExec requires a POSIX workspace.");
            }

            if ((flags & ~(PosixExecutionFlags)3) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(flags));
            }

            var limits = options.Resolve(_pattern.MatchLimits);
            LastFailure = default;
            var publish = !adapter.NoSub && !matches.IsEmpty;
            var operation = !publish ? SearchOperation.Boolean : matches.Length == 1 ? SearchOperation.Range : SearchOperation.Captures;
            RegexWorkspaceCapacity required;
            try
            { required = adapter.GetRequiredCapacity(operation); }
            catch (OverflowException) { return NativeAdmissionFailure(RegexExecutionFailureCode.WorkspaceCapacityExceeded, matches, publish); }
            if (ReservedBytes > limits.MaxWorkspaceBytes)
            {
                return NativeAdmissionFailure(RegexExecutionFailureCode.WorkspaceLimitExceeded, matches, publish);
            }

            if (!Capacity.Contains(required))
            {
                if (!_allowGrowth)
                {
                    return NativeAdmissionFailure(RegexExecutionFailureCode.WorkspaceCapacityExceeded, matches, publish);
                }

                if (!TryReserve(required, limits.MaxWorkspaceBytes))
                {
                    return NativeAdmissionFailure(RegexExecutionFailureCode.WorkspaceLimitExceeded, matches, publish);
                }
            }
            var state = new RunState(new WorkspaceBuffers(_states, _captures, _scratch, _pcrePairs), limits.MaxWork, options.CancellationToken, options.Timeout);
            var groups = operation == SearchOperation.Captures ? adapter.Publication(_scratch) : default;
            var status = adapter.Executor.Search(input, 0, operation, flags, PosixExecutionCapacity.Default,
                ref state, groups, out var range);
            if (publish && status == RegexSearchStatus.Match)
            {
                // Charge complete publication before writing any endpoint, preserving
                // failure atomicity and the same cumulative budget as path selection.
                if (!state.TryCharge(matches.Length, range.Index))
                {
                    status = state.Failure.Code switch
                    {
                        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
                        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
                        _ => RegexSearchStatus.LimitExceeded
                    };
                }
                else
                {
                    matches[0] = new(range.Index, checked(range.Index + range.Length));
                    for (var i = 1; i < matches.Length; i++)
                    {
                        matches[i] = i < _pattern.GroupCount && groups[i].Participated
                            ? new(groups[i].Index, checked(groups[i].Index + groups[i].Length)) : new(-1, -1);
                    }
                }
            }
            if (publish && status != RegexSearchStatus.Match)
            {
                matches.Fill(new(-1, -1));
            }

            LastFailure = state.Failure;
            return status;
        }
        finally { Exit(); }
    }

    private RegexSearchStatus NativeAdmissionFailure(RegexExecutionFailureCode code, Span<PosixMatch> matches, bool publish)
    {
        LastFailure = new(code, 0, 0);
        if (publish)
        {
            matches.Fill(new(-1, -1));
        }

        return RegexSearchStatus.LimitExceeded;
    }

    private RegexSearchStatus AdmissionFailure(RegexExecutionFailureCode code, int startAt,
        SearchOperation operation, Span<RegexCapture> groups)
    {
        LastFailure = new(code, startAt, 0);
        if (operation == SearchOperation.Captures)
        {
            groups[.._pattern.GroupCount].Clear();
        }

        return RegexSearchStatus.LimitExceeded;
    }

    private void Enter()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 1, 0);
        if (previous == 2)
        {
            throw new ObjectDisposedException(nameof(RegexWorkspace));
        }

        if (previous != 0)
        {
            throw new InvalidOperationException("Workspace is already in use.");
        }
    }
    private void Exit() => Volatile.Write(ref _lifecycle, 0);

    /// <summary>Releases references to owned arrays; repeated disposal is harmless. Active use is rejected.</summary>
    public void Dispose()
    {
        var previous = Interlocked.CompareExchange(ref _lifecycle, 2, 0);
        if (previous == 1)
        {
            throw new InvalidOperationException("Workspace is already in use.");
        }

        if (previous == 2)
        {
            return;
        }

        _states = [];
        _captures = [];
        _scratch = [];
        _pcrePairs = [];
    }
}
