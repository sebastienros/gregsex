using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx;
using GregsEx.SourceGeneration;
using System.CommandLine;
using GregsEx.Tests.Integration.EcmaScript;

var cli = new RootCommand("Finite GregsEx semantic correctness probes.");
var measure = new Option<bool>("--measure-allocations") { Description = "Retired option: allocation acceptance now uses BenchmarkDotNet only." };
cli.Add(measure);
cli.SetAction(result =>
{
    var allocation = result.GetValue(measure);
    if (allocation)
    {
        throw new InvalidOperationException("Custom allocation measurement is retired; use AllocationPolicy benchmarks in benchmarks/GregsEx.Benchmarks.");
    }

    {
        var directory = Path.Combine(AppContext.BaseDirectory, "corpora");
        var total = 0;
        foreach (var (file, count) in new[] { ("oracle-corpus.json", 939), ("imported-corpus.json", 1381), ("contract-corpus.json", 27), ("resource-corpus.json", 10) })
        {
            var actual = ProductCorpusRunner.Run(directory, file, Console.WriteLine);
            Check(actual == count, "native corpus count");
            total += actual;
            Console.WriteLine($"Native corpus {file} PASS cases={actual}");
        }
        ProductCorpusRunner.RawFlag(directory);
        Console.WriteLine($"Native corpus PASS total={total} rawFlag=1");
    }
    RunCore(allocation);
    Check(EcmaSmoke.Run(allocation, Console.WriteLine) == 36, "ECMAScript probe count");
    RunXml();
    RunPosix();
    RunPcre2();
    RunAutoAndGenerated();
    UnicodeVersionSmoke.Run();
});
return cli.Parse(args).Invoke();

static bool IsAutoCompilationExpected() =>
    RuntimeFeature.IsDynamicCodeSupported &&
    RuntimeFeature.IsDynamicCodeCompiled &&
    AppContext.TryGetSwitch("GregsEx.RuntimeCompilation.Auto", out var enabled) &&
    enabled;

static void RunCore(bool allocation)
{
    Console.WriteLine($"Core smoke: RID={RuntimeInformation.RuntimeIdentifier}; runtime={RuntimeInformation.FrameworkDescription}; dynamic={RuntimeFeature.IsDynamicCodeSupported}; compiled={RuntimeFeature.IsDynamicCodeCompiled}; autoSwitch={IsAutoCompilationExpected()}");
    long totalChecksum = 0;
    var probes = 0;
    foreach (var (mode, kind) in new (RegularCoreMode, RegexInputKind)[]
    {
    (RegularCoreMode.UnicodeScalars,RegexInputKind.Utf16), (RegularCoreMode.UnicodeScalars,RegexInputKind.Utf8),
    (RegularCoreMode.Utf16CodeUnits,RegexInputKind.Utf16), (RegularCoreMode.RawBytes,RegexInputKind.Bytes)
    })
    {
        foreach (var policy in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
        {
            var profile = new RegularCoreProfile(mode);
            foreach (var scenario in new[] { "positive", "miss", "empty", "native-width" })
            {
                var source = scenario == "empty" ? "()" : scenario == "native-width" ? "(.)" : "(a+)(b)?";
                var p = RegexPattern.Parse(source, profile, new(kind, policy));
                if (policy == RegexExecutionMode.Interpreted || kind != RegexInputKind.Utf16 || !IsAutoCompilationExpected())
                {
                    Check(p.ExecutionBackend == RegexExecutionBackend.Interpreted, "Interpreted/non-UTF16/AOT selects interpreter");
                }
                using var w = p.CreateWorkspace();
                var chars = (scenario switch { "positive" => "xaaab", "miss" => "zzz", "empty" => "x😀", _ => "x😀" }).ToCharArray();
                byte[] bytes = scenario switch { "positive" => [120, 97, 97, 97, 98], "miss" => [122, 122, 122], _ => [120, 240, 159, 152, 128] };
                if (mode == RegularCoreMode.RawBytes && scenario == "native-width")
                {
                    bytes = [120, 255];
                }

                var start = scenario == "native-width" ? 1 : 0;
                var expectedIndex = scenario is "positive" or "native-width" ? 1 : 0;
                var expectedLength = scenario == "positive" ? 4 : scenario == "native-width" ? (mode == RegularCoreMode.UnicodeScalars ? (kind == RegexInputKind.Utf8 ? 4 : 2) : 1) : 0;
                var expectedStatus = scenario == "miss" ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match;
                var groups = new RegexCapture[p.GroupCount + 1];
                groups[^1] = new(true, 999, 999);
                var status = kind == RegexInputKind.Utf16 ? w.FindCaptures(chars, start, groups, out var r) : w.FindCaptures(bytes, start, groups, out r);
                Check(status == expectedStatus, "capture status");
                Check(r == (scenario == "miss" ? default : new RegexMatchRange(expectedIndex, expectedLength)), "native range");
                Check(groups[0] == (scenario == "miss" ? default : new RegexCapture(true, expectedIndex, expectedLength)), "group zero");
                Check(groups[^1] == new RegexCapture(true, 999, 999), "excess output untouched");
                Check((kind == RegexInputKind.Utf16 ? p.IsMatch(chars) : p.IsMatch(bytes)) == (scenario != "miss"), "convenience agrees");
                var expected = Cycle(w, kind, mode, chars, bytes, start, groups);
                Check(Cycle(w, kind, mode, chars, bytes, start, groups) == expected, "repeat checksum");
                totalChecksum += expected;
                probes++;
            }
            using var empty = RegexPattern.Parse("", profile, new(kind, policy)).CreateWorkspace();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Check((kind == RegexInputKind.Utf16 ? empty.IsMatch("", options: new() { CancellationToken = cancellation.Token }) : empty.IsMatch(ReadOnlySpan<byte>.Empty, options: new() { CancellationToken = cancellation.Token })) == RegexSearchStatus.Cancelled, "empty cancellation");
            Check(empty.LastFailure.Code == RegexExecutionFailureCode.Cancelled, "cancellation failure");
        }
    }

    // Input-domain and admission precedence/recovery, distinct work/capacity/memory failures.
    var scalar = new RegularCoreProfile();
    using (var w = RegexPattern.Parse("a", scalar, new(RegexInputKind.Utf8)).CreateWorkspace())
    {
        Check(w.Find(new byte[] { 97, 255 }, 0, out _) == RegexSearchStatus.InvalidInput, "bad UTF8 tail before early match");
        Check(w.LastFailure.Code == RegexExecutionFailureCode.InvalidEncoding && w.LastFailure.Offset == 1, "first malformed offset");
        Check(w.Find("😀"u8, 1, out _) == RegexSearchStatus.InvalidInput, "interior boundary");
        Check(w.LastFailure.Code == RegexExecutionFailureCode.InvalidStartBoundary, "boundary failure");
        Check(w.Find("a"u8, 0, out _) == RegexSearchStatus.Match, "reuse after UTF8 errors");
    }
    using (var w = RegexPattern.Parse("a", scalar).CreateWorkspace())
    {
        Check(w.Find(new char[] { 'a', '\uD800' }, 0, out _) == RegexSearchStatus.InvalidInput, "bad UTF16 tail");
        Check(w.LastFailure.Offset == 1, "UTF16 malformed offset");
        Check(w.IsMatch("a", options: new() { Limits = new(1) }) == RegexSearchStatus.LimitExceeded, "work failure");
        Check(w.LastFailure.Code == RegexExecutionFailureCode.WorkLimitExceeded, "distinct work failure");
        Check(w.IsMatch("a") == RegexSearchStatus.Match, "work recovery");
    }
    var capturePattern = RegexPattern.Parse("(a)(b)?", scalar);
    using (var w = capturePattern.CreateWorkspace(new()))
    {
        RegexCapture[] groups = [new(true, 9, 9), new(true, 9, 9), new(true, 9, 9), new(true, 9, 9)];
        Check(w.FindCaptures(new char[] { '\uD800' }, 0, groups.AsSpan(0, 1), out var r) == RegexSearchStatus.DestinationTooSmall, "destination precedence");
        Check(r == default && groups[0] == new RegexCapture(true, 9, 9) && w.LastFailure.RequiredCapacity == 3 && w.LastFailure.WorkConsumed == 0, "short destination untouched");
        Check(w.FindCaptures("a", 0, groups, out _) == RegexSearchStatus.LimitExceeded, "capacity failure");
        Check(w.LastFailure.Code == RegexExecutionFailureCode.WorkspaceCapacityExceeded && groups[0] == default && groups[^1] == new RegexCapture(true, 9, 9), "logical clear");
        w.Reserve(capturePattern.CaptureCapacity);
        Check(w.FindCaptures("a", 0, groups, out r) == RegexSearchStatus.Match && groups[1] == new RegexCapture(true, 0, 1) && groups[2] == default, "capture recovery");
        var before = w.Capacity;
        try
        { w.Reserve(new(int.MaxValue, int.MaxValue, int.MaxValue)); throw new InvalidOperationException("Expected reserve rejection."); }
        catch (RegexExecutionException ex) { Check(ex.Failure.Code == RegexExecutionFailureCode.WorkspaceLimitExceeded, "reserve memory failure"); }
        Check(w.Capacity == before && w.IsMatch("a") == RegexSearchStatus.Match, "transactional reservation");
        w.Dispose();
        w.Dispose();
        try
        { w.IsMatch("a"); throw new InvalidOperationException("Expected disposed rejection."); }
        catch (ObjectDisposedException) { }
    }
    Check(RegexPattern.TryParse(new byte[] { 255 }, new RegularCoreProfile(RegularCoreMode.RawBytes), out var raw, out _, new(RegexInputKind.Bytes)), "byte pattern factory");
    Check(raw!.IsMatch(new byte[] { 0, 255 }), "exact raw byte");
    Check(RegexPattern.Parse("^.$", new RegularCoreProfile(RegularCoreMode.Utf16CodeUnits)).IsMatch(new char[] { '\uD800' }), "code-unit lone surrogate admitted");
    try
    { RegexPattern.Parse("a", scalar).IsMatch(new char[] { 'a', '\uD800' }); throw new InvalidOperationException("Expected convenience encoding exception."); }
    catch (RegexExecutionException ex) { Check(ex.Failure.Code == RegexExecutionFailureCode.InvalidEncoding && ex.Failure.Offset == 1, "convenience failure distinct from false"); }
    Check(!RegexPattern.TryParse("\\1".AsSpan(), scalar, out _, out var diagnostic) && diagnostic.Code == RegexDiagnosticCode.UnsupportedFeature, "unsupported diagnostic");
    Console.WriteLine($"PASS probes={probes}; totalChecksum={totalChecksum}; all statuses, native offsets and reuse passed; allocation={allocation}.");
}

static void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException(label);
    }
}

static long Cycle(RegexWorkspace w, RegexInputKind kind, RegularCoreMode mode, char[] chars, byte[] bytes, int start, RegexCapture[] groups)
{
    var utf16 = kind == RegexInputKind.Utf16;
    long sum = (int)(utf16 ? w.IsMatch(chars, start) : w.IsMatch(bytes, start));
    sum += (int)(utf16 ? w.Find(chars, start, out var r) : w.Find(bytes, start, out r));
    sum += r.Index + 17L * r.Length;
    sum += (int)(utf16 ? w.FindCaptures(chars, start, groups, out r) : w.FindCaptures(bytes, start, groups, out r));
    for (var i = 0; i < groups.Length - 1; i++)
    {
        sum += (groups[i].Participated ? 1 : 0) + groups[i].Index + 31L * groups[i].Length;
    }
    // Full-subject validation occurs at every Find; no mutable-input validity cache.
    int next = 0, length = utf16 ? chars.Length : bytes.Length;
    while ((utf16 ? w.Find(chars, next, out r) : w.Find(bytes, next, out r)) == RegexSearchStatus.Match)
    {
        sum += r.Index + 7L * r.Length + 1;
        if (r.Length != 0)
        {
            next = r.Index + r.Length;
        }
        else if (r.Index == length)
        {
            break;
        }
        else if (mode != RegularCoreMode.UnicodeScalars)
        {
            next = r.Index + 1;
        }
        else if (utf16)
        {
            next = r.Index + (char.IsHighSurrogate(chars[r.Index]) ? 2 : 1);
        }
        else
        { var lead = bytes[r.Index]; next = r.Index + (lead < 128 ? 1 : lead < 224 ? 2 : lead < 240 ? 3 : 4); }
    }
    return sum;
}

static void RunXml()
{
    foreach (var mode in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
    {
        Check(XsdFunctions.IsValid("bcdf", "[a-z-[aeiou]]+", new(RegexInputKind.Utf16, mode)), "XSD subtraction match");
        Check(!XsdFunctions.IsValid("bcef", "[a-z-[aeiou]]+", new(RegexInputKind.Utf16, mode)), "XSD subtraction miss");
        var xsdUtf8 = RegexPattern.Parse("(\\i\\c*)", new XsdProfile(), new(RegexInputKind.Utf8, mode));
        Check(xsdUtf8.IsMatch("item_1"u8), "XSD UTF8 match");
        Check(!xsdUtf8.IsMatch("1item"u8), "XSD UTF8 miss");

        Check(XPathFunctions.Matches("Hello World", "world", "i", new(RegexInputKind.Utf16, mode)), "XPath Matches i");
        Check(XPathFunctions.Replace("a1b2c3", "([a-z])([0-9])", "$2$1", "", new(RegexInputKind.Utf16, mode)) == "1a2b3c", "XPath Replace");
        var tokens = XPathFunctions.Tokenize("alpha,beta,,gamma", ",", "", new(RegexInputKind.Utf16, mode));
        Check(tokens.Count == 4 && tokens[0] == "alpha" && tokens[2] == "" && tokens[3] == "gamma", "XPath Tokenize");
        var analyzed = XPathFunctions.AnalyzeString("ab", "((a)|b)+", "", new(RegexInputKind.Utf16, mode));
        Check(analyzed.Nodes.Count > 0 && analyzed.Nodes[0].Kind == XPathAnalyzeNodeKind.Match, "XPath AnalyzeString capture containment");
    }
    Console.WriteLine("XML (XSD + XPath) smoke PASS");
}

static void RunPosix()
{
    Span<PosixMatch> matches = stackalloc PosixMatch[1];
    Span<PosixMatch> breMatches = stackalloc PosixMatch[2];
    foreach (var mode in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
    {
        var ere = PosixFunctions.Parse("a|ab"u8, new PosixProfile(PosixDialect.Extended), new(RegexInputKind.Bytes, mode));
        Check(ere.ExecutionBackend == RegexExecutionBackend.Interpreted, "POSIX always interpreted");
        using var ereWs = ere.CreateWorkspace();
        Check(PosixFunctions.RegExec(ereWs, "ab"u8, matches) == RegexSearchStatus.Match, "POSIX ERE match");
        Check(matches[0].Start == 0 && matches[0].End == 2, "POSIX leftmost-longest");

        var bre = PosixFunctions.Parse("\\([[:alpha:]]\\{1,\\}\\)=\\1"u8, new PosixProfile(PosixDialect.Basic), new(RegexInputKind.Bytes, mode));
        using var breWs = bre.CreateWorkspace();
        Check(PosixFunctions.RegExec(breWs, "foo=foo"u8, breMatches) == RegexSearchStatus.Match, "POSIX BRE backref match");
        Check(breMatches[1].Start == 0 && breMatches[1].End == 3, "POSIX BRE submatch");
    }
    Span<byte> errBuf = stackalloc byte[32];
    Check(PosixFunctions.RegError(PosixErrorCode.NoMatch, errBuf) > 0, "POSIX RegError");
    Console.WriteLine("POSIX (BRE + ERE) smoke PASS");
}

static void RunPcre2()
{
    foreach (var mode in new[] { RegexExecutionMode.Interpreted, RegexExecutionMode.Auto })
    {
        var pcreUtf16 = RegexPattern.Parse("(?<k>[a-z]+)=(?<v>[0-9]+)", new Pcre2Profile(), new(RegexInputKind.Utf16, mode));
        using var ws16 = pcreUtf16.CreateWorkspace();
        var caps = new RegexCapture[pcreUtf16.GroupCount];
        Check(ws16.FindCaptures("id=42", 0, caps, out var range) == RegexSearchStatus.Match, "PCRE2 generic UTF16 match");
        Check(range == new RegexMatchRange(0, 5) && caps[1] == new RegexCapture(true, 0, 2) && caps[2] == new RegexCapture(true, 3, 2), "PCRE2 generic captures");

        var pcreUtf8 = RegexPattern.Parse("([a-z]+)-(\\d+)", new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf), new(RegexInputKind.Utf8, mode));
        Check(pcreUtf8.IsMatch("item-99"u8), "PCRE2 generic UTF8 match");
    }

    var native16 = Pcre2Functions.Parse("^(a(?1)?z)$", new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen));
    using var nativeWs16 = native16.CreateWorkspace();
    Span<Pcre2OffsetPair> pairs16 = stackalloc Pcre2OffsetPair[native16.GroupCount];
    Check(nativeWs16.Match("aaazzz", 0, pairs16).Status == Pcre2MatchStatus.Match, "PCRE2 native16 recursion match");
    Check(nativeWs16.Match("aaazz", 0, pairs16).Status == Pcre2MatchStatus.NoMatch, "PCRE2 native16 recursion miss");

    var native8 = Pcre2Functions.Parse("(?<w>[a-z]+)"u8, new Pcre2Profile(Pcre2CodeUnitWidth.Eight));
    using var nativeWs8 = native8.CreateWorkspace();
    Span<byte> subOut = stackalloc byte[32];
    var subRes = nativeWs8.Substitute("hello world"u8, 0, "[$<w>]"u8, subOut, Pcre2SubstituteFlags.Global);
    Check(subRes.NativeReturnCode == 2 && subOut[..subRes.OutputUnits].SequenceEqual("[hello] [world]"u8), "PCRE2 native8 substitute");
    Console.WriteLine("PCRE2 (generic + native8/16) smoke PASS");
}

static void RunAutoAndGenerated()
{
    const string General = "((a)|b)+c";
    var expectAutoCompiled = IsAutoCompilationExpected();
    var expectedAutoBackend = expectAutoCompiled ? RegexExecutionBackend.Compiled : RegexExecutionBackend.Interpreted;

    RegexProfile[] profiles =
    [
        new RegularCoreProfile(),
        new EcmaScriptProfile(),
        new XsdProfile(),
        new XPathProfile(),
        new Pcre2Profile()
    ];

    foreach (var profile in profiles)
    {
        var interp = RegexPattern.Parse(General, profile, new(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
        var auto = RegexPattern.Parse(General, profile, new(RegexInputKind.Utf16, RegexExecutionMode.Auto));
        Check(interp.ExecutionBackend == RegexExecutionBackend.Interpreted, $"Interpreted backend for {profile.GetType().Name}");
        Check(auto.ExecutionBackend == expectedAutoBackend, $"Auto backend for {profile.GetType().Name}: expected {expectedAutoBackend}, got {auto.ExecutionBackend}");
        Check(interp.IsMatch("ababc") && auto.IsMatch("ababc"), $"Match parity for {profile.GetType().Name}");
        Check(!interp.IsMatch("ababd") && !auto.IsMatch("ababd"), $"Miss parity for {profile.GetType().Name}");
    }

    (string Name, RegexPattern Pattern, string Input, RegexMatchRange Expected)[] generated =
    [
        ("RegularCore", AotGeneratedPatterns.CoreGeneral(), "xx ababc yy", new(3, 5)),
        ("ECMAScript", AotGeneratedPatterns.EcmaGeneral(), "xx ababc yy", new(3, 5)),
        ("XSD", AotGeneratedPatterns.XsdGeneral(), "ababc", new(0, 5)),
        ("XPath", AotGeneratedPatterns.XPathGeneral(), "xx ababc yy", new(3, 5)),
        ("PCRE2", AotGeneratedPatterns.Pcre2General(), "xx ababc yy", new(3, 5))
    ];

    foreach (var (name, pattern, input, expected) in generated)
    {
        Check(pattern.ExecutionBackend == RegexExecutionBackend.Compiled, $"Generated {name} must report Compiled backend");
        using var ws = pattern.CreateWorkspace();
        var caps = new RegexCapture[pattern.GroupCount];
        Check(ws.FindCaptures(input, 0, caps, out var range) == RegexSearchStatus.Match, $"Generated {name} match");
        Check(range == expected, $"Generated {name} range");
    }
    Console.WriteLine($"Auto + GeneratedPattern smoke PASS (autoCompiled={expectAutoCompiled})");
}

internal static partial class AotGeneratedPatterns
{
    [GeneratedPattern("((a)|b)+c")]
    public static partial RegexPattern CoreGeneral();

    [GeneratedPattern("((a)|b)+c", EcmaScriptFlags.None)]
    public static partial RegexPattern EcmaGeneral();

    [GeneratedPattern("((a)|b)+c", GeneratedPatternFlavor.Xsd)]
    public static partial RegexPattern XsdGeneral();

    [GeneratedPattern("((a)|b)+c", XPathFlags.None)]
    public static partial RegexPattern XPathGeneral();

    [GeneratedPattern("((a)|b)+c", Pcre2CompileFlags.None)]
    public static partial RegexPattern Pcre2General();
}
