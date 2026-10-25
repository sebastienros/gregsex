using GregsEx.SourceGeneration;

namespace GregsEx.Tests.Execution.Generated;

// Patterns compiled by the GregsEx source generator at build time. Each one is checked against the interpreter.
internal static partial class GeneratedPatterns
{
    internal const string Ing = "[a-z]+(ing|ed)";

    [GeneratedPattern(Ing)]
    public static partial RegexPattern CoreIng();

    [GeneratedPattern(Ing, EcmaScriptFlags.None)]
    public static partial RegexPattern EcmaIng();

    [GeneratedPattern(Ing, EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Global)]
    public static partial RegexPattern EcmaIngIgnoreCase();

    [GeneratedPattern(Ing, GeneratedPatternFlavor.Xsd)]
    public static partial RegexPattern XsdIng();

    [GeneratedPattern(Ing, XPathFlags.None)]
    public static partial RegexPattern XPathIng();

    [GeneratedPattern("^[a-z]+(ing|ed)$", XPathFlags.Multiline)]
    public static partial RegexPattern XPathIngLines();

    [GeneratedPattern(Ing, Pcre2CompileFlags.None)]
    public static partial RegexPattern Pcre2Ing();

    [GeneratedPattern("^[a-z]+(ing|ed)$", Pcre2CompileFlags.Multiline)]
    public static partial RegexPattern Pcre2IngLines();

    [GeneratedPattern("(a|aa)*c")]
    public static partial RegexPattern CoreAmbiguous();

    internal const string Look = @"(?<!\w)(?!(?:ing|ed)\b)([a-z]+)(?=(ing|ed)\b)";

    [GeneratedPattern(Look, EcmaScriptFlags.None)]
    public static partial RegexPattern EcmaLook();

    [GeneratedPattern(Look, Pcre2CompileFlags.None)]
    public static partial RegexPattern Pcre2Look();

    [GeneratedPattern(@"\G(?<=\s|^)[a-z]+(?= \d)", Pcre2CompileFlags.None)]
    public static partial RegexPattern Pcre2SearchStart();

    [GeneratedPattern(@"(?<=\d )(?:(?=w)([a-z]+)|(?!w)([a-z]+))", EcmaScriptFlags.Sticky)]
    public static partial RegexPattern EcmaStickyLook();

    // A literal is served by a hand-written accelerated path, so the generator leaves it on the interpreter.
    [GeneratedPattern("needle")]
    public static partial RegexPattern CoreLiteral();

    internal static partial class Nested
    {
        [GeneratedPattern(@"([a-z]+) ([0-9]+)(ing|ed)")]
        internal static partial RegexPattern Range();
    }
}
