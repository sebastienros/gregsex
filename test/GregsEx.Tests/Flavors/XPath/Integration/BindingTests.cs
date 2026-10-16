using GregsEx.Contracts;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Integration;
using Xunit;

namespace GregsEx.Tests.Flavors.XPath.Integration;

public sealed class BindingTests
{
    [Theory]
    [InlineData(XPathFlags.None)]
    [InlineData(XPathFlags.IgnoreCase)]
    public void CumulativeRetainedAdmissionIncludesEarlierOwnerAndBinding(XPathFlags flags)
    {
        var budget = new CompileBudget(new());
        Assert.True(budget.TryReserveBytes(256, new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out _));
        Assert.True(XPathBinding.TryCreate("(a)+", new(flags), RegexInputKind.Utf16, ref budget, out var binding, out _));
        var total = binding!.RetainedBytes;
        Assert.Equal(budget.ReservedBytes, total);
        Assert.True(total > XmlUnicodeData.RetainedBytes + binding.Program.RetainedBytes + 256);
        foreach (var ceiling in new[] { total - 1, total })
        {
            var bounded = new CompileBudget(new(maxPlanBytes: ceiling));
            Assert.True(bounded.TryReserveBytes(256, new(0, 0, RegexOffsetUnit.Utf16CodeUnits), out _));
            var success = XPathBinding.TryCreate("(a)+", new(flags), RegexInputKind.Utf16,
                ref bounded, out var result, out var failure);
            Assert.Equal(ceiling == total, success);
            if (success)
            {
                Assert.Equal(total, result!.RetainedBytes);
            }
            else
            { Assert.Null(result); Assert.Equal(RegexDiagnosticCode.PlanLimitExceeded, failure.Diagnostic.Code); }
        }
    }

    [Fact]
    public void CompileWorkCannotResetBetweenDataParserExecutorAndBinding()
    {
        var baseline = new CompileBudget(new());
        Assert.True(XPathBinding.TryCreate("(a)\\1", new(), RegexInputKind.Utf16, ref baseline, out _, out _));
        var bounded = new CompileBudget(new(maxCompileWork: baseline.WorkConsumed - 1));
        Assert.False(XPathBinding.TryCreate("(a)\\1", new(), RegexInputKind.Utf16, ref bounded, out var result, out var failure));
        Assert.Null(result);
        Assert.Equal(RegexDiagnosticCode.CompileWorkLimitExceeded, failure.Diagnostic.Code);
        Assert.Equal(baseline.WorkConsumed - 1, bounded.WorkConsumed);
    }

    [Fact]
    public void RepresentationAndOversizedPatternAreRejectedBeforeDataAdmission()
    {
        var budget = new CompileBudget(new(maxPatternUnits: 1));
        Assert.False(XPathBinding.TryCreate("aa", new(), RegexInputKind.Utf16, ref budget, out _, out var failure));
        Assert.Equal(RegexDiagnosticCode.PatternLimitExceeded, failure.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.WorkConsumed);
        Assert.False(XPathBinding.TryCreate("a", new(), RegexInputKind.Bytes, ref budget, out _, out failure));
        Assert.Equal(RegexDiagnosticCode.UnsupportedRepresentation, failure.Diagnostic.Code);
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(0, budget.WorkConsumed);
    }

    [Fact]
    public void BorrowedPatternIsCopiedAndOriginalInputAnchorsAndNativeCapturesSurviveComposition()
    {
        var pattern = "^(😀)(a+)\\2$".ToCharArray();
        var budget = new CompileBudget(new());
        Assert.True(XPathBinding.TryCreate(pattern, new(), RegexInputKind.Utf8, ref budget, out var binding, out _));
        Array.Fill(pattern, 'x');
        var cap = binding!.RequiredCapacity(SearchOperation.Captures);
        Assert.Equal(binding.Executor.GetRequiredCapacity(SearchOperation.Captures, 4096), cap);
        var buffers = new WorkspaceBuffers(new int[cap.StateSlots], new int[cap.CaptureSlots], new int[cap.ScratchSlots]);
        var groups = new RegexCapture[3];
        var state = new RunState(buffers, new());
        Assert.Equal(RegexSearchStatus.Match, binding.Executor.Search<byte, XmlByteTextReader>("😀aaaa"u8, 0,
            SearchOperation.Captures, XPathBinding.FrameCapacity, ref state, groups, out var range));
        Assert.Equal(new(0, 8), range);
        Assert.Equal(new(true, 0, 4), groups[1]);
        Assert.Equal(new(true, 4, 2), groups[2]);
        state = new(buffers, new());
        Assert.Equal(RegexSearchStatus.NoMatch, binding.Executor.Search<byte, XmlByteTextReader>("x😀aaaa"u8, 1,
            SearchOperation.Captures, XPathBinding.FrameCapacity, ref state, groups, out _));
        Assert.All(groups, g => Assert.Equal(default, g));
        state = new(buffers, new(maxWork: 1));
        Assert.Equal(RegexSearchStatus.LimitExceeded, binding.Executor.Search<byte, XmlByteTextReader>("😀aaaa"u8, 0,
            SearchOperation.Captures, XPathBinding.FrameCapacity, ref state, groups, out _));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        state = new(buffers, new());
        Assert.Equal(RegexSearchStatus.Match, binding.Executor.Search<byte, XmlByteTextReader>("😀aaaa"u8, 0,
            SearchOperation.Captures, XPathBinding.FrameCapacity, ref state, groups, out _));
    }
}
