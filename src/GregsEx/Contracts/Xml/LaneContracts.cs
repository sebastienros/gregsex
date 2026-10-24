#nullable enable
using GregsEx.IR;

namespace GregsEx.Contracts.Xml;

internal enum XmlDialect { Xsd, XPath }
internal enum XmlNodeKind { Empty, Literal, Class, Sequence, Alternative, Repeat, Capture, Backreference, Start, End }
// Children are bottom-up IDs; unused X/Y=-1, Value=0. Max=-1 only for an
// unbounded Repeat. Capture/Backreference Value is positive group ID. XSD has
// no capture/backreference/anchor/lazy nodes. Source is ORIGINAL UTF-16 pattern.
internal readonly record struct XmlNode(XmlNodeKind Kind, int X, int Y, int Value,
    int Min, int Max, bool Lazy, SourceRange Source);
internal readonly record struct XmlInterval(int First, int Last);
internal readonly record struct XmlSetSlice(int Offset, int Count);
// Generated ordinal identity of FULL default lowercase/uppercase sequences.
// Keys <=0x10FFFF identify singleton scalars; larger keys identify expansions.
// Case-variant is equal LowerKey OR equal UpperKey, never ECMA C/S folding.
internal readonly record struct XmlCaseMapping(int Scalar, int LowerKey, int UpperKey);
internal enum XmlPredefinedClass { Whitespace, Digit, Word, NameStart, NameChar }

internal interface IXmlUnicodeData
{
    string DataId { get; }
    ReadOnlySpan<XmlInterval> Alphabet { get; }
    // Exact XSD category spelling; Cs and long aliases are not grammar.
    bool TryGetCategory(ReadOnlySpan<char> name, out ReadOnlySpan<XmlInterval> intervals);
    // Caller handles XSD unknown-block default versus XPath FORX0002.
    // Exact normalized Unicode16 name plus frozen explicit legacy aliases.
    bool TryGetBlock(ReadOnlySpan<char> normalizedName, out ReadOnlySpan<XmlInterval> intervals);
    ReadOnlySpan<XmlInterval> GetPredefined(XmlPredefinedClass kind);
    // Bound once; matching loops use direct spans and charged static helpers.
    ReadOnlySpan<XmlCaseMapping> CaseMappings { get; }
}

internal enum XmlSetOperation { Union, Subtract, Complement, CaseVariants }
internal interface IXmlClassAlgebra
{
    // Outputs OWNED normalized intervals clipped to XML alphabet; work and
    // every temporary/final allocation admitted through caller's SAME budget.
    // CaseVariants takes only left; Complement is alphabet-minus-left.
    bool TryApply(XmlSetOperation operation, ReadOnlySpan<XmlInterval> left,
        ReadOnlySpan<XmlInterval> right, IXmlUnicodeData data, ref CompileBudget budget,
        SourceRange source, out XmlInterval[]? intervals, out RegexDiagnostic diagnostic);
}

#if !GREGSEX_GENERATOR
internal interface IXmlTextReader<TUnit> where TUnit : unmanaged
{
    // Whole subject encoding AND XML Char validation before start-boundary
    // validation; every inspected native unit charged in the same RunState.
    RegexSearchStatus Validate(ReadOnlySpan<TUnit> input, int startAt, ref RunState state);
    bool TryRead(ReadOnlySpan<TUnit> input, int offset, out int scalar, out int width);
}
#endif

internal interface IXmlCompiler
{
    bool TryCompile(ReadOnlySpan<char> pattern, RegexProfile profile, RegexInputKind inputKind,
        IXmlUnicodeData unicode, IXmlClassAlgebra algebra, ref CompileBudget budget,
        out XmlProgram? program, out XmlRegexDiagnostic diagnostic);
}

internal interface IXmlExecutor
{
    XmlProgram Program { get; }
    RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, int frameCapacity);
#if !GREGSEX_GENERATOR
    // Both methods receive the SAME positive finite frameCapacity. Search
    // enforces it even when buffers have spare capacity, and checks that each
    // supplied logical partition covers GetRequiredCapacity for this request.
    // Public runtime forwards its admitted fixed 4096-frame ceiling.
    // Only internal tests/custom bindings configure another finite ceiling.
    // One virtual boundary per invocation; readers static-specialized. XSD
    // requires startAt0, membership only, whole-value range/group0 on success.
    // XPath uses ordered lower-bound search, original-input LF anchors, final
    // captures, last participating subgroup, unmatched backreference=empty.
    RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, int frameCapacity, ref RunState state, Span<RegexCapture> groups,
        out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, IXmlTextReader<TUnit>;
#endif
}

internal interface IXmlExecutorFactory
{
    bool TryCreate(XmlProgram program, IXmlUnicodeData unicode, ref CompileBudget budget,
        out IXmlExecutor? executor, out RegexDiagnostic diagnostic);
}

internal static class XmlAlphabet
{
    internal static bool Contains(int scalar) => scalar is 0x9 or 0xA or 0xD or
        (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF);
    internal static bool ContainsRange(XmlInterval interval) => interval.First <= interval.Last &&
        (interval.First >= 0x9 && interval.Last <= 0xA ||
         interval.First == 0xD && interval.Last == 0xD ||
         interval.First >= 0x20 && interval.Last <= 0xD7FF ||
         interval.First >= 0xE000 && interval.Last <= 0xFFFD ||
         interval.First >= 0x10000 && interval.Last <= 0x10FFFF);
}
