using BenchmarkDotNet.Attributes;

namespace GregsEx.Benchmarks.Posix;

[MemoryDiagnoser]
[ShortRunJob]
public class PosixPublicBenchmarks
{
    private RegexPattern[] _patterns = null!;
    private RegexWorkspace[] _workspaces = null!;
    private RegexCapture[][] _captures = null!;
    private PosixMatch[][] _full = null!, _partial = null!;
    private byte[][] _inputs = null!;
    private byte[] _error = null!;
    private RegexWorkspace _noSub = null!;
    private PosixMatch[] _ignored = null!;
    private RegexWorkspaceCapacity[] _capacities = null!;
    public const int OperationsPerMatrix = 52;
    public static readonly string[] CaseIds = ["literal-hit", "literal-miss", "empty", "ambiguity",
        "BRE-reference-hit", "BRE-reference-miss", "nullable-empty", "opaque-high-byte", "newline-flags", "NoSub-reference", "RegError"];

    [GlobalSetup]
    public void Setup()
    {
        var ere = new PosixProfile(PosixDialect.Extended);
        _patterns = [PosixFunctions.Parse("a"u8, ere), PosixFunctions.Parse("a"u8, ere),
            PosixFunctions.Parse(""u8, ere), PosixFunctions.Parse("(a|aa)(a?)"u8, ere),
            PosixFunctions.Parse(@"\(ab\)\1", new()), PosixFunctions.Parse(@"\(ab\)\1", new()),
            PosixFunctions.Parse("(a*)*"u8, ere), PosixFunctions.Parse(new byte[] {128,255}, ere),
            PosixFunctions.Parse("^a$"u8, new(PosixDialect.Extended, PosixCompileFlags.Newline))];
        _inputs = ["xxa"u8.ToArray(), "bbb"u8.ToArray(), [], "aa"u8.ToArray(), "xxabab"u8.ToArray(),
            "aba"u8.ToArray(), [], [128,255], "x\na\nx"u8.ToArray()];
        _workspaces = _patterns.Select(p => p.CreateWorkspace()).ToArray();
        _captures = _patterns.Select(p => new RegexCapture[p.GroupCount]).ToArray();
        _full = _patterns.Select(p => new PosixMatch[p.GroupCount + 1]).ToArray();
        _partial = _patterns.Select(p => new PosixMatch[Math.Min(p.GroupCount, 2)]).ToArray();
        _capacities = _workspaces.Select(w => w.Capacity).ToArray();
        _noSub = PosixFunctions.Parse(@"\(ab\)\1", new(PosixDialect.Basic, PosixCompileFlags.NoSub)).CreateWorkspace();
        _ignored = Enumerable.Repeat(new PosixMatch(77, 88), 3).ToArray();
        _error = new byte[24];
        var expected = WarmMatrix();
        for (var i = 0; i < 64; i++)
        {
            if (WarmMatrix() != expected)
            {
                throw new InvalidOperationException("Unstable matrix");
            }
        }

        if (_captures[3][1] != new RegexCapture(true, 0, 2) || _captures[3][2] != new RegexCapture(true, 2, 0) ||
            _captures[4][1] != new RegexCapture(true, 2, 2) || _captures[6][1] != new RegexCapture(true, 0, 0))
        {
            throw new InvalidOperationException("Capture semantic witness");
        }

        VerifyCapacity();
    }

    // 9 cases x 5 actual public operations + 4 NoSub native/generic searches
    // + 2 typed endpoint-suppression calls + 1 RegError = 52 calls/matrix.
    [Benchmark]
    public int WarmMatrix()
    {
        var sum = 0;
        for (var i = 0; i < _patterns.Length; i++)
        {
            var w = _workspaces[i];
            var input = _inputs[i];
            var expected = i is 1 or 5 ? RegexSearchStatus.NoMatch : RegexSearchStatus.Match;
            sum += Check(w.IsMatch(input), expected);
            sum += Check(w.Find(input, 0, out var range), expected) + range.Index + range.Length;
            sum += Check(w.FindCaptures(input, 0, _captures[i], out range), expected) + range.Index + range.Length;
            foreach (var c in _captures[i])
            {
                if (c.Participated)
                {
                    sum += 1 + c.Index + c.Length;
                }
            }

            sum += Check(PosixFunctions.RegExec(w, input, _full[i]), expected);
            sum += Check(PosixFunctions.RegExec(w, input, _partial[i]), expected);
            foreach (var n in _full[i])
            {
                sum += n.Start + n.End;
            }

            foreach (var n in _partial[i])
            {
                sum += n.Start + n.End;
            }
        }
        sum += Check(PosixFunctions.RegExec(_noSub, "abab"u8, _ignored), RegexSearchStatus.Match);
        sum += Check(PosixFunctions.RegExec(_noSub, "aba"u8, _ignored), RegexSearchStatus.NoMatch);
        sum += Check(_noSub.IsMatch("abab"u8), RegexSearchStatus.Match);
        sum += Check(_noSub.Find("abab"u8, 0, out var noSubRange), RegexSearchStatus.Match) + noSubRange.Length;
        sum += Check(PosixFunctions.RegExec(_workspaces[8], _inputs[8], _partial[8], PosixExecutionFlags.NotBol | PosixExecutionFlags.NotEol), RegexSearchStatus.Match);
        sum += Check(PosixFunctions.RegExec(_workspaces[0], _inputs[0], _partial[0], PosixExecutionFlags.NotBol | PosixExecutionFlags.NotEol), RegexSearchStatus.Match);
        sum += PosixFunctions.RegError(PosixErrorCode.InvalidBackreference, _error) + _error[0] + _error[21];
        return sum;
    }
    private static int Check(RegexSearchStatus actual, RegexSearchStatus expected)
    { if (actual != expected) { throw new InvalidOperationException("Public operation mismatch"); } return (int)actual; }
    public void VerifyCapacity()
    {
        for (var i = 0; i < _workspaces.Length; i++)
        {
            if (_workspaces[i].Capacity != _capacities[i])
            {
                throw new InvalidOperationException("Growth");
            }
        }

        if (_ignored.Any(n => n != new PosixMatch(77, 88)))
        {
            throw new InvalidOperationException("NoSub output mutation");
        }
    }
    [GlobalCleanup]
    public void Cleanup() { VerifyCapacity(); foreach (var w in _workspaces) { w.Dispose(); } _noSub.Dispose(); }
}

[MemoryDiagnoser]
[ShortRunJob]
public class PosixLifecycleBenchmarks
{
    private readonly PosixProfile _profile = new(PosixDialect.Extended);
    private RegexPattern _pattern = null!;
    [GlobalSetup]
    public void Setup()
    {
        _pattern = PosixFunctions.Parse("(a|aa)(a?)"u8, _profile);
        if (Parse() <= 0 || Workspace() <= 0 || !Convenience())
        {
            throw new InvalidOperationException("Cold phase witness");
        }
    }
    [Benchmark]
    public long Parse() => PosixFunctions.Parse("(a|aa)(a?)"u8, _profile).RetainedBytes;
    [Benchmark]
    public long Workspace() { using var w = _pattern.CreateWorkspace(); return w.ReservedBytes; }
    [Benchmark]
    public bool Convenience() => _pattern.IsMatch("aa"u8);
}
