using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using GregsEx;

internal static class Program
{
    private static int Main()
    {
        var dynamicCode = RuntimeFeature.IsDynamicCodeSupported;
        Console.WriteLine($"AotDiagnostics: dynamicCode={dynamicCode}");
        VerifyExplicitCompileCalls(dynamicCode);
        Console.WriteLine("AotDiagnostics PASS");
        return 0;
    }

#if !UNSUPPRESSED_AOT_DIAGNOSTICS
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Negative runtime fixture intentionally invokes explicit Compile() under NativeAOT to verify PlatformNotSupportedException.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Negative runtime fixture intentionally invokes explicit Compile() under NativeAOT to verify PlatformNotSupportedException.")]
#endif
    private static void VerifyExplicitCompileCalls(bool dynamicCode)
    {
        var opts = new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted);
        var core = RegexPattern.Parse("ab+c", new RegularCoreProfile(), opts);
        var ecma = RegexPattern.Parse("ab+c", new EcmaScriptProfile(), opts);
        var xsd = RegexPattern.Parse("ab+c", new XsdProfile(), opts);
        var pcre2 = RegexPattern.Parse("ab+c", new Pcre2Profile(), opts);

        if (!dynamicCode)
        {
            ExpectPlatformNotSupported(() => _ = core.Compile(), "RegularCore.Compile()");
            ExpectPlatformNotSupported(() => _ = ecma.Compile(), "EcmaScript.Compile()");
            ExpectPlatformNotSupported(() => _ = xsd.Compile(), "Xsd.Compile()");
            ExpectPlatformNotSupported(() => _ = pcre2.Compile(), "Pcre2.Compile()");
            return;
        }

        var cCore = core.Compile();
        Check(cCore.ExecutionBackend == RegexExecutionBackend.Compiled && cCore.IsMatch("abbbc"), "RegularCore.Compile() JIT");
        var cEcma = ecma.Compile();
        Check(cEcma.ExecutionBackend == RegexExecutionBackend.Compiled && cEcma.IsMatch("abbbc"), "EcmaScript.Compile() JIT");
        var cXsd = xsd.Compile();
        Check(cXsd.ExecutionBackend == RegexExecutionBackend.Compiled && cXsd.IsMatch("abbbc"), "Xsd.Compile() JIT");
        var cPcre2 = pcre2.Compile();
        Check(cPcre2.ExecutionBackend == RegexExecutionBackend.Compiled && cPcre2.IsMatch("abbbc"), "Pcre2.Compile() JIT");
    }

    private static void ExpectPlatformNotSupported(Action action, string label)
    {
        try
        {
            action();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.WriteLine($"PASS {label} threw PlatformNotSupportedException: {ex.Message}");
            return;
        }
        throw new InvalidOperationException($"Expected PlatformNotSupportedException for {label}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
