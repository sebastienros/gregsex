using GregsEx.SourceGeneration;

namespace GregsEx.Benchmarks.Compiled;

// The shape patterns compiled by the GregsEx source generator at build time (the "gregsex-core-codegen" engine).
internal static partial class GeneratedShapes
{
    [GeneratedPattern(@"(the|and|of) ([a-z]+)")] private static partial RegexPattern AltCaptures();
    [GeneratedPattern(@"[a-z]+(ing|ed)")] private static partial RegexPattern LoopThenAlt();
    [GeneratedPattern(@"(ERROR|WARN) ([a-z]+)")] private static partial RegexPattern LogLevel();
    [GeneratedPattern(@"(wi|ri)n[a-z]*")] private static partial RegexPattern AltPrefixLoop();
    [GeneratedPattern(@"t[a-z]*e")] private static partial RegexPattern WildcardSpan();
    [GeneratedPattern(@"(a|b)*c")] private static partial RegexPattern AltStarTail();
    [GeneratedPattern(@"([a-z]+)=([a-z0-9]+)")] private static partial RegexPattern KvCaptures();

    internal static readonly Dictionary<string, Func<RegexPattern>> Map = new()
    {
        [@"(the|and|of) ([a-z]+)"] = AltCaptures,
        [@"[a-z]+(ing|ed)"] = LoopThenAlt,
        [@"(ERROR|WARN) ([a-z]+)"] = LogLevel,
        [@"(wi|ri)n[a-z]*"] = AltPrefixLoop,
        [@"t[a-z]*e"] = WildcardSpan,
        [@"(a|b)*c"] = AltStarTail,
        [@"([a-z]+)=([a-z0-9]+)"] = KvCaptures,
    };
}
