using BenchmarkDotNet.Attributes;
using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Release;

[MemoryDiagnoser]
[ShortRunJob]
public partial class ReleaseAllocationBenchmarks
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

    private RegexWorkspace _coreAutoWs = null!;
    private RegexWorkspace _ecmaAutoWs = null!;
    private RegexWorkspace _xsdAutoWs = null!;
    private RegexWorkspace _xpathAutoWs = null!;
    private RegexWorkspace _pcre2AutoWs = null!;
    private RegexWorkspace _posixAutoWs = null!;
    private RegexWorkspace _coreGenWs = null!;
    private RegexWorkspace _ecmaBackrefWs = null!;
    private RegexCapture[] _captures = null!;
    private RegexCapture[] _backrefCaptures = null!;
    private PosixMatch[] _posixMatches = null!;
    private byte[] _posixInput = null!;

    [GlobalSetup]
    public void Setup()
    {
        AppContext.SetSwitch("GregsEx.RuntimeCompilation.Auto", true);
        var opts = new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Auto);
        var coreAuto = RegexPattern.Parse("((a)|b)+c", new RegularCoreProfile(), opts);
        var ecmaAuto = RegexPattern.Parse("((a)|b)+c", new EcmaScriptProfile(), opts);
        var xsdAuto = RegexPattern.Parse("((a)|b)+c", new XsdProfile(), opts);
        var xpathAuto = RegexPattern.Parse("((a)|b)+c", new XPathProfile(XPathFlags.IgnoreCase), opts);
        var pcre2Auto = RegexPattern.Parse("((a)|b)+c", new Pcre2Profile(), opts);
        var posixAuto = PosixFunctions.Parse("a|ab"u8, new PosixProfile(PosixDialect.Extended), new(RegexInputKind.Bytes, RegexExecutionMode.Auto));
        var ecmaBackref = RegexPattern.Parse("([a-z]+)=\\1", new EcmaScriptProfile(), opts);

        _coreAutoWs = coreAuto.CreateWorkspace();
        _ecmaAutoWs = ecmaAuto.CreateWorkspace();
        _xsdAutoWs = xsdAuto.CreateWorkspace();
        _xpathAutoWs = xpathAuto.CreateWorkspace();
        _pcre2AutoWs = pcre2Auto.CreateWorkspace();
        _posixAutoWs = posixAuto.CreateWorkspace();
        _coreGenWs = GenCore().CreateWorkspace();
        _ecmaBackrefWs = ecmaBackref.CreateWorkspace();
        _captures = new RegexCapture[coreAuto.GroupCount];
        _backrefCaptures = new RegexCapture[ecmaBackref.GroupCount];
        _posixMatches = new PosixMatch[posixAuto.GroupCount];
        _posixInput = "xx ab yy"u8.ToArray();

        // Warm all admitted paths outside measured methods per PERF-016.
        _ = _coreAutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);
        _ = _ecmaAutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);
        _ = _xsdAutoWs.IsMatch("ababc");
        _ = _xpathAutoWs.FindCaptures("xx ABABC yy", 0, _captures, out _);
        _ = _pcre2AutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);
        _ = PosixFunctions.RegExec(_posixAutoWs, _posixInput, _posixMatches);
        _ = _coreGenWs.FindCaptures("xx ababc yy", 0, _captures, out _);
        _ = _ecmaBackrefWs.FindCaptures("xx foo=bar yy abc=abc zz", 0, _backrefCaptures, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _coreAutoWs.Dispose();
        _ecmaAutoWs.Dispose();
        _xsdAutoWs.Dispose();
        _xpathAutoWs.Dispose();
        _pcre2AutoWs.Dispose();
        _posixAutoWs.Dispose();
        _coreGenWs.Dispose();
        _ecmaBackrefWs.Dispose();
    }

    [Benchmark]
    public RegexSearchStatus RegularCoreAutoCaptures() =>
        _coreAutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus EcmaScriptAutoCaptures() =>
        _ecmaAutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus XsdAutoIsMatch() =>
        _xsdAutoWs.IsMatch("ababc");

    [Benchmark]
    public RegexSearchStatus XPathAutoCaptures() =>
        _xpathAutoWs.FindCaptures("xx ABABC yy", 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus Pcre2AutoCaptures() =>
        _pcre2AutoWs.FindCaptures("xx ababc yy", 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus PosixAutoRegExec() =>
        PosixFunctions.RegExec(_posixAutoWs, _posixInput, _posixMatches);

    [Benchmark]
    public RegexSearchStatus GeneratedRegularCoreCaptures() =>
        _coreGenWs.FindCaptures("xx ababc yy", 0, _captures, out _);

    [Benchmark]
    public RegexSearchStatus EcmaFallbackBackrefCaptures() =>
        _ecmaBackrefWs.FindCaptures("xx foo=bar yy abc=abc zz", 0, _backrefCaptures, out _);

    [Benchmark]
    public long LifecycleParseAutoAndCreateWorkspace()
    {
        var pattern = RegexPattern.Parse("((a)|b)+c", new RegularCoreProfile(), new(RegexInputKind.Utf16, RegexExecutionMode.Auto));
        using var ws = pattern.CreateWorkspace();
        return pattern.RetainedBytes + ws.ReservedBytes;
    }
}
