using GregsEx.IR;

namespace GregsEx.Contracts;

internal enum TextModel { UnicodeScalars, Utf16CodeUnits, RawBytes }
internal enum MatchPolicy { OrderedLeftmostFirst }
internal enum SearchOperation { Boolean, Range, Captures }

[Flags]
internal enum CoreFeatures
{
    None = 0, Literal = 1, CharacterClass = 2, Any = 4, Alternation = 8,
    Captures = 16, AbsoluteAnchors = 32
}

// Normalized once, never based on ambient culture or runtime Unicode category data.
internal sealed record ProfileDescriptor
{
    internal RegexInputKind InputKind { get; }
    internal TextModel TextModel { get; }
    internal RegularCoreFlags Flags { get; }
    internal MatchPolicy MatchPolicy => MatchPolicy.OrderedLeftmostFirst;
    internal string Id => "RegularCore-1-experimental";
    internal string DataId => "GregsEx-ASCII-1";
    internal RegexOffsetUnit InputUnit => InputKind == RegexInputKind.Utf16
        ? RegexOffsetUnit.Utf16CodeUnits : RegexOffsetUnit.Bytes;

    private ProfileDescriptor(RegexInputKind inputKind, TextModel model, RegularCoreFlags flags)
    {
        InputKind = inputKind;
        TextModel = model;
        Flags = flags;
    }

    internal static bool TryCreate(RegexProfile profile, RegexInputKind inputKind,
        out ProfileDescriptor? descriptor, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(inputKind))
        {
            throw new ArgumentOutOfRangeException(nameof(inputKind));
        }

        descriptor = null;
        diagnostic = default;
        if (profile is not RegularCoreProfile core || !core.Supports(inputKind))
        {
            diagnostic = new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Utf16CodeUnits);
            return false;
        }
        var model = core.Mode switch
        {
            RegularCoreMode.UnicodeScalars => TextModel.UnicodeScalars,
            RegularCoreMode.Utf16CodeUnits => TextModel.Utf16CodeUnits,
            _ => TextModel.RawBytes
        };
        descriptor = new(inputKind, model, core.Flags);
        return true;
    }

    internal bool Contains(int value) => TextModel switch
    {
        TextModel.UnicodeScalars => (uint)value <= 0x10FFFF && value is not (>= 0xD800 and <= 0xDFFF),
        TextModel.Utf16CodeUnits => (uint)value <= 0xFFFF,
        _ => (uint)value <= 255
    };
}

internal interface IFlavorCompiler
{
    // Char pattern locations are UTF-16 units even when the subject uses bytes.
    bool TryCompile(ReadOnlySpan<char> pattern, ProfileDescriptor profile, ref CompileBudget budget,
        out SemanticProgram? program, out RegexDiagnostic diagnostic);
    // RegularCore admits byte pattern syntax only with the RawBytes profile.
    bool TryCompile(ReadOnlySpan<byte> pattern, ProfileDescriptor profile, ref CompileBudget budget,
        out SemanticProgram? program, out RegexDiagnostic diagnostic);
}

#if !NETSTANDARD2_0
// Reader specialization is static; no per-character virtual interface dispatch is required.
internal interface ITextReader<TUnit> where TUnit : unmanaged
{
    // Full-subject validity precedes a search. Charge each inspected input unit in state.Budget.
    // Validate start boundary only after full encoding validation succeeds.
    static abstract RegexSearchStatus Validate(ReadOnlySpan<TUnit> input, int startAt,
        ProfileDescriptor profile, ref RunState state);
    // On already validated input, decode exactly one element. End returns false, width=0.
    // The executor charges one read unit; the reader never retains the span.
    static abstract bool TryRead(ReadOnlySpan<TUnit> input, int offset, ProfileDescriptor profile,
        out int value, out int width);
}

internal interface IRegularExecutor
{
    // A bound executor retains the validated program, never a caller's subject or workspace.
    RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation);

    // Runtime binds the profile's reader types; executor tests bind independent nonshipping readers.
    // One interface call per operation is permitted; the character loop is statically specialized.
    RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>;
}
#endif

internal readonly ref struct WorkspaceBuffers
{
    internal Span<int> States { get; }
    internal Span<int> Captures { get; }
    internal Span<int> Scratch { get; }
    internal RegexWorkspaceCapacity Capacity => new(States.Length, Captures.Length, Scratch.Length);
    internal Span<Pcre2OffsetPair> Pcre2Pairs { get; }

    internal WorkspaceBuffers(Span<int> states, Span<int> captures, Span<int> scratch, Span<Pcre2OffsetPair> pcre2Pairs = default)
    {
        States = states;
        Captures = captures;
        Scratch = scratch;
        Pcre2Pairs = pcre2Pairs;
    }
}

internal ref struct RunState
{
    internal WorkspaceBuffers Buffers { get; }
    internal RunBudget Budget;
    internal RegexExecutionFailure Failure;
    internal bool PrevalidatedCleanSubject;
    internal bool ValidatedCleanSubject;

    internal RunState(WorkspaceBuffers buffers, RegexMatchLimits limits, RegexMatchOptions options = default)
    {
        Buffers = buffers;
        Budget = new(limits, options);
        Failure = default;
    }

    internal RunState(WorkspaceBuffers buffers, RegexMatchLimits resolvedLimits, CancellationToken cancellation, TimeSpan? timeout)
    {
        Buffers = buffers;
        Budget = new(resolvedLimits.MaxWork, cancellation, timeout);
        Failure = default;
    }

    internal RunState(WorkspaceBuffers buffers, long maxWork, CancellationToken cancellation, TimeSpan? timeout)
    {
        Buffers = buffers;
        Budget = new(maxWork, cancellation, timeout);
        Failure = default;
    }

    // One charge for a run of one-unit-per-offset work that is known to fit the remaining budget;
    // otherwise per-unit charges reproduce the exact failing offset and consumed work.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool TryChargeRun(int firstOffset, int length)
    {
        if (length <= Budget.RemainingWork)
        {
            return TryCharge(length, firstOffset);
        }

        return TryChargeRunSlow(firstOffset, length);
    }

    private bool TryChargeRunSlow(int firstOffset, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (!TryCharge(1, firstOffset + i))
            {
                return false;
            }
        }

        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal bool TryCharge(long units, int offset)
    {
        if (Failure.Code != RegexExecutionFailureCode.None)
        {
            return false;
        }

        if (Budget.TryCharge(units, offset, out var failure))
        {
            return true;
        }

        Failure = failure;
        return false;
    }
}
