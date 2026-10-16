using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.XmlShared;

// Generated primitive spans are immutable image data. Every admission charges the
// entire fixed data image/literal metadata conservatively, even when already warm.
// No process cache, mutable exposed arrays, culture, or ambient Unicode APIs.
internal sealed class XmlUnicodeData : IXmlUnicodeData
{
    private readonly UnicodeVersion _version;
    private bool IsUnicode17 => _version == UnicodeVersion.Unicode17_0;
    private XmlUnicodeData(UnicodeVersion version) => _version = version;
    public string DataId => IsUnicode17 ? "Unicode-17.0.0+XML-1.0-5e" : "Unicode-16.0.0+XML-1.0-5e";
    internal const long RetainedBytes = GeneratedXmlTables.ImageBytes + GeneratedXmlTables.LiteralBytes + 96;
    internal static long GetRetainedBytes(UnicodeVersion version) => version == UnicodeVersion.Unicode17_0
        ? GeneratedXml17Tables.ImageBytes + GeneratedXml17Tables.LiteralBytes + 96 : RetainedBytes;
    internal static bool TryCreate(ref CompileBudget budget, out XmlUnicodeData? data,
        out RegexDiagnostic diagnostic)
        => TryCreate(UnicodeVersion.Unicode16_0, ref budget, out data, out diagnostic);

    internal static bool TryCreate(UnicodeVersion version, ref CompileBudget budget, out XmlUnicodeData? data,
        out RegexDiagnostic diagnostic)
    {
        UnicodeVersionPolicy.Validate(version);
        data = null;
        var source = new SourceRange(0, 0, RegexOffsetUnit.Utf16CodeUnits);
        diagnostic = default;
        var bytes = GetRetainedBytes(version);
        if (bytes > budget.Limits.MaxPlanBytes - budget.ReservedBytes)
        {
            diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, 0, source.Unit);
            return false;
        }
        if (!budget.TryCharge(1, source, out diagnostic) ||
            !budget.TryReserveBytes(bytes, source, out diagnostic))
        {
            return false;
        }

        data = new(version);
        return true;
    }
    public ReadOnlySpan<XmlInterval> Alphabet => Slice(IsUnicode17 ? GeneratedXml17Tables.Alphabet : GeneratedXmlTables.Alphabet);
    public ReadOnlySpan<XmlCaseMapping> CaseMappings =>
        MemoryMarshal.Cast<int, XmlCaseMapping>(IsUnicode17 ? GeneratedXml17Tables.CaseMappings : GeneratedXmlTables.CaseMappings);
    public bool TryGetCategory(ReadOnlySpan<char> name, out ReadOnlySpan<XmlInterval> intervals)
    {
        var found = IsUnicode17 ? GeneratedXml17Tables.Category(name, out var slice)
            : GeneratedXmlTables.Category(name, out slice);
        intervals = found ? Slice(slice) : [];
        return found;
    }
    public bool TryGetBlock(ReadOnlySpan<char> normalizedName, out ReadOnlySpan<XmlInterval> intervals)
    {
        var found = IsUnicode17 ? GeneratedXml17Tables.Block(normalizedName, out var slice)
            : GeneratedXmlTables.Block(normalizedName, out slice);
        intervals = found ? Slice(slice) : [];
        return found;
    }
    public ReadOnlySpan<XmlInterval> GetPredefined(XmlPredefinedClass kind) =>
        Slice(IsUnicode17 ? GeneratedXml17Tables.Predefined(kind) : GeneratedXmlTables.Predefined(kind));
    private ReadOnlySpan<XmlInterval> Slice(XmlSetSlice slice) =>
        MemoryMarshal.Cast<int, XmlInterval>(IsUnicode17 ? GeneratedXml17Tables.Intervals : GeneratedXmlTables.Intervals)
            .Slice(slice.Offset, slice.Count);

    internal static bool Contains(ReadOnlySpan<XmlInterval> intervals, int scalar)
    {
        int lo = 0, hi = intervals.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var interval = intervals[mid];
            if (scalar < interval.First)
            {
                hi = mid - 1;
            }
            else if (scalar > interval.Last)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }
    // Missing sparse rows are identity singleton mappings. Bind CaseMappings once
    // before a matching loop; the static helper never performs virtual dispatch.
    internal static XmlCaseMapping CaseMapping(ReadOnlySpan<XmlCaseMapping> mappings, int scalar)
    {
        int lo = 0, hi = mappings.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var row = mappings[mid];
            if (scalar < row.Scalar)
            {
                hi = mid - 1;
            }
            else if (scalar > row.Scalar)
            {
                lo = mid + 1;
            }
            else
            {
                return row;
            }
        }
        return new(scalar, scalar, scalar);
    }
    internal static bool CaseEquals(ReadOnlySpan<XmlCaseMapping> mappings, int left, int right)
    {
        var a = CaseMapping(mappings, left);
        var b = CaseMapping(mappings, right);
        return a.LowerKey == b.LowerKey || a.UpperKey == b.UpperKey;
    }
}
