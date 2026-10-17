#nullable enable
using GregsEx.Flavors.EcmaScript.Integration;

namespace GregsEx;

public sealed partial class EcmaScriptMatcher
{
    /// <summary>Parses a real immutable ECMAScript plan and prepares a private bounded session.
    /// Syntax/admission failures return diagnostics; workspace construction failures throw.</summary>
    public static bool TryParse(ReadOnlySpan<char> pattern, EcmaScriptProfile profile,
        out EcmaScriptMatcher? matcher, out EcmaScriptDiagnostic diagnostic,
        RegexCompileOptions? compile = null, RegexWorkspaceOptions? workspace = null,
        int frameCapacity = 4096, EcmaScriptHostLimits? hostLimits = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCapacity);
        matcher = null;
        compile ??= new();
        if (!EcmaPatternFactory.TryParse(pattern, profile, compile, out var parsed, out diagnostic))
        {
            return false;
        }

        matcher = Bind(parsed!, workspace, frameCapacity, hostLimits);
        return true;
    }

    /// <summary>Binds an ECMAScript pattern to private state with finite frame and output ceilings.</summary>
    public static EcmaScriptMatcher Bind(RegexPattern pattern, RegexWorkspaceOptions? workspace = null,
        int frameCapacity = 4096, EcmaScriptHostLimits? hostLimits = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Profile is not EcmaScriptProfile || pattern.InputKind != RegexInputKind.Utf16 ||
            pattern.EcmaExecutor is not { } executor || !ReferenceEquals(executor.Program.Profile, pattern.Profile))
        {
            throw new ArgumentException("An executable UTF-16 ECMAScript pattern is required.", nameof(pattern));
        }

        return new(executor, pattern.MatchLimits, workspace, frameCapacity, hostLimits);
    }
}
