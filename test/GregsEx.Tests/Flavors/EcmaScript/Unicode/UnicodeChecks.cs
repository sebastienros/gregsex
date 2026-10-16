using System.Globalization;
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Unicode;

namespace GregsEx.Tests.Flavors.EcmaScript.Unicode;

internal static class UnicodeChecks
{
    private static void Check(bool value, string label)
    {
        if (!value)
        {
            throw new InvalidOperationException(label);
        }
    }
    internal static void Run(UnicodeVersion version = UnicodeVersion.Unicode16_0)
    {
        var added = version == UnicodeVersion.Unicode17_0;
        Check(Unsafe.SizeOf<EcmaInterval>() == 8 && Unsafe.SizeOf<EcmaStringSlice>() == 8 &&
            Unsafe.SizeOf<EcmaCaseMapping>() == 8, "primitive span ABI layout");
        var tiny = new CompileBudget(new RegexCompileLimits(maxPlanBytes: 1));
        Check(!EcmaUnicodeData.TryCreate(version, ref tiny, out _, out var diag) && diag.Code == RegexDiagnosticCode.PlanLimitExceeded, "plan admission");
        var memory = new CompileBudget(new RegexCompileLimits(maxCompileMemoryBytes: 1));
        Check(!EcmaUnicodeData.TryCreate(version, ref memory, out _, out diag) && diag.Code == RegexDiagnosticCode.CompileMemoryLimitExceeded, "memory admission");
        var exhausted = new CompileBudget(new RegexCompileLimits(maxCompileWork: 1));
        Check(exhausted.TryCharge(1, new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out _), "work setup");
        Check(!EcmaUnicodeData.TryCreate(version, ref exhausted, out _, out diag) && diag.Code == RegexDiagnosticCode.CompileWorkLimitExceeded, "work admission");
        var budget = new CompileBudget(new());
        Check(EcmaUnicodeData.TryCreate(version, ref budget, out var data, out _), "cold/shared data admission");
        var next = new CompileBudget(new());
        Check(EcmaUnicodeData.TryCreate(version, ref next, out var same, out _) && ReferenceEquals(data, same) &&
            next.ReservedBytes == EcmaUnicodeData.ReferenceBytes, "once-shared reference");
        Check(data!.DataId == new EcmaScriptProfile(unicodeVersion: version).DataId && data.RetainedBytes < 400000, "data identity and bounded retention");
        var aliases = added ? Unicode17Inventory.Aliases : UnicodeInventory.Aliases;
        var boundaries = added ? Unicode17Inventory.Boundaries : UnicodeInventory.Boundaries;
        foreach (var (kind, alias, canonical) in aliases)
        {
            var name = kind switch { "Script" => "sc", "ScriptExtensions" => "scx", _ => alias };
            var value = kind is "Script" or "ScriptExtensions" ? alias : "";
            Check(data.TryResolveProperty(name, value, true, out var property) && property.Kind.ToString() == kind &&
                property.CanonicalName == canonical, "alias:" + kind + ":" + alias);
            Check(!data.TryResolveProperty(name + " ", value, true, out _), "exact alias spelling:" + alias);
            if (kind == "GeneralCategory")
            {
                Check(data.TryResolveProperty("gc", alias, false, out var gp) && gp == property &&
                    data.TryResolveProperty("General_Category", alias, false, out gp) && gp == property, "named category aliases");
            }
            if (kind is "Script" or "ScriptExtensions")
            {
                Check(data.TryResolveProperty(kind == "Script" ? "Script" : "Script_Extensions", alias, false, out var sp) && sp == property, "long property aliases");
            }

            if (kind == "String")
            {
                Check(!data.TryResolveProperty(alias, "", false, out _), "u string rejection");
            }
        }
        int intervals = 0, strings = 0;
        foreach (var (kind, name, first, last, count) in boundaries)
        {
            Check(Enum.TryParse<EcmaPropertyKind>(kind, out var pk), "kind");
            var p = new EcmaProperty(pk, name);
            var ranges = data.GetIntervals(p);
            Check(ranges.Length == count, "interval count:" + name);
            if (count > 0)
            {
                Check(ranges[0].First == first && ranges[^1].Last == last, "source endpoints:" + name);
            }

            var previous = -2;
            foreach (var r in ranges)
            {
                Check(r.First > previous + 1 && r.First >= 0 && r.Last >= r.First && r.Last <= 0x10ffff, "ordered interval:" + name);
                Check(In(ranges, r.First) && In(ranges, r.Last), "positive boundary:" + name);
                if (r.First > 0 && r.First > previous + 1)
                {
                    Check(!In(ranges, r.First - 1), "negative boundary:" + name);
                }

                previous = r.Last;
                intervals++;
            }
            if (pk == EcmaPropertyKind.String)
            {
                Check(!Has(data, name, []) && !Has(data, name, ['A', 'A']), "empty/valid nonmember string:" + name);
            }

            var ss = data.GetStrings(p);
            var cps = data.GetStringCodePoints(p);
            ReadOnlySpan<int> prev = [];
            foreach (var slice in ss)
            {
                Check(slice.Length > 1 && slice.Offset >= 0 && slice.Offset <= cps.Length - slice.Length, "bounded string:" + name);
                var s = cps.Slice(slice.Offset, slice.Length);
                foreach (var cp in s)
                {
                    Check((uint)cp <= 0x10ffff && cp is not (>= 0xd800 and <= 0xdfff), "emoji scalar");
                }

                Check(prev.IsEmpty || prev.Length > s.Length || prev.Length == s.Length && Compare(prev, s) < 0, "distinct longest-first strings:" + name);
                prev = s;
                strings++;
            }
        }
        Check(intervals == (added ? 23205 : 22925) && strings == (added ? 5520 : 5208) &&
            boundaries.Length == (added ? 450 : 442) && aliases.Length == (added ? 877 : 861), "complete inventory sizes");
        Check(data.TryResolveProperty("gc", "Cs", false, out var surrogate) && In(data.GetIntervals(surrogate), 0xd800) && In(data.GetIntervals(surrogate), 0xdfff), "surrogate category");
        Check(data.TryResolveProperty("Any", "", false, out var any) && In(data.GetIntervals(any), 0xd800) && In(data.GetIntervals(any), 0x10ffff), "Any includes surrogate and maximum");
        Check(data.TryResolveProperty("sc", "Unknown", false, out var unknown) && In(data.GetIntervals(unknown), 0xd800), "script default");
        Check(data.TryResolveProperty("scx", "Hira", false, out var hira) && In(data.GetIntervals(hira), 0x30fc), "scx override shared Hiragana");
        Check(data.TryResolveProperty("sc", "Hira", false, out var scriptHira) && !In(data.GetIntervals(scriptHira), 0x30fc), "script distinct scx");
        foreach (var bad in new[] { "IsLatin", "latin", "Block", "blk", "Age", "NFKC_Quick_Check", "IDS_Unary_Operator", "Emoji=Yes", "RGI_emoji", "" })
        {
            Check(!data.TryResolveProperty(bad, "", true, out _), "unadmitted:" + bad);
        }

        Check(!data.TryResolveProperty("General_Category", "", true, out _) && !data.TryResolveProperty("Script", "latin", true, out _) &&
            !data.TryResolveProperty("Alphabetic", "Yes", true, out _), "property/value grammar");
        Check(data.IsIdentifierStart('$') && data.IsIdentifierStart('_') && data.IsIdentifierStart(0x10400) && !data.IsIdentifierStart('0'), "identifier start");
        Check(data.IsIdentifierContinue('0') && data.IsIdentifierContinue(0x200c) && data.IsIdentifierContinue(0x200d) && !data.IsIdentifierStart(0x200c), "identifier additions");
        foreach (var bad in new[] { -1, 0xd800, 0xdfff, 0x110000, int.MaxValue })
        {
            Check(!data.IsIdentifierStart(bad) && !data.IsIdentifierContinue(bad), "identifier invalid/surrogate");
        }

        foreach (var r in data.GetIntervals(new(EcmaPropertyKind.Binary, "ID_Start")))
        {
            Check(data.IsIdentifierStart(r.First) && data.IsIdentifierStart(r.Last), "identifier start endpoints");
        }

        foreach (var r in data.GetIntervals(new(EcmaPropertyKind.Binary, "ID_Continue")))
        {
            Check(data.IsIdentifierContinue(r.First) && data.IsIdentifierContinue(r.Last), "identifier continue endpoints");
        }

        var fold = data.CaseTables.UnicodeFold;
        var upper = data.CaseTables.LegacyUppercase;
        Check(fold.Length == (added ? 1512 : 1484) && upper.Length == (added ? 1169 : 1166), "case inventory");
        foreach (var m in fold)
        {
            Check(Fold(fold, m.Target) == m.Target && m.Source != m.Target, "idempotent simple fold");
        }

        foreach (var m in upper)
        {
            Check(m.Source <= 65535 && m.Target <= 65535 && !(m.Source >= 128 && m.Target < 128), "legacy canonicalize restriction");
        }

        Check(Fold(fold, 0x212a) == 'k' && Fold(upper, 0x212a) == 0x212a && Fold(fold, 0x17f) == 's' && Fold(upper, 0x17f) == 0x17f, "Unicode/legacy ASCII restriction");
        Check(Fold(fold, 0x1e9e) == 0xdf && Fold(upper, 0xdf) == 0xdf && Fold(fold, 0x130) == 0x130 && Fold(upper, 'i') == 'I', "full/Turkic mappings excluded");
        Check(Fold(fold, 0x10400) == 0x10428 && Fold(upper, 0xd800) == 0xd800 && Fold(fold, 0xd800) == 0xd800, "supplementary and surrogate case");
        var checksum = 0;
        for (var i = 0; i < 4; i++)
        {
            checksum += LookupCycle(data);
        }

        for (var i = 0; i < 4096; i++)
        {
            checksum += LookupCycle(data);
        }

        Check(checksum > 0, "repeated lookup checksum");
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "tr-TR", "az-Latn-AZ", "en-US" })
            { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture); Check(Fold(upper, 'i') == 'I' && data.IsIdentifierStart(0x10400), "culture independence"); }
        }
        finally { CultureInfo.CurrentCulture = original; }
        Check(Has(data, "Emoji_Keycap_Sequence", [0x31, 0xfe0f, 0x20e3]) && !Has(data, "Emoji_Keycap_Sequence", [0x31, 0x20e3]), "exact emoji keycaps");
        Check(Has(data, "RGI_Emoji_Flag_Sequence", [0x1f1fa, 0x1f1f8]) && !Has(data, "RGI_Emoji_Flag_Sequence", [0x1f1e6, 0x1f1e6]), "real flags only");
        Check(Has(data, "RGI_Emoji_ZWJ_Sequence", [0x1f469, 0x200d, 0x1f4bb]) && !Has(data, "RGI_Emoji_ZWJ_Sequence", [0x1f469, 0x200d]), "ZWJ exact/truncation");
        // Every component is included in the prescribed RGI union, including singleton intervals.
        var rgi = new EcmaProperty(EcmaPropertyKind.String, "RGI_Emoji");
        foreach (var n in new[] { "Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji_Modifier_Sequence", "RGI_Emoji_Flag_Sequence", "RGI_Emoji_Tag_Sequence", "RGI_Emoji_ZWJ_Sequence" })
        {
            var p = new EcmaProperty(EcmaPropertyKind.String, n);
            foreach (var r in data.GetIntervals(p))
            {
                for (var cp = r.First; cp <= r.Last; cp++)
                {
                    Check(In(data.GetIntervals(rgi), cp), "RGI singleton union");
                }
            }

            var cpv = data.GetStringCodePoints(p);
            foreach (var s in data.GetStrings(p))
            {
                Check(Has(data, "RGI_Emoji", cpv.Slice(s.Offset, s.Length)), "RGI string union");
            }
        }
    }
    private static int LookupCycle(EcmaUnicodeData data)
    {
        var sum = data.IsIdentifierStart(0x10400) ? 1 : 0;
        sum += data.IsIdentifierContinue(0x200d) ? 1 : 0;
        sum += data.IsIdentifierContinue(-1) ? 0 : 1;
        if (data.TryResolveProperty("scx", "Hira", true, out var p))
        {
            sum += data.GetIntervals(p).Length;
        }

        if (data.TryResolveProperty("RGI_Emoji", "", true, out p))
        {
            sum += data.GetStrings(p).Length + data.GetStringCodePoints(p).Length;
        }

        sum += data.TryResolveProperty("not_a_property", "", true, out _) ? 0 : 1;
        sum += data.CaseTables.UnicodeFold.Length + data.CaseTables.LegacyUppercase.Length;
        return sum;
    }
    private static bool Has(EcmaUnicodeData data, string name, ReadOnlySpan<int> desired)
    {
        var p = new EcmaProperty(EcmaPropertyKind.String, name);
        var cp = data.GetStringCodePoints(p);
        foreach (var s in data.GetStrings(p))
        {
            if (cp.Slice(s.Offset, s.Length).SequenceEqual(desired))
            {
                return true;
            }
        }

        return false;
    }
    private static int Compare(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    { for (var i = 0; i < a.Length; i++) { if (a[i] != b[i]) { return a[i].CompareTo(b[i]); } } return 0; }
    private static int Fold(ReadOnlySpan<EcmaCaseMapping> m, int value)
    { foreach (var pair in m) { if (pair.Source == value) { return pair.Target; } } return value; }
    private static bool In(ReadOnlySpan<EcmaInterval> ranges, int cp)
    { foreach (var r in ranges) { if (cp >= r.First && cp <= r.Last) { return true; } } return false; }
}
