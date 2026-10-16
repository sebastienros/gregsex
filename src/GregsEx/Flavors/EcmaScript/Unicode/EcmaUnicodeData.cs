using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.IR;

namespace GregsEx.Flavors.EcmaScript.Unicode;

// The process-wide immutable data is admitted once, before any managed table copy.
// Static primitive spans are image data. Their exact size plus a conservative
// bound for literal metadata is disclosed and charged in the cold admission.
// Subsequent factories charge only their 32-byte table reference, not a hidden copy.
internal sealed class EcmaUnicodeData : IEcmaUnicodeData
{
    private static readonly Lock Gate = new();
    private static EcmaUnicodeData? _shared;
    private static EcmaUnicodeData? _shared17;
    private readonly UnicodeVersion _version;
    private bool IsUnicode17 => _version == UnicodeVersion.Unicode17_0;
    public string DataId => IsUnicode17 ? "Unicode-17.0.0+Emoji-17.0" : "Unicode-16.0.0+Emoji-16.0";
    public EcmaCaseTables CaseTables { get; }
    internal long RetainedBytes => ImageBytes(_version) + CaseTables.RetainedBytes + 64;
    internal const long ReferenceBytes = 32;
    private EcmaUnicodeData(EcmaCaseTables tables, UnicodeVersion version)
    { CaseTables = tables; _version = version; }

    private static long ImageBytes(UnicodeVersion version) => version == UnicodeVersion.Unicode17_0
        ? GeneratedUnicode17Tables.StaticImageBytes + GeneratedUnicode17Tables.LiteralBytes
        : GeneratedUnicodeTables.StaticImageBytes + GeneratedUnicodeTables.LiteralBytes;

    internal static bool TryCreate(ref CompileBudget budget, out EcmaUnicodeData? data,
        out RegexDiagnostic diagnostic)
        => TryCreate(UnicodeVersion.Unicode16_0, ref budget, out data, out diagnostic);

    internal static bool TryCreate(UnicodeVersion version, ref CompileBudget budget, out EcmaUnicodeData? data,
        out RegexDiagnostic diagnostic)
    {
        UnicodeVersionPolicy.Validate(version);
        data = null;
        diagnostic = default;
        var source = new SourceRange(0, 0, RegexOffsetUnit.Utf16CodeUnits);
        lock (Gate)
        {
            ref var shared = ref (version == UnicodeVersion.Unicode17_0 ? ref _shared17 : ref _shared);
            var fold = version == UnicodeVersion.Unicode17_0
                ? GeneratedUnicode17Tables.UnicodeFold : GeneratedUnicodeTables.UnicodeFold;
            var upper = version == UnicodeVersion.Unicode17_0
                ? GeneratedUnicode17Tables.LegacyUppercase : GeneratedUnicodeTables.LegacyUppercase;
            var bytes = ReferenceBytes;
            if (shared is null)
            {
                bytes += ImageBytes(version) + 64 + 128 + 4L * (fold.Length + upper.Length);
            }

            if (bytes > budget.Limits.MaxPlanBytes)
            {
                diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, 0, source.Unit);
                return false;
            }
            if (!budget.TryCharge(1, source, out diagnostic) ||
                !budget.TryReserveBytes(ReferenceBytes, source, out diagnostic))
            {
                return false;
            }

            if (shared is null)
            {
                var imageBytes = ImageBytes(version) + 64;
                if (!budget.TryReserveBytes(imageBytes, source, out diagnostic) ||
                    !EcmaCaseTables.TryCreate(
                        MemoryMarshal.Cast<int, EcmaCaseMapping>(fold),
                        MemoryMarshal.Cast<int, EcmaCaseMapping>(upper),
                        ref budget, out var tables, out diagnostic))
                {
                    return false;
                }

                shared = new(tables!, version);
            }
            data = shared;
            return true;
        }
    }

    public bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value,
        bool unicodeSets, out EcmaProperty property)
    {
        property = default;
        string? canonical;
        EcmaPropertyKind kind;
        if (!value.IsEmpty)
        {
            if (name.SequenceEqual("General_Category") || name.SequenceEqual("gc"))
            { kind = EcmaPropertyKind.GeneralCategory; canonical = CategoryAlias(value); }
            else if (name.SequenceEqual("Script") || name.SequenceEqual("sc"))
            { kind = EcmaPropertyKind.Script; canonical = ScriptAlias(value); }
            else if (name.SequenceEqual("Script_Extensions") || name.SequenceEqual("scx"))
            { kind = EcmaPropertyKind.ScriptExtensions; canonical = ScriptAlias(value); }
            else
            {
                return false;
            }
        }
        else
        {
            kind = EcmaPropertyKind.GeneralCategory;
            canonical = CategoryAlias(name);
            if (canonical is null)
            { kind = EcmaPropertyKind.Binary; canonical = IsUnicode17
                ? GeneratedUnicode17Tables.BinaryAlias(name) : GeneratedUnicodeTables.BinaryAlias(name); }
            if (canonical is null && unicodeSets)
            { kind = EcmaPropertyKind.String; canonical = IsUnicode17
                ? GeneratedUnicode17Tables.StringAlias(name) : GeneratedUnicodeTables.StringAlias(name); }
        }
        if (canonical is null)
        {
            return false;
        }

        property = new(kind, canonical);
        return true;
    }

    public ReadOnlySpan<EcmaInterval> GetIntervals(EcmaProperty property)
    {
        var (offset, count) = IntervalSlice(property);
        var intervals = IsUnicode17 ? GeneratedUnicode17Tables.Intervals : GeneratedUnicodeTables.Intervals;
        return MemoryMarshal.Cast<int, EcmaInterval>(intervals).Slice(offset, count);
    }

    public ReadOnlySpan<EcmaStringSlice> GetStrings(EcmaProperty property)
    {
        if (property.Kind != EcmaPropertyKind.String)
        { _ = IntervalSlice(property); return []; }
        var (offset, count) = IsUnicode17 ? GeneratedUnicode17Tables.StringSlice(property.CanonicalName)
            : GeneratedUnicodeTables.StringSlice(property.CanonicalName);
        var slices = IsUnicode17 ? GeneratedUnicode17Tables.StringSlices : GeneratedUnicodeTables.StringSlices;
        return MemoryMarshal.Cast<int, EcmaStringSlice>(slices).Slice(offset, count);
    }

    public ReadOnlySpan<int> GetStringCodePoints(EcmaProperty property)
    {
        _ = IntervalSlice(property);
        // String slices use global offsets; consumers always access only their slices.
        return property.Kind == EcmaPropertyKind.String
            ? IsUnicode17 ? GeneratedUnicode17Tables.StringCodePoints : GeneratedUnicodeTables.StringCodePoints
            : [];
    }

    private string? CategoryAlias(ReadOnlySpan<char> name) => IsUnicode17
        ? GeneratedUnicode17Tables.CategoryAlias(name) : GeneratedUnicodeTables.CategoryAlias(name);
    private string? ScriptAlias(ReadOnlySpan<char> name) => IsUnicode17
        ? GeneratedUnicode17Tables.ScriptAlias(name) : GeneratedUnicodeTables.ScriptAlias(name);
    private (int Offset, int Count) IntervalSlice(EcmaProperty property) => IsUnicode17
        ? GeneratedUnicode17Tables.IntervalSlice(property) : GeneratedUnicodeTables.IntervalSlice(property);

    public bool IsIdentifierStart(int codePoint) => codePoint is '$' or '_' ||
        Contains(GetIntervals(new(EcmaPropertyKind.Binary, "ID_Start")), codePoint);
    public bool IsIdentifierContinue(int codePoint) => codePoint is '$' or '_' or 0x200c or 0x200d ||
        Contains(GetIntervals(new(EcmaPropertyKind.Binary, "ID_Continue")), codePoint);

    private static bool Contains(ReadOnlySpan<EcmaInterval> intervals, int value)
    {
        if ((uint)value > 0x10ffff)
        {
            return false;
        }

        int lo = 0, hi = intervals.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            var interval = intervals[mid];
            if (value < interval.First)
            {
                hi = mid - 1;
            }
            else if (value > interval.Last)
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
}
