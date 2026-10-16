using System.Collections.Frozen;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Integration;
using GregsEx.Tests.Contracts.Posix;
using GregsEx.Tests.Conformance.Posix;
using Xunit;

namespace GregsEx.Tests.Integration.Posix;

public sealed class CorpusTests
{
    private static readonly FrozenDictionary<string, PosixFixture> Recipes =
        PosixConformanceRunner.ReadAll().ToFrozenDictionary(f => f.Id, StringComparer.Ordinal);

    public static IEnumerable<TheoryDataRow<string>> Cases() =>
        Recipes.Keys.Select(id => new TheoryDataRow<string>(id));

    [Fact]
    public void CompleteImmutableInventory() => Assert.Equal(7265, Recipes.Count);

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryIndependentPublicRecipe(string id)
        => PosixConformanceRunner.RunCase(Recipes[id], Observe);

    internal static PosixExpected Observe(PosixFixture f)
    {
        if (f.Operation == "RegError")
        {
            var destination = Enumerable.Repeat((byte)165, f.DestinationLength!.Value).ToArray();
            var required = PosixFunctions.RegError(Enum.Parse<PosixErrorCode>(f.ErrorCode!), destination);
            return new("Formatted", null, null, [], [], false, null, required, [.. destination.Select(b => (int)b)]);
        }
        if (!PosixLocale.TryGet(f.Locale, out var locale, out var detail))
        {
            return ParseFailure(detail);
        }

        var profile = new PosixProfile(f.ProfileId == "PosixBre2018-CByte" ? PosixDialect.Basic : PosixDialect.Extended,
            (PosixCompileFlags)f.CompileFlags, locale);
        var l = f.Limits;
        var compileLimits = new RegexCompileLimits(maxPatternUnits: l?.MaxPatternUnits ?? 65536,
            maxCompileWork: l?.MaxCompileWork ?? 10000000, maxPlanBytes: l?.MaxPlanBytes ?? 67108864,
            maxCompileMemoryBytes: l?.MaxCompileMemoryBytes ?? 67108864);
        // Per-operation memory ceilings are lower than the pattern's default.
        var options = new RegexCompileOptions(Enum.Parse<RegexInputKind>(f.InputKind), limits: compileLimits);
        var success = f.PatternKind == "Bytes"
            ? PosixFunctions.TryParse(f.PatternUnits.Select(b => (byte)b).ToArray(), profile, out var pattern, out detail, options)
            : PosixFunctions.TryParse(f.PatternUnits.Select(b => (char)b).ToArray(), profile, out pattern, out detail, options);
        if (!success)
        {
            return ParseFailure(detail);
        }

        if (f.Operation == "Parse")
        {
            return new("ParseSuccess", null, pattern!.GroupCount, [], [], false, null, null, null);
        }

        var input = f.InputUnits.Select(b => (byte)b).ToArray();
        using var cancellation = new CancellationTokenSource();
        if (l?.Cancelled == true)
        {
            cancellation.Cancel();
        }

        var matchOptions = new RegexMatchOptions
        {
            CancellationToken = cancellation.Token,
            Limits = new RegexMatchLimits(l?.MaxWork ?? 10000000, l?.MaxWorkspaceBytes ?? 67108864)
        };
        // Explicit internal logical capacities are fixture-only bindings to the real
        // verified executor. All ordinary recipes use the actual public workspace.
        if (l?.FrameCapacity is not null || l?.HistoryCapacity is not null)
        {
            var executor = ((PosixWorkspaceExecutor)pattern!.Executor).Executor;
            var capacity = new PosixExecutionCapacity(l?.FrameCapacity ?? 4096, l?.HistoryCapacity ?? 65536);
            var required = executor.GetRequiredCapacity(SearchOperation.Range, capacity);
            var state = new RunState(new(new int[required.StateSlots], new int[required.CaptureSlots], new int[required.ScratchSlots]),
                pattern.MatchLimits, matchOptions);
            var status = executor.Search(input, f.StartAt, SearchOperation.Range, (PosixExecutionFlags)f.ExecutionFlags,
                capacity, ref state, [], out var range);
            return Execution(f, status, state.Failure, pattern.GroupCount, range, [], [], false);
        }
        using var workspace = pattern!.CreateWorkspace();
        var sentinel = new RegexCapture(true, 54321, 12345);
        var captures = Enumerable.Repeat(sentinel, f.DestinationLength ?? pattern.GroupCount).ToArray();
        var nativeSentinel = new PosixMatch(54321, 12345);
        var native = Enumerable.Repeat(nativeSentinel, f.DestinationLength ?? 0).ToArray();
        // Coordinator-approved additive input erratum, exactly one immutable
        // expanded recipe. Check the ORIGINAL short-buffer precondition as well
        // as the intended BRE operation with its corrected full generic arity.
        if (f.Id == "POSIX-E-bre-parent")
        {
            Assert.Equal("FindCaptures", f.Operation);
            Assert.Equal(1, f.DestinationLength);
            Assert.Equal(3, pattern.GroupCount);
            Assert.Equal(RegexSearchStatus.DestinationTooSmall,
                workspace.FindCaptures(input, f.StartAt, captures, out var originalRange, matchOptions));
            Assert.Equal(default, originalRange);
            Assert.Equal(RegexExecutionFailureCode.DestinationTooSmall, workspace.LastFailure.Code);
            Assert.Equal(0, workspace.LastFailure.WorkConsumed);
            Assert.Equal(3, workspace.LastFailure.RequiredCapacity);
            Assert.All(captures, c => Assert.Equal(sentinel, c));
            captures = [.. Enumerable.Repeat(sentinel, pattern.GroupCount)];
            Console.WriteLine("POSIX-E-bre-parent input erratum: original dest1 => DestinationTooSmall/work0/untouched; effective full generic dest3 is executed below against unchanged intended NoMatch.");
        }
        RegexSearchStatus result;
        RegexMatchRange match = default;
        // Three frozen Find recipes carry native eflags, which generic Find does
        // not expose. Exercise their actual typed range call, preserving source
        // endpoints; the fixture observation requests only the resulting range.
        if (f.Operation == "Find" && f.ExecutionFlags != 0)
        {
            Assert.Equal(0, f.StartAt);
            Span<PosixMatch> endpoint = stackalloc PosixMatch[1];
            result = PosixFunctions.RegExec(workspace, input, endpoint, (PosixExecutionFlags)f.ExecutionFlags, matchOptions);
            if (result == RegexSearchStatus.Match)
            {
                match = new(endpoint[0].Start, endpoint[0].End - endpoint[0].Start);
            }

            return Execution(f, result, workspace.LastFailure, pattern.GroupCount, match, [], [], false);
        }
        try
        {
            result = f.Operation switch
            {
                "Boolean" => workspace.IsMatch(input, f.StartAt, matchOptions),
                "Find" => workspace.Find(input, f.StartAt, out match, matchOptions),
                "FindCaptures" => workspace.FindCaptures(input, f.StartAt, captures, out match, matchOptions),
                "RegExec" => PosixFunctions.RegExec(workspace, input, native, (PosixExecutionFlags)f.ExecutionFlags, matchOptions),
                _ => throw new InvalidDataException("Unknown real operation")
            };
        }
        catch (NotSupportedException)
        {
            return new("OperationRejected", null, null, [], [], false,
                new("NotSupportedException", "None", "None", "Options", 0, 0, "Utf16CodeUnits"), null, null);
        }
        var unchanged = f.Operation == "RegExec" && native.All(n => n == nativeSentinel);
        if (result == RegexSearchStatus.DestinationTooSmall)
        {
            Assert.All(captures, c => Assert.Equal(sentinel, c));
        }
        // Only requested outputs appear in the observation. Failure clearing is also
        // checked directly because the interchange has no generic failure buffer field.
        if (f.Operation == "FindCaptures" && result != RegexSearchStatus.Match && result != RegexSearchStatus.DestinationTooSmall)
        {
            Assert.All(captures.Take(pattern.GroupCount), c => Assert.Equal(default, c));
        }

        return Execution(f, result, workspace.LastFailure, pattern.GroupCount, match, captures,
            f.Operation == "RegExec" && !unchanged ? native : [], unchanged);
    }
    private static PosixExpected ParseFailure(PosixRegexDiagnostic d)
    {
        var status = d.Diagnostic.Code.ToString();
        if (status.EndsWith("LimitExceeded", StringComparison.Ordinal))
        {
            status = "LimitExceeded";
        }

        return new(status, null, null, [], [], false,
            new(d.Diagnostic.Code.ToString(), d.ErrorCode.ToString(), d.Disposition.ToString(), d.Location.ToString(),
                d.Diagnostic.Offset, d.Diagnostic.Length, d.Diagnostic.LocationUnit.ToString()), null, null);
    }
    private static PosixExpected Execution(PosixFixture f, RegexSearchStatus status, RegexExecutionFailure failure,
        int count, RegexMatchRange range, RegexCapture[] captures, PosixMatch[] native, bool unchanged)
    {
        PosixFixtureMatch[] matches = status == RegexSearchStatus.Match && f.Operation is "Find" or "FindCaptures"
            ? [new(range, f.Operation == "Find" ? [new(true, range.Index, range.Length)] : captures[..count])] : [];
        var error = failure.Code == RegexExecutionFailureCode.None ? null : new PosixFixtureError(failure.Code.ToString(),
            "None", "None", "Input", failure.Offset, failure.Code == RegexExecutionFailureCode.InvalidInputDomain ? 1 : 0, "Bytes");
        return new(status.ToString(), f.Operation == "Boolean" ? status == RegexSearchStatus.Match : null,
            count, matches, native, unchanged, error, null, null);
    }
}
