#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Flavors.Pcre2.Runtime.Host;

// Immutable, compile-budgeted host views. Native physical layouts are excluded.
internal sealed class Pcre2HostMetadata
{
    private readonly Pcre2Program _program;
    private readonly Pcre2NamedGroup[] _names;
    private readonly Pcre2CalloutSiteInfo[] _callouts;
    private Pcre2HostMetadata(Pcre2Program program)
    {
        _program = program;
        var count = 0;
        foreach (var name in program.Names)
        {
            count += name.Numbers.Count;
        }

        _names = new Pcre2NamedGroup[count];
        var at = 0;
        foreach (var name in program.Names)
        {
            foreach (var number in program.Numbers.Slice(name.Numbers.Offset, name.Numbers.Count))
            {
                _names[at++] = new(name.Name, number);
            }
        }

        Array.Sort(_names, (a, b) => { var c = CompareNames(program.Profile, a.Name, b.Name); return c != 0 ? c : a.Number.CompareTo(b.Number); });
        _callouts = new Pcre2CalloutSiteInfo[program.Callouts.Length];
        for (var i = 0; i < _callouts.Length; i++)
        {
            var site = program.Callouts[i];
            _callouts[i] = new(site.Number, site.IsString, site.Automatic, site.PatternPosition,
                site.NextItemLength, site.StringSourceOffset, site.StringUnits.Count);
        }
    }
    internal static bool TryCreate(Pcre2Program program, ref CompileBudget budget,
        out Pcre2HostMetadata? metadata, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        metadata = null;
        diagnostic = default;
        long count = 0;
        foreach (var name in program.Names)
        {
            count += name.Numbers.Count;
        }

        var bytes = checked(192 + count * 24 + program.Callouts.Length * 32L);
        var source = new GregsEx.IR.SourceRange(0, 0, program.SourceUnit);
        if (!budget.TryCharge(count * (count == 0 ? 0 : System.Numerics.BitOperations.Log2((uint)count) + 1) + program.Callouts.Length, source, out var failure) || !budget.TryReserveBytes(bytes, source, out failure))
        { diagnostic = new(failure, Pcre2DiagnosticKind.CompileLimit); return false; }
        metadata = new(program);
        return true;
    }
    private static int CompareNames(Pcre2Profile profile, ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (profile.Width != Pcre2CodeUnitWidth.Eight || !profile.Utf)
        {
            return left.SequenceCompareTo(right);
        }

        int a = 0, b = 0;
        while (a < left.Length && b < right.Length)
        {
            int x = left[a++], y = right[b++];
            if (x is >= 0xD800 and <= 0xDBFF)
            {
                x = 0x10000 + ((x - 0xD800) << 10) + left[a++] - 0xDC00;
            }

            if (y is >= 0xD800 and <= 0xDBFF)
            {
                y = 0x10000 + ((y - 0xD800) << 10) + right[b++] - 0xDC00;
            }

            if (x != y)
            {
                return x.CompareTo(y);
            }
        }
        return (left.Length - a).CompareTo(right.Length - b);
    }
    internal ReadOnlySpan<Pcre2NamedGroup> GetNamedGroups() => _names;
    internal ReadOnlySpan<int> GetGroupNumbers(ReadOnlySpan<char> name)
    {
        foreach (var entry in _program.Names)
        {
            if (name.SequenceEqual(entry.Name))
            {
                return _program.Numbers.Slice(entry.Numbers.Offset, entry.Numbers.Count);
            }
        }

        return default;
    }
    internal int GroupNumberFromName(ReadOnlySpan<char> name)
    {
        var numbers = GetGroupNumbers(name);
        return numbers.Length == 0 ? (int)Pcre2ErrorCode.Nosubstring : numbers.Length == 1 ? numbers[0] : (int)Pcre2ErrorCode.Nouniquesubstring;
    }
    internal ReadOnlySpan<Pcre2CalloutSiteInfo> GetCalloutSites() => _callouts;
    internal ReadOnlySpan<char> GetMarkChars(int index) => _program.NativeChars(_program.Marks[index]);
    internal ReadOnlySpan<byte> GetMarkBytes(int index) => _program.NativeBytes(_program.Marks[index]);
    internal ReadOnlySpan<char> GetCalloutStringChars(int index) => _program.NativeChars(_program.Callouts[index].StringUnits);
    internal ReadOnlySpan<byte> GetCalloutStringBytes(int index) => _program.NativeBytes(_program.Callouts[index].StringUnits);
    internal bool TryGetInfo(Pcre2PatternInfoKey key, out long value, out Pcre2Diagnostic diagnostic)
    {
        value = 0;
        diagnostic = default;
        var hints = _program.SearchHints;
        var profile = _program.Profile;
        switch (key)
        {
            case Pcre2PatternInfoKey.Alloptions:
                value = (uint)profile.Flags;
                break;
            case Pcre2PatternInfoKey.Argoptions:
                value = (uint)_program.ArgumentFlags;
                break;
            case Pcre2PatternInfoKey.Extraoptions:
                value = (uint)profile.ExtraFlags;
                break;
            case Pcre2PatternInfoKey.Bsr:
                value = (int)profile.Bsr;
                break;
            case Pcre2PatternInfoKey.Newline:
                value = (int)profile.Newline;
                break;
            case Pcre2PatternInfoKey.Capturecount:
                value = _program.GroupCount - 1;
                break;
            case Pcre2PatternInfoKey.Namecount:
                value = _names.Length;
                break;
            case Pcre2PatternInfoKey.Hascrorlf:
                value = _program.HasExplicitCrOrLf ? 1 : 0;
                break;
            case Pcre2PatternInfoKey.Hasbackslashc:
                value = _program.HasBackslashC ? 1 : 0;
                break;
            case Pcre2PatternInfoKey.Backrefmax:
                if (hints?.MaximumBackreference is { } maximum)
                { value = maximum; break; }
                foreach (var node in _program.Nodes)
                {
                    var index = node.Kind == Pcre2NodeKind.Backreference ? node.Operand :
                        node.Kind == Pcre2NodeKind.Conditional && _program.Conditions[node.Operand].Kind == Pcre2ConditionKind.CaptureSet ? _program.Conditions[node.Operand].ReferenceIndex : -1;
                    if (index < 0)
                    {
                        continue;
                    }

                    var reference = _program.References[index];
                    value = Math.Max(value, reference.Number);
                    foreach (var number in _program.Numbers.Slice(reference.Candidates.Offset, reference.Candidates.Count))
                    {
                        value = Math.Max(value, number);
                    }
                }
                break;
            case Pcre2PatternInfoKey.Jchanged:
                value = _program.HasDuplicateNameOptionChange ? 1 : 0;
                break;
            case Pcre2PatternInfoKey.Matchlimit:
                if (_program.PatternLimits.NativeMatchLimit == uint.MaxValue)
                {
                    return Unset(out diagnostic);
                }

                if ((_program.PatternLimits.NativeMatchLimit ?? _program.PatternLimits.MatchWork) is not { } work)
                {
                    return Unset(out diagnostic);
                }

                value = work;
                break;
            case Pcre2PatternInfoKey.Depthlimit:
                if (_program.PatternLimits.NativeDepthLimit == uint.MaxValue)
                {
                    return Unset(out diagnostic);
                }

                if (((long?)_program.PatternLimits.NativeDepthLimit ?? _program.PatternLimits.Depth) is not { } depth)
                {
                    return Unset(out diagnostic);
                }

                value = depth;
                break;
            case Pcre2PatternInfoKey.Heaplimit:
                if (_program.PatternLimits.NativeHeapLimit == uint.MaxValue)
                {
                    return Unset(out diagnostic);
                }

                if (_program.PatternLimits.NativeHeapLimit is { } nativeHeap)
                { value = nativeHeap; break; }
                if (_program.PatternLimits.HeapBytes is not { } heap)
                {
                    return Unset(out diagnostic);
                }

                value = heap / 1024;
                break;
            case Pcre2PatternInfoKey.Firstcodeunit:
                value = hints?.FirstCodeUnit is >= 0 ? hints.Value.FirstCodeUnit : 0;
                break;
            case Pcre2PatternInfoKey.Firstcodetype:
                value = hints?.FirstCodeUnit is >= 0 ? 1 : hints?.StartLine == true ? 2 : 0;
                break;
            case Pcre2PatternInfoKey.Lastcodeunit:
                value = hints?.RequiredCodeUnit is >= 0 ? hints.Value.RequiredCodeUnit : 0;
                break;
            case Pcre2PatternInfoKey.Lastcodetype:
                value = hints?.RequiredCodeUnit is >= 0 ? 1 : 0;
                break;
            case Pcre2PatternInfoKey.Matchempty:
                value = hints?.MatchesEmpty == true ? 1 : 0;
                break;
            case Pcre2PatternInfoKey.Minlength:
                value = hints?.MinimumLength ?? 0;
                break;
            case Pcre2PatternInfoKey.Maxlookbehind:
                value = hints?.MaximumLookbehind ?? 0;
                break;
            default:
                diagnostic = new(new(RegexDiagnosticCode.UnsupportedFeature, 0, 0, _program.SourceUnit,
                    "Native physical, bitmap, name-table layout and JIT queries are excluded."), Pcre2DiagnosticKind.Capability, CapabilityId: "PH-004/PH-012");
                return false;
        }
        return true;
    }
    private static bool Unset(out Pcre2Diagnostic diagnostic)
    { diagnostic = new(default, Pcre2DiagnosticKind.None, Pcre2ErrorCode.Unset); return false; }

}
