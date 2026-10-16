using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;
using GregsEx.Flavors.XPath.Execution;
using GregsEx.Flavors.XPath.Parsing;

namespace GregsEx.Flavors.XPath.Host;

// One operation owns all cold buffers and one execution budget. No mutable cache,
// pool lease, input span retention, or reset between the precheck and later matches.
internal ref struct HostContext
{
    internal const int Frames = 4096;
    internal RunState State;
    internal readonly IXmlExecutor? Executor;
    internal readonly RegexCapture[] Groups;
    internal readonly XPathHostLimits Limits;
    private long _memory, _units, _results;
    internal long PeakMemory { get; private set; }
    internal readonly bool Quote => Executor is not null &&
        (((XPathProfile)Executor.Program.Profile).Flags & XPathFlags.Quote) != 0;

    internal HostContext(IXmlExecutor? executor, RegexCompileOptions options,
        RegexMatchOptions match, XPathHostLimits? limits, long compileBytes)
    {
        Executor = executor;
        Limits = limits ?? new();
        Groups = [];
        _memory = _units = _results = 0;
        PeakMemory = 0;
        State = new(new([], [], []), options.DefaultMatchLimits, match);
        // Workspace admission precedes cancellation/subject validation.
        var ceiling = match.Resolve(options.DefaultMatchLimits);
        var capacity = executor?.GetRequiredCapacity(SearchOperation.Captures, Frames) ?? new();
        var workspaceBytes = 128L + capacity.RequiredBytes;
        if (workspaceBytes > ceiling.MaxWorkspaceBytes)
        {
            throw new RegexExecutionException(new(RegexExecutionFailureCode.WorkspaceLimitExceeded,
                0, 0, workspaceBytes));
        }

        Reserve(compileBytes + workspaceBytes + 256);
        var count = executor?.Program.GroupCount ?? 0;
        Reserve(32L + 12L * count);
        Groups = new RegexCapture[count];
        var buffers = new WorkspaceBuffers(new int[capacity.StateSlots], new int[capacity.CaptureSlots],
            new int[capacity.ScratchSlots]);
        var cumulative = State.Budget;
        State = new(buffers, options.DefaultMatchLimits, match)
        {
            Budget = cumulative
        };
    }

    internal static HostContext Create(string pattern, string flags, RegexCompileOptions? compile,
        RegexMatchOptions match, XPathHostLimits? host, UnicodeVersion unicodeVersion = UnicodeVersion.Unicode16_0)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(flags);
        if (!XPathProfile.TryParseFlags(flags, out var profile, out var xml, unicodeVersion))
        {
            throw new XPathRegexException(xml);
        }

        var options = compile ?? new();
        if (options.InputKind != RegexInputKind.Utf16)
        {
            throw new RegexParseException(new(RegexDiagnosticCode.UnsupportedRepresentation, 0,
                pattern.Length, RegexOffsetUnit.Utf16CodeUnits));
        }

        var limits = options.Limits;
        // Host cold compilation is separately bounded before allocations. Keep
        // the caller's other compile ceilings, and map this reduced memory cap
        // to host exhaustion only when it is the binding ceiling.
        var memory = host?.MaxMemoryBytes ?? 67_108_864;
        var effective = new RegexCompileLimits(limits.MaxPatternUnits, limits.MaxNestingDepth,
            limits.MaxInstructions, limits.MaxClassRanges, limits.MaxCaptureGroups, limits.MaxCompileWork,
            limits.MaxPlanBytes, Math.Min(limits.MaxCompileMemoryBytes, memory));
        var budget = new CompileBudget(effective);
        try
        {
            if (!XmlUnicodeData.TryCreate(profile!.UnicodeVersion, ref budget, out var data, out var diagnostic))
            {
                throw new RegexParseException(diagnostic);
            }

            if (!new XPathCompiler().TryCompile(pattern, profile!, RegexInputKind.Utf16, data!,
                new XmlClassAlgebra(), ref budget, out var program, out xml))
            {
                if (xml.ErrorCode != XPathErrorCode.None)
                {
                    throw new XPathRegexException(xml);
                }
                // Invalid XML pattern text is a domain failure, never FORX syntax.
                throw new RegexParseException(xml.Diagnostic);
            }
            if (!new XPathExecutorFactory().TryCreate(program!, data!, ref budget, out var executor, out diagnostic))
            {
                throw new RegexParseException(diagnostic);
            }

            return new(executor!, options, match, host, budget.ReservedBytes);
        }
        catch (RegexParseException ex) when (memory < limits.MaxCompileMemoryBytes &&
            ex.Diagnostic.Code == RegexDiagnosticCode.CompileMemoryLimitExceeded)
        {
            throw new XPathHostException(new(XPathHostFailureCode.MemoryLimitExceeded, memory + 1, 0));
        }
    }

    internal bool Search(string input, int start, SearchOperation operation, out RegexMatchRange range)
    {
        var status = Executor!.Search<char, XmlCharTextReader>(input, start, operation,
            Frames, ref State, Groups, out range);
        if (status is RegexSearchStatus.Match or RegexSearchStatus.NoMatch)
        {
            return status == RegexSearchStatus.Match;
        }

        throw new RegexExecutionException(State.Failure);
    }
    internal void NonEmptyPattern()
    {
        if (Search("", 0, SearchOperation.Boolean, out _))
        {
            throw new XPathRegexException(new(new(RegexDiagnosticCode.InvalidSyntax, 0,
                0, RegexOffsetUnit.Utf16CodeUnits),
                XmlRegexDiagnosticLocation.Pattern, XPathErrorCode.FORX0003, "XP-015"));
        }
    }
    internal void Validate(string input)
    {
        if (XmlCharTextReader.Validate(input, 0, ref State) != RegexSearchStatus.NoMatch)
        {
            throw new RegexExecutionException(State.Failure);
        }
    }
    internal void Charge(long units, int offset = 0)
    {
        if (!State.TryCharge(units, offset))
        {
            throw new RegexExecutionException(State.Failure);
        }
    }
    internal void Reserve(long bytes)
    {
        if (bytes > Limits.MaxMemoryBytes - _memory)
        {
            Fail(XPathHostFailureCode.MemoryLimitExceeded, checked(_memory + bytes));
        }

        _memory += bytes;
        PeakMemory = Math.Max(PeakMemory, _memory);
    }
    internal void Release(long bytes) => _memory -= bytes;
    internal void Units(int units)
    {
        if (units > Limits.MaxOutputUnits - _units)
        {
            Fail(XPathHostFailureCode.OutputLimitExceeded, checked(_units + units));
        }

        _units += units;
    }
    internal void Result()
    {
        if (_results == Limits.MaxResults)
        {
            Fail(XPathHostFailureCode.ResultLimitExceeded, _results + 1);
        }

        _results++;
        Charge(1);
    }
    private void Fail(XPathHostFailureCode code, long required) =>
        throw new XPathHostException(new(code, required, State.Budget.WorkConsumed));
}

// Explicit array ownership. Replacement growth admits old+new live overlap,
// and final immutable copies/views are admitted by callers before publication.
internal struct HostBuffer<T>
{
    private T[]? _items;
    internal int Count { get; private set; }
    private readonly int _width;
    internal HostBuffer(int width) { _width = width; _items = null; Count = 0; }
    internal readonly ReadOnlySpan<T> Items => _items.AsSpan(0, Count);
    internal void Add(T value, ref HostContext context)
    {
        Ensure(checked(Count + 1), ref context);
        _items![Count++] = value;
    }
    internal void Append(ReadOnlySpan<T> values, ref HostContext context)
    {
        context.Charge(values.Length);
        Ensure(checked(Count + values.Length), ref context);
        values.CopyTo(_items.AsSpan(Count));
        Count += values.Length;
    }
    private void Ensure(int count, ref HostContext context)
    {
        if (_items is not null && count <= _items.Length)
        {
            return;
        }

        var next = Math.Max(count, _items is null ? 8 : (int)Math.Min(int.MaxValue, (long)_items.Length * 2));
        var bytes = 32L + (long)_width * next;
        context.Reserve(bytes);
        var replacement = new T[next];
        context.Charge(Count);
        Items.CopyTo(replacement);
        if (_items is not null)
        {
            context.Release(32L + (long)_width * _items.Length);
        }

        _items = replacement;
    }
}
