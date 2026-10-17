#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using GregsEx.Contracts.Acceleration;

namespace GregsEx;

// Frozen mode semantics. Interpreted is the reference: no accelerator may run, which makes it the
// differential oracle. Auto may select any accelerator allowed by SwitchesFor; the Compiled switch is
// present only when CompilationContract.IsAutoCompilationPermitted holds. Accelerated non-compiled execution still reports the
// Interpreted backend because no code is generated; only Compiled reports RegexExecutionBackend.Compiled.
internal static class ExecutionModePolicy
{
    internal static AcceleratorSwitches SwitchesFor(RegexExecutionMode mode)
    {
        if (mode != RegexExecutionMode.Auto)
        {
            return AcceleratorSwitches.None;
        }

        return CompilationContract.IsAutoCompilationPermitted
            ? AcceleratorSwitches.All
            : AcceleratorSwitches.Semantic;
    }

    internal static RegexExecutionBackend BackendFor(AcceleratorKind kind)
        => kind == AcceleratorKind.Compiled ? RegexExecutionBackend.Compiled : RegexExecutionBackend.Interpreted;
}

internal static class CompilationContract
{
    internal const string RequiresDynamicCodeMessage =
        "Regex compilation requires dynamic code. Use the parsed pattern directly under NativeAOT.";

    internal const string RequiresUnreferencedCodeMessage =
        "FastExpressionCompiler is not supported in trimming scenarios. Use the parsed pattern directly in trimmed applications.";

    // AppContext switch supplied by the package's build assets only for applications that are neither trimmed
    // nor NativeAOT-published. Absent or false means the FEC path may not be proven intact, so Auto stays
    // interpreted/accelerated. See docs/specs/acceleration.md.
    internal const string AutoCompilationSwitch = "GregsEx.RuntimeCompilation.Auto";

    [FeatureSwitchDefinition(AutoCompilationSwitch)]
    internal static bool IsAutoCompilationSwitchEnabled
        => AppContext.TryGetSwitch(AutoCompilationSwitch, out var enabled) && enabled;

    // Direct feature guards: both must hold before any emission path may be called.
    internal static bool IsRuntimeCompilationAvailable
        => RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled;

    internal static bool IsAutoCompilationPermitted
        => IsRuntimeCompilationAvailable && IsAutoCompilationSwitchEnabled;
}

// Compile is both dynamic-code-dependent and trim-unsafe; ordinary matching stays unannotated.
internal interface IRegexCompilation
{
    [RequiresDynamicCode(CompilationContract.RequiresDynamicCodeMessage)]
    [RequiresUnreferencedCode(CompilationContract.RequiresUnreferencedCodeMessage)]
    RegexPattern Compile();
}
