using BenchmarkDotNet.Attributes;
using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Compiled;

public sealed record LookaroundScenario(string Name, string Flavor, string Engine)
{
    public override string ToString() => $"{Name}:{Flavor}:{Engine}";
}

/// <summary>
/// Lookaround and \G patterns through the three execution tiers (interpreter, <c>Compile()</c>, <c>[GeneratedPattern]</c>) for
/// the ECMAScript and PCRE2 flavors. The TextMate shapes are C# grammar patterns (as Shiki's oniguruma-to-es converts them for
/// ECMAScript, and as PCRE2 reads the Oniguruma source); the synthetic shapes isolate one lookaround form each. Every pattern is
/// searched for every match per line the way a tokenizer drives lastIndex, over this repository's C# sources. Setup cross-checks
/// the match count of every engine against the interpreter.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public partial class CompiledLookaroundBenchmarks
{
    // Name, ECMAScript pattern (flags dgv), PCRE2 pattern (null when the flavor has no equivalent).
    public static readonly (string Name, string Es, string? Pcre2)[] Shapes =
    [
        ("tm-element-access", @"(?:(?:(\?)\p{space}*)?(\.)\p{space}*|(->)\p{space}*)?(?:(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)\p{space}*)?(?:(\?)\p{space}*)?(?=\[)",
            @"(?:(?:(\?)\s*)?(\.)\s*|(->)\s*)?(?:(@?[_[:alpha:]][_[:alnum:]]*)\s*)?(?:(\?)\s*)?(?=\[)"),
        ("tm-lookahead-bracket", @"(?=\[)", @"(?=\[)"),
        ("tm-member-ident", @"(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)(?=\p{space}*(?:(?:\?\p{space}*)?\.|->)\p{space}*@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)",
            @"(@?[_[:alpha:]][_[:alnum:]]*)(?=\s*(?:(?:\?\s*)?\.|->)\s*@?[_[:alpha:]][_[:alnum:]]*)"),
        ("tm-comment", @"(^\p{space}+)?(//)[^\n]*(?=\n?$)", @"(^\s+)?(//)[^\n]*(?=\n?$)"),
        ("keyword-lookbehind", @"(?<=\s|^)(?:var|int|string|void)(?=\s)", @"(?<=\s|^)(?:var|int|string|void)(?=\s)"),
        ("ident-not-prefixed", @"\b(?!_)[a-z]+(?=\()", @"\b(?!_)[a-z]+(?=\()"),
        ("word-before-dot", @"[a-zA-Z]+(?=\.)", @"[a-zA-Z]+(?=\.)"),
        ("sticky-token", @"\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])", @"\G\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])"),
    ];

    public static readonly string[] Engines = ["interpreted", "compiled", "codegen"];

    [ParamsSource(nameof(Scenarios))]
    public LookaroundScenario Scenario { get; set; } = null!;

    public static IEnumerable<LookaroundScenario> Scenarios()
    {
        foreach (var (name, _, pcre2) in Shapes)
        {
            foreach (var flavor in new[] { "ecma", "pcre2" })
            {
                if (flavor == "pcre2" && pcre2 is null)
                {
                    continue;
                }

                foreach (var engine in Engines)
                {
                    yield return new LookaroundScenario(name, flavor, engine);
                }
            }
        }
    }

    private string[] _lines = null!;
    private RegexWorkspace _workspace = null!;
    private RegexCapture[] _groups = null!;
    private bool _sticky;

    [GlobalSetup]
    public void Setup()
    {
        _lines = EcmaScript.TextMateLineSearch.LoadLines();
        var pattern = Create(Scenario.Engine, Scenario.Name, Scenario.Flavor);
        _sticky = Scenario.Name == "sticky-token";
        _workspace = pattern.CreateWorkspace(new RegexWorkspaceOptions(default, allowGrowth: true));
        _groups = new RegexCapture[pattern.GroupCount];
        var expectedBackend = Scenario.Engine == "interpreted" ? RegexExecutionBackend.Interpreted : RegexExecutionBackend.Compiled;
        if (pattern.ExecutionBackend != expectedBackend)
        {
            throw new InvalidOperationException($"{Scenario}: backend is {pattern.ExecutionBackend}.");
        }

        var oracle = Create("interpreted", Scenario.Name, Scenario.Flavor);
        var oracleSpace = oracle.CreateWorkspace(new RegexWorkspaceOptions(default, allowGrowth: true));
        var oracleGroups = new RegexCapture[oracle.GroupCount];
        long expected = Run(oracleSpace, oracleGroups), actual = Run();
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with the interpreter's {expected}.");
        }
    }

    [Benchmark]
    public long Run() => Run(_workspace, _groups);

    private long Run(RegexWorkspace workspace, RegexCapture[] groups)
    {
        long sum = 0;
        foreach (var line in _lines)
        {
            var p = 0;
            while (p <= line.Length && workspace.FindCaptures(line, p, groups, out var range) == RegexSearchStatus.Match)
            {
                sum += 1 + range.Index + range.Length;
                p = range.Index + Math.Max(1, range.Length);
                if (_sticky && range.Length == 0)
                {
                    break;
                }
            }
        }

        return sum;
    }

    private static RegexPattern Create(string engine, string name, string flavor)
    {
        if (engine == "codegen")
        {
            return GeneratedLookaroundShapes.Map[(name, flavor)]();
        }

        var (_, es, pcre2) = Shapes.First(s => s.Name == name);
        var limits = new RegexMatchLimits(20_000_000_000L, 1L << 30);
        var options = new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits);
        RegexPattern pattern;
        if (flavor == "ecma")
        {
            // The sticky shape drives lastIndex, so it uses the y flag where PCRE2 uses \G.
            EcmaScriptProfile.TryParseFlags(name == "sticky-token" ? "dgvy" : "dgv", out var profile, out _);
            pattern = RegexPattern.Parse(es, profile!, options);
        }
        else
        {
            pattern = RegexPattern.Parse(pcre2!, new Pcre2Profile(), options);
        }

        return engine == "compiled" ? pattern.Compile() : pattern;
    }
}

// The lookaround shapes compiled by the GregsEx source generator at build time (the "codegen" engine).
internal static partial class GeneratedLookaroundShapes
{
    private const EcmaScriptFlags Es = EcmaScriptFlags.HasIndices | EcmaScriptFlags.Global | EcmaScriptFlags.UnicodeSets;

    [GeneratedPattern(@"(?:(?:(\?)\p{space}*)?(\.)\p{space}*|(->)\p{space}*)?(?:(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)\p{space}*)?(?:(\?)\p{space}*)?(?=\[)", Es)]
    private static partial RegexPattern EsElementAccess();
    [GeneratedPattern(@"(?:(?:(\?)\s*)?(\.)\s*|(->)\s*)?(?:(@?[_[:alpha:]][_[:alnum:]]*)\s*)?(?:(\?)\s*)?(?=\[)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreElementAccess();
    [GeneratedPattern(@"(?=\[)", Es)]
    private static partial RegexPattern EsLookaheadBracket();
    [GeneratedPattern(@"(?=\[)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreLookaheadBracket();
    [GeneratedPattern(@"(@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)(?=\p{space}*(?:(?:\?\p{space}*)?\.|->)\p{space}*@?[_\p{Alpha}][_\p{Alpha}\p{Nd}]*)", Es)]
    private static partial RegexPattern EsMemberIdent();
    [GeneratedPattern(@"(@?[_[:alpha:]][_[:alnum:]]*)(?=\s*(?:(?:\?\s*)?\.|->)\s*@?[_[:alpha:]][_[:alnum:]]*)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreMemberIdent();
    [GeneratedPattern(@"(^\p{space}+)?(//)[^\n]*(?=\n?$)", Es)]
    private static partial RegexPattern EsComment();
    [GeneratedPattern(@"(^\s+)?(//)[^\n]*(?=\n?$)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreComment();
    [GeneratedPattern(@"(?<=\s|^)(?:var|int|string|void)(?=\s)", Es)]
    private static partial RegexPattern EsKeywordLookbehind();
    [GeneratedPattern(@"(?<=\s|^)(?:var|int|string|void)(?=\s)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreKeywordLookbehind();
    [GeneratedPattern(@"\b(?!_)[a-z]+(?=\()", Es)]
    private static partial RegexPattern EsIdentNotPrefixed();
    [GeneratedPattern(@"\b(?!_)[a-z]+(?=\()", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreIdentNotPrefixed();
    [GeneratedPattern(@"[a-zA-Z]+(?=\.)", Es)]
    private static partial RegexPattern EsWordBeforeDot();
    [GeneratedPattern(@"[a-zA-Z]+(?=\.)", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreWordBeforeDot();
    [GeneratedPattern(@"\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])", Es | EcmaScriptFlags.Sticky)]
    private static partial RegexPattern EsStickyToken();
    [GeneratedPattern(@"\G\s*([a-zA-Z_]+|\d+|[^\sa-zA-Z_\d])", Pcre2CompileFlags.None)]
    private static partial RegexPattern PcreStickyToken();

    internal static readonly Dictionary<(string Name, string Flavor), Func<RegexPattern>> Map = new()
    {
        [("tm-element-access", "ecma")] = EsElementAccess,
        [("tm-element-access", "pcre2")] = PcreElementAccess,
        [("tm-lookahead-bracket", "ecma")] = EsLookaheadBracket,
        [("tm-lookahead-bracket", "pcre2")] = PcreLookaheadBracket,
        [("tm-member-ident", "ecma")] = EsMemberIdent,
        [("tm-member-ident", "pcre2")] = PcreMemberIdent,
        [("tm-comment", "ecma")] = EsComment,
        [("tm-comment", "pcre2")] = PcreComment,
        [("keyword-lookbehind", "ecma")] = EsKeywordLookbehind,
        [("keyword-lookbehind", "pcre2")] = PcreKeywordLookbehind,
        [("ident-not-prefixed", "ecma")] = EsIdentNotPrefixed,
        [("ident-not-prefixed", "pcre2")] = PcreIdentNotPrefixed,
        [("word-before-dot", "ecma")] = EsWordBeforeDot,
        [("word-before-dot", "pcre2")] = PcreWordBeforeDot,
        [("sticky-token", "ecma")] = EsStickyToken,
        [("sticky-token", "pcre2")] = PcreStickyToken,
    };
}
