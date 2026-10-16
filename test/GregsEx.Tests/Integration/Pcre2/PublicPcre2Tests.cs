using System.Text;
using Xunit;
namespace GregsEx.Tests.Integration.Pcre2;

public sealed class PublicPcre2Tests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TypedAndGenericMatchCapturesReuseAndNativeMetadata(int domain)
    {
        var profile = Profile(domain);
        var pattern = Pcre2Functions.Parse("(?<n>a+)b", profile);
        using var typed = pattern.CreateWorkspace();
        var pairs = new Pcre2OffsetPair[2];
        var bytes = Encoding.UTF8.GetBytes("zaab");
        var result = domain < 2 ? typed.Match(bytes, 0, pairs) : typed.Match("zaab", 0, pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 1, 4), pairs[0]);
        Assert.Equal(new(true, 1, 3), pairs[1]);
        Assert.Equal(1, pattern.GroupNumberFromName("n"));
        Assert.Equal(new[] { 1 }, pattern.GetGroupNumbers("n").ToArray());
        Assert.True(pattern.TryGetInfo(Pcre2PatternInfoKey.Capturecount, out var count, out _));
        Assert.Equal(1, count);
        Assert.True(pattern.TryProject(Pcre2ProjectionPolicy.WholeSubjectValidated, out var generic, out _));
        using var workspace = generic!.CreateWorkspace();
        var groups = new RegexCapture[3];
        groups[2] = new(true, 99, 99);
        var status = domain < 2 ? workspace.FindCaptures(bytes, 0, groups, out var range) : workspace.FindCaptures("zaab", 0, groups, out range);
        Assert.Equal(RegexSearchStatus.Match, status);
        Assert.Equal(new(1, 3), range);
        Assert.Equal(new(true, 1, 2), groups[1]);
        Assert.Equal(new(true, 99, 99), groups[2]);
        var reserved = workspace.ReservedBytes;
        status = domain < 2 ? workspace.FindCaptures("z"u8, 0, groups, out _) : workspace.FindCaptures("z", 0, groups, out _);
        Assert.Equal(RegexSearchStatus.NoMatch, status);
        Assert.Equal(default, groups[0]);
        Assert.Equal(default, groups[1]);
        Assert.Equal(new(true, 99, 99), groups[2]);
        Assert.Equal(reserved, workspace.ReservedBytes);
        var shortGroups = new[] { new RegexCapture(true, 88, 88) };
        status = domain < 2 ? workspace.FindCaptures(bytes, 0, shortGroups, out _) : workspace.FindCaptures("zaab", 0, shortGroups, out _);
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, status);
        Assert.Equal(new(true, 88, 88), shortGroups[0]);
        var options = new RegexCompileOptions(profile.InputKind);
        Assert.True(domain < 2 ? RegexPattern.TryParse("a+b"u8, profile, out var parsed, out _, options) : RegexPattern.TryParse("a+b", profile, out parsed, out _, options));
        Assert.NotNull(parsed);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void NativeResultsStayTypedAndProjectionIsDecidedBeforeExecution(int domain)
    {
        var profile = new Pcre2Profile(Profile(domain).Width, Profile(domain).Flags, extraFlags: Pcre2ExtraFlags.AllowLookaroundBsk);
        var pattern = Pcre2Functions.Parse("(?=ab\\K)", profile);
        Assert.False(pattern.TryProject(Pcre2ProjectionPolicy.WholeSubjectValidated, out _, out var diagnostic));
        Assert.Equal(Pcre2DiagnosticKind.Capability, diagnostic.Kind);
        using var workspace = pattern.CreateWorkspace();
        var pairs = new Pcre2OffsetPair[1];
        var result = domain < 2 ? workspace.Match("ab"u8, 0, pairs) : workspace.Match("ab", 0, pairs);
        Assert.Equal(Pcre2MatchStatus.Match, result.Status);
        Assert.Equal(new(true, 2, 0), pairs[0]);
        var partial = Pcre2Functions.Parse("abc", Profile(domain));
        using var w = partial.CreateWorkspace();
        result = domain < 2 ? w.Match("ab"u8, 0, pairs, new() { Flags = Pcre2MatchFlags.PartialHard }) : w.Match("ab", 0, pairs, new() { Flags = Pcre2MatchFlags.PartialHard });
        Assert.Equal(Pcre2MatchStatus.Partial, result.Status);
        Assert.Equal(new(true, 0, 2), pairs[0]);
        var destination = new char[4];
        Assert.Equal(0, Pcre2Functions.CopySubstring("ab", pairs, 0, destination, result).NativeReturnCode);
        Assert.Equal(-2, Pcre2Functions.CopySubstring("ab", pairs, 1, destination, result).NativeReturnCode);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void IterationOwnsOneBudgetAndReleasesEveryTerminalOrExceptionPath(int domain)
    {
        var pattern = Pcre2Functions.Parse("a", Profile(domain));
        using var w = pattern.CreateWorkspace();
        var pairs = new Pcre2OffsetPair[1];
        using var it = w.CreateIteration();
        Assert.Throws<InvalidOperationException>(() => w.Reserve(new()));
        Assert.Throws<InvalidOperationException>(() => w.Dispose());
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? it.Next("aa"u8, pairs) : it.Next("aa", pairs)).Status);
        var work = it.WorkConsumed;
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? it.Next("aa"u8, pairs) : it.Next("aa", pairs)).Status);
        Assert.True(it.WorkConsumed > work);
        Assert.Equal(Pcre2MatchStatus.NoMatch, (domain < 2 ? it.Next("aa"u8, pairs) : it.Next("aa", pairs)).Status);
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? w.Match("a"u8, 0, pairs) : w.Match("a", 0, pairs)).Status);
        using var broken = w.CreateIteration();
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? broken.Next("aa"u8, pairs) : broken.Next("aa", pairs)).Status);
        Assert.Throws<ArgumentException>(() => { if (domain < 2) { broken.Next("a"u8, pairs); } else { broken.Next("a", pairs); } });
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? w.Match("a"u8, 0, pairs) : w.Match("a", 0, pairs)).Status);
        using var cancelled = w.CreateIteration(options: new() { CancellationToken = new(true) });
        Assert.Equal(Pcre2MatchStatus.Cancelled, (domain < 2 ? cancelled.Next("a"u8, pairs) : cancelled.Next("a", pairs)).Status);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CopyMatchedSubjectIsOwnedBoundedAndClearedByNextOperation(int domain)
    {
        var pattern = Pcre2Functions.Parse("a", Profile(domain));
        using var w = pattern.CreateWorkspace();
        var pairs = new Pcre2OffsetPair[1];
        char[] chars = ['a', 'b'];
        byte[] bytes = [97, 98];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.CopyMatchedSubject };
        Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? w.Match(bytes, 0, pairs, options) : w.Match(chars, 0, pairs, options)).Status);
        bytes[0] = 122;
        chars[0] = 'z';
        Assert.Equal("ab", domain < 2 ? Encoding.UTF8.GetString(w.CopiedSubjectBytes) : new string(w.CopiedSubjectChars));
        var reserved = w.ReservedBytes;
        Assert.Equal(Pcre2MatchStatus.NoMatch, (domain < 2 ? w.Match(bytes, 0, pairs) : w.Match(chars, 0, pairs)).Status);
        Assert.True(w.CopiedSubjectBytes.IsEmpty);
        Assert.True(w.CopiedSubjectChars.IsEmpty);
        Assert.Equal(reserved, w.ReservedBytes);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CallbackExclusionAndC048ExactCapacitiesArePubliclyUsable(int domain)
    {
        var pattern = Pcre2Functions.Parse("(a)", Profile(domain));
        using var w = pattern.CreateWorkspace();
        var p = Pcre2Functions.Parse("(?C1)a", Profile(domain));
        using (var callbackWorkspace = p.CreateWorkspace())
        {
            Pcre2CharCallbacks? cc = domain >= 2 ? new(Match: (in Pcre2CharCalloutContext _, object? _) => { Assert.Throws<InvalidOperationException>(() => callbackWorkspace.Dispose()); return 0; }) : null;
            Pcre2ByteCallbacks? bc = domain < 2 ? new(Match: (in Pcre2ByteCalloutContext _, object? _) => { Assert.Throws<InvalidOperationException>(() => callbackWorkspace.Dispose()); return 0; }) : null;
            var pairs = new Pcre2OffsetPair[1];
            Assert.Equal(Pcre2MatchStatus.Match, (domain < 2 ? callbackWorkspace.Match("a"u8, 0, pairs, callbacks: bc) : callbackWorkspace.Match("a", 0, pairs, callbacks: cc)).Status);
        }
        foreach (var cap in new int?[] { 1, 2, null })
        {
            var output8 = new byte[16];
            var output16 = new char[16];
            var result = domain < 2 ? w.Substitute("a"u8, 0, "$1$1"u8, output8, pairCapacity: cap) : w.Substitute("a", 0, "$1$1", output16, pairCapacity: cap);
            Assert.Equal(cap == 1 ? -54 : 1, result.NativeReturnCode);
            if (cap != 1)
            {
                Assert.Equal("aa", domain < 2 ? Encoding.UTF8.GetString(output8.AsSpan(0, result.OutputUnits)) : new string(output16, 0, result.OutputUnits));
            }
        }
    }
    [Fact]
    public void GenericUtfValidationBoundsCancellationAndCapacityRemainDistinct()
    {
        var p = RegexPattern.Parse("a", new Pcre2Profile(Pcre2CodeUnitWidth.Eight, Pcre2CompileFlags.Utf), new(RegexInputKind.Utf8));
        using var w = p.CreateWorkspace();
        var groups = new RegexCapture[1];
        Assert.Equal(RegexSearchStatus.InvalidInput, w.FindCaptures([255, 97], 1, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.InvalidEncoding, w.LastFailure.Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.IsMatch("a"u8, 2));
        Assert.Equal(RegexSearchStatus.Cancelled, w.IsMatch("a"u8, options: new() { CancellationToken = new(true) }));
        using var small = p.CreateWorkspace(new(new(1, 1, 1)));
        Assert.Equal(RegexSearchStatus.LimitExceeded, small.IsMatch("a"u8));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, small.LastFailure.Code);
        using var growth = p.CreateWorkspace(new(new(1, 1, 1), true));
        Assert.Equal(RegexSearchStatus.Match, growth.IsMatch("a"u8));
    }
    [Fact]
    public void TypedPositiveOffsetsReturnBadoffsetAndArgumentsRetainSafety()
    {
        var p = Pcre2Functions.Parse("a"u8, new(Pcre2CodeUnitWidth.Eight));
        using var w = p.CreateWorkspace();
        var pairs = new[] { new Pcre2OffsetPair(true, 7, 8) };
        Assert.Equal(-33, w.Match("a"u8, 2, pairs).NativeReturnCode);
        Assert.Equal(new(true, 7, 8), pairs[0]);
        var output = new byte[8];
        Assert.Equal(-33, w.Substitute("a"u8, 2, "b"u8, output).NativeReturnCode);
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Match("a"u8, -1, pairs));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AllocatingHostOperationsKeepBoundsAndDoNotReplayCallbacks(int domain)
    {
        var p = Pcre2Functions.Parse("(a)(b)?", Profile(domain));
        using var w = p.CreateWorkspace();
        var pairs = new Pcre2OffsetPair[3];
        var result = domain < 2 ? w.Match("a"u8, 0, pairs) : w.Match("a", 0, pairs);
        if (domain < 2)
        { var list = Pcre2Functions.MaterializeSubstrings("a"u8, pairs, result); Assert.Equal(2, list.Length); Assert.Equal("a", Encoding.UTF8.GetString(list[1])); }
        else
        { var list = Pcre2Functions.MaterializeSubstrings("a", pairs, result); Assert.Equal(new[] { "a", "a" }, list); }
        var called = 0;
        var cc = new Pcre2CharCallbacks(Substitute: (in _, _) => { called++; return 0; });
        var bc = new Pcre2ByteCallbacks(Substitute: (in _, _) => { called++; return 0; });
        var output = domain < 2 ? Encoding.UTF8.GetString(w.SubstituteBytes("aa"u8, 0, "$1$1"u8, 16, Pcre2SubstituteFlags.Global, callbacks: bc)) : w.SubstituteString("aa", 0, "$1$1", 16, Pcre2SubstituteFlags.Global, callbacks: cc);
        Assert.Equal("aaaa", output);
        Assert.Equal(2, called);
        var error = Assert.Throws<Pcre2SubstituteException>(() => { if (domain < 2) { w.SubstituteBytes("a"u8, 0, "$1$1"u8, 1); } else { w.SubstituteString("a", 0, "$1$1", 1); } });
        Assert.Equal(-48, error.Result.NativeReturnCode);
        Assert.Throws<Pcre2SubstituteException>(() => { if (domain < 2) { w.SubstituteBytes("a"u8, 0, "b"u8, 16, limits: new(maxMemoryBytes: 16)); } else { w.SubstituteString("a", 0, "b", 16, limits: new(maxMemoryBytes: 16)); } });
    }
    [Fact]
    public void CopySubjectPeakBudgetAndNormalizedRepresentationAreEnforced()
    {
        var profile = new Pcre2Profile();
        var p = Pcre2Functions.Parse("a", profile);
        using var w = p.CreateWorkspace(new(8, 16, 4, 16));
        var pairs = new Pcre2OffsetPair[1];
        var options = new Pcre2MatchOptions { Flags = Pcre2MatchFlags.CopyMatchedSubject, Limits = new(maxWorkspaceBytes: w.ReservedBytes) };
        var result = w.Match("a", 0, pairs, options);
        Assert.Equal(Pcre2MatchStatus.LimitExceeded, result.Status);
        Assert.True(w.CopiedSubjectChars.IsEmpty);
        Assert.Equal(Pcre2MatchStatus.Match, w.Match("a", 0, pairs).Status);
        Assert.False(RegexPattern.TryParse("(*UTF)a"u8, new Pcre2Profile(Pcre2CodeUnitWidth.Eight), out _, out var diagnostic, new(RegexInputKind.Bytes)));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, diagnostic.Code);
    }
    [Fact]
    public void SharedPatternHasPrivateConcurrentScratchAndSessionDeadlineDoesNotReset()
    {
        var p = Pcre2Functions.Parse("(?<s>a+)\\k<s>", new Pcre2Profile());
        Parallel.For(0, 16, _ => { using var w = p.CreateWorkspace(new(64, 256, 16, 64)); var pairs = new Pcre2OffsetPair[2]; Assert.Equal(Pcre2MatchStatus.Match, w.Match("aaaa", 0, pairs).Status); Assert.Equal(new(true, 0, 4), pairs[0]); });
        using var workspace = p.CreateWorkspace();
        using var session = workspace.CreateIteration(options: new() { Timeout = TimeSpan.FromMilliseconds(1) });
        Thread.Sleep(2);
        var groups = new Pcre2OffsetPair[2];
        Assert.Equal(Pcre2MatchStatus.TimedOut, session.Next("aaaa", groups).Status);
        Assert.Equal(Pcre2MatchStatus.Match, workspace.Match("aaaa", 0, groups).Status);
    }
    internal static Pcre2Profile Profile(int domain) => new(domain < 2 ? Pcre2CodeUnitWidth.Eight : Pcre2CodeUnitWidth.Sixteen, (domain & 1) != 0 ? Pcre2CompileFlags.Utf : Pcre2CompileFlags.None);
}
