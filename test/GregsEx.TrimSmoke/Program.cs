using System.Runtime.CompilerServices;
using GregsEx;
using GregsEx.SourceGeneration;

internal static partial class Program
{
    [GeneratedPattern("((a)|b)+c")]
    private static partial RegexPattern GenCore();

    [GeneratedPattern("((a)|b)+c", EcmaScriptFlags.None)]
    private static partial RegexPattern GenEcma();

    [GeneratedPattern("((a)|b)+c", GeneratedPatternFlavor.Xsd)]
    private static partial RegexPattern GenXsd();

    [GeneratedPattern("((a)|b)+c", XPathFlags.IgnoreCase)]
    private static partial RegexPattern GenXPath();

    [GeneratedPattern("((a)|b)+c", Pcre2CompileFlags.None)]
    private static partial RegexPattern GenPcre2();

    private static int Main()
    {
        var dynamicCode = RuntimeFeature.IsDynamicCodeSupported;
        var hasSwitch = AppContext.TryGetSwitch("GregsEx.RuntimeCompilation.Auto", out var autoEnabled);
        Console.WriteLine($"TrimSmoke: dynamicCode={dynamicCode}; hasAutoSwitch={hasSwitch}; autoEnabled={autoEnabled}");

        var autoOpts = new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto);
        var coreAuto = RegexPattern.Parse("((a)|b)+c", new RegularCoreProfile(), autoOpts);
        var ecmaAuto = RegexPattern.Parse("((a)|b)+c", new EcmaScriptProfile(), autoOpts);
        var xsdAuto = RegexPattern.Parse("((a)|b)+c", new XsdProfile(), autoOpts);
        var xpathAuto = RegexPattern.Parse("((a)|b)+c", new XPathProfile(XPathFlags.IgnoreCase), autoOpts);
        var pcre2Auto = RegexPattern.Parse("((a)|b)+c", new Pcre2Profile(), autoOpts);
        var posixAuto = PosixFunctions.Parse("a|ab"u8, new PosixProfile(PosixDialect.Extended), new(RegexInputKind.Bytes, RegexExecutionMode.Auto));

        // When published with PublishTrimmed=true (via GregsEx.targets), GregsEx.RuntimeCompilation.Auto is false
        // and ILLink substitutes CompilationContract.IsAutoCompilationSwitchEnabled to false, so Auto stays Interpreted
        // even on patterns ("((a)|b)+c") that Auto compiles under untrimmed JIT.
        var expectedAuto = (!hasSwitch || autoEnabled) && dynamicCode
            ? RegexExecutionBackend.Compiled
            : RegexExecutionBackend.Interpreted;
        Check(coreAuto.ExecutionBackend == expectedAuto, $"Trimmed Auto RegularCore expected {expectedAuto}, got {coreAuto.ExecutionBackend}");
        Check(ecmaAuto.ExecutionBackend == expectedAuto, $"Trimmed Auto EcmaScript expected {expectedAuto}, got {ecmaAuto.ExecutionBackend}");
        Check(xsdAuto.ExecutionBackend == expectedAuto, $"Trimmed Auto XSD expected {expectedAuto}, got {xsdAuto.ExecutionBackend}");
        Check(xpathAuto.ExecutionBackend == expectedAuto, $"Trimmed Auto XPath expected {expectedAuto}, got {xpathAuto.ExecutionBackend}");
        Check(pcre2Auto.ExecutionBackend == expectedAuto, $"Trimmed Auto PCRE2 expected {expectedAuto}, got {pcre2Auto.ExecutionBackend}");

        Check(posixAuto.ExecutionBackend == RegexExecutionBackend.Interpreted, "POSIX Auto should be Interpreted");
        Check(coreAuto.IsMatch("ababc") && !coreAuto.IsMatch("ababd"), "RegularCore Auto match");
        Check(ecmaAuto.IsMatch("ababc") && !ecmaAuto.IsMatch("ababd"), "ECMAScript Auto match");
        Check(xsdAuto.IsMatch("ababc") && !xsdAuto.IsMatch("ababd"), "XSD Auto match");
        Check(xpathAuto.IsMatch("ABABC") && !xpathAuto.IsMatch("ABABD"), "XPath Auto match");
        Check(pcre2Auto.IsMatch("ababc") && !pcre2Auto.IsMatch("ababd"), "PCRE2 Auto match");

        // [GeneratedPattern] always uses Compiled source-generated kernels even under PublishTrimmed=true!
        var gCore = GenCore();
        Check(gCore.ExecutionBackend == RegexExecutionBackend.Compiled && gCore.IsMatch("ababc"), "GenCore Compiled in TrimSmoke");
        var gEcma = GenEcma();
        Check(gEcma.ExecutionBackend == RegexExecutionBackend.Compiled && gEcma.IsMatch("ababc"), "GenEcma Compiled in TrimSmoke");
        var gXsd = GenXsd();
        Check(gXsd.ExecutionBackend == RegexExecutionBackend.Compiled && gXsd.IsMatch("ababc") && !gXsd.IsMatch("ababd"), "GenXsd Compiled in TrimSmoke");
        var gXPath = GenXPath();
        Check(gXPath.ExecutionBackend == RegexExecutionBackend.Compiled && gXPath.IsMatch("ABABC"), "GenXPath Compiled in TrimSmoke");
        var gPcre2 = GenPcre2();
        Check(gPcre2.ExecutionBackend == RegexExecutionBackend.Compiled && gPcre2.IsMatch("ababc"), "GenPcre2 Compiled in TrimSmoke");

        UnicodeVersionSmoke.Run();
        Console.WriteLine("TrimSmoke PASS (all Auto + GeneratedPattern verified)");
        return 0;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
