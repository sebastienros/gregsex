using GregsEx.Contracts.Acceleration;
using GregsEx.Execution.Compiled;
using GregsEx.Generator;

namespace GregsEx.SourceGeneration;

/// <summary>
/// Emits the C# source of the kernels <c>[GeneratedPattern]</c> would generate for a pattern, for source generators that
/// cannot use the attribute: a Roslyn generator never sees another generator's output, so one that turns its own inputs
/// (a TextMate grammar, say) into patterns has to produce the kernel source itself. The emitted classes are bound with
/// <c>OnigurumaPattern.WithGeneratedKernels(fingerprint, Plain.Run, Capture.Run)</c>, exactly as the generator binds its
/// own; the library recomputes the fingerprint and ignores a kernel of another GregsEx revision. This type is the
/// supported public surface of <c>GregsEx.Generator.dll</c> besides <see cref="PatternGenerator"/>, versioned with the
/// library; a generator package reaches it through the consumer's own GregsEx package
/// (<c>$(PkgGregsEx)/analyzers/dotnet/cs/GregsEx.Generator.dll</c>) so the emitting and running revisions always agree.
/// </summary>
public static class KernelSource
{
    /// <summary>
    /// Plans an Oniguruma pattern and emits its kernels as C# source.
    /// <see cref="KernelSourceStatus.Succeeded"/>: place <see cref="KernelSourceResult.PlainClass"/> (and
    /// <see cref="KernelSourceResult.CaptureClass"/> when it is not null) inside a type of the consumer, in a file that
    /// starts with <c>using System;</c> (the kernels call span extension methods), and bind with
    /// <c>pattern.WithGeneratedKernels(Fingerprint, PlainClass.Run, CaptureClass.Run)</c>, passing the plain class's
    /// <c>Run</c> twice when there is no capture class. The pattern must be parsed with the same options and compile
    /// limits. Kernels bind .NET 8+ span APIs (<c>SearchValues</c>, <c>IndexOfAnyInRange</c>); a consumer targeting an
    /// older framework gets the interpreted pattern instead, as <see cref="PatternGenerator"/> decides by checking that
    /// <c>System.MemoryExtensions.IndexOfAnyInRange</c> exists in the compilation.
    /// <see cref="KernelSourceStatus.Declined"/>: the planner leaves the pattern to the interpreter;
    /// <see cref="KernelSourceResult.DeclineReason"/> says why (the text of the generator's GREGEX003 diagnostic).
    /// <see cref="KernelSourceStatus.Invalid"/>: the pattern does not parse; <see cref="KernelSourceResult.Diagnostic"/>
    /// carries the parse error (the content of the generator's GREGEX001 diagnostic).
    /// </summary>
    /// <param name="pattern">The Oniguruma pattern text, as the consumer will pass it to <c>OnigurumaPattern.Parse</c>.</param>
    /// <param name="options">The profile options the consumer will parse with.</param>
    /// <param name="plainClassName">The name of the emitted class whose <c>Run</c> reports the overall match only.</param>
    /// <param name="captureClassName">The name of the emitted class whose <c>Run</c> also records every group's bounds.</param>
    /// <param name="compileOptions">The compile options (limits) the consumer will parse with; finite defaults when omitted.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A class name is empty, or the options are not valid for a profile.</exception>
    public static KernelSourceResult TryEmit(string pattern, OnigurumaOptions options, string plainClassName, string captureClassName,
        OnigurumaCompileOptions? compileOptions = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentException.ThrowIfNullOrEmpty(plainClassName);
        ArgumentException.ThrowIfNullOrEmpty(captureClassName);
        var profile = new OnigurumaProfile(options);
        var limits = (compileOptions ?? new OnigurumaCompileOptions()).Limits;

        // The same planning call WithGeneratedKernels makes to recompute the fingerprint, so both always agree.
        CoreKernelPlan? plan;
        string reason;
        RegexDiagnostic diagnostic;
        bool parsed;
        try
        { parsed = OnigKernelPlan.TryPlan(pattern, profile, limits, LoweringLimits.Default, out plan, out reason, out diagnostic); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            plan = null;
            reason = ex.Message;
            diagnostic = new RegexDiagnostic(RegexDiagnosticCode.InvalidSyntax, 0, 0, RegexOffsetUnit.Utf16CodeUnits, ex.Message);
            parsed = false;
        }

        if (!parsed)
        {
            return new KernelSourceResult(KernelSourceStatus.Invalid, 0, 0, null, null, null, diagnostic);
        }

        if (plan is null)
        {
            return new KernelSourceResult(KernelSourceStatus.Declined, 0, 0, null, null, reason, default);
        }

        var plain = KernelEmitter.EmitClass(plainClassName, plan, track: false);
        var capture = plan.GroupCount > 1 ? KernelEmitter.EmitClass(captureClassName, plan, track: true) : null;
        return new KernelSourceResult(KernelSourceStatus.Succeeded, plan.Fingerprint(), plan.GroupCount, plain, capture, null, default);
    }
}

/// <summary>The outcome of <see cref="KernelSource.TryEmit"/>.</summary>
public enum KernelSourceStatus
{
    /// <summary>Kernels were emitted.</summary>
    Succeeded,
    /// <summary>The pattern is valid but the planner left it to the interpreter.</summary>
    Declined,
    /// <summary>The pattern does not parse.</summary>
    Invalid
}

/// <summary>The kernel source of a pattern, or why there is none.</summary>
public sealed class KernelSourceResult
{
    internal KernelSourceResult(KernelSourceStatus status, ulong fingerprint, int groupCount, string? plainClass, string? captureClass,
        string? declineReason, RegexDiagnostic diagnostic)
    {
        Status = status;
        Fingerprint = fingerprint;
        GroupCount = groupCount;
        PlainClass = plainClass;
        CaptureClass = captureClass;
        DeclineReason = declineReason;
        Diagnostic = diagnostic;
    }

    /// <summary>Whether kernels were emitted, declined, or the pattern is invalid.</summary>
    public KernelSourceStatus Status { get; }

    /// <summary>The plan identity to pass to <c>WithGeneratedKernels</c>; zero unless <see cref="Status"/> is Succeeded.</summary>
    public ulong Fingerprint { get; }

    /// <summary>The pattern's group count including group zero; zero unless <see cref="Status"/> is Succeeded.</summary>
    public int GroupCount { get; }

    /// <summary>
    /// The C# source of a <c>private static class</c> (to nest inside a consumer type) whose <c>Run</c> method is the kernel
    /// reporting the overall match; null unless <see cref="Status"/> is Succeeded.
    /// </summary>
    public string? PlainClass { get; }

    /// <summary>
    /// The C# source of the class whose <c>Run</c> method also records every group's bounds; null unless <see cref="Status"/>
    /// is Succeeded and <see cref="GroupCount"/> is greater than one (bind the plain class's <c>Run</c> for both then).
    /// </summary>
    public string? CaptureClass { get; }

    /// <summary>Why the pattern stays on the interpreter; null unless <see cref="Status"/> is Declined.</summary>
    public string? DeclineReason { get; }

    /// <summary>The parse error; default unless <see cref="Status"/> is Invalid.</summary>
    public RegexDiagnostic Diagnostic { get; }
}
