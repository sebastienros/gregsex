using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Parsing;

// Independently stated nonshipping ABI fixture. These are illustrative exact
// witnesses, NOT generated Unicode16 data or a production table implementation.
internal sealed class ContractUnicode : IEcmaUnicodeData
{
    internal static readonly ContractUnicode Instance = new();
    public string DataId => "Unicode-16.0.0+Emoji-16.0";
    public EcmaCaseTables CaseTables { get; }
    private ContractUnicode()
    {
        var unicode = new List<EcmaCaseMapping>();
        var legacy = new List<EcmaCaseMapping>();
        for (var i = 0; i < 26; i++)
        { unicode.Add(new('A' + i, 'a' + i)); legacy.Add(new('a' + i, 'A' + i)); }
        unicode.AddRange([new(0x017F, 's'), new(0x03A9, 0x03C9), new(0x212A, 'k'), new(0x10400, 0x10428)]);
        legacy.Add(new(0x03C9, 0x03A9));
        var budget = new CompileBudget(new());
        Assert.True(EcmaCaseTables.TryCreate(unicode.ToArray(), legacy.ToArray(), ref budget, out var tables, out _));
        CaseTables = tables!;
    }
    public bool IsIdentifierStart(int cp) => cp is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or 0x03B1 or 0x03C0 or 0x10400;
    public bool IsIdentifierContinue(int cp) => IsIdentifierStart(cp) || cp is >= '0' and <= '9' or 0x0301;
    internal static readonly string[] StringNames = ["Basic_Emoji", "Emoji_Keycap_Sequence", "RGI_Emoji_Modifier_Sequence", "RGI_Emoji_Flag_Sequence", "RGI_Emoji_Tag_Sequence", "RGI_Emoji_ZWJ_Sequence", "RGI_Emoji"];
    public bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value, bool sets, out EcmaProperty property)
    {
        property = default;
        if (value.IsEmpty)
        {
            if (name.SequenceEqual("Ll") || name.SequenceEqual("Lowercase_Letter"))
            { property = new(EcmaPropertyKind.GeneralCategory, "Ll"); return true; }
            if (name.SequenceEqual("Lu") || name.SequenceEqual("Uppercase_Letter"))
            { property = new(EcmaPropertyKind.GeneralCategory, "Lu"); return true; }
            if (name.SequenceEqual("ASCII") || name.SequenceEqual("Any") || name.SequenceEqual("Emoji"))
            { property = new(EcmaPropertyKind.Binary, new(name)); return true; }
            if (sets)
            {
                foreach (var key in StringNames)
                {
                    if (name.SequenceEqual(key))
                    { property = new(EcmaPropertyKind.String, key); return true; }
                }
            }

            return false;
        }
        if (name.SequenceEqual("gc") || name.SequenceEqual("General_Category"))
        {
            return TryResolveProperty(value, default, false, out property) && property.Kind == EcmaPropertyKind.GeneralCategory;
        }

        bool script = name.SequenceEqual("sc") || name.SequenceEqual("Script"), extensions = name.SequenceEqual("scx") || name.SequenceEqual("Script_Extensions");
        if ((script || extensions) && (value.SequenceEqual("Greek") || value.SequenceEqual("Grek")))
        { property = new(script ? EcmaPropertyKind.Script : EcmaPropertyKind.ScriptExtensions, "Greek"); return true; }
        return false;
    }
    public ReadOnlySpan<EcmaInterval> GetIntervals(EcmaProperty property) => property.CanonicalName switch
    {
        "Ll" => Lower,
        "Lu" => Upper,
        "ASCII" => Ascii,
        "Any" => Any,
        "Greek" => Greek,
        "Emoji" or "Basic_Emoji" or "RGI_Emoji" => Emoji,
        _ => []
    };
    private static readonly EcmaInterval[] Lower = [new('a', 'z'), new(0x03C9, 0x03C9)];
    private static readonly EcmaInterval[] Upper = [new('A', 'Z'), new(0x03A9, 0x03A9)];
    private static readonly EcmaInterval[] Ascii = [new(0, 127)], Any = [new(0, 0x10FFFF)], Greek = [new(0x0370, 0x03FF)], Emoji = [new(0x1F600, 0x1F600)];
    // Deliberately both singleton intervals and ordered finite strings.
    private static readonly int[] StringPoints = ['a', 'b', 'A', 'B', 'x', 'y', 'z'];
    private static readonly EcmaStringSlice[] StringSlices = [new(4, 3), new(2, 2), new(0, 2)];
    public ReadOnlySpan<EcmaStringSlice> GetStrings(EcmaProperty property) => property.Kind == EcmaPropertyKind.String ? StringSlices : [];
    public ReadOnlySpan<int> GetStringCodePoints(EcmaProperty property) => property.Kind == EcmaPropertyKind.String ? StringPoints : [];
}
