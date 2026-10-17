#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.IR;

namespace GregsEx.Flavors.Pcre2.Parsing.Syntax;

// Stateless/shareable compile boundary. All mutable storage belongs to one Parse.
internal sealed class Pcre2SyntaxParser : IPcre2SyntaxParser
{
    public bool TryParse(ReadOnlySpan<char> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(data);
        syntax = null;
        diagnostic = default;
        var initial = budget.ReservedBytes;
        try
        {
            var source = new SourceRange(0, pattern.Length, RegexOffsetUnit.Utf16CodeUnits);
            CheckLength(pattern.Length, ref budget, source);
            var utf = profile.Utf || HasUtfDirective(pattern, profile, ref budget, source);
            var nativeLength = pattern.Length;
            if (profile.Width == Pcre2CodeUnitWidth.Eight && utf)
            {
                nativeLength = 0;
                for (var i = 0; i < pattern.Length; i++)
                {
                    SyntaxFailure.Charge(ref budget, 1, source);
                    int cp = pattern[i];
                    if (cp is >= 0xD800 and <= 0xDBFF && i + 1 < pattern.Length && pattern[i + 1] is >= '\uDC00' and <= '\uDFFF')
                    {
                        cp = 0x10000 + ((cp - 0xD800) << 10) + pattern[++i] - 0xDC00;
                    }
                    else if (cp is >= 0xD800 and <= 0xDFFF)
                    {
                        EncodingFailure(i, i, source.Unit, 0);
                    }

                    var width = cp < 128 ? 1 : cp < 2048 ? 2 : cp < 65536 ? 3 : 4;
                    if (width > budget.Limits.MaxPatternUnits - nativeLength)
                    {
                        throw new SyntaxFailure(new(
                        new(RegexDiagnosticCode.PatternLimitExceeded, i, 1, source.Unit), Pcre2DiagnosticKind.CompileLimit));
                    }

                    nativeLength += width;
                }
                CheckLength(nativeLength, ref budget, source);
            }
            CheckPlan(nativeLength, profile.Width, ref budget, source);
            SyntaxFailure.Reserve(ref budget, checked(64L + 4L * nativeLength + 4L * (nativeLength + 1)), source);
            int[] units = new int[nativeLength], map = new int[nativeLength + 1];
            var n = 0;
            for (var i = 0; i < pattern.Length; i++)
            {
                SyntaxFailure.Charge(ref budget, 1, source);
                int cp = pattern[i], start = i;
                if (profile.Width == Pcre2CodeUnitWidth.Eight && utf)
                {
                    if (cp is >= 0xD800 and <= 0xDBFF)
                    {
                        cp = 0x10000 + ((cp - 0xD800) << 10) + pattern[++i] - 0xDC00;
                    }

                    var width = cp < 128 ? 1 : cp < 2048 ? 2 : cp < 65536 ? 3 : 4;
                    map[n] = start;
                    if (width == 1)
                    {
                        units[n++] = cp;
                    }
                    else
                    {
                        units[n++] = (width == 2 ? 0xC0 : width == 3 ? 0xE0 : 0xF0) | (cp >> (6 * (width - 1)));
                        for (var k = width - 2; k >= 0; k--)
                        { map[n] = start; units[n++] = 0x80 | ((cp >> (6 * k)) & 63); }
                    }
                    map[n] = i + 1;
                }
                else
                {
                    if (profile.Width == Pcre2CodeUnitWidth.Eight && cp > 255)
                    {
                        EncodingFailure(i, i, source.Unit, 0);
                    }

                    map[n] = i;
                    units[n++] = cp;
                    map[n] = i + 1;
                }
            }
            return Parse(units, map, pattern.Length, source.Unit, profile, data, ref budget, out syntax, out diagnostic);
        }
        catch (SyntaxFailure failure) { diagnostic = failure.Diagnostic; return false; }
        finally { budget.ReleaseBytes(budget.ReservedBytes - initial - (syntax?.RetainedBytes ?? 0)); }
    }

    public bool TryParse(ReadOnlySpan<byte> pattern, Pcre2Profile profile, IPcre2Data data,
        ref CompileBudget budget, out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(data);
        syntax = null;
        diagnostic = default;
        var initial = budget.ReservedBytes;
        try
        {
            var source = new SourceRange(0, pattern.Length, RegexOffsetUnit.Bytes);
            if (profile.Width != Pcre2CodeUnitWidth.Eight)
            {
                EncodingFailure(0, 0, source.Unit, 0);
            }

            CheckLength(pattern.Length, ref budget, source);
            CheckPlan(pattern.Length, profile.Width, ref budget, source);
            SyntaxFailure.Reserve(ref budget, checked(68L + 8L * pattern.Length), source);
            int[] units = new int[pattern.Length], map = new int[pattern.Length + 1];
            for (var i = 0; i < pattern.Length; i++)
            { SyntaxFailure.Charge(ref budget, 1, source); units[i] = pattern[i]; map[i] = i; }
            map[^1] = pattern.Length;
            return Parse(units, map, pattern.Length, source.Unit, profile, data, ref budget, out syntax, out diagnostic);
        }
        catch (SyntaxFailure failure) { diagnostic = failure.Diagnostic; return false; }
        finally { budget.ReleaseBytes(budget.ReservedBytes - initial - (syntax?.RetainedBytes ?? 0)); }
    }
    private static bool Parse(int[] units, int[] map, int sourceLength, RegexOffsetUnit sourceUnit,
        Pcre2Profile profile, IPcre2Data data, ref CompileBudget budget,
        out Pcre2Program? syntax, out Pcre2Diagnostic diagnostic)
    {
        var parser = new SyntaxReader(units, map, sourceLength, sourceUnit, profile, data, ref budget);
#if NETSTANDARD2_0
        try
        { return parser.Parse(out syntax, out diagnostic); }
        finally { budget = parser.Budget; }
#else
        return parser.Parse(out syntax, out diagnostic);
#endif
    }
    private static void CheckLength(int length, ref CompileBudget budget, SourceRange source)
    {
        if (length > budget.Limits.MaxPatternUnits)
        {
            throw new SyntaxFailure(new(
            new(RegexDiagnosticCode.PatternLimitExceeded, source.Offset, source.Length, source.Unit), Pcre2DiagnosticKind.CompileLimit));
        }
    }
    private static void CheckPlan(int nativeLength, Pcre2CodeUnitWidth width, ref CompileBudget budget, SourceRange source)
    {
        if (1604L + (width == Pcre2CodeUnitWidth.Eight ? 9L : 10L) * nativeLength > budget.Limits.MaxPlanBytes)
        {
            throw new SyntaxFailure(new(
            new(RegexDiagnosticCode.PlanLimitExceeded, source.Offset, source.Length, source.Unit), Pcre2DiagnosticKind.CompileLimit));
        }
    }
    private static bool HasUtfDirective(ReadOnlySpan<char> pattern, Pcre2Profile profile, ref CompileBudget budget, SourceRange source)
    {
        if ((profile.Flags & Pcre2CompileFlags.Literal) != 0)
        {
            return false;
        }

        while (pattern.StartsWith("(*"))
        {
            var end = pattern.IndexOf(')');
            SyntaxFailure.Charge(ref budget, end < 0 ? pattern.Length : end + 1, source);
            if (end < 0)
            {
                break;
            }

            var name = pattern.Slice(2, end - 2);
            if (name.SequenceEqual("UTF") || name.SequenceEqual("UTF8"))
            {
                return true;
            }
            // Only recognized start directives can precede UTF. Reader owns admission.
            if (!IsDirective(name))
            {
                break;
            }

            pattern = pattern[(end + 1)..];
        }
        return false;
    }
    private static bool IsDirective(ReadOnlySpan<char> name)
    {
        if (name.SequenceEqual("UCP") || name.SequenceEqual("CR") || name.SequenceEqual("LF") || name.SequenceEqual("CRLF") ||
            name.SequenceEqual("ANY") || name.SequenceEqual("ANYCRLF") || name.SequenceEqual("NUL") ||
            name.SequenceEqual("BSR_ANYCRLF") || name.SequenceEqual("BSR_UNICODE") || name.SequenceEqual("NO_AUTO_POSSESS") ||
            name.SequenceEqual("NO_DOTSTAR_ANCHOR") || name.SequenceEqual("NO_START_OPT") || name.SequenceEqual("NO_JIT") ||
            name.SequenceEqual("NOTEMPTY") || name.SequenceEqual("NOTEMPTY_ATSTART") || name.SequenceEqual("CASELESS_RESTRICT") ||
            name.SequenceEqual("TURKISH_CASING"))
        {
            return true;
        }

        var equal = name.IndexOf('=');
        if (equal < 0)
        {
            return false;
        }

        var key = name[..equal];
        if (!key.SequenceEqual("LIMIT_MATCH") && !key.SequenceEqual("LIMIT_DEPTH") && !key.SequenceEqual("LIMIT_RECURSION") && !key.SequenceEqual("LIMIT_HEAP"))
        {
            return false;
        }

        var digits = name[(equal + 1)..];
        if (digits.IsEmpty)
        {
            return false;
        }

        uint n = 0;
        foreach (var c in digits)
        { if (c is < '0' or > '9' || n > uint.MaxValue / 10 - 1) { return false; } n = n * 10 + (uint)(c - '0'); }
        return true;
    }
    private static void EncodingFailure(int sourceOffset, int nativeOffset, RegexOffsetUnit unit, int code)
        => throw new SyntaxFailure(new(new(RegexDiagnosticCode.InvalidPatternEncoding, sourceOffset, 1, unit),
            Pcre2DiagnosticKind.PatternEncoding, (Pcre2ErrorCode)code, NativeOffset: nativeOffset));
}
