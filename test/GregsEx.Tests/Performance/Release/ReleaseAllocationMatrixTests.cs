using GregsEx.SourceGeneration;
using Xunit;

namespace GregsEx.Tests.Performance.Release;

[Collection("AutoCompilationSwitch")]
public sealed partial class ReleaseAllocationMatrixTests
{
    private readonly struct AutoSwitchScope : IDisposable
    {
        public AutoSwitchScope(bool enabled) => AppContext.SetSwitch(CompilationContract.AutoCompilationSwitch, enabled);
        public void Dispose() => AppContext.SetSwitch(CompilationContract.AutoCompilationSwitch, false);
    }

    [GeneratedPattern("((a)|b)+c")]
    private static partial RegexPattern GenCore();

    [GeneratedPattern("((a)|b)+c", EcmaScriptFlags.None)]
    private static partial RegexPattern GenEcma();

    [GeneratedPattern("((a)|b)+c", GeneratedPatternFlavor.Xsd)]
    private static partial RegexPattern GenXsd();

    [GeneratedPattern("((a)|b)+c", XPathFlags.IgnoreCase)]
    private static partial RegexPattern GenXPath();

    [GeneratedPattern("((a)|b)+c", Pcre2CompileFlags.None)]
    private static partial RegexPattern GenPcre2();

    public static TheoryData<string> ProfileNames =>
    [
        "RegularCore",
        "ECMAScript",
        "XSD",
        "XPath",
        "POSIX",
        "PCRE2"
    ];

#if !GREGSEX_NETSTANDARD_ASSET
    [Theory]
    [MemberData(nameof(ProfileNames))]
    public void AllProfilesPreserveWorkspaceCapacityAndResourceStabilityAcrossModes(string profileName)
    {
        using var _ = new AutoSwitchScope(true);
        if (profileName == "POSIX")
        {
            VerifyPosixStability(RegexExecutionMode.Interpreted);
            VerifyPosixStability(RegexExecutionMode.Auto);
            return;
        }

        RegexProfile profile = profileName switch
        {
            "RegularCore" => new RegularCoreProfile(),
            "ECMAScript" => new EcmaScriptProfile(),
            "XSD" => new XsdProfile(),
            "XPath" => new XPathProfile(XPathFlags.IgnoreCase),
            "PCRE2" => new Pcre2Profile(),
            _ => throw new ArgumentOutOfRangeException(nameof(profileName))
        };

        var subject = profileName == "XSD" ? "ababc" : "xx ababc yy";
        var expectedRange = profileName == "XSD" ? new RegexMatchRange(0, 5) : new RegexMatchRange(3, 5);

        var interp = RegexPattern.Parse("((a)|b)+c", profile, new(RegexInputKind.Utf16, RegexExecutionMode.Interpreted));
        var auto = RegexPattern.Parse("((a)|b)+c", profile, new(RegexInputKind.Utf16, RegexExecutionMode.Auto));
        var compiled = interp.Compile();
        var generated = profileName switch
        {
            "RegularCore" => GenCore(),
            "ECMAScript" => GenEcma(),
            "XSD" => GenXsd(),
            "XPath" => GenXPath(),
            "PCRE2" => GenPcre2(),
            _ => throw new ArgumentOutOfRangeException(nameof(profileName))
        };

        Assert.Equal(RegexExecutionBackend.Interpreted, interp.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Compiled, auto.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Compiled, compiled.ExecutionBackend);
        Assert.Equal(RegexExecutionBackend.Compiled, generated.ExecutionBackend);
        Assert.True(auto.RetainedBytes > interp.RetainedBytes);
        Assert.True(compiled.RetainedBytes > interp.RetainedBytes);
        Assert.Equal(interp.RetainedBytes + 4096, generated.RetainedBytes);

        foreach (var pattern in new[] { interp, auto, compiled, generated })
        {
            using var ws = pattern.CreateWorkspace();
            var capacityBefore = ws.Capacity;
            var reservedBefore = ws.ReservedBytes;
            var retainedBefore = pattern.RetainedBytes;
            var captures = new RegexCapture[pattern.GroupCount];

            // Warm all admitted paths (match, no-match, range, captures).
            Assert.Equal(RegexSearchStatus.Match, ws.IsMatch(subject));
            Assert.Equal(RegexSearchStatus.NoMatch, ws.IsMatch("zzz"));
            Assert.Equal(RegexSearchStatus.Match, ws.Find(subject, 0, out var r));
            Assert.Equal(expectedRange, r);
            Assert.Equal(RegexSearchStatus.Match, ws.FindCaptures(subject, 0, captures, out var cr));
            Assert.Equal(expectedRange, cr);

            var subLen = captures.Length > 1 && captures[1].Participated ? captures[1].Length : 0;
            long checksum = 0;
            for (var i = 0; i < 4_096; i++)
            {
                if (ws.IsMatch(subject) == RegexSearchStatus.Match)
                {
                    checksum += 1;
                }

                if (ws.IsMatch("zzz") == RegexSearchStatus.NoMatch)
                {
                    checksum += 2;
                }

                if (ws.Find(subject, 0, out var range) == RegexSearchStatus.Match)
                {
                    checksum += range.Index + range.Length;
                }

                if (ws.FindCaptures(subject, 0, captures, out var capRange) == RegexSearchStatus.Match)
                {
                    checksum += capRange.Index + capRange.Length + (captures.Length > 1 && captures[1].Participated ? captures[1].Length : 0);
                }
            }

            long expectedPerIter = 1 + 2 + (expectedRange.Index + expectedRange.Length) + (expectedRange.Index + expectedRange.Length + subLen);
            Assert.Equal(4_096L * expectedPerIter, checksum);
            Assert.Equal(capacityBefore, ws.Capacity);
            Assert.Equal(reservedBefore, ws.ReservedBytes);
            Assert.Equal(retainedBefore, pattern.RetainedBytes);
            Assert.Equal(default, ws.LastFailure);
        }
    }
#endif

    [Fact]
    public void FallbackBackreferencePathsPreserveCapacityAndResourceStability()
    {
        using var _ = new AutoSwitchScope(true);
        RegexProfile[] profiles =
        [
            new EcmaScriptProfile(),
            new XPathProfile(),
            new Pcre2Profile()
        ];

        foreach (var profile in profiles)
        {
            var pattern = RegexPattern.Parse("([a-z]+)=\\1", profile, new(RegexInputKind.Utf16, RegexExecutionMode.Auto));
            Assert.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
            using var ws = pattern.CreateWorkspace();
            var capacityBefore = ws.Capacity;
            var reservedBefore = ws.ReservedBytes;
            var captures = new RegexCapture[pattern.GroupCount];

            Assert.Equal(RegexSearchStatus.Match, ws.FindCaptures("xx foo=bar yy abc=abc zz", 0, captures, out var warmRange));
            Assert.Equal(new RegexMatchRange(14, 7), warmRange);

            long checksum = 0;
            for (var i = 0; i < 2_048; i++)
            {
                if (ws.FindCaptures("xx foo=bar yy abc=abc zz", 0, captures, out var r) == RegexSearchStatus.Match)
                {
                    checksum += r.Index + r.Length;
                }
            }

            Assert.Equal(2_048L * 21, checksum);
            Assert.Equal(capacityBefore, ws.Capacity);
            Assert.Equal(reservedBefore, ws.ReservedBytes);
            Assert.Equal(default, ws.LastFailure);
        }
    }

    private static void VerifyPosixStability(RegexExecutionMode mode)
    {
        var pattern = PosixFunctions.Parse("([[:alpha:]]+)=([[:digit:]]+)"u8, new PosixProfile(PosixDialect.Extended), new(RegexInputKind.Bytes, mode));
        Assert.Equal(RegexExecutionBackend.Interpreted, pattern.ExecutionBackend);
        using var ws = pattern.CreateWorkspace();
        var capacityBefore = ws.Capacity;
        var reservedBefore = ws.ReservedBytes;
        var matches = new PosixMatch[pattern.GroupCount];
        var subject = "xx key=42 yy"u8.ToArray();

        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(ws, subject, matches));
        Assert.Equal(3, matches[0].Start);
        Assert.Equal(9, matches[0].End);

        long checksum = 0;
        for (var i = 0; i < 4_096; i++)
        {
            if (PosixFunctions.RegExec(ws, subject, matches) == RegexSearchStatus.Match)
            {
                checksum += matches[0].Start + matches[0].End;
            }
        }

        Assert.Equal(4_096L * 12, checksum);
        Assert.Equal(capacityBefore, ws.Capacity);
        Assert.Equal(reservedBefore, ws.ReservedBytes);
        Assert.Equal(default, ws.LastFailure);
    }
}
