using System.Text;
using System.Text.RegularExpressions;

namespace GregsEx.Benchmarks.Acceleration.Corpus;

/// <summary>A prepared engine for one case. Operations return deterministic checksums so results are comparable across engines.</summary>
public abstract class CaseEngine : IDisposable
{
    /// <summary>1 on match else 0.</summary>
    public abstract int IsMatch();
    /// <summary>First match start plus length, or -1.</summary>
    public abstract int Find();
    /// <summary>Number of non-overlapping leftmost matches, empty matches advance one unit.</summary>
    public abstract int Count();
    /// <summary>Sum over all matches of every participating group offset and length.</summary>
    public abstract long Captures();
    public virtual void Dispose() => GC.SuppressFinalize(this);

    public int Run(string operation) => operation switch
    {
        "is_match" => IsMatch(),
        "find" => Find(),
        "count" => Count(),
        "captures" => unchecked((int)Captures()),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}

public sealed class UnsupportedEngineException(string message) : Exception(message);

/// <summary>Engine identifiers match docs/benchmarks/release-manifest.json comparators.</summary>
public static class CaseEngines
{
    public const long BenchmarkWork = 20_000_000_000L;

    public static readonly string[] InProcess =
    [
        "gregsex-core", "gregsex-core-auto", "gregsex-core-compiled", "gregsex-ecma", "gregsex-pcre2", "gregsex-posix",
        "bcl-interpreted", "bcl-compiled", "bcl-sourcegen", "bcl-ecma-interpreted", "bcl-ecma-compiled",
    ];

    public static string ManifestProfile(string engine) => engine switch
    {
        "gregsex-core" or "gregsex-core-auto" or "gregsex-core-compiled" => "RegularCore",
        "gregsex-ecma" or "bcl-ecma-interpreted" or "bcl-ecma-compiled" => "ECMAScript",
        "gregsex-pcre2" => "PCRE2",
        "gregsex-posix" => "POSIX",
        _ => "",
    };

    public static bool IsApplicable(string engine, CorpusCase c)
    {
        var profile = ManifestProfile(engine);
        if (profile.Length != 0 && !c.Profiles.Contains(profile))
        {
            return false;
        }
        // Compile() declines patterns an accelerated path already serves; those cases have no compiled row.
        if (engine == "gregsex-core-compiled")
        {
            return CoreEngine.TryCompile(c.Pattern, out _);
        }
        // RegexOptions.ECMAScript rejects constructs such as \p{L}; those cases simply have no BCL-ECMAScript row.
        if (engine.StartsWith("bcl-ecma-", StringComparison.Ordinal))
        {
            return TryCreateBclEcma(c.Pattern, RegexOptions.ECMAScript) is not null;
        }

        if (profile.Length != 0)
        {
            return true;
        }
        // BCL engines run the same pattern text for every pinned case.
        return true;
    }

    public static CaseEngine Create(string engine, CorpusCase c, string text) => engine switch
    {
        "gregsex-core" => new CoreEngine(c, text),
        "gregsex-core-auto" => new CoreEngine(c, text, RegexExecutionMode.Auto),
        "gregsex-core-compiled" => new CoreEngine(c, text, RegexExecutionMode.Auto, compile: true),
        "gregsex-ecma" => new EcmaEngine(c, text),
        "gregsex-pcre2" => new Pcre2Engine(c, text),
        "gregsex-posix" => new PosixEngine(c, text),
        "bcl-interpreted" => new BclEngine(new Regex(c.Pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(20)), text),
        "bcl-compiled" => new BclEngine(new Regex(c.Pattern, System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromSeconds(20)), text),
        "bcl-sourcegen" => new BclEngine(GeneratedPatterns.Get(c.Pattern), text),
        "bcl-ecma-interpreted" => new BclEngine(TryCreateBclEcma(c.Pattern, RegexOptions.ECMAScript)!, text),
        "bcl-ecma-compiled" => new BclEngine(TryCreateBclEcma(c.Pattern, RegexOptions.ECMAScript | RegexOptions.Compiled)!, text),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown in-process engine."),
    };

    private static Regex? TryCreateBclEcma(string pattern, RegexOptions options)
    {
        try
        { return new Regex(pattern, options, TimeSpan.FromSeconds(20)); }
        catch (ArgumentException) { return null; }
    }

    internal const int FrameCapacity = 1 << 16;

    internal static RegexMatchLimits Limits => new(BenchmarkWork, 1L << 30);
}

internal sealed class BclEngine(Regex regex, string text) : CaseEngine
{
    public override int IsMatch() => regex.IsMatch(text) ? 1 : 0;
    public override int Find()
    {
        foreach (var match in regex.EnumerateMatches(text))
        {
            return match.Index + match.Length;
        }

        return -1;
    }
    public override int Count() => regex.Count(text);
    public override long Captures()
    {
        long sum = 0;
        for (var m = regex.Match(text); m.Success; m = m.NextMatch())
        {
            for (var g = 0; g < m.Groups.Count; g++)
            {
                var group = m.Groups[g];
                if (group.Success)
                {
                    sum += 1 + group.Index + group.Length;
                }
            }
        }

        return sum;
    }
}

internal sealed class CoreEngine : CaseEngine
{
    private readonly RegexWorkspace _workspace;
    private readonly RegexCapture[] _groups;
    private readonly string _text;
    private readonly int _lengthLimit;

    internal static RegexPattern Parse(string pattern, RegexExecutionMode mode)
        => RegexPattern.Parse(pattern, new RegularCoreProfile(), new RegexCompileOptions(RegexInputKind.Utf16, mode, defaultMatchLimits: CaseEngines.Limits));

    internal static bool TryCompile(string pattern, out RegexPattern? compiled)
    {
        try
        {
            compiled = Parse(pattern, RegexExecutionMode.Auto).Compile();
            return true;
        }
        catch (Exception e) when (e is RegexParseException or NotSupportedException)
        {
            compiled = null;
            return false;
        }
    }

    public CoreEngine(CorpusCase c, string text, RegexExecutionMode mode = RegexExecutionMode.Interpreted, bool compile = false)
    {
        _text = text;
        _lengthLimit = text.Length;
        try
        {
            var pattern = Parse(c.Pattern, mode);
            if (compile)
            {
                pattern = pattern.Compile();
            }

            _workspace = pattern.CreateWorkspace(new(pattern.CaptureCapacity, allowGrowth: true));
            _groups = new RegexCapture[pattern.GroupCount];
        }
        catch (RegexParseException e) { throw new UnsupportedEngineException(e.Message); }
    }

    // Manifest patterns contain only plain capturing groups, so counting unescaped '(' without '?' is exact.
    internal static int CountGroups(string pattern)
    {
        var count = 1;
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\')
            {
                i++;
            }
            else if (pattern[i] == '(' && (i + 1 >= pattern.Length || pattern[i + 1] != '?'))
            {
                count++;
            }
        }
        return count;
    }

    private void Check(RegexSearchStatus status)
    {
        if (status is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
        {
            throw new InvalidOperationException($"GregsEx returned {status} {_workspace.LastFailure}.");
        }
    }

    public override int IsMatch()
    {
        var status = _workspace.IsMatch(_text);
        Check(status);
        return status == RegexSearchStatus.Match ? 1 : 0;
    }
    public override int Find()
    {
        var status = _workspace.Find(_text, 0, out var range);
        Check(status);
        return status == RegexSearchStatus.Match ? range.Index + range.Length : -1;
    }
    public override int Count()
    {
        int count = 0, start = 0;
        while (start <= _lengthLimit)
        {
            var status = _workspace.Find(_text, start, out var range);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            count++;
            start = range.Index + range.Length + (range.Length == 0 ? 1 : 0);
        }
        return count;
    }
    public override long Captures()
    {
        long sum = 0;
        var start = 0;
        while (start <= _lengthLimit)
        {
            var status = _workspace.FindCaptures(_text, start, _groups, out var range);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            foreach (var g in _groups)
            {
                if (g.Participated)
                {
                    sum += 1 + g.Index + g.Length;
                }
            }

            start = range.Index + range.Length + (range.Length == 0 ? 1 : 0);
        }
        return sum;
    }
    public override void Dispose()
    {
        _workspace.Dispose();
        base.Dispose();
    }
}

internal sealed class PosixEngine : CaseEngine
{
    private readonly RegexWorkspace _workspace;
    private readonly RegexCapture[] _groups;
    private readonly byte[] _bytes;

    public PosixEngine(CorpusCase c, string text)
    {
        _bytes = Encoding.UTF8.GetBytes(text);
        if (_bytes.Length != text.Length)
        {
            throw new UnsupportedEngineException("POSIX cases are ASCII-only.");
        }

        if (!PosixFunctions.TryParse(c.Pattern, new PosixProfile(PosixDialect.Extended), out var pattern, out var diagnostic,
                new RegexCompileOptions(RegexInputKind.Bytes, defaultMatchLimits: CaseEngines.Limits)))
        {
            throw new UnsupportedEngineException(diagnostic.ToString());
        }

        _workspace = pattern!.CreateWorkspace(new(pattern.CaptureCapacity, allowGrowth: true));
        _groups = new RegexCapture[pattern.GroupCount];
    }

    private void Check(RegexSearchStatus status)
    {
        if (status is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
        {
            throw new InvalidOperationException($"GregsEx returned {status} {_workspace.LastFailure}.");
        }
    }

    public override int IsMatch()
    {
        var status = _workspace.IsMatch(_bytes);
        Check(status);
        return status == RegexSearchStatus.Match ? 1 : 0;
    }
    public override int Find()
    {
        var status = _workspace.Find(_bytes, 0, out var range);
        Check(status);
        return status == RegexSearchStatus.Match ? range.Index + range.Length : -1;
    }
    public override int Count()
    {
        int count = 0, start = 0;
        while (start <= _bytes.Length)
        {
            var status = _workspace.Find(_bytes, start, out var range);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            count++;
            start = range.Index + range.Length + (range.Length == 0 ? 1 : 0);
        }
        return count;
    }
    public override long Captures()
    {
        long sum = 0;
        var start = 0;
        while (start <= _bytes.Length)
        {
            var status = _workspace.FindCaptures(_bytes, start, _groups, out var range);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            foreach (var g in _groups)
            {
                if (g.Participated)
                {
                    sum += 1 + g.Index + g.Length;
                }
            }

            start = range.Index + range.Length + (range.Length == 0 ? 1 : 0);
        }
        return sum;
    }
    public override void Dispose()
    {
        _workspace.Dispose();
        base.Dispose();
    }
}

internal sealed class EcmaEngine : CaseEngine
{
    private readonly EcmaScriptMatcher _single;
    private readonly EcmaScriptMatcher _global;
    private readonly RegexCapture[] _groups;
    private readonly string _text;

    public EcmaEngine(CorpusCase c, string text)
    {
        _text = text;
        var flags = c.Flags.TryGetValue("ECMAScript", out var f) && f.Contains('u') ? EcmaScriptFlags.Unicode : EcmaScriptFlags.None;
        var compile = new RegexCompileOptions(RegexInputKind.Utf16, defaultMatchLimits: CaseEngines.Limits);
        if (!EcmaScriptMatcher.TryParse(c.Pattern, new EcmaScriptProfile(flags), out var single, out var d1, compile, frameCapacity: CaseEngines.FrameCapacity))
        {
            throw new UnsupportedEngineException(d1.ToString());
        }

        if (!EcmaScriptMatcher.TryParse(c.Pattern, new EcmaScriptProfile(flags | EcmaScriptFlags.Global), out var global, out var d2, compile, frameCapacity: CaseEngines.FrameCapacity))
        {
            throw new UnsupportedEngineException(d2.ToString());
        }

        _single = single!;
        _global = global!;
        _groups = new RegexCapture[CoreEngine.CountGroups(c.Pattern)];
    }

    private void Check(RegexSearchStatus status)
    {
        if (status is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
        {
            throw new InvalidOperationException($"GregsEx returned {status} ({_single.LastFailure} / {_global.LastFailure}).");
        }
    }

    public override int IsMatch()
    {
        var status = _single.Test(_text);
        Check(status);
        return status == RegexSearchStatus.Match ? 1 : 0;
    }
    public override int Find()
    {
        var status = _single.Exec(_text, _groups, out var range);
        Check(status);
        return status == RegexSearchStatus.Match ? range.Index + range.Length : -1;
    }
    public override int Count()
    {
        var count = 0;
        _global.LastIndex = 0;
        while (true)
        {
            var status = _global.ExecNext(_text, _groups, out _);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            count++;
        }
        return count;
    }
    public override long Captures()
    {
        long sum = 0;
        _global.LastIndex = 0;
        while (true)
        {
            var status = _global.ExecNext(_text, _groups, out _);
            Check(status);
            if (status != RegexSearchStatus.Match)
            {
                break;
            }

            foreach (var g in _groups)
            {
                if (g.Participated)
                {
                    sum += 1 + g.Index + g.Length;
                }
            }
        }
        return sum;
    }
    public override void Dispose()
    {
        _single.Dispose();
        _global.Dispose();
        base.Dispose();
    }
}

internal sealed class Pcre2Engine : CaseEngine
{
    private readonly Pcre2Workspace _workspace;
    private readonly Pcre2OffsetPair[] _pairs;
    private readonly string _text;

    public Pcre2Engine(CorpusCase c, string text)
    {
        _text = text;
        var flags = Pcre2CompileFlags.None;
        if (c.Flags.TryGetValue("PCRE2", out var f) && f.Contains("UCP"))
        {
            flags |= Pcre2CompileFlags.Ucp | Pcre2CompileFlags.Utf;
        }

        var options = new Pcre2CompileOptions(defaultMatchLimits: new Pcre2MatchLimits(CaseEngines.BenchmarkWork, 1 << 20, 1L << 30, 1 << 22));
        if (!Pcre2Functions.TryParse(c.Pattern, new Pcre2Profile(Pcre2CodeUnitWidth.Sixteen, flags), out var pattern, out var diagnostic, options))
        {
            throw new UnsupportedEngineException(diagnostic.ToString());
        }

        _workspace = pattern!.CreateWorkspace(new Pcre2WorkspaceOptions(1 << 16, 1 << 18, 1 << 16, 512, allowGrowth: true));
        _pairs = new Pcre2OffsetPair[pattern.GroupCount];
    }

    private static void Check(Pcre2MatchResult result)
    {
        if (result.Status is not (Pcre2MatchStatus.Match or Pcre2MatchStatus.NoMatch))
        {
            throw new InvalidOperationException($"GregsEx PCRE2 returned {result.Status} {result.Failure}.");
        }
    }

    public override int IsMatch()
    {
        var result = _workspace.Match(_text, 0, _pairs);
        Check(result);
        return result.Status == Pcre2MatchStatus.Match ? 1 : 0;
    }
    public override int Find()
    {
        var result = _workspace.Match(_text, 0, _pairs);
        Check(result);
        return result.Status == Pcre2MatchStatus.Match ? _pairs[0].End : -1;
    }
    public override int Count()
    {
        int count = 0, start = 0;
        while (start <= _text.Length)
        {
            var result = _workspace.Match(_text, start, _pairs);
            Check(result);
            if (result.Status != Pcre2MatchStatus.Match)
            {
                break;
            }

            count++;
            start = _pairs[0].End + (_pairs[0].End == _pairs[0].Start ? 1 : 0);
        }
        return count;
    }
    public override long Captures()
    {
        long sum = 0;
        var start = 0;
        while (start <= _text.Length)
        {
            var result = _workspace.Match(_text, start, _pairs);
            Check(result);
            if (result.Status != Pcre2MatchStatus.Match)
            {
                break;
            }

            foreach (var p in _pairs)
            {
                if (p.Participated)
                {
                    sum += 1 + p.Start + (p.End - p.Start);
                }
            }

            start = _pairs[0].End + (_pairs[0].End == _pairs[0].Start ? 1 : 0);
        }
        return sum;
    }
    public override void Dispose()
    {
        _workspace.Dispose();
        base.Dispose();
    }
}
