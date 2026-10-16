using GregsEx.Contracts;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Flavors.Pcre2.Runtime.Host;
using GregsEx.IR;
namespace GregsEx;

/// <summary>Native typed PCRE2 factories and checked substring host operations.</summary>
public static class Pcre2Functions
{
    /// <summary>Compile owned native character units or throw the complete typed diagnosis.</summary>
    public static Pcre2Pattern Parse(ReadOnlySpan<char> pattern, Pcre2Profile profile, Pcre2CompileOptions? options = null)
    { if (!TryParse(pattern, profile, out var result, out var diagnostic, options)) { throw new Pcre2ParseException(diagnostic); } return result!; }
    /// <summary>Compile owned native8 units or throw the complete typed diagnosis.</summary>
    public static Pcre2Pattern Parse(ReadOnlySpan<byte> pattern, Pcre2Profile profile, Pcre2CompileOptions? options = null)
    { if (!TryParse(pattern, profile, out var result, out var diagnostic, options)) { throw new Pcre2ParseException(diagnostic); } return result!; }
    /// <summary>Compile through the real pinned syntax/lowering/executor pipeline.</summary>
    public static bool TryParse(ReadOnlySpan<char> pattern, Pcre2Profile profile, out Pcre2Pattern? result, out Pcre2Diagnostic diagnostic, Pcre2CompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new();
        result = null;
        var budget = new CompileBudget(options.Limits);
        return Pcre2ExecutorFactory.TryCompile(pattern, profile, ref budget, out var executor, out diagnostic) && Publish(executor!, options, ref budget, out result, out diagnostic);
    }
    /// <summary>Compile exact native8 units without retaining the borrowed source.</summary>
    public static bool TryParse(ReadOnlySpan<byte> pattern, Pcre2Profile profile, out Pcre2Pattern? result, out Pcre2Diagnostic diagnostic, Pcre2CompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new();
        result = null;
        var budget = new CompileBudget(options.Limits);
        return Pcre2ExecutorFactory.TryCompile(pattern, profile, ref budget, out var executor, out diagnostic) && Publish(executor!, options, ref budget, out result, out diagnostic);
    }
    private static bool Publish(Pcre2Executor executor, Pcre2CompileOptions options, ref CompileBudget budget, out Pcre2Pattern? result, out Pcre2Diagnostic diagnostic)
    {
        result = null;
        var source = new SourceRange(0, executor.Program.SourceLength, executor.Program.SourceUnit);
        if (executor.RetainedBytes > options.Limits.MaxPlanBytes - Pcre2Pattern.WrapperBytes)
        { diagnostic = new(new(RegexDiagnosticCode.PlanLimitExceeded, 0, source.Length, source.Unit), Pcre2DiagnosticKind.CompileLimit); return false; }
        if (!budget.TryReserveBytes(Pcre2Pattern.WrapperBytes, source, out var failure))
        { diagnostic = new(failure, Pcre2DiagnosticKind.CompileLimit); return false; }
        result = new(executor, options);
        diagnostic = default;
        return true;
    }
    /// <summary>Copy a defined character pair, including native terminator capacity; optional result preserves partial/error state.</summary>
    public static Pcre2SubstringResult CopySubstring(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Span<char> destination, Pcre2MatchResult? matchResult = null) => Pcre2Substrings.Copy<char>(subject, pairs, number, destination, matchResult: matchResult);
    /// <summary>Copy a defined byte pair, including native terminator capacity.</summary>
    public static Pcre2SubstringResult CopySubstring(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Span<byte> destination, Pcre2MatchResult? matchResult = null) => Pcre2Substrings.Copy<byte>(subject, pairs, number, destination, matchResult: matchResult);
    /// <summary>Resolve duplicate names by first participating capture and preserve unavailable versus unknown.</summary>
    public static Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Span<char> destination, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); var number = Pcre2Substrings.Number(pattern.Executor.Program, pairs, name); return number == pattern.GroupCount ? new((int)Pcre2ErrorCode.Nosubstring, 0, 0) : Pcre2Substrings.Copy<char>(subject, pairs, number, destination, pattern.GroupCount, matchResult); }
    /// <summary>Native8 checked named-copy operation.</summary>
    public static Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Span<byte> destination, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); var number = Pcre2Substrings.Number(pattern.Executor.Program, pairs, name); return number == pattern.GroupCount ? new((int)Pcre2ErrorCode.Nosubstring, 0, 0) : Pcre2Substrings.Copy<byte>(subject, pairs, number, destination, pattern.GroupCount, matchResult); }
    /// <summary>Pair length or source helper error; optional result retains failed/partial state.</summary>
    public static int SubstringLength(ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2MatchResult? matchResult = null) => Pcre2Substrings.Length(pairs, number, matchResult: matchResult);
    /// <summary>Program-aware numeric character copy distinguishes unknown from short-vector unavailable.</summary>
    public static Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Span<char> destination, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); return Pcre2Substrings.Copy<char>(subject, pairs, number, destination, pattern.GroupCount, matchResult); }
    /// <summary>Program-aware native8 numeric substring copy.</summary>
    public static Pcre2SubstringResult CopySubstring(Pcre2Pattern pattern, ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Span<byte> destination, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); return Pcre2Substrings.Copy<byte>(subject, pairs, number, destination, pattern.GroupCount, matchResult); }
    /// <summary>Program-aware length with source unknown/unavailable/partial distinctions.</summary>
    public static int SubstringLength(Pcre2Pattern pattern, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); return Pcre2Substrings.Length(pairs, number, pattern.GroupCount, matchResult); }
    /// <summary>Named source length, selecting the first participating duplicate capture.</summary>
    public static int SubstringLength(Pcre2Pattern pattern, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Pcre2MatchResult? matchResult = null)
    { ArgumentNullException.ThrowIfNull(pattern); var number = Pcre2Substrings.Number(pattern.Executor.Program, pairs, name); return number == pattern.GroupCount ? (int)Pcre2ErrorCode.Nosubstring : Pcre2Substrings.Length(pairs, number, pattern.GroupCount, matchResult); }
    /// <summary>Materialize an owned bounded named character substring.</summary>
    public static string MaterializeSubstring(Pcre2Pattern pattern, ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Pcre2HostLimits? limits = null)
    { ArgumentNullException.ThrowIfNull(pattern); return Pcre2Substrings.MaterializeChars(subject, pairs, Pcre2Substrings.Number(pattern.Executor.Program, pairs, name), limits ?? new(), pattern.GroupCount); }
    /// <summary>Materialize an owned bounded named native8 substring.</summary>
    public static byte[] MaterializeSubstring(Pcre2Pattern pattern, ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, ReadOnlySpan<char> name, Pcre2HostLimits? limits = null)
    { ArgumentNullException.ThrowIfNull(pattern); return Pcre2Substrings.Materialize<byte>(subject, pairs, Pcre2Substrings.Number(pattern.Executor.Program, pairs, name), limits ?? new(), pattern.GroupCount); }
    /// <summary>Allocate an owned bounded character substring.</summary>
    public static string MaterializeSubstring(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2HostLimits? limits = null) => Pcre2Substrings.MaterializeChars(subject, pairs, number, limits ?? new());
    /// <summary>Allocate an owned bounded byte substring.</summary>
    public static byte[] MaterializeSubstring(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, int number, Pcre2HostLimits? limits = null) => Pcre2Substrings.Materialize<byte>(subject, pairs, number, limits ?? new());
    /// <summary>Materialize the defined complete-match substring list; unset entries become empty strings.</summary>
    public static string[] MaterializeSubstrings(ReadOnlySpan<char> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2MatchResult result, Pcre2HostLimits? limits = null)
    {
        limits ??= new();
        var count = ListCount(pairs, result);
        long bytes = checked(32L + 8L * count), units = 0;
        for (var i = 0; i < count; i++)
        { var length = ListLength(subject.Length, pairs[i]); units = checked(units + length + 1L); bytes = checked(bytes + 32L + 2L * (length + 1L)); }
        AdmitList(count, units, bytes, limits);
        var output = new string[count];
        for (var i = 0; i < count; i++)
        {
            output[i] = pairs[i].Participated ? new string(subject.Slice(pairs[i].Start, ListLength(subject.Length, pairs[i]))) : string.Empty;
        }

        return output;
    }
    /// <summary>Materialize the native8 substring list with bounded aggregate output and memory.</summary>
    public static byte[][] MaterializeSubstrings(ReadOnlySpan<byte> subject, ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2MatchResult result, Pcre2HostLimits? limits = null)
    {
        limits ??= new();
        var count = ListCount(pairs, result);
        long bytes = checked(32L + 8L * count), units = 0;
        for (var i = 0; i < count; i++)
        { var length = ListLength(subject.Length, pairs[i]); units = checked(units + length + 1L); bytes = checked(bytes + 32L + length); }
        AdmitList(count, units, bytes, limits);
        var output = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            output[i] = pairs[i].Participated ? subject.Slice(pairs[i].Start, ListLength(subject.Length, pairs[i])).ToArray() : [];
        }

        return output;
    }
    private static int ListCount(ReadOnlySpan<Pcre2OffsetPair> pairs, Pcre2MatchResult result)
    {
        if (result.Status != Pcre2MatchStatus.Match || result.NativeReturnCode < 0)
        {
            throw new ArgumentException($"Native substring list error {result.NativeReturnCode}.", nameof(result));
        }

        if (result.NativeReturnCode > pairs.Length)
        {
            throw new ArgumentException("The defined match vector is unavailable.", nameof(pairs));
        }

        return result.NativeReturnCode == 0 ? pairs.Length : result.NativeReturnCode;
    }
    private static int ListLength(int subjectLength, Pcre2OffsetPair pair)
    {
        if (!pair.Participated)
        {
            return 0;
        }

        if (pair.Start > subjectLength || pair.End > subjectLength)
        {
            throw new ArgumentException("Invalid native offsets.", nameof(pair));
        }

        return Math.Max(0, pair.End - pair.Start);
    }
    private static void AdmitList(int count, long units, long bytes, Pcre2HostLimits limits)
    {
        if (count > limits.MaxResults || units > limits.MaxOutputUnits || bytes > limits.MaxMemoryBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0, bytes));
        }
    }

}
