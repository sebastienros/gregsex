#nullable enable
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using GregsEx.Execution.Compiled;
using GregsEx.Execution.Thompson;
using GregsEx.Flavors.EcmaScript.Integration;
using GregsEx.Flavors.Pcre2.Integration;
using GregsEx.Flavors.Posix.Integration;
using GregsEx.Flavors.XmlIntegration;

namespace GregsEx;

public sealed partial class RegexPattern
{
    private RegexPattern(RegexPattern source, IRegularExecutor executor, long? extraBytes = null)
    {
        Executor = executor;
        Profile = source.Profile;
        InputKind = source.InputKind;
        MatchLimits = source.MatchLimits;
        GroupCount = source.GroupCount;
        RetainedBytes = Math.Max(AdditionalPlanBytes, source.RetainedBytes + (extraBytes ?? (AdditionalPlanBytes + (executor switch
        {
            CompiledRegularExecutor { Lease: { } lease } => lease.Estimate,
            EcmaCompiledExecutor { Lease: { } lease } => lease.Estimate,
            XmlCompiledExecutor { Lease: { } lease } => lease.Estimate,
            Pcre2CompiledExecutor { Lease: { } lease } => lease.Estimate,
            _ => 4096
        }))));
    }

    // Isolated behind a non-inlined boundary and guarded by CompilationContract.IsRuntimeCompilationAvailable
    // && CompilationContract.IsAutoCompilationSwitchEnabled at all call sites, so NativeAOT and trimmed JIT builds
    // fold the guard to false and strip the entire runtime-compilation graph.
    [MethodImpl(MethodImplOptions.NoInlining)]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Guarded by CompilationContract.IsAutoCompilationSwitchEnabled ([FeatureSwitchDefinition]), which is substituted to false when PublishTrimmed or PublishAot is true.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Guarded by CompilationContract.IsRuntimeCompilationAvailable (RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled).")]
    private static IRegularExecutor TryAutoCompile(IRegularExecutor executor, RegexCompileOptions options, ref long retainedBytes)
    {
        if (options.InputKind != RegexInputKind.Utf16)
        {
            return executor;
        }

        var maxAdditionalBytes = Math.Min(options.Limits.MaxPlanBytes, options.Limits.MaxCompileMemoryBytes) - retainedBytes;
        if (maxAdditionalBytes <= 0)
        {
            return executor;
        }

        switch (executor)
        {
            case ThompsonExecutor thompson:
                if (!CompiledBackend.TryCreate(thompson, out var coreCompiled, out _, maxAdditionalBytes))
                {
                    return executor;
                }

                retainedBytes += coreCompiled!.Lease!.Estimate;
                return coreCompiled;
            case EcmaWorkspaceExecutor ecma:
                if (!EcmaCompiledBackend.TryCreate(ecma, out var ecmaCompiled, out _, maxAdditionalBytes))
                {
                    return executor;
                }

                retainedBytes += ecmaCompiled!.Lease!.Estimate;
                return ecmaCompiled;
            case XmlWorkspaceExecutor xml:
                if (!XmlCompiledBackend.TryCreate(xml, out var xmlCompiled, out _, maxAdditionalBytes))
                {
                    return executor;
                }

                retainedBytes += xmlCompiled!.Lease!.Estimate;
                return xmlCompiled;
            case Pcre2WorkspaceExecutor pcre2:
                if (!Pcre2CompiledBackend.TryCreate(pcre2, out var pcre2Compiled, out _, maxAdditionalBytes))
                {
                    return executor;
                }

                retainedBytes += pcre2Compiled!.Lease!.Estimate;
                return pcre2Compiled;
            default:
                return executor;
        }
    }

    /// <summary>
    /// Returns an immutable pattern whose matching loop is generated code. Results, captures, limits and failure
    /// statuses equal the interpreter's; subjects the generated code does not model fall back to the interpreter.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">The runtime cannot generate code.</exception>
    /// <exception cref="NotSupportedException">
    /// The pattern is not a RegularCore, ECMAScript, XSD, XPath or PCRE2 pattern, exceeds the lowering bounds, uses a construct the lowering
    /// does not model, or is already served by a faster accelerated path.
    /// </exception>
    [RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
    public RegexPattern Compile()
    {
        if (!CompilationContract.IsRuntimeCompilationAvailable)
        {
            throw new PlatformNotSupportedException(CompilationContract.RequiresDynamicCodeMessage);
        }

        if (Executor is CompiledRegularExecutor or EcmaCompiledExecutor or XmlCompiledExecutor or Pcre2CompiledExecutor)
        {
            return this;
        }

        switch (Executor)
        {
            case ThompsonExecutor interpreter:
                if (!CompiledBackend.TryCreate(interpreter, out var compiled, out var reason))
                {
                    throw new NotSupportedException(reason);
                }

                return new(this, compiled!);
            case EcmaWorkspaceExecutor ecma:
                if (!EcmaCompiledBackend.TryCreate(ecma, out var ecmaCompiled, out reason))
                {
                    throw new NotSupportedException(reason);
                }

                return new(this, ecmaCompiled!);
            case XmlWorkspaceExecutor xml:
                if (!XmlCompiledBackend.TryCreate(xml, out var xmlCompiled, out reason))
                {
                    throw new NotSupportedException(reason);
                }

                return new(this, xmlCompiled!);
            case Pcre2WorkspaceExecutor pcre2:
                if (!Pcre2CompiledBackend.TryCreate(pcre2, out var pcre2Compiled, out reason))
                {
                    throw new NotSupportedException(reason);
                }

                return new(this, pcre2Compiled!);
            case PosixWorkspaceExecutor:
                // POSIX requires leftmost-longest matches with POSIX subexpression rules; the kernel is leftmost-first
                // (for a|ab on "ab" it yields "a", POSIX yields "ab"), so it cannot preserve the semantics.
                throw new NotSupportedException("POSIX patterns are not compiled: leftmost-longest semantics are not modelled by the compiled kernel.");
            default:
                throw new NotSupportedException("This executor is not compiled.");
        }
    }

    /// <summary>
    /// Wraps kernels emitted by the GregsEx source generator in the same executor that serves <see cref="Compile"/>, so
    /// admission, limits, cancellation, timeouts and the interpreter fallback are shared. The kernels must have been
    /// generated for this exact pattern, profile and options; the source generator guarantees that. Needs no runtime code
    /// generation and is compatible with trimming and NativeAOT.
    /// </summary>
    /// <remarks>
    /// <paramref name="fingerprint"/> identifies the kernel plan the generator emitted code for. If this library derives a
    /// different plan (a generator/library revision mismatch), the interpreted pattern is returned unchanged, so a stale
    /// generated kernel can never produce wrong results; <see cref="ExecutionBackend"/> then reports Interpreted.
    /// </remarks>
    /// <exception cref="NotSupportedException">The pattern's flavor has no compiled kernels, or it already has them.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public RegexPattern WithGeneratedKernels(ulong fingerprint, CoreKernel plain, CoreKernel capture)
    {
        ArgumentNullException.ThrowIfNull(plain);
        ArgumentNullException.ThrowIfNull(capture);
        if (Executor is PosixWorkspaceExecutor)
        {
            throw new NotSupportedException("POSIX patterns have no compiled kernels.");
        }

        if (Executor is CompiledRegularExecutor { Lease: null }
            or EcmaCompiledExecutor { Lease: null }
            or XmlCompiledExecutor { Lease: null }
            or Pcre2CompiledExecutor { Lease: null })
        {
            throw new NotSupportedException("This pattern already has compiled or generated kernels.");
        }

        var unwrapped = Executor switch
        {
            CompiledRegularExecutor c => (IRegularExecutor)c.Interpreter,
            EcmaCompiledExecutor c => c.Interpreter,
            XmlCompiledExecutor c => c.Interpreter,
            Pcre2CompiledExecutor c => c.Interpreter,
            var other => other
        };
        var existingLease = Executor switch
        {
            CompiledRegularExecutor c => c.Lease,
            EcmaCompiledExecutor c => c.Lease,
            XmlCompiledExecutor c => c.Lease,
            Pcre2CompiledExecutor c => c.Lease,
            _ => null
        };
        if (!KernelPlanner.TryPlan(this, out var plan, out _) || plan!.Fingerprint() != fingerprint)
        {
            if (ReferenceEquals(unwrapped, Executor))
            {
                return this;
            }

            existingLease?.Dispose();
            return new(this, unwrapped, -(existingLease?.Estimate ?? 0));
        }
        existingLease?.Dispose();
        long? extraBytes = existingLease is { } lease ? 4096 - lease.Estimate : null;
        return unwrapped switch
        {
            ThompsonExecutor interpreter => new(this, new CompiledRegularExecutor(new ThompsonExecutor(interpreter.Program, AcceleratorSwitches.Semantic), plain, capture), extraBytes),
            EcmaWorkspaceExecutor ecma => new(this, new EcmaCompiledExecutor(ecma, plain, capture), extraBytes),
            XmlWorkspaceExecutor xml => new(this, new XmlCompiledExecutor(xml, plain, capture), extraBytes),
            Pcre2WorkspaceExecutor pcre2 => new(this, new Pcre2CompiledExecutor(pcre2, plain, capture), extraBytes),
            _ => throw new NotSupportedException("This pattern already has compiled or generated kernels.")
        };
    }
}
