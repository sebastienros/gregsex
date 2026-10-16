#if JINT_QUICKJS
using Jint.Runtime.RegExp;

namespace GregsEx.Benchmarks.Acceleration.Corpus;

/// <summary>Jint's QuickJS-derived bytecode engine (compiled from source). Execute materializes Substring/group results exactly as Jint's callers see them.</summary>
internal sealed class JintEngine : CaseEngine
{
    private readonly JintRegExpEngine _engine;
    private readonly string _text;

    public JintEngine(CorpusCase c, string text)
    {
        _text = text;
        var flags = c.Flags.TryGetValue("ECMAScript", out var f) && f.Contains('u') ? RegExpFlags.Unicode : RegExpFlags.None;
        try
        { _engine = JintRegExpEngine.Compile(c.Pattern, flags); }
        catch (RegExpSyntaxException e) { throw new UnsupportedEngineException(e.Message); }
    }

    public override int IsMatch() => _engine.IsMatch(_text, 0) ? 1 : 0;

    public override int Find()
    {
        var r = _engine.Execute(_text, 0);
        return r.Success ? r.Index + r.Length : -1;
    }

    public override int Count()
    {
        var count = 0;
        for (var at = 0; at <= _text.Length;)
        {
            var r = _engine.Execute(_text, at);
            if (!r.Success)
            {
                break;
            }

            count++;
            at = r.Length == 0 ? r.Index + 1 : r.Index + r.Length;
        }
        return count;
    }

    public override long Captures()
    {
        long sum = 0;
        for (var at = 0; at <= _text.Length;)
        {
            var r = _engine.Execute(_text, at);
            if (!r.Success)
            {
                break;
            }

            if (r.Groups is { } groups)
            {
                foreach (var g in groups)
                {
                    if (g.Success)
                    {
                        sum += 1 + g.Index + g.Length;
                    }
                }
            }

            at = r.Length == 0 ? r.Index + 1 : r.Index + r.Length;
        }
        return sum;
    }

    public override void Dispose() => base.Dispose();
}
#endif
