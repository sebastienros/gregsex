using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Compiled;

public sealed record Pcre2Scenario(string Name, string Kind, string Pattern, string Engine)
{
    public override string ToString() => $"{Name}:{Engine}";
}

/// <summary>
/// PCRE2 search with captures over many short strings. Compared against BCL interpreted and compiled.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ShapeConfig))]
public class CompiledPcre2Benchmarks
{
    public static readonly (string Name, string Kind, string Pattern)[] Shapes =
    [
        ("kv", "scan", "([a-z]+)=([a-z0-9]+)"),
        ("log", "scan", "(ERROR|WARN) ([a-z]+)"),
        ("ing", "scan", "[a-z]+(ing|ed)"),
        ("date", "scan", "[0-9]{4}-[0-9]{2}-[0-9]{2}x?"),
        ("email", "scan", "[a-z0-9]+@[a-z]+\\.(com|org|net)"),
        ("word", "scan", "\\b[a-z]+ing\\b"),
        ("ident", "scan", "[A-Za-z_][A-Za-z0-9_]*\\(\\)"),
        ("id-anch", "anchored", "^[A-Z]{2}[0-9]{6}$"),
        ("tok-anch", "anchored", "^(ERROR|WARN|INFO): [a-z ]+$"),
    ];

    public static readonly string[] Engines = ["gregsex-pcre2", "gregsex-pcre2-compiled", "bcl-interpreted", "bcl-compiled"];

    [ParamsSource(nameof(Scenarios))]
    public Pcre2Scenario Scenario { get; set; } = null!;

    public static IEnumerable<Pcre2Scenario> Scenarios()
    {
        foreach (var (name, kind, pattern) in Shapes)
        {
            foreach (var engine in Engines)
            {
                yield return new Pcre2Scenario(name, kind, pattern, engine);
            }
        }
    }

    private Func<long> _run = null!;
    private IDisposable? _owner;

    // Mixed short strings: roughly a third match the XSD shapes, the rest are near misses.
    public static string[] BuildStrings()
    {
        var random = new Random(77);
        string[] pieces = ["AB123456", "ab123456", "joe@mail.com", "x@y.org", "bad@", "2024-05-17", "2024-5-17", "ERROR: disk full",
            "WARN: low space", "INFO: ok", "key=value1", "name=abc", "winning the game", "the cat sat", "task failed", "ERROR failed", "WARN slow", "plain text only", "12345", "a=b c=d e=f"];
        var result = new string[2000];
        for (var i = 0; i < result.Length; i++)
        {
            var sb = new StringBuilder();
            var n = random.Next(1, 4);
            for (var p = 0; p < n; p++)
            {
                if (p > 0 && random.Next(3) > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(pieces[random.Next(pieces.Length)]);
            }
            result[i] = i % 3 == 0 ? pieces[random.Next(pieces.Length)] : sb.ToString();
        }
        return result;
    }

    [GlobalSetup]
    public void Setup()
    {
        var strings = BuildStrings();
        var (run, owner) = Create(Scenario.Engine, Scenario.Kind, Scenario.Pattern, strings);
        _run = run;
        _owner = owner;
        var expected = Create("bcl-interpreted", Scenario.Kind, Scenario.Pattern, strings).Run();
        var actual = run();
        if (expected != actual)
        {
            throw new InvalidOperationException($"{Scenario}: checksum {actual} disagrees with BCL {expected}.");
        }
    }

    [Benchmark]
    public long Run() => _run();

    [GlobalCleanup]
    public void Cleanup() => _owner?.Dispose();

    private static (Func<long> Run, IDisposable? Owner) Create(string engine, string kind, string pattern, string[] strings)
    {
        var xsd = false;
        if (engine.StartsWith("bcl-", StringComparison.Ordinal))
        {
            var options = RegexOptions.CultureInvariant | (engine == "bcl-compiled" ? RegexOptions.Compiled : 0);
            var re = new Regex(pattern, options, TimeSpan.FromSeconds(20));
            return (() =>
            {
                long sum = 0;
                foreach (var s in strings)
                {
                    if (xsd)
                    {
                        if (re.IsMatch(s))
                        {
                            sum++;
                        }
                    }
                    else
                    {
                        var m = re.Match(s);
                        if (m.Success)
                        {
                            sum += 1 + m.Index + m.Length + m.Groups.Count;
                        }
                    }
                }
                return sum;
            }, null);
        }
        var limits = new RegexMatchLimits(20_000_000_000L, 1L << 30);
        RegexProfile profile = new Pcre2Profile();
        var parsed = RegexPattern.Parse(pattern, profile, new RegexCompileOptions(RegexInputKind.Utf16, RegexExecutionMode.Interpreted, defaultMatchLimits: limits));
        if (engine == "gregsex-pcre2-compiled")
        {
            parsed = parsed.Compile();
        }

        var ws = parsed.CreateWorkspace(new(parsed.CaptureCapacity, allowGrowth: true));
        var groups = new RegexCapture[parsed.GroupCount];
        return (() =>
        {
            long sum = 0;
            foreach (var s in strings)
            {
                if (xsd)
                {
                    if (ws.IsMatch(s) == RegexSearchStatus.Match)
                    {
                        sum++;
                    }
                }
                else if (ws.FindCaptures(s, 0, groups, out var r) == RegexSearchStatus.Match)
                {
                    sum += 1 + r.Index + r.Length + groups.Length;
                }
            }
            return sum;
        }, ws);
    }
}
