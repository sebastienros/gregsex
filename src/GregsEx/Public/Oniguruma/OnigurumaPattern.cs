#nullable enable
using GregsEx.Flavors.Oniguruma;

namespace GregsEx;

/// <summary>An immutable Oniguruma pattern; share it across threads and create a workspace per thread.</summary>
public sealed class OnigurumaPattern
{
    internal OnigProgram Program { get; }

    internal RegexMatchLimits MatchLimits { get; }

    /// <summary>The syntax and data identity.</summary>
    public OnigurumaProfile Profile { get; }

    /// <summary>The number of captures including group zero.</summary>
    public int GroupCount => Program.GroupCount;

    private OnigurumaPattern(OnigProgram program, OnigurumaProfile profile, RegexMatchLimits limits)
    {
        Program = program;
        Profile = profile;
        MatchLimits = limits;
    }

    /// <summary>Parses a pattern, throwing <see cref="RegexParseException"/> on a diagnostic.</summary>
    public static OnigurumaPattern Parse(string pattern, OnigurumaProfile? profile = null, OnigurumaCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!TryParse(pattern, profile ?? RegexProfiles.Oniguruma, out var result, out var diagnostic, options))
        {
            throw new RegexParseException(diagnostic);
        }

        return result!;
    }

    /// <summary>Parses UTF-16 pattern units without retaining the borrowed source.</summary>
    public static bool TryParse(ReadOnlySpan<char> pattern, OnigurumaProfile profile, out OnigurumaPattern? result,
        out RegexDiagnostic diagnostic, OnigurumaCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        options ??= new OnigurumaCompileOptions();
        result = null;
        diagnostic = default;
        var flags = profile.Options;
        var inline = OnigInlineOptions.None;
        if ((flags & OnigurumaOptions.IgnoreCase) != 0)
        {
            inline |= OnigInlineOptions.IgnoreCase;
        }

        if ((flags & OnigurumaOptions.Multiline) != 0)
        {
            inline |= OnigInlineOptions.DotAll;
        }

        if ((flags & OnigurumaOptions.Extended) != 0)
        {
            inline |= OnigInlineOptions.Extended;
        }

        try
        {
            var program = OnigCompiler.Compile(pattern.ToString(), inline,
                (flags & OnigurumaOptions.CaptureGroup) != 0, (flags & OnigurumaOptions.DontCaptureGroup) != 0, options.Limits);
            result = new OnigurumaPattern(program, profile, options.DefaultMatchLimits);
            return true;
        }
        catch (OnigSyntaxException e)
        {
            diagnostic = new RegexDiagnostic(e.Code, e.Offset, e.Length, RegexOffsetUnit.Utf16CodeUnits, e.Message);
            return false;
        }
    }

    /// <summary>The group numbers with this name, in pattern order; empty when none.</summary>
    public ReadOnlySpan<int> GetGroupNumbers(ReadOnlySpan<char> name)
    {
        var numbers = new List<int>();
        var names = Program.GroupNames;
        for (var g = 1; g < names.Length; g++)
        {
            if (names[g] is { } n && name.SequenceEqual(n))
            {
                numbers.Add(g);
            }
        }
        return numbers.ToArray();
    }

    /// <summary>The name of a group, or null.</summary>
    public string? GetGroupName(int number)
        => (uint)number < (uint)Program.GroupNames.Length ? Program.GroupNames[number] : null;

    /// <summary>Creates an exclusively owned, reusable workspace.</summary>
    public OnigurumaWorkspace CreateWorkspace() => new(this);

    /// <summary>
    /// Whether the pattern matches anywhere; throws <see cref="RegexExecutionException"/> on a limit. A convenience
    /// that creates a workspace per call: reuse <see cref="CreateWorkspace"/> for repeated searches.
    /// </summary>
    public bool IsMatch(ReadOnlySpan<char> input)
    {
        var status = CreateWorkspace().IsMatch(input);
        if (status == RegexSearchStatus.LimitExceeded)
        {
            throw new RegexExecutionException(new RegexExecutionFailure(RegexExecutionFailureCode.WorkLimitExceeded, 0, 0));
        }

        return status == RegexSearchStatus.Match;
    }
}
