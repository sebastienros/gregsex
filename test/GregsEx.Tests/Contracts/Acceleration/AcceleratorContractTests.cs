using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using GregsEx.Contracts;
using GregsEx.Contracts.Acceleration;
using Xunit;

namespace GregsEx.Tests.Contracts.Acceleration;

[Collection("AutoCompilationSwitch")]
public sealed class AcceleratorContractTests
{
    [Fact]
    public void InterpretedModeIsTheAcceleratorFreeReference()
    {
        Assert.Equal(AcceleratorSwitches.None, ExecutionModePolicy.SwitchesFor(RegexExecutionMode.Interpreted));
        var auto = ExecutionModePolicy.SwitchesFor(RegexExecutionMode.Auto);
        Assert.Equal(AcceleratorSwitches.Semantic, auto & AcceleratorSwitches.Semantic);
        Assert.Equal(CompilationContract.IsAutoCompilationPermitted, auto.HasFlag(AcceleratorSwitches.Compiled));
        Assert.False(AppContext.TryGetSwitch(CompilationContract.AutoCompilationSwitch, out var enabled) && enabled);
        Assert.False(auto.HasFlag(AcceleratorSwitches.Compiled));
    }

    [Fact]
    public void OnlyCompiledExecutionReportsTheCompiledBackend()
    {
        foreach (var kind in Enum.GetValues<AcceleratorKind>())
        {
            Assert.Equal(kind == AcceleratorKind.Compiled ? RegexExecutionBackend.Compiled : RegexExecutionBackend.Interpreted,
                ExecutionModePolicy.BackendFor(kind));
        }
    }

    [Fact]
    public void CompileDeclarationAndShippedMethodAreAnnotated()
    {
        var method = typeof(IRegexCompilation).GetMethod(nameof(IRegexCompilation.Compile))!;
        var attribute = method.GetCustomAttribute<RequiresDynamicCodeAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(CompilationContract.RequiresDynamicCodeMessage, attribute!.Message);
        var trim = method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
        Assert.NotNull(trim);
        Assert.Equal(CompilationContract.RequiresUnreferencedCodeMessage, trim!.Message);
        var shipped = typeof(RegexPattern).GetMethod("Compile", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.Equal(CompilationContract.RequiresDynamicCodeMessage, shipped.GetCustomAttribute<RequiresDynamicCodeAttribute>()!.Message);
        Assert.Equal(CompilationContract.RequiresUnreferencedCodeMessage, shipped.GetCustomAttribute<RequiresUnreferencedCodeAttribute>()!.Message);
    }

    [Fact]
    public void SpanKernelDelegatesTakeSpansByValueAndRunStateByReference()
    {
        foreach (var (type, unit) in new[] { (typeof(Utf16SearchKernel), typeof(char)), (typeof(ByteSearchKernel), typeof(byte)) })
        {
            var parameters = type.GetMethod("Invoke")!.GetParameters();
            Assert.Equal(typeof(ReadOnlySpan<>).MakeGenericType(unit), parameters[0].ParameterType);
            Assert.True(parameters[3].ParameterType.IsByRef);
            Assert.Equal(typeof(RunState), parameters[3].ParameterType.GetElementType());
            Assert.True(parameters[5].IsOut);
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(object) || p.ParameterType == typeof(object[]));
        }
        Assert.True(LoweringLimits.Default.IsValid);
        Assert.False(default(LoweringLimits).IsValid);
    }

    [Fact]
    public void FastExpressionCompilerSourceIsInternalOnly()
    {
        var assembly = typeof(RegexPattern).Assembly;
        var fec = assembly.GetTypes().Where(t => t.Namespace?.StartsWith("FastExpressionCompiler", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(fec);
        Assert.DoesNotContain(fec, t => t.IsVisible);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("FastExpressionCompiler", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(AppContext.BaseDirectory, "FastExpressionCompiler*.dll"));
    }

    [Fact]
    public void ThirdPartyNoticeCarriesTheMitTermsAndIsPacked()
    {
        var notice = File.ReadAllText(RepositoryFile("THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("FastExpressionCompiler.Internal.src 6.0.0", notice);
        Assert.Contains("Maksim Volkau", notice);
        Assert.Contains("Permission is hereby granted", notice);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", File.ReadAllText(RepositoryFile("src/GregsEx/GregsEx.csproj")));
    }

    [Fact]
    public void ScanChargesAreChunkedAndNeverExceedTheWindow()
    {
        var state = new RunState(new([], [], []), new(5000));
        Assert.Equal(AcceleratorWork.ScanChunkUnits, AcceleratorWork.TryReserveScan(ref state, 100_000, 0));
        Assert.Equal(10, AcceleratorWork.TryReserveScan(ref state, 10, 1024));
        Assert.Equal(0, AcceleratorWork.TryReserveScan(ref state, 0, 1034));
        Assert.Equal(AcceleratorWork.ScanChunkUnits + 10, state.Budget.WorkConsumed);
    }

    [Fact]
    public void ScanLimitIsReportedNeverTurnedIntoAnEmptyWindow()
    {
        var state = new RunState(new([], [], []), new(100));
        Assert.Equal(-1, AcceleratorWork.TryReserveScan(ref state, 1 << 20, 7));
        Assert.Equal(RegexExecutionFailureCode.WorkLimitExceeded, state.Failure.Code);
        Assert.Equal(7, state.Failure.Offset);
    }

    [Fact]
    public void ReleaseManifestPinsCasesCohortsHostPolicyAndThresholds()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepositoryFile("docs/benchmarks/release-manifest.json")));
        var root = document.RootElement;
        Assert.Equal("1.0", root.GetProperty("schema_version").GetString());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        var ids = cases.Select(c => c.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        var categories = cases.Select(c => c.GetProperty("category").GetString()!).ToHashSet();
        foreach (var required in new[] { "literal", "alternation", "anchored", "captures", "classes-unicode", "backtracking-prone", "pathological", "large-input" })
        {
            Assert.Contains(required, categories);
        }

        var profiles = cases.SelectMany(c => c.GetProperty("profiles").EnumerateArray().Select(p => p.GetString()!)).ToHashSet();
        foreach (var required in new[] { "RegularCore", "ECMAScript", "POSIX", "PCRE2" })
        {
            Assert.Contains(required, profiles);
        }

        var thresholds = root.GetProperty("thresholds");
        Assert.True(thresholds.GetProperty("win_ratio").GetDouble() < 1.0);
        Assert.True(thresholds.GetProperty("regression_ratio").GetDouble() > 1.0);
        Assert.True(thresholds.GetProperty("noise_multiplier").GetDouble() >= 1.0);
        Assert.Contains(root.GetProperty("comparators").EnumerateArray().Select(c => c.GetProperty("id").GetString()), id => id == "bcl-sourcegen");
        Assert.Equal("osx-arm64", root.GetProperty("host_policy").GetProperty("local_rid").GetString());
    }

    internal static string RepositoryFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GregsEx.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }
}
