using GregsEx.Contracts;
using GregsEx.Contracts.Xml;

namespace GregsEx.Tests.Contracts.Xml.Declarations;

// NONSHIPPING signatures of future concrete function surfaces. Abstract
// declarations have no implementation, throw-stub, registration or engine.
// Host owner ships concrete static functions with these arguments/results.
internal abstract class XPathFunctions
{
    public abstract bool Matches(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default);
    public abstract string Replace(string? input, string pattern, string replacement, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null);
    public abstract IReadOnlyList<string> Tokenize(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null);
    public abstract IReadOnlyList<string> Tokenize(string? input,
        RegexMatchOptions match = default, XPathHostLimits? host = null);
    public abstract XPathAnalyzeResult AnalyzeString(string? input, string pattern, string flags = "",
        RegexCompileOptions? compile = null, RegexMatchOptions match = default, XPathHostLimits? host = null);
}

internal abstract class XsdFunctions
{
    public abstract bool IsValid(string lexicalValue, string pattern,
        RegexCompileOptions? compile = null, RegexMatchOptions match = default);
}

internal static class CompileSamples
{
    internal static bool Profiles(XsdFunctions xsd, XPathFunctions xpath)
    {
        _ = xsd.IsValid("a:b", @"\i\c*");
        _ = xpath.Replace("z", "(z)", "$12");
        _ = xpath.Tokenize(",a,", ",");
        _ = xpath.Tokenize("  a b  ");
        var result = xpath.AnalyzeString("ab", "(a(b))");
        foreach (var node in result.Nodes)
        {
            _ = result.Input.AsSpan(node.Range.Index, node.Range.Length);
        }

        return xpath.Matches(null, "a*", "ssimxq");
    }
    internal static RegexSearchStatus Borrowed(RegexPattern pattern, ReadOnlySpan<byte> input)
    {
        using var workspace = pattern.CreateWorkspace();
        Span<RegexCapture> groups = stackalloc RegexCapture[3];
        var status = workspace.FindCaptures(input, 0, groups, out var range);
        if (status == RegexSearchStatus.Match)
        {
            _ = input.Slice(range.Index, range.Length);
        }

        return status;
    }
    internal static bool Parser(IXmlCompiler compiler, IXmlUnicodeData data, IXmlClassAlgebra algebra,
        ReadOnlySpan<char> pattern, ref CompileBudget budget, out XmlProgram? program, out XmlRegexDiagnostic diagnostic)
        => compiler.TryCompile(pattern, new XPathProfile(XPathFlags.IgnoreCase), RegexInputKind.Utf8,
            data, algebra, ref budget, out program, out diagnostic);
    internal static RegexSearchStatus Executor<TUnit, TReader>(IXmlExecutor executor,
        ReadOnlySpan<TUnit> input, ref RunState state, Span<RegexCapture> captures, out RegexMatchRange range)
        where TUnit : unmanaged where TReader : struct, IXmlTextReader<TUnit>
        => executor.Search<TUnit, TReader>(input, 0, SearchOperation.Captures, 4096, ref state, captures, out range);
}
