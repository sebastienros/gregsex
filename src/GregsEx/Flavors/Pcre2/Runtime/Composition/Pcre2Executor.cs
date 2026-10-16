using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;
using GregsEx.Flavors.Pcre2.Parsing.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Controls;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using GregsEx.Flavors.Pcre2.Tables;
using GregsEx.IR;

namespace GregsEx.Flavors.Pcre2.Runtime.Composition;

// Immutable operation boundary. Readers/VM modules remain statically bound in
// ControlSearch; this object retains no subject, workspace or callback state.
internal sealed class Pcre2Executor : IPcre2Executor
{
    internal static Pcre2MatchLimits DefaultLimits { get; } = new();
    private readonly Pcre2ControlPlan _controls;
    private readonly IPcre2Data _data;
    private readonly Pcre2Accelerator _accelerator;
    public Pcre2Program Program => _controls.Program;
    internal Pcre2HostMetadata Metadata { get; }
    internal long RetainedBytes { get; }
    internal bool HasDirectPlan => _accelerator.HasDirectPlan;

    internal Pcre2Executor(Pcre2ControlPlan controls, IPcre2Data data,
        Pcre2HostMetadata metadata, long retainedBytes)
    {
        _controls = controls;
        _data = data;
        Metadata = metadata;
        RetainedBytes = retainedBytes;
        _accelerator = new Pcre2Accelerator(controls.Program, data.Bind(controls.Program.Profile.CharacterTables));
    }

    public Pcre2StorageRequirements GetRequiredCapacity(Pcre2ExecutionCapacity capacity) =>
        capacity.RequiredStorage(Program.GroupCount);

    public Pcre2MatchResult Match(ReadOnlySpan<char> input, int startAt, Pcre2MatchOptions options,
        Pcre2CharCallbacks? callbacks, Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy,
        ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs)
    {
        Validate(input.Length, startAt, Pcre2CodeUnitWidth.Sixteen, options, policy);
        var dataView = _data.Bind(Program.Profile.CharacterTables);
        if (callbacks is null && startAt <= input.Length &&
            _accelerator.TryMatch(Program, dataView, input, startAt, options, capacity, policy, ref state, pairs, out var fastResult))
        {
            return fastResult;
        }
        var machine = new Pcre2VmState(capacity, options.Limits ?? DefaultLimits,
            input.Length, Program.PatternLimits, Math.Min(startAt, input.Length));
        if (startAt > input.Length)
        {
            if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            return Pcre2KernelOperation.BadOffset();
        }
        return Pcre2ControlSearch.Find(_controls, input, dataView,
            options, policy, callbacks, callbackPairs, ref machine, ref state, pairs);
    }

    public Pcre2MatchResult Match(ReadOnlySpan<byte> input, int startAt, Pcre2MatchOptions options,
        Pcre2ByteCallbacks? callbacks, Pcre2ExecutionCapacity capacity, Pcre2ValidationPolicy policy,
        ref RunState state, Span<Pcre2OffsetPair> callbackPairs, Span<Pcre2OffsetPair> pairs)
    {
        Validate(input.Length, startAt, Pcre2CodeUnitWidth.Eight, options, policy);
        var dataView = _data.Bind(Program.Profile.CharacterTables);
        if (callbacks is null && startAt <= input.Length &&
            _accelerator.TryMatch(Program, dataView, input, startAt, options, capacity, policy, ref state, pairs, out var fastResult))
        {
            return fastResult;
        }
        var machine = new Pcre2VmState(capacity, options.Limits ?? DefaultLimits,
            input.Length, Program.PatternLimits, Math.Min(startAt, input.Length));
        if (startAt > input.Length)
        {
            if (!Pcre2KernelOperation.PollEntry(Program, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            return Pcre2KernelOperation.BadOffset();
        }
        return Pcre2ControlSearch.Find(_controls, input, dataView,
            options, policy, callbacks, callbackPairs, ref machine, ref state, pairs);
    }

    private void Validate(int length, int startAt, Pcre2CodeUnitWidth width,
        Pcre2MatchOptions options, Pcre2ValidationPolicy policy)
    {
        if (Program.Profile.Width != width)
        {
            throw new ArgumentException("Native subject width mismatch.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(startAt);
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        _ = options.Resolve(options.Limits ?? DefaultLimits);
    }
}

internal sealed class Pcre2ExecutorFactory : IPcre2ExecutorFactory
{
    public bool TryCreate(Pcre2Program program, IPcre2Data data, ref CompileBudget budget,
        out IPcre2Executor? executor, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(data);
        if (!program.IsExecutable)
        {
            throw new ArgumentException("Executable plan required.", nameof(program));
        }

        executor = null;
        diagnostic = default;
        if (data.DataId != Pcre2Data.Instance.DataId || data.SourceDigest != Pcre2Data.Instance.SourceDigest)
        {
            diagnostic = new(new(RegexDiagnosticCode.UnsupportedFeature, 0, program.SourceLength, program.SourceUnit),
                Pcre2DiagnosticKind.Capability, CapabilityId: "PC-024");
            return false;
        }
        long names = 0;
        foreach (var name in program.Names)
        {
            names += name.Numbers.Count;
        }

        var extra = checked(128L + 96L + 4L * program.Nodes.Length + 192L + names * 24L + program.Callouts.Length * 32L);
        var source = new SourceRange(0, program.SourceLength, program.SourceUnit);
        if (extra > budget.Limits.MaxPlanBytes - program.RetainedBytes)
        {
            diagnostic = new(new(RegexDiagnosticCode.PlanLimitExceeded, 0, program.SourceLength, program.SourceUnit), Pcre2DiagnosticKind.CompileLimit);
            return false;
        }
        var before = budget.ReservedBytes;
        var retained = false;
        try
        {
            if (!budget.TryReserveBytes(128, source, out var error))
            { diagnostic = new(error, Pcre2DiagnosticKind.CompileLimit); return false; }
            if (!Pcre2ControlPlan.TryCreate(program, ref budget, out var controls, out diagnostic) ||
                !Pcre2HostMetadata.TryCreate(program, ref budget, out var metadata, out diagnostic))
            {
                return false;
            }

            executor = new Pcre2Executor(controls!, data, metadata!, checked(program.RetainedBytes + extra));
            retained = true;
            return true;
        }
        finally
        {
            if (!retained)
            {
                budget.ReleaseBytes(budget.ReservedBytes - before);
            }
        }
    }

    // Shared compiler/factory composition, with one compile budget. Failed
    // binding releases its plan too; a separately supplied plan remains owned
    // by its caller when TryCreate fails.
    internal static bool TryCompile(ReadOnlySpan<char> pattern, Pcre2Profile profile,
        ref CompileBudget budget, out Pcre2Executor? executor, out Pcre2Diagnostic diagnostic)
    {
        executor = null;
        var before = budget.ReservedBytes;
        var retained = false;
        try
        {
            if (!new Pcre2Compiler().TryCompile(pattern, profile, Pcre2Data.Instance, ref budget, out var program, out diagnostic) ||
                !new Pcre2ExecutorFactory().TryCreate(program!, Pcre2Data.Instance, ref budget, out var bound, out diagnostic))
            {
                return false;
            }

            executor = (Pcre2Executor)bound!;
            retained = true;
            return true;
        }
        finally
        {
            if (!retained)
            {
                budget.ReleaseBytes(budget.ReservedBytes - before);
            }
        }
    }

    internal static bool TryCompile(ReadOnlySpan<byte> pattern, Pcre2Profile profile,
        ref CompileBudget budget, out Pcre2Executor? executor, out Pcre2Diagnostic diagnostic)
    {
        executor = null;
        var before = budget.ReservedBytes;
        var retained = false;
        try
        {
            if (!new Pcre2Compiler().TryCompile(pattern, profile, Pcre2Data.Instance, ref budget, out var program, out diagnostic) ||
                !new Pcre2ExecutorFactory().TryCreate(program!, Pcre2Data.Instance, ref budget, out var bound, out diagnostic))
            {
                return false;
            }

            executor = (Pcre2Executor)bound!;
            retained = true;
            return true;
        }
        finally
        {
            if (!retained)
            {
                budget.ReleaseBytes(budget.ReservedBytes - before);
            }
        }
    }
}
