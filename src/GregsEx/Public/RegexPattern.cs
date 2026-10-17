#nullable enable
using GregsEx.Compilation.Core;
using GregsEx.Contracts;
using GregsEx.Execution.Thompson;
using GregsEx.IR;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Flavors.EcmaScript.Integration;
using GregsEx.Contracts.Posix;
using GregsEx.Flavors.Posix.Integration;
using GregsEx.Flavors.Pcre2.Integration;
using GregsEx.Flavors.Pcre2.Runtime.Composition;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlIntegration;

namespace GregsEx;

/// <summary>An immutable executable pattern; share it across workspaces.</summary>
public sealed partial class RegexPattern
{
    private const long AdditionalPlanBytes = 256;
    internal IRegularExecutor Executor { get; }
    internal RegexMatchLimits MatchLimits { get; }
    internal IEcmaExecutor? EcmaExecutor => Executor switch
    {
        EcmaWorkspaceExecutor w => w.Executor,
        GregsEx.Execution.Compiled.EcmaCompiledExecutor c => c.Interpreter.Executor,
        _ => null,
    };
    /// <summary>The explicit syntax and data policy.</summary>
    public RegexProfile Profile { get; }
    /// <summary>The subject storage and offset representation.</summary>
    public RegexInputKind InputKind { get; }
    /// <summary>Compiled when runtime-compiled or source-generated kernels are active; otherwise Interpreted.</summary>
    public RegexExecutionBackend ExecutionBackend
        => Executor is GregsEx.Execution.Compiled.CompiledRegularExecutor or GregsEx.Execution.Compiled.EcmaCompiledExecutor or GregsEx.Execution.Compiled.XmlCompiledExecutor or GregsEx.Execution.Compiled.Pcre2CompiledExecutor ? RegexExecutionBackend.Compiled : RegexExecutionBackend.Interpreted;
    /// <summary>The number of captures including group zero.</summary>
    public int GroupCount { get; }
    /// <summary>Conservative admitted plan bytes. First ECMAScript creation includes once-shared data.</summary>
    public long RetainedBytes { get; }
    /// <summary>Reservation needed for boolean operations. ECMAScript includes VM capture registers.</summary>
    public RegexWorkspaceCapacity BooleanCapacity => RequiredCapacity(SearchOperation.Boolean);
    /// <summary>Reservation needed for range operations. ECMAScript includes VM capture registers.</summary>
    public RegexWorkspaceCapacity RangeCapacity => RequiredCapacity(SearchOperation.Range);
    /// <summary>Reservation needed for all capture operations.</summary>
    public RegexWorkspaceCapacity CaptureCapacity => RequiredCapacity(SearchOperation.Captures);

    internal RegexPattern(SemanticProgram program, RegexProfile profile, RegexCompileOptions options, IRegularExecutor? executor = null)
    {
        var resolved = executor ?? new ThompsonExecutor(program, ExecutionModePolicy.SwitchesFor(options.ExecutionMode));
        var retainedBytes = program.RetainedBytes + AdditionalPlanBytes;
        if (executor is null
            && options.ExecutionMode == RegexExecutionMode.Auto
            && CompilationContract.IsRuntimeCompilationAvailable
            && CompilationContract.IsAutoCompilationSwitchEnabled)
        {
            resolved = TryAutoCompile(resolved, options, ref retainedBytes);
        }
        Executor = resolved;
        Profile = profile;
        InputKind = options.InputKind;
        MatchLimits = options.DefaultMatchLimits;
        GroupCount = program.GroupCount;
        RetainedBytes = retainedBytes;
    }

    internal RegexPattern(IEcmaExecutor executor, RegexCompileOptions options, long retainedBytes)
    {
        IRegularExecutor resolved = new EcmaWorkspaceExecutor(executor);
        if (options.ExecutionMode == RegexExecutionMode.Auto
            && CompilationContract.IsRuntimeCompilationAvailable
            && CompilationContract.IsAutoCompilationSwitchEnabled)
        {
            resolved = TryAutoCompile(resolved, options, ref retainedBytes);
        }
        Executor = resolved;
        Profile = executor.Program.Profile;
        InputKind = options.InputKind;
        MatchLimits = options.DefaultMatchLimits;
        GroupCount = executor.Program.GroupCount;
        RetainedBytes = retainedBytes;
    }

    internal RegexPattern(IXmlExecutor executor, RegexCompileOptions options, long retainedBytes)
    {
        IRegularExecutor resolved = new XmlWorkspaceExecutor(executor);
        if (options.ExecutionMode == RegexExecutionMode.Auto
            && CompilationContract.IsRuntimeCompilationAvailable
            && CompilationContract.IsAutoCompilationSwitchEnabled)
        {
            resolved = TryAutoCompile(resolved, options, ref retainedBytes);
        }
        Executor = resolved;
        Profile = executor.Program.Profile;
        InputKind = options.InputKind;
        MatchLimits = options.DefaultMatchLimits;
        GroupCount = executor.Program.GroupCount;
        RetainedBytes = retainedBytes;
    }

    internal RegexPattern(IPosixExecutor executor, RegexCompileOptions options, long retainedBytes)
    {
        Executor = new PosixWorkspaceExecutor(executor);
        Profile = executor.Program.Profile;
        InputKind = options.InputKind;
        MatchLimits = options.DefaultMatchLimits;
        GroupCount = executor.Program.GroupCount;
        RetainedBytes = retainedBytes;
    }

    internal RegexPattern(Pcre2Executor executor, Pcre2MatchLimits ceiling, RegexCompileOptions options, long retainedBytes)
    {
        IRegularExecutor resolved = new Pcre2WorkspaceExecutor(executor, ceiling);
        if (options.ExecutionMode == RegexExecutionMode.Auto
            && CompilationContract.IsRuntimeCompilationAvailable
            && CompilationContract.IsAutoCompilationSwitchEnabled)
        {
            resolved = TryAutoCompile(resolved, options, ref retainedBytes);
        }
        Executor = resolved;
        Profile = executor.Program.Profile;
        InputKind = options.InputKind;
        MatchLimits = options.DefaultMatchLimits;
        GroupCount = executor.Program.GroupCount;
        RetainedBytes = retainedBytes;
    }

    /// <summary>Parses an owned immutable plan or throws a structured parse exception.</summary>
    public static RegexPattern Parse(string pattern, RegexProfile profile, RegexCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (!TryParse(pattern.AsSpan(), profile, out var result, out var diagnostic, options))
        {
            throw new RegexParseException(diagnostic);
        }

        return result!;
    }

    /// <summary>Parses UTF-16 pattern units without retaining the borrowed source.</summary>
    public static bool TryParse(ReadOnlySpan<char> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic, RegexCompileOptions? options = null)
    {
        result = null;
        options ??= new();
        if (profile is Pcre2Profile pcre)
        {
            var success = Pcre2PatternFactory.TryParse(pattern, pcre, options, out result, out var detail);
            diagnostic = detail.Diagnostic;
            return success;
        }
        if (profile is PosixProfile posix)
        {
            var success = PosixFunctions.TryParse(pattern, posix, out result, out var detail, options);
            diagnostic = detail.Diagnostic;
            return success;
        }
        if (profile is XsdProfile or XPathProfile)
        {
            var success = XmlPatternFactory.TryParse(pattern, profile, options, out result, out diagnostic);
            return success || ExpectedFailure(diagnostic);
        }
        if (profile is EcmaScriptProfile ecma)
        {
            var success = EcmaPatternFactory.TryParse(pattern, ecma, options, out result, out var detail);
            diagnostic = detail.Diagnostic;
            return success;
        }
        if (!ProfileDescriptor.TryCreate(profile, options.InputKind, out var descriptor, out diagnostic))
        {
            return false;
        }

        var budget = new CompileBudget(options.Limits);
        if (!new CoreCompiler().TryCompile(pattern, descriptor!, ref budget, out var program, out diagnostic))
        {
            return ExpectedFailure(diagnostic);
        }

        return Publish(program!, profile, options, ref budget, out result, out diagnostic);
    }

    /// <summary>Parses exact byte-pattern units for RegularCore RawBytes, named POSIX byte profiles, or native 8-bit PCRE2 profiles.</summary>
    public static bool TryParse(ReadOnlySpan<byte> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic, RegexCompileOptions? options = null)
    {
        result = null;
        options ??= new();
        if (profile is Pcre2Profile pcre)
        {
            var success = Pcre2PatternFactory.TryParse(pattern, pcre, options, out result, out var detail);
            diagnostic = detail.Diagnostic;
            return success;
        }
        if (profile is PosixProfile posix)
        {
            var success = PosixFunctions.TryParse(pattern, posix, out result, out var detail, options);
            diagnostic = detail.Diagnostic;
            return success;
        }
        if (profile is XsdProfile or XPathProfile)
        {
            diagnostic = new(RegexDiagnosticCode.UnsupportedRepresentation, 0, 0, RegexOffsetUnit.Bytes);
            return false;
        }
        if (!ProfileDescriptor.TryCreate(profile, options.InputKind, out var descriptor, out diagnostic))
        {
            diagnostic = diagnostic with { LocationUnit = RegexOffsetUnit.Bytes };
            return false;
        }
        var budget = new CompileBudget(options.Limits);
        if (!new CoreCompiler().TryCompile(pattern, descriptor!, ref budget, out var program, out diagnostic))
        {
            return ExpectedFailure(diagnostic);
        }

        return Publish(program!, profile, options, ref budget, out result, out diagnostic);
    }

    private static bool ExpectedFailure(RegexDiagnostic diagnostic)
    {
        if (diagnostic.Code is RegexDiagnosticCode.InvalidProgram or RegexDiagnosticCode.None)
        {
            throw new InvalidOperationException("Compiler violated validated-program admission.");
        }

        return false;
    }

    private static bool Publish(SemanticProgram program, RegexProfile profile, RegexCompileOptions options,
        ref CompileBudget budget, out RegexPattern? result, out RegexDiagnostic diagnostic)
    {
        result = null;
        var source = new SourceRange(0, program.PatternLength, program.PatternUnit);
        if (program.RetainedBytes > options.Limits.MaxPlanBytes - AdditionalPlanBytes)
        {
            diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, program.PatternLength, program.PatternUnit);
            return false;
        }
        // Program accounting already includes its own metadata. Admit the bound executor,
        // public wrapper and retained profile/match-policy metadata before publishing them.
        if (!budget.TryReserveBytes(AdditionalPlanBytes, source, out diagnostic))
        {
            return false;
        }

        result = new(program, profile, options);
        diagnostic = default;
        return true;
    }

    internal RegexWorkspaceCapacity RequiredCapacity(SearchOperation operation)
    {
        try
        { return Executor.GetRequiredCapacity(operation); }
        catch (OverflowException)
        { throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceCapacityExceeded, 0, 0)); }
    }

    /// <summary>Creates owned scratch. A null policy prepares all capture operations without growth.
    /// Supply BooleanCapacity or RangeCapacity explicitly; RegularCore can use smaller reservations.</summary>
    public RegexWorkspace CreateWorkspace(RegexWorkspaceOptions? options = null)
    {
        options ??= new(CaptureCapacity);
        if (RegexWorkspace.MetadataBytes + options.InitialCapacity.RequiredBytes > MatchLimits.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded, 0, 0));
        }

        return new(this, options);
    }

    /// <summary>An allocating convenience boolean operation. Execution failures throw.</summary>
    public bool IsMatch(ReadOnlySpan<char> input)
    {
        using var workspace = CreateWorkspace(new(BooleanCapacity));
        return BooleanResult(workspace.IsMatch(input), workspace.LastFailure);
    }

    /// <summary>An allocating convenience byte boolean operation. Execution failures throw.</summary>
    public bool IsMatch(ReadOnlySpan<byte> input)
    {
        using var workspace = CreateWorkspace(new(BooleanCapacity));
        return BooleanResult(workspace.IsMatch(input), workspace.LastFailure);
    }

    private static bool BooleanResult(RegexSearchStatus status, RegexExecutionFailure failure) => status switch
    {
        RegexSearchStatus.Match => true,
        RegexSearchStatus.NoMatch => false,
        _ => throw new RegexExecutionException(failure)
    };
}
