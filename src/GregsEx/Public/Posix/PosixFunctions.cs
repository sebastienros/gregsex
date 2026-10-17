#nullable enable
using GregsEx.Flavors.Posix.Integration;

namespace GregsEx;

/// <summary>Issue 7/2018 managed C/POSIX byte regex interfaces; no native ABI or ambient locale.</summary>
public static class PosixFunctions
{
    /// <summary>Parses exact native byte pattern units. Null options select Bytes.</summary>
    public static RegexPattern Parse(ReadOnlySpan<byte> pattern, PosixProfile profile, RegexCompileOptions? options = null)
    {
        if (!TryParse(pattern, profile, out var result, out var diagnostic, options))
        {
            throw new PosixRegexException(diagnostic);
        }

        return result!;
    }
    /// <summary>Parses one-byte character units 1–255, retaining UTF-16 diagnostic coordinates.</summary>
    public static RegexPattern Parse(string pattern, PosixProfile profile, RegexCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!TryParse(pattern.AsSpan(), profile, out var result, out var diagnostic, options))
        {
            throw new PosixRegexException(diagnostic);
        }

        return result!;
    }
    /// <summary>Parses native bytes, returning a classified syntax, domain or resource diagnostic.</summary>
    public static bool TryParse(ReadOnlySpan<byte> pattern, PosixProfile profile, out RegexPattern? result,
        out PosixRegexDiagnostic diagnostic, RegexCompileOptions? options = null)
        => PosixPatternFactory.TryParse(default, pattern, false, profile, options ?? new(RegexInputKind.Bytes), out result, out diagnostic);
    /// <summary>Parses one-byte characters without transcoding or retaining the borrowed source.</summary>
    public static bool TryParse(ReadOnlySpan<char> pattern, PosixProfile profile, out RegexPattern? result,
        out PosixRegexDiagnostic diagnostic, RegexCompileOptions? options = null)
        => PosixPatternFactory.TryParse(pattern, default, true, profile, options ?? new(RegexInputKind.Bytes), out result, out diagnostic);
    /// <summary>Searches original bytes. Native nmatch may be partial; NoSub and empty output leave all slots untouched.</summary>
    public static RegexSearchStatus RegExec(RegexWorkspace workspace, ReadOnlySpan<byte> input, Span<PosixMatch> matches,
        PosixExecutionFlags flags = PosixExecutionFlags.None, RegexMatchOptions options = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return workspace.PosixRegExec(input, matches, flags, options);
    }
    /// <summary>Returns ASCII message size including NUL. Short output truncates and terminates; excess bytes stay untouched.</summary>
    public static int RegError(PosixErrorCode code, Span<byte> destination)
    {
        var message = code switch
        {
            PosixErrorCode.BadPattern => "Invalid pattern"u8,
            PosixErrorCode.BadInterval => "Invalid interval"u8,
            PosixErrorCode.BadRepeat => "Invalid repetition"u8,
            PosixErrorCode.UnbalancedBrace => "Unbalanced brace"u8,
            PosixErrorCode.UnbalancedBracket => "Unbalanced bracket"u8,
            PosixErrorCode.InvalidCollatingElement => "Invalid collating element"u8,
            PosixErrorCode.InvalidCharacterClass => "Invalid character class"u8,
            PosixErrorCode.TrailingEscape => "Trailing escape"u8,
            PosixErrorCode.UnbalancedParenthesis => "Unbalanced parenthesis"u8,
            PosixErrorCode.InvalidRange => "Invalid range"u8,
            PosixErrorCode.InvalidBackreference => "Invalid backreference"u8,
            PosixErrorCode.NoMemory => "Out of memory"u8,
            PosixErrorCode.NoMatch => "No match"u8,
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        };
        if (!destination.IsEmpty)
        {
            var count = Math.Min(message.Length, destination.Length - 1);
            message[..count].CopyTo(destination);
            destination[count] = 0;
        }
        return message.Length + 1;
    }
}
