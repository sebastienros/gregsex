using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Host;

public sealed class HostLimitTests
{
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("all")]
    public void RetainedResultsFailDistinctlyAndSessionsRemainReusable(string operation)
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind(host: new(maxResults: 1));
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                source.Match("aa");
            }
            else if (operation == "replace")
            {
                source.Replace("aa", "");
            }
            else if (operation == "split")
            {
                source.Split("a");
            }
            else
            {
                using var iterator = source.MatchAll("aa").GetEnumerator();
                Assert.True(iterator.MoveNext());
                try
                { iterator.MoveNext(); }
                catch (EcmaScriptHostException e)
                { Assert.Equal(e.Failure, iterator.HostFailure); Assert.Equal(RegexSearchStatus.LimitExceeded, iterator.TerminalStatus); throw; }
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.ResultLimitExceeded, error.Failure.Code);
        Assert.Equal(2, error.Failure.RequiredCapacity);
        Assert.True(error.Failure.WorkConsumed > 0);
        source.LastIndex = 0;
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
    }
    [Fact]
    public void SplitCapturesCountAsSeparateEntries()
    {
        using var matcher = new ContractExecutor(",", captures: [default, new(true, 0, 0)]).Bind(host: new(maxResults: 2));
        Assert.Equal(EcmaScriptHostFailureCode.ResultLimitExceeded, Assert.Throws<EcmaScriptHostException>(() => matcher.Split("a,b")).Failure.Code);
        Assert.Equal(new string?[] { "a", null }, matcher.Split("a,b", 2));
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    public void MaterializedUtf16UnitsHaveIndependentCeiling(string operation)
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind(host: new(maxOutputUnits: 1));
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                source.Match("aa");
            }
            else if (operation == "replace")
            {
                source.Replace("a", "$`$&$'$&");
            }
            else
            {
                source.Split("bc");
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.OutputLimitExceeded, error.Failure.Code);
        Assert.Equal(2, error.Failure.RequiredCapacity);
    }
    [Fact]
    public void ReplacementPrefixSuffixExpansionCannotEscapeBudget()
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind(host: new(maxOutputUnits: 4));
        var error = Assert.Throws<EcmaScriptHostException>(() => source.ReplaceAll("xay", "$`$`$'$'$&"));
        Assert.Equal(EcmaScriptHostFailureCode.OutputLimitExceeded, error.Failure.Code);
        Assert.Equal(0d, source.LastIndex); // match collection already completed before templates
    }
    [Theory]
    [InlineData("match")]
    [InlineData("replace")]
    [InlineData("split")]
    [InlineData("all")]
    public void EveryAllocatingOperationAdmitsWrappersBeforeAllocating(string operation)
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind(host: new(maxMemoryBytes: 1));
        var error = Assert.Throws<EcmaScriptHostException>(() =>
        {
            if (operation == "match")
            {
                source.Match("a");
            }
            else if (operation == "replace")
            {
                source.Replace("a", "");
            }
            else if (operation == "split")
            {
                source.Split("", 0);
            }
            else
            {
                source.MatchAll("a");
            }
        });
        Assert.Equal(EcmaScriptHostFailureCode.MemoryLimitExceeded, error.Failure.Code);
        Assert.True(error.Failure.RequiredCapacity > 1);
    }
    [Theory]
    [InlineData(240, true)]
    [InlineData(239, false)]
    public void SingleResultExactMemoryBoundary(long bytes, bool passes)
    {
        using var source = new ContractExecutor("a").Bind(host: new(maxMemoryBytes: bytes));
        if (passes)
        {
            Assert.NotNull(source.Match("a"));
        }
        else
        {
            Assert.Equal(EcmaScriptHostFailureCode.MemoryLimitExceeded, Assert.Throws<EcmaScriptHostException>(() => source.Match("a")).Failure.Code);
        }
    }
    [Fact]
    public void ResultCollectionChargesLiveOldAndNewGrowth()
    {
        // Global one-unit strings: list64 + four strings136 + array64 =264.
        // A fifth string34 and replacement array96 need394, while final retained
        // list64+five strings170+new array96 would need330.
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global).Bind(host: new(maxMemoryBytes: 393));
        var error = Assert.Throws<EcmaScriptHostException>(() => source.Match("aaaaa"));
        Assert.Equal(EcmaScriptHostFailureCode.MemoryLimitExceeded, error.Failure.Code);
        Assert.Equal(394, error.Failure.RequiredCapacity);
    }
    [Fact]
    public void ReplacementChargesBuilderAndFinalStringWhileBothLive()
    {
        // no matches: list64, text64, char[16]64; final string with17? Here
        // one-unit output needs34 more:226 versus192 before final materialization.
        using var source = new ContractExecutor("z").Bind(host: new(maxMemoryBytes: 225));
        var error = Assert.Throws<EcmaScriptHostException>(() => source.Replace("a", ""));
        Assert.Equal(EcmaScriptHostFailureCode.MemoryLimitExceeded, error.Failure.Code);
        Assert.Equal(226, error.Failure.RequiredCapacity);
    }
    [Fact]
    public void NamedLookupTemplateAndCopiesConsumeSameWorkBudget()
    {
        using var source = new ContractExecutor("a", captures: [new(true, 0, 1)],
            names: [new("abcdefghijklmnopqrstuvwxyz", 0, 1)], aliases: [1]).Bind();
        var error = Assert.Throws<RegexExecutionException>(() => source.Replace("a", "$<abcdefghijklmnopqrstuvwxyz>", new() { Limits = new(maxWork: 40) }));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, error.Failure.Code);
        Assert.True(error.Failure.WorkConsumed > 20);
    }
    [Fact]
    public void BorrowedFailuresClearLogicalCapturesAndReleaseSession()
    {
        using var source = new ContractExecutor("a", EcmaScriptFlags.Global, [default]).Bind();
        var captures = new[] { new RegexCapture(true, 9, 1), new RegexCapture(true, 10, 1), new RegexCapture(true, 11, 1) };
        Assert.Equal(RegexSearchStatus.Cancelled, source.Exec("a", captures, out var range, new() { CancellationToken = new(true) }));
        Assert.Equal(default, range);
        Assert.Equal(default, captures[0]);
        Assert.Equal(default, captures[1]);
        Assert.Equal(new(true, 11, 1), captures[2]);
        Assert.Equal(RegexSearchStatus.Match, source.Exec("a", captures, out _));
        Assert.Equal(RegexExecutionFailureCode.None, source.LastFailure.Code);
        Assert.Equal(RegexSearchStatus.NoMatch, source.Exec("a", captures, out _));
        Assert.Equal(default, captures[0]);
        Assert.Equal(default, captures[1]);
    }
    [Fact]
    public void InvalidOptionsAndDisposedUseAreArgumentLifecycleFailures()
    {
        using var source = new ContractExecutor("a").Bind();
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Test("a", new() { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Test("a", new() { Limits = new(maxWork: long.MaxValue) }));
        Assert.Throws<ArgumentNullException>(() => source.Replace(null!, ""));
        Assert.Throws<ArgumentNullException>(() => source.Replace("", null!));
        Assert.Equal(RegexSearchStatus.Match, source.Test("a"));
        source.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Split(""));
    }
}
