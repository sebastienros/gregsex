#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Text;

internal readonly struct Pcre2ByteTextReader : IPcre2TextReader<byte>
{
    public static Pcre2ValidationResult Validate(scoped ReadOnlySpan<byte> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state) =>
        Pcre2TextCore<byte, Pcre2ByteUnits>.Validate(input, startAt, maxLookbehind, profile, flags, policy, ref state);
    public static Pcre2ReadResult Read(scoped ReadOnlySpan<byte> input, int offset, int visibleEnd, bool codeUnit,
        Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<byte, Pcre2ByteUnits>.Read(input, offset, visibleEnd, codeUnit, profile, ref state);
    public static Pcre2ReadResult ReadPrevious(scoped ReadOnlySpan<byte> input, int offset, Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<byte, Pcre2ByteUnits>.ReadPrevious(input, offset, profile, ref state);
    public static int AdvanceAfterEmpty(scoped ReadOnlySpan<byte> input, int offset, Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<byte, Pcre2ByteUnits>.AdvanceAfterEmpty(input, offset, profile, ref state);
    internal static Pcre2TextWindow Bind(scoped ReadOnlySpan<byte> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state) =>
        Pcre2TextFragments<byte, Pcre2ByteUnits>.Bind(input, startAt, maxLookbehind, profile, flags, policy, ref state);
    internal static Pcre2TextWindow NextFragment(scoped ReadOnlySpan<byte> input, Pcre2TextWindow previous,
        Pcre2Profile profile, ref RunState state) =>
        Pcre2TextFragments<byte, Pcre2ByteUnits>.Next(input, previous, profile, ref state);
}

internal readonly struct Pcre2CharTextReader : IPcre2TextReader<char>
{
    public static Pcre2ValidationResult Validate(scoped ReadOnlySpan<char> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state) =>
        Pcre2TextCore<char, Pcre2CharUnits>.Validate(input, startAt, maxLookbehind, profile, flags, policy, ref state);
    public static Pcre2ReadResult Read(scoped ReadOnlySpan<char> input, int offset, int visibleEnd, bool codeUnit,
        Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<char, Pcre2CharUnits>.Read(input, offset, visibleEnd, codeUnit, profile, ref state);
    public static Pcre2ReadResult ReadPrevious(scoped ReadOnlySpan<char> input, int offset, Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<char, Pcre2CharUnits>.ReadPrevious(input, offset, profile, ref state);
    public static int AdvanceAfterEmpty(scoped ReadOnlySpan<char> input, int offset, Pcre2Profile profile, ref RunState state) =>
        Pcre2TextCore<char, Pcre2CharUnits>.AdvanceAfterEmpty(input, offset, profile, ref state);
    internal static Pcre2TextWindow Bind(scoped ReadOnlySpan<char> input, int startAt, int maxLookbehind,
        Pcre2Profile profile, Pcre2MatchFlags flags, Pcre2ValidationPolicy policy, ref RunState state) =>
        Pcre2TextFragments<char, Pcre2CharUnits>.Bind(input, startAt, maxLookbehind, profile, flags, policy, ref state);
    internal static Pcre2TextWindow NextFragment(scoped ReadOnlySpan<char> input, Pcre2TextWindow previous,
        Pcre2Profile profile, ref RunState state) =>
        Pcre2TextFragments<char, Pcre2CharUnits>.Next(input, previous, profile, ref state);
}
