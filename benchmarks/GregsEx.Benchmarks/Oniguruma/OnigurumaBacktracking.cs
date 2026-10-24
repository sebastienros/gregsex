using BenchmarkDotNet.Attributes;
using GregsEx;

namespace GregsEx.Benchmarks.Oniguruma;

// Searches that backtrack over ambiguous alternations under a star, which Oniguruma explores exponentially: the
// Perl 6 grammar's q/Q quoting rule on a line from TextMateSharp's grammar fuzzer, and its reduced form.
[MemoryDiagnoser]
[ShortRunJob]
public class OnigurumaBacktracking
{
    private const string QuoteRule = @"(q|Q)(x|exec|w|words|ww|quotewords|v|val|q|single|qq|double|s|scalar|a|array|h|hash|f|function|c|closure|b|blackslash|regexp|substr|trans|codes|p|path)*\s*:?(x|exec|w|words|ww|quotewords|v|val|q|single|qq|double|s|scalar|a|array|h|hash|f|function|c|closure|b|blackslash|regexp|substr|trans|codes|p|path)*\s*\(\(";
    private const string FuzzLine = "wwqqwwwwwwwwwwwwwwwwwwwwquotewordswwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwthrows_okwwwwwwwwgetloginwwheredocwwwwwwww/>wwwwwwwwww{ww[[wwww((wwwwww wwww]wwKEEPwwxxwwww/wwwwwwygawwwwwwwwq4uncy70vwwwwww[wwrr9b4yndwwwwww*/wwwwwwwwwwsupersedewwwwwwwwwwwwwwww'wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww\twwww6n9d2gwwwwasecwwwwxxwwww>wwasinhwwwwwwwwwwwwßwwwwww\"wwwwwwwwwwwwwwwwww\"wwww6mww>wwwwwwwwforkwwwwvwwww'wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwGraphemewwRangewwwwwwis_deeplywwwwwwwwwwwwwwwwwwwwwwwwrewinddirwwwwww[wwwwwwwwwwwwwwwwww\"wwwwwwwwwwwwww]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]";

    private OnigurumaWorkspace _quote = null!;
    private OnigurumaWorkspace _reduced = null!;
    private string _reducedInput = null!;

    [GlobalSetup]
    public void Prepare()
    {
        _quote = OnigurumaPattern.Parse(QuoteRule, RegexProfiles.OnigurumaTextMate).CreateWorkspace();
        _reduced = OnigurumaPattern.Parse(@"q(w|ww)*(w|ww)*\(\(", RegexProfiles.OnigurumaTextMate).CreateWorkspace();
        _reducedInput = "q" + new string('w', 22) + "x((";
    }

    [Benchmark]
    public RegexSearchStatus Perl6QuoteRule() => _quote.Find(FuzzLine, 0, out _);

    [Benchmark]
    public RegexSearchStatus ReducedAmbiguousStars() => _reduced.Find(_reducedInput, 0, out _);
}
