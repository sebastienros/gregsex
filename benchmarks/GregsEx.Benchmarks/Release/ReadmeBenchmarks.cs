using System.Text;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Reports;
using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Release;

public sealed record ReadmeScenario(string Flavor, string Backend)
{
    public override string ToString() => $"{Flavor}:{Backend}";
}

[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(ReadmeBenchmarkConfig))]
public partial class ReadmeBenchmarks
{
    private const string Pattern = "((a)|b)+c";
    private const string ShortInput = "bbaaac";
    private const string MissInput = "zzzzzz";
    private readonly string _longInput = new string('a', 256) + "c";

    [GeneratedPattern(Pattern)]
    private static partial RegexPattern GeneratedCore();

    [GeneratedPattern(Pattern, EcmaScriptFlags.None)]
    private static partial RegexPattern GeneratedEcma();

    [GeneratedPattern(Pattern, GeneratedPatternFlavor.Xsd)]
    private static partial RegexPattern GeneratedXsd();

    [GeneratedPattern(Pattern, XPathFlags.None)]
    private static partial RegexPattern GeneratedXPath();

    [GeneratedPattern(Pattern, Pcre2CompileFlags.None)]
    private static partial RegexPattern GeneratedPcre2();

    [GeneratedRegex(Pattern, RegexOptions.None, 20_000)]
    private static partial Regex GeneratedBcl();

    [ParamsSource(nameof(Scenarios))]
    public ReadmeScenario Scenario { get; set; } = null!;

    public static IEnumerable<ReadmeScenario> Scenarios()
    {
        foreach (var flavor in new[] { "RegularCore", "ECMAScript", "XSD", "XPath", "POSIX", "PCRE2", "BCL" })
        {
            yield return new(flavor, "Interpreted");
            if (flavor != "POSIX")
            {
                yield return new(flavor, "Compiled");
                yield return new(flavor, "SourceGenerated");
            }
        }
    }

    private Func<bool> _shortIsMatch = null!;
    private Func<bool> _longIsMatch = null!;
    private Func<long> _shortCaptures = null!;
    private RegexPattern? _pattern;
    private RegexWorkspace? _workspace;
    private RegexWorkspaceCapacity _capacity;
    private long _reservedBytes;
    private long _retainedBytes;

    [GlobalSetup]
    public void Setup()
    {
        if (Scenario.Flavor == "BCL")
        {
            SetupBcl();
        }
        else
        {
            SetupGregsEx();
        }

        for (var i = 0; i < 64; i++)
        {
            if (!_shortIsMatch() || !_longIsMatch() || _shortCaptures() != (Scenario.Flavor == "XSD" ? 7 : 19))
            {
                throw new InvalidOperationException($"{Scenario}: shared-workload semantic check failed.");
            }
        }

        if (_workspace is { } workspace && _pattern is { } pattern)
        {
            _capacity = workspace.Capacity;
            _reservedBytes = workspace.ReservedBytes;
            _retainedBytes = pattern.RetainedBytes;
            Console.WriteLine($"README prepared {Scenario}: backend={pattern.ExecutionBackend}; profile={pattern.Profile.Id}; data={pattern.Profile.DataId}; input={pattern.InputKind}; capacity={_capacity}; reserved={_reservedBytes}; retained={_retainedBytes}.");
        }
    }

    private void SetupBcl()
    {
        var regex = Scenario.Backend switch
        {
            "Interpreted" => new Regex(Pattern, RegexOptions.None, TimeSpan.FromSeconds(20)),
            "Compiled" => new Regex(Pattern, RegexOptions.Compiled, TimeSpan.FromSeconds(20)),
            "SourceGenerated" => GeneratedBcl(),
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
        };
        var match = regex.Match(ShortInput);
        if (!match.Success || match.Groups.Count != 3 || regex.IsMatch(MissInput))
        {
            throw new InvalidOperationException($"{Scenario}: unexpected BCL match or group count.");
        }

        for (var i = 0; i < match.Groups.Count; i++)
        {
            var group = match.Groups[i];
            var expected = ExpectedCapture(i);
            if (!group.Success || group.Index != expected.Index || group.Length != expected.Length)
            {
                throw new InvalidOperationException($"{Scenario}: incorrect BCL capture {i}.");
            }
        }

        _shortIsMatch = () => regex.IsMatch(ShortInput);
        _longIsMatch = () => regex.IsMatch(_longInput);
        _shortCaptures = () =>
        {
            var result = regex.Match(ShortInput);
            if (!result.Success)
            {
                throw new InvalidOperationException($"{Scenario}: BCL capture match failed.");
            }

            long sum = 0;
            for (var i = 0; i < result.Groups.Count; i++)
            {
                var group = result.Groups[i];
                if (group.Success)
                {
                    sum += 1 + group.Index + group.Length;
                }
            }
            return sum;
        };
    }

    private void SetupGregsEx()
    {
        RegexProfile profile = Scenario.Flavor switch
        {
            "RegularCore" => new RegularCoreProfile(),
            "ECMAScript" => new EcmaScriptProfile(),
            "XSD" => new XsdProfile(),
            "XPath" => new XPathProfile(),
            "POSIX" => new PosixProfile(PosixDialect.Extended),
            "PCRE2" => new Pcre2Profile(),
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
        };
        var inputKind = Scenario.Flavor == "POSIX" ? RegexInputKind.Bytes : RegexInputKind.Utf16;
        var pattern = Scenario.Backend switch
        {
            "Interpreted" => RegexPattern.Parse(Pattern, profile, new(inputKind, RegexExecutionMode.Interpreted)),
            "Compiled" => RegexPattern.Parse(Pattern, profile, new(inputKind, RegexExecutionMode.Interpreted)).Compile(),
            "SourceGenerated" => Scenario.Flavor switch
            {
                "RegularCore" => GeneratedCore(),
                "ECMAScript" => GeneratedEcma(),
                "XSD" => GeneratedXsd(),
                "XPath" => GeneratedXPath(),
                "PCRE2" => GeneratedPcre2(),
                _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
            },
            _ => throw new ArgumentOutOfRangeException(nameof(Scenario))
        };
        var expectedBackend = Scenario.Backend == "Interpreted"
            ? RegexExecutionBackend.Interpreted : RegexExecutionBackend.Compiled;
        var expectedGroups = Scenario.Flavor == "XSD" ? 1 : 3;
        if (pattern.ExecutionBackend != expectedBackend || pattern.GroupCount != expectedGroups ||
            pattern.Profile.Id != profile.Id || pattern.Profile.DataId != profile.DataId ||
            pattern.InputKind != inputKind)
        {
            throw new InvalidOperationException($"{Scenario}: incorrect backend, profile, input kind or group count.");
        }

        _pattern = pattern;
        var workspace = pattern.CreateWorkspace(new(pattern.CaptureCapacity));
        _workspace = workspace;
        var captures = new RegexCapture[pattern.GroupCount];
        RegexSearchStatus captureStatus;
        RegexMatchRange range;
        bool miss;
        if (inputKind == RegexInputKind.Bytes)
        {
            var shortBytes = Encoding.ASCII.GetBytes(ShortInput);
            var longBytes = Encoding.ASCII.GetBytes(_longInput);
            var missBytes = Encoding.ASCII.GetBytes(MissInput);
            _shortIsMatch = () => CheckStatus(workspace.IsMatch(shortBytes), workspace);
            _longIsMatch = () => CheckStatus(workspace.IsMatch(longBytes), workspace);
            _shortCaptures = () => CaptureChecksum(workspace, workspace.FindCaptures(shortBytes, 0, captures, out _), captures);
            captureStatus = workspace.FindCaptures(shortBytes, 0, captures, out range);
            miss = CheckStatus(workspace.IsMatch(missBytes), workspace);
        }
        else
        {
            _shortIsMatch = () => CheckStatus(workspace.IsMatch(ShortInput), workspace);
            _longIsMatch = () => CheckStatus(workspace.IsMatch(_longInput), workspace);
            _shortCaptures = () => CaptureChecksum(workspace, workspace.FindCaptures(ShortInput, 0, captures, out _), captures);
            captureStatus = workspace.FindCaptures(ShortInput, 0, captures, out range);
            miss = CheckStatus(workspace.IsMatch(MissInput), workspace);
        }

        if (captureStatus != RegexSearchStatus.Match || range != new RegexMatchRange(0, 6) || miss)
        {
            throw new InvalidOperationException($"{Scenario}: incorrect match result ({workspace.LastFailure}).");
        }
        for (var i = 0; i < captures.Length; i++)
        {
            if (captures[i] != ExpectedCapture(i))
            {
                throw new InvalidOperationException($"{Scenario}: incorrect GregsEx capture {i}.");
            }
        }
    }

    private static RegexCapture ExpectedCapture(int group) =>
        group == 0 ? new(true, 0, 6) : new(true, 4, 1);

    private static bool CheckStatus(RegexSearchStatus status, RegexWorkspace workspace) => status switch
    {
        RegexSearchStatus.Match => true,
        RegexSearchStatus.NoMatch => false,
        _ => throw new InvalidOperationException($"Matching failed: {status}; {workspace.LastFailure}.")
    };

    private static long CaptureChecksum(RegexWorkspace workspace, RegexSearchStatus status, RegexCapture[] captures)
    {
        if (status != RegexSearchStatus.Match)
        {
            throw new InvalidOperationException($"Capture matching failed: {status}; {workspace.LastFailure}.");
        }

        long sum = 0;
        foreach (var capture in captures)
        {
            if (capture.Participated)
            {
                sum += 1 + capture.Index + capture.Length;
            }
        }
        return sum;
    }

    [Benchmark]
    public bool ShortIsMatch() => _shortIsMatch();

    [Benchmark]
    public bool LongIsMatch() => _longIsMatch();

    [Benchmark]
    public long ShortCaptures() => _shortCaptures();

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_workspace is { } workspace && _pattern is { } pattern)
        {
            var stable = workspace.Capacity == _capacity && workspace.ReservedBytes == _reservedBytes &&
                pattern.RetainedBytes == _retainedBytes;
            workspace.Dispose();
            if (!stable)
            {
                throw new InvalidOperationException($"{Scenario}: prepared storage grew during measurement.");
            }
        }
    }
}

internal sealed class ReadmeBenchmarkConfig : ManualConfig
{
    public ReadmeBenchmarkConfig()
    {
        SummaryStyle = SummaryStyle.Default.WithMaxParameterColumnWidth(40);
        AddFilter(new SimpleFilter(benchmark =>
            benchmark.Descriptor.WorkloadMethod.Name != nameof(ReadmeBenchmarks.ShortCaptures) ||
            benchmark.Parameters.Items.All(parameter => parameter.Value is not ReadmeScenario { Flavor: "XSD" })));
    }
}
