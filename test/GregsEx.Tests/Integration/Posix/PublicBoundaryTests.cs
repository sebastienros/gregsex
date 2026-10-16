using System.Reflection;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Integration;
using Xunit;

namespace GregsEx.Tests.Integration.Posix;

public sealed class PublicBoundaryTests
{
    private static PosixProfile Ere(PosixCompileFlags flags = 0) => new(PosixDialect.Extended, flags);
    [Fact]
    public void Generic_and_typed_factories_preserve_defaults_domain_and_diagnostics()
    {
        Assert.False(RegexPattern.TryParse("a", Ere(), out _, out var generic));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, generic.Code);
        Assert.True(RegexPattern.TryParse("a"u8, Ere(), out var bytes, out _, new(RegexInputKind.Bytes, RegexExecutionMode.Auto)));
        Assert.Equal(RegexExecutionBackend.Interpreted, bytes!.ExecutionBackend);
        Assert.Equal(RegexInputKind.Bytes, PosixFunctions.Parse("a", Ere()).InputKind);
        Assert.False(PosixFunctions.TryParse("\u0100", Ere(), out _, out var character));
        Assert.Equal(RegexOffsetUnit.Utf16CodeUnits, character.Diagnostic.LocationUnit);
        Assert.Equal(0, character.Diagnostic.Offset);
        Assert.False(PosixFunctions.TryParse([97, 0], Ere(), out _, out var zero));
        Assert.Equal(RegexOffsetUnit.Bytes, zero.Diagnostic.LocationUnit);
        Assert.Equal(1, zero.Diagnostic.Offset);
        Assert.Equal(zero, Assert.Throws<PosixRegexException>(() => PosixFunctions.Parse([97, 0], Ere())).Diagnostic);
        Assert.Equal(generic, Assert.Throws<RegexParseException>(() => RegexPattern.Parse("a", Ere())).Diagnostic);
        Assert.Throws<ArgumentNullException>(() => PosixFunctions.Parse("a", null!));
        Assert.Throws<ArgumentNullException>(() => PosixFunctions.Parse((string)null!, Ere()));
    }
    [Fact]
    public void Native_partial_full_excess_and_reuse_select_identical_complete_winner()
    {
        var pattern = PosixFunctions.Parse("(a|aa)(a?)"u8, Ere());
        using var workspace = pattern.CreateWorkspace();
        var prepared = workspace.Capacity;
        for (var length = 0; length <= 6; length++)
        {
            var native = Enumerable.Repeat(new PosixMatch(77, 88), length).ToArray();
            Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(workspace, "aa"u8, native));
            if (length > 0)
            {
                Assert.Equal(new(0, 2), native[0]);
            }

            if (length > 1)
            {
                Assert.Equal(new(0, 2), native[1]);
            }

            if (length > 2)
            {
                Assert.Equal(new(2, 2), native[2]);
            }

            Assert.All(native.Skip(3), n => Assert.Equal(new(-1, -1), n));
            Assert.Equal(RegexSearchStatus.NoMatch, PosixFunctions.RegExec(workspace, "z"u8, native));
            Assert.All(native, n => Assert.Equal(new(-1, -1), n));
            Assert.Equal(prepared, workspace.Capacity);
        }
        var captures = new RegexCapture[4];
        captures[3] = new(true, 77, 88);
        Assert.Equal(RegexSearchStatus.Match, workspace.FindCaptures("aa"u8, 0, captures, out var range));
        Assert.Equal(new(0, 2), range);
        Assert.Equal(new(true, 77, 88), captures[3]);
        Assert.Equal(new(true, 2, 0), captures[2]);
    }
    [Fact]
    public void NoSub_keeps_native_outputs_untouched_even_when_reference_or_validation_fails()
    {
        var pattern = PosixFunctions.Parse(@"\(ab\)\1", new(PosixDialect.Basic, PosixCompileFlags.NoSub));
        using var workspace = pattern.CreateWorkspace();
        var output = Enumerable.Repeat(new PosixMatch(77, 88), 4).ToArray();
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(workspace, "abab"u8, output));
        Assert.Equal(RegexSearchStatus.NoMatch, PosixFunctions.RegExec(workspace, "aba"u8, output));
        Assert.Equal(RegexSearchStatus.InvalidInput, PosixFunctions.RegExec(workspace, [97, 0], output));
        Assert.Equal(RegexSearchStatus.Cancelled, PosixFunctions.RegExec(workspace, "abab"u8, output,
            options: new() { CancellationToken = new(true) }));
        Assert.All(output, n => Assert.Equal(new(77, 88), n));
        Assert.Throws<NotSupportedException>(() => workspace.FindCaptures("abab"u8, 0, new RegexCapture[1], out _));
        Assert.Equal(RegexSearchStatus.Match, workspace.Find("abab"u8, 0, out var range));
        Assert.Equal(new(0, 4), range);
    }
    [Fact]
    public void Native_publication_is_atomic_and_charged_to_the_existing_budget()
    {
        var p = PosixFunctions.Parse("(a)"u8, Ere());
        using var w = p.CreateWorkspace();
        var executor = ((PosixWorkspaceExecutor)p.Executor).Executor;
        var cap = executor.GetRequiredCapacity(SearchOperation.Captures, PosixExecutionCapacity.Default);
        var state = new RunState(new(new int[cap.StateSlots], new int[cap.CaptureSlots], new int[cap.ScratchSlots]), p.MatchLimits);
        Assert.Equal(RegexSearchStatus.Match, executor.Search("a"u8, 0, SearchOperation.Captures, 0,
            PosixExecutionCapacity.Default, ref state, new RegexCapture[2], out _));
        var selectedWork = state.Budget.WorkConsumed;
        var output = Enumerable.Repeat(new PosixMatch(77, 88), 3).ToArray();
        Assert.Equal(RegexSearchStatus.LimitExceeded, PosixFunctions.RegExec(w, "a"u8, output,
            options: new() { Limits = new(selectedWork + 2) }));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, w.LastFailure.Code);
        Assert.Equal(selectedWork, w.LastFailure.WorkConsumed);
        Assert.All(output, n => Assert.Equal(new(-1, -1), n));
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(w, "a"u8, output,
            options: new() { Limits = new(selectedWork + 3) }));
        Assert.Equal(new(0, 1), output[0]);
        Assert.Equal(new(0, 1), output[1]);
        Assert.Equal(new(-1, -1), output[2]);
    }
    [Fact]
    public void Admission_failure_precedence_clearing_growth_and_reuse()
    {
        var p = PosixFunctions.Parse("(a)"u8, Ere());
        using var small = p.CreateWorkspace(new());
        var output = Enumerable.Repeat(new PosixMatch(77, 88), 2).ToArray();
        Assert.Equal(RegexSearchStatus.LimitExceeded, PosixFunctions.RegExec(small, [0], output,
            options: new() { CancellationToken = new(true) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceCapacityExceeded, small.LastFailure.Code);
        Assert.All(output, n => Assert.Equal(new(-1, -1), n));
        var groups = new[] { new RegexCapture(true, 77, 88) };
        Assert.Equal(RegexSearchStatus.DestinationTooSmall, small.FindCaptures([0], 0, groups, out _));
        Assert.Equal(new(true, 77, 88), groups[0]);
        small.Reserve(p.CaptureCapacity);
        Assert.Equal(RegexSearchStatus.InvalidInput, PosixFunctions.RegExec(small, [97, 0], output));
        Assert.Equal(1, small.LastFailure.Offset);
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(small, "a"u8, output));
        Assert.Equal(default, small.LastFailure);
        Assert.Equal(RegexSearchStatus.LimitExceeded, PosixFunctions.RegExec(small, "a"u8, output,
            options: new() { Limits = new(maxWorkspaceBytes: small.ReservedBytes - 1) }));
        Assert.Equal(RegexExecutionFailureCode.WorkspaceLimitExceeded, small.LastFailure.Code);
        using var growth = p.CreateWorkspace(new(allowGrowth: true));
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(growth, "a"u8, output));
        Assert.Equal(p.CaptureCapacity, growth.Capacity);
        Assert.Throws<RegexExecutionException>(() => p.CreateWorkspace(new(new(int.MaxValue, int.MaxValue, int.MaxValue))));
    }
    [Fact]
    public void Original_input_anchors_high_bytes_and_failure_convenience_are_preserved()
    {
        using var w = PosixFunctions.Parse("^a$"u8, Ere(PosixCompileFlags.Newline)).CreateWorkspace();
        var output = new PosixMatch[1];
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(w, "x\na\nx"u8, output, PosixExecutionFlags.NotBol | PosixExecutionFlags.NotEol));
        Assert.Equal(new(2, 3), output[0]);
        Assert.Equal(RegexSearchStatus.NoMatch, w.Find("xa"u8, 1, out _));
        Assert.Equal(RegexSearchStatus.Match, w.Find("x\na\nx"u8, 2, out var range));
        Assert.Equal(new(2, 1), range);
        Assert.True(PosixFunctions.Parse([255], Ere()).IsMatch([128, 255]));
        Assert.Equal(RegexExecutionFailureCode.InvalidInputDomain,
            Assert.Throws<RegexExecutionException>(() => PosixFunctions.Parse("a", Ere()).IsMatch([97, 0])).Failure.Code);
    }
    [Fact]
    public void Initial_cancellation_deadline_and_logical_failure_leave_public_storage_reusable()
    {
        using var w = PosixFunctions.Parse("", Ere()).CreateWorkspace();
        var output = new[] { new PosixMatch(77, 88) };
        Assert.Equal(RegexSearchStatus.Cancelled, PosixFunctions.RegExec(w, [], output,
            options: new() { CancellationToken = new(true) }));
        Assert.Equal(RegexExecutionFailureCode.Cancelled, w.LastFailure.Code);
        Assert.Equal(0, w.LastFailure.WorkConsumed);
        Assert.Equal(new(-1, -1), output[0]);
        Assert.Equal(RegexSearchStatus.TimedOut, PosixFunctions.RegExec(w, [], output,
            options: new() { Timeout = TimeSpan.FromTicks(1) }));
        Assert.Equal(RegexExecutionFailureCode.TimedOut, w.LastFailure.Code);
        Assert.Equal(new(-1, -1), output[0]);
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(w, [], output));
        Assert.Equal(new(0, 0), output[0]);
        Assert.Equal(default, w.LastFailure);
        var p = PosixFunctions.Parse("(a|aa)+", Ere());
        using var bounded = p.CreateWorkspace();
        Assert.Equal(RegexSearchStatus.LimitExceeded, PosixFunctions.RegExec(bounded, "aa"u8, new PosixMatch[2],
            options: new() { Limits = new(maxWork: 1) }));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, bounded.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(bounded, "aa"u8, new PosixMatch[2]));
    }
    [Fact]
    public void Lifecycle_arguments_options_and_reentrancy_precede_native_output()
    {
        var p = PosixFunctions.Parse("a", Ere());
        using var w = p.CreateWorkspace();
        var output = new[] { new PosixMatch(77, 88) };
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixFunctions.RegExec(w, "a"u8, output, (PosixExecutionFlags)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixFunctions.RegExec(w, "a"u8, output,
            options: new() { Timeout = TimeSpan.Zero }));
        using var core = RegexPattern.Parse("a", new RegularCoreProfile()).CreateWorkspace();
        Assert.Throws<ArgumentException>(() => PosixFunctions.RegExec(core, "a"u8, output));
        Assert.Throws<ArgumentException>(() => w.IsMatch("a"));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Find("a"u8, 2, out _));
        var field = typeof(RegexWorkspace).GetField("_lifecycle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(w, 1);
        try
        {
            Assert.Throws<InvalidOperationException>(() => PosixFunctions.RegExec(w, "a"u8, output));
            Assert.Throws<InvalidOperationException>(() => w.Reserve(p.CaptureCapacity));
            Assert.Throws<InvalidOperationException>(() => w.Dispose());
        }
        finally { field.SetValue(w, 0); }
        Assert.All(output, n => Assert.Equal(new(77, 88), n));
        w.Dispose();
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PosixFunctions.RegExec(w, "a"u8, output));
    }
    [Fact]
    public async Task Immutable_plan_has_independent_concurrent_workspaces()
    {
        var p = PosixFunctions.Parse("(a|aa)(a?)"u8, Ere());
        var anchor = PosixFunctions.Parse("^a$"u8, Ere());
        await Task.WhenAll(Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            using var w = p.CreateWorkspace();
            using var flagged = anchor.CreateWorkspace();
            var output = new PosixMatch[3];
            for (var i = 0; i < 16; i++)
            {
                Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(w, "aa"u8, output));
                Assert.Equal(new(0, 2), output[1]);
                Assert.Equal(new(2, 2), output[2]);
                var flags = (PosixExecutionFlags)(worker % 4);
                Assert.Equal(flags == 0 ? RegexSearchStatus.Match : RegexSearchStatus.NoMatch,
                    PosixFunctions.RegExec(flagged, "a"u8, output, flags));
                Assert.Equal(RegexSearchStatus.Match, PosixFunctions.RegExec(flagged, "a"u8, output));
            }
        })));
    }
    [Theory]
    [InlineData(PosixErrorCode.BadPattern)]
    [InlineData(PosixErrorCode.BadInterval)]
    [InlineData(PosixErrorCode.BadRepeat)]
    [InlineData(PosixErrorCode.UnbalancedBrace)]
    [InlineData(PosixErrorCode.UnbalancedBracket)]
    [InlineData(PosixErrorCode.InvalidCollatingElement)]
    [InlineData(PosixErrorCode.InvalidCharacterClass)]
    [InlineData(PosixErrorCode.TrailingEscape)]
    [InlineData(PosixErrorCode.UnbalancedParenthesis)]
    [InlineData(PosixErrorCode.InvalidRange)]
    [InlineData(PosixErrorCode.InvalidBackreference)]
    [InlineData(PosixErrorCode.NoMemory)]
    [InlineData(PosixErrorCode.NoMatch)]
    public void Every_symbolic_error_formats_exact_truncated_and_excess_storage(PosixErrorCode code)
    {
        var size = PosixFunctions.RegError(code, []);
        var exact = new byte[size];
        Assert.Equal(size, PosixFunctions.RegError(code, exact));
        Assert.Equal(0, exact[^1]);
        Assert.All(exact[..^1], b => Assert.InRange(b, (byte)32, (byte)126));
        foreach (var n in new[] { 1, 3, size - 1, size + 2 })
        {
            var dst = Enumerable.Repeat((byte)165, n).ToArray();
            Assert.Equal(size, PosixFunctions.RegError(code, dst));
            var prefix = Math.Min(n - 1, size - 1);
            Assert.Equal(exact[..prefix], dst[..prefix]);
            Assert.Equal(0, dst[prefix]);
            Assert.All(dst[(prefix + 1)..], b => Assert.Equal(165, b));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixFunctions.RegError(PosixErrorCode.None, exact));
        Assert.Throws<ArgumentOutOfRangeException>(() => PosixFunctions.RegError((PosixErrorCode)999, exact));
    }
}
