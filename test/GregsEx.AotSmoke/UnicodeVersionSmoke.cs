using GregsEx;
using GregsEx.SourceGeneration;

internal static partial class UnicodeVersionSmoke
{
    [GeneratedPattern("(\uA7CE|a)+x", EcmaScriptFlags.IgnoreCase, UnicodeVersion = UnicodeVersion.Unicode17_0)]
    private static partial RegexPattern Ecma17();

    [GeneratedPattern("([\uA7CEa-[\\p{Cn}]])+x", GeneratedPatternFlavor.Xsd, UnicodeVersion = UnicodeVersion.Unicode17_0)]
    private static partial RegexPattern Xsd17();

    [GeneratedPattern("(\uA7CE|a)+x", XPathFlags.IgnoreCase, UnicodeVersion = UnicodeVersion.Unicode17_0)]
    private static partial RegexPattern XPath17();

    internal static void Run()
    {
        foreach (var version in new[] { UnicodeVersion.Unicode16_0, UnicodeVersion.Unicode17_0 })
        {
            var added = version == UnicodeVersion.Unicode17_0;
            RegexProfile[] profiles =
            [
                new EcmaScriptProfile(EcmaScriptFlags.Unicode, version),
                new XsdProfile(version),
                new XPathProfile(unicodeVersion: version)
            ];
            foreach (var profile in profiles)
            {
                var pattern = RegexPattern.Parse(@"(\p{L})+x", profile, new(executionMode: RegexExecutionMode.Auto));
                Check(pattern.IsMatch("\U00010940x") == added, $"{profile.Id}/{version} Auto");
                Check(pattern.Profile.DataId == profile.DataId, $"{profile.Id}/{version} identity");
                if (profile is XsdProfile or XPathProfile)
                {
                    using var workspace = RegexPattern.Parse(@"\p{L}", profile, new(RegexInputKind.Utf8)).CreateWorkspace();
                    var status = workspace.Find("\U00010940"u8, 0, out var range);
                    Check(status == (added ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch), $"{profile.Id}/{version} UTF-8 status");
                    Check(range == (added ? new RegexMatchRange(0, 4) : default), $"{profile.Id}/{version} UTF-8 offsets");
                }
            }
        }

        var ecma = Ecma17();
        Check(ecma.ExecutionBackend == RegexExecutionBackend.Compiled && ecma.IsMatch("\uA7CFx"), "Unicode17 generated ECMAScript casing");
        var xpath = XPath17();
        Check(xpath.ExecutionBackend == RegexExecutionBackend.Compiled && xpath.IsMatch("\uA7CFx"), "Unicode17 generated XPath casing");
        var xsd = Xsd17();
        Check(xsd.ExecutionBackend == RegexExecutionBackend.Compiled && xsd.IsMatch("\uA7CEx"), "Unicode17 generated XSD categories");
        Check(!RegexPattern.Parse("([\uA7CEa-[\\p{Cn}]])+x", new XsdProfile()).IsMatch("\uA7CEx"), "Unicode16 category unchanged");
        Console.WriteLine("Unicode versions PASS (16/17 profiles, Auto, UTF-8 offsets, generated kernels)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
