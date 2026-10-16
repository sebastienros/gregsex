namespace GregsEx.Flavors.Pcre2.Integration;

internal static class Pcre2PatternFactory
{
    internal static bool TryParse(ReadOnlySpan<char> source, Pcre2Profile profile, RegexCompileOptions options, out RegexPattern? result, out Pcre2Diagnostic diagnostic)
    {
        result = null;
        if (!Admit(profile, options, out diagnostic))
        {
            return false;
        }

        return Pcre2Functions.TryParse(source, profile, out var typed, out diagnostic, Typed(options)) && Project(typed!, options, out result, out diagnostic);
    }
    internal static bool TryParse(ReadOnlySpan<byte> source, Pcre2Profile profile, RegexCompileOptions options, out RegexPattern? result, out Pcre2Diagnostic diagnostic)
    {
        result = null;
        if (!Admit(profile, options, out diagnostic))
        {
            return false;
        }

        return Pcre2Functions.TryParse(source, profile, out var typed, out diagnostic, Typed(options)) && Project(typed!, options, out result, out diagnostic);
    }
    private static bool Project(Pcre2Pattern typed, RegexCompileOptions options, out RegexPattern? result, out Pcre2Diagnostic diagnostic)
    {
        result = null;
        if (!Admit(typed.Profile, options, out diagnostic))
        {
            return false;
        }

        return typed.TryProject(Pcre2ProjectionPolicy.WholeSubjectValidated, out result, out diagnostic);
    }
    private static Pcre2CompileOptions Typed(RegexCompileOptions options) => new(options.Limits, new(maxWork: options.DefaultMatchLimits.MaxWork, maxWorkspaceBytes: options.DefaultMatchLimits.MaxWorkspaceBytes), options.ExecutionMode);
    private static bool Admit(Pcre2Profile profile, RegexCompileOptions options, out Pcre2Diagnostic diagnostic)
    {
        diagnostic = default;
        if (profile.Supports(options.InputKind))
        {
            return true;
        }

        diagnostic = new(new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, profile.Width == Pcre2CodeUnitWidth.Eight ? RegexOffsetUnit.Bytes : RegexOffsetUnit.Utf16CodeUnits), Pcre2DiagnosticKind.Capability, CapabilityId: "PH-002");
        return false;
    }
}
