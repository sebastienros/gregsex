#nullable enable
#if !GREGSEX_GENERATOR
using GregsEx.Execution.Thompson;
using GregsEx.Flavors.EcmaScript.Integration;
using GregsEx.Flavors.Pcre2.Integration;
using GregsEx.Flavors.Posix.Integration;
using GregsEx.Flavors.XmlIntegration;

namespace GregsEx.Execution.Compiled;

// One analysis entry point for every flavor. It applies the same eligibility and benefit rules as Compile() but allocates
// no kernel memory and emits no code, so it is the shared front end of the build-time source generator.
internal static class KernelPlanner
{
    internal static bool TryPlan(RegexPattern pattern, out CoreKernelPlan? plan, out string reason)
    {
        switch (pattern.Executor)
        {
            case ThompsonExecutor thompson:
                return CompiledBackend.TryPlan(thompson, out plan, out reason);
            case CompiledRegularExecutor compiledCore:
                return CompiledBackend.TryPlan(compiledCore.Interpreter, out plan, out reason);
            case EcmaWorkspaceExecutor ecma:
                return EcmaCompiledBackend.TryPlan(ecma, out plan, out reason);
            case EcmaCompiledExecutor compiledEcma:
                return EcmaCompiledBackend.TryPlan(compiledEcma.Interpreter, out plan, out reason);
            case XmlWorkspaceExecutor xml:
                return XmlCompiledBackend.TryPlan(xml, out plan, out reason);
            case XmlCompiledExecutor compiledXml:
                return XmlCompiledBackend.TryPlan(compiledXml.Interpreter, out plan, out reason);
            case Pcre2WorkspaceExecutor pcre2:
                return Pcre2CompiledBackend.TryPlan(pcre2, out plan, out reason);
            case Pcre2CompiledExecutor compiledPcre2:
                return Pcre2CompiledBackend.TryPlan(compiledPcre2.Interpreter, out plan, out reason);
            case PosixWorkspaceExecutor:
                plan = null;
                reason = "POSIX patterns are not compiled: leftmost-longest semantics are not modelled by the compiled kernel.";
                return false;
            default:
                plan = null;
                reason = "The pattern already has compiled or generated kernels.";
                return false;
        }
    }
}
#endif
