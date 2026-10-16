using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Core;

[MemoryDiagnoser]
[ShortRunJob]
[Config(typeof(GregsEx.Benchmarks.Core.CoreTimingConfig))]
public class CoreSearchBenchmarks
{
    [Params("UTF16-HIT", "UTF8-MISS", "UNITS-EMPTY", "BYTES-HIT")]
    public string Case { get; set; } = "";
    private RegexWorkspace _workspace = null!;
    private RegexCapture[] _groups = null!;
    private string _text = "";
    private byte[] _bytes = [];
    private bool _charInput;

    [GlobalSetup]
    public void Setup()
    {
        var mode = Case switch { "UNITS-EMPTY" => RegularCoreMode.Utf16CodeUnits, "BYTES-HIT" => RegularCoreMode.RawBytes, _ => RegularCoreMode.UnicodeScalars };
        var kind = Case switch { "UTF8-MISS" => RegexInputKind.Utf8, "BYTES-HIT" => RegexInputKind.Bytes, _ => RegexInputKind.Utf16 };
        var source = Case == "UNITS-EMPTY" ? "(a*)" : "(a+)(b)?";
        _text = Case == "UTF8-MISS" || Case == "UNITS-EMPTY" ? "xxx!" : "xxaa!";
        _bytes = System.Text.Encoding.UTF8.GetBytes(_text);
        _charInput = kind == RegexInputKind.Utf16;
        var pattern = RegexPattern.Parse(source, new RegularCoreProfile(mode), new(kind));
        _workspace = pattern.CreateWorkspace();
        _groups = new RegexCapture[pattern.GroupCount];
        var expectedStatus = Case == "UTF8-MISS" ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match;
        var expectedRange = Case is "UTF16-HIT" or "BYTES-HIT" ? 4 : 0;
        var expectedCaptures = Case is "UTF16-HIT" or "BYTES-HIT" ? 14 : Case == "UNITS-EMPTY" ? 2 : 0;
        if (Boolean() != expectedStatus || Range() != expectedRange || Captures() != expectedCaptures)
        {
            throw new InvalidOperationException("Prepared benchmark output disagrees with handwritten fixture.");
        }

        Console.WriteLine($"CORE-PREP {Case} plan={pattern.RetainedBytes} reserved={_workspace.ReservedBytes} capacity={_workspace.Capacity} boolean={Boolean()} range={Range()} captures={Captures()}");
        for (var i = 0; i < 16_384; i++)
        { _ = Boolean(); _ = Range(); _ = Captures(); }
    }

    [Benchmark]
    public RegexSearchStatus Boolean() => _charInput ? _workspace.IsMatch(_text) : _workspace.IsMatch(_bytes);
    [Benchmark]
    public int Range()
    {
        var status = _charInput ? _workspace.Find(_text, 0, out var r) : _workspace.Find(_bytes, 0, out r);
        return status == RegexSearchStatus.Match ? r.Index + r.Length : 0;
    }
    [Benchmark]
    public int Captures()
    {
        var status = _charInput ? _workspace.FindCaptures(_text, 0, _groups, out var r) : _workspace.FindCaptures(_bytes, 0, _groups, out r);
        var sum = r.Index + r.Length;
        if (status == RegexSearchStatus.Match)
        {
            for (var i = 0; i < _groups.Length; i++)
            {
                if (_groups[i].Participated)
                {
                    sum += 1 + _groups[i].Index + _groups[i].Length;
                }
            }
        }

        return sum;
    }
    [GlobalCleanup]
    public void Cleanup() => _workspace.Dispose();
}
