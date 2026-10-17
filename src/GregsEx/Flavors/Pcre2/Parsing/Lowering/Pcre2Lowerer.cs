#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.IR;

namespace GregsEx.Flavors.Pcre2.Parsing.Lowering;

internal sealed class Pcre2Lowerer : IPcre2Lowerer
{
    public bool TryLower(Pcre2Program syntax, IPcre2Data data, ref CompileBudget budget,
        out Pcre2Program? executable, out Pcre2Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        ArgumentNullException.ThrowIfNull(data);
        executable = null;
        diagnostic = default;
        if (syntax.Stage != Pcre2PlanStage.Syntax)
        {
            diagnostic = new(new(RegexDiagnosticCode.InvalidProgram, 0, syntax.SourceLength, syntax.SourceUnit), Pcre2DiagnosticKind.InvalidProgram);
            return false;
        }
        var initial = budget.ReservedBytes;
        try
        {
            // A standalone lowerer budget must also account for its still-live
            // borrowed syntax while temporary buffers and copied output overlap.
            LowerFailure.Reserve(ref budget, Math.Max(0, syntax.RetainedBytes - initial), new(0, syntax.SourceLength, syntax.SourceUnit));
            LowerFailure.Reserve(ref budget, 4096, new(0, syntax.SourceLength, syntax.SourceUnit));
            var reader = new LowerReader(syntax, data, ref budget);
#if NETSTANDARD2_0
            try
            { return reader.Lower(out executable, out diagnostic); }
            finally { budget = reader.Budget; }
#else
            return reader.Lower(out executable, out diagnostic);
#endif
        }
        catch (LowerFailure failure) { diagnostic = failure.Diagnostic; return false; }
        finally { budget.ReleaseBytes(budget.ReservedBytes - initial - (executable?.RetainedBytes ?? 0)); }
    }
}

internal sealed class LowerFailure(Pcre2Diagnostic diagnostic) : Exception
{
    internal Pcre2Diagnostic Diagnostic { get; } = diagnostic;
    internal static void Charge(ref CompileBudget budget, long work, SourceRange source)
    {
        if (!budget.TryCharge(work, source, out var d))
        {
            throw new LowerFailure(new(d, Pcre2DiagnosticKind.CompileLimit));
        }
    }
    internal static void Reserve(ref CompileBudget budget, long bytes, SourceRange source)
    {
        if (!budget.TryReserveBytes(bytes, source, out var d))
        {
            throw new LowerFailure(new(d, Pcre2DiagnosticKind.CompileLimit));
        }
    }
}

internal sealed class LowerBuffer<T>(int elementBytes)
{
    private T[] _items = [];
    internal int Count { get; private set; }
    internal Span<T> Span => _items.AsSpan(0, Count);
    internal ref T this[int index] => ref _items[index];
    internal void Add(T value, ref CompileBudget budget, SourceRange source)
    {
        LowerFailure.Charge(ref budget, 1, source);
        if (Count == _items.Length)
        {
            var capacity = _items.Length == 0 ? 4 : checked(_items.Length * 2);
            var bytes = checked(32L + elementBytes * (long)capacity);
            LowerFailure.Reserve(ref budget, bytes, source);
            LowerFailure.Charge(ref budget, Count, source);
            var replacement = new T[capacity];
            _items.AsSpan(0, Count).CopyTo(replacement);
            if (_items.Length != 0)
            {
                budget.ReleaseBytes(32L + elementBytes * (long)_items.Length);
            }

            _items = replacement;
        }
        _items[Count++] = value;
    }
    internal T Pop() { var value = _items[--Count]; _items[Count] = default!; return value; }
}

internal ref partial struct LowerReader
{
    private readonly Pcre2Program _syntax;
    private readonly IPcre2Data _provider;
#if NETSTANDARD2_0
    // The netstandard2.0 build (generator host) has no ref fields; the caller copies the budget back.
    private CompileBudget _budget;
    internal readonly CompileBudget Budget => _budget;
#else
    private ref CompileBudget _budget;
#endif
    private readonly SourceRange _source;
    private readonly int[] _parents, _firstDefinitions, _definitionCounts;
    private readonly LowerBuffer<Pcre2Reference> _references = new(32);
    private readonly LowerBuffer<int> _numbers = new(4);
    private readonly LowerBuffer<Pcre2Node> _nodes = new(64);
    private readonly LowerBuffer<int> _children = new(4);
    private readonly LowerBuffer<Pcre2GroupDefinition> _groups = new(16);
    private readonly LowerBuffer<Pcre2Condition> _conditions = new(32);
    private readonly LowerBuffer<Pcre2Assertion> _assertions = new(24);
    private readonly LowerBuffer<Pcre2LookbehindBranch> _behind = new(16);
    private readonly LowerBuffer<Pcre2ClassNode> _classes = new(48);
    private readonly LowerBuffer<Pcre2Interval> _intervals = new(8);
    private readonly LowerBuffer<Pcre2CalloutSite> _callouts = new(40);
    private long _planned;
    private int[] _nodeClass = [];
    private int[] _classMap = [];
    private byte[] _classBits = [];
    private StaticLength[] _lengths = [];
    private byte[] _lengthState = [];
    private byte[] _lengthMode = [];
    private readonly LowerBuffer<LengthFrame> _lengthStack = new(24);
    private bool _duplicateCaptures;
    private readonly LowerBuffer<PossessCursor> _possessStack = new(12);
    private int _possessRemaining = 1000;
    private CompileFacts[] _facts = [];
    private bool[] _anchor = [];
    private bool[] _line = [];
    private bool[] _dotForbidden = [];
    private byte[] _startBits = [];
    private byte[] _bitmapState = [];
    private int[] _beforeSites = [];
    private int[] _afterSites = [];
    private int[] _nextSites = [];
    private int[] _definitionMap = [];
    private int[] _executionParents = [];
    private readonly LowerBuffer<int> _origins = new(4);
    private readonly LowerBuffer<int> _copyParts = new(4);
    private readonly LowerBuffer<CopyFrame> _copyStack = new(24);
    private readonly LowerBuffer<Pcre2CalloutSite> _sourceSites = new(40);
    private readonly LowerBuffer<Pcre2LookbehindBranch> _sourceBehind = new(16);
    private readonly LowerBuffer<EnumerationFrame> _enumerationStack = new(12);
    private byte[]? _classValues;
    private byte[] _classSummary = [];
    private byte[] _classReachable = [];
    private int[] _minimum = [];
    private byte[] _minimumState = [], _minimumRecursive = [], _minimumRefActive = [];
    private bool[] _varyBefore = [];
    private readonly LowerBuffer<LengthFrame> _minimumStack = new(24);

    internal LowerReader(Pcre2Program syntax, IPcre2Data provider, ref CompileBudget budget)
    {
        _syntax = syntax;
        _provider = provider;
#if NETSTANDARD2_0
        _budget = budget;
#else
        _budget = ref budget;
#endif
        _source = new(0, syntax.SourceLength, syntax.SourceUnit);
        LowerFailure.Reserve(ref _budget, checked(4L * syntax.Nodes.Length + 8L * syntax.GroupCount), _source);
        Charge(syntax.Nodes.Length + 2L * syntax.GroupCount);
        _parents = new int[syntax.Nodes.Length];
        Array.Fill(_parents, -1);
        _firstDefinitions = new int[syntax.GroupCount];
        Array.Fill(_firstDefinitions, -1);
        _definitionCounts = new int[syntax.GroupCount];
        for (var i = 0; i < syntax.Nodes.Length; i++)
        {
            foreach (var child in syntax.Children.Slice(syntax.Nodes[i].Children.Offset, syntax.Nodes[i].Children.Count))
            { Charge(); _parents[child] = i; }
        }

        for (var i = 0; i < syntax.Groups.Length; i++)
        {
            Charge();
            var number = syntax.Groups[i].Number;
            if (_firstDefinitions[number] < 0)
            {
                _firstDefinitions[number] = i;
            }

            _definitionCounts[number]++;
        }
        _planned = checked(syntax.RetainedBytes - 64L * syntax.Nodes.Length - 4L * syntax.Children.Length - 48L * syntax.Classes.Length -
            8L * syntax.Intervals.Length - 16L * syntax.Groups.Length - 4L * syntax.Numbers.Length - 32L * syntax.References.Length -
            24L * syntax.Assertions.Length - 32L * syntax.Conditions.Length - 40L * syntax.Callouts.Length);
    }
    private void Charge(long work = 1) => LowerFailure.Charge(ref _budget, work, _source);
    private void Add<T>(LowerBuffer<T> buffer, T value, int retainedBytes = 0)
    {
        if (!Pcre2Program.TryAddPlanBytes(_planned, retainedBytes, _budget.Limits.MaxPlanBytes, out _planned))
        {
            Limit(RegexDiagnosticCode.PlanLimitExceeded);
        }

        buffer.Add(value, ref _budget, _source);
    }
    private void Limit(RegexDiagnosticCode code) => throw new LowerFailure(new(new(code, 0, _syntax.SourceLength, _syntax.SourceUnit), Pcre2DiagnosticKind.CompileLimit));
    private int NativeAt(int offset)
    {
        int low = 0, high = _syntax.SourceMap.Length;
        while (low < high)
        {
            Charge();
            var mid = low + (high - low) / 2;
            if (_syntax.SourceMap[mid] < offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return Math.Min(low, _syntax.NativePatternLength);
    }
    private readonly void Error(int code, int nativeOffset)
    {
        nativeOffset = Math.Clamp(nativeOffset, 0, _syntax.NativePatternLength);
        throw new LowerFailure(new(new(RegexDiagnosticCode.InvalidSyntax, _syntax.SourceMap[nativeOffset], 0, _syntax.SourceUnit),
            Pcre2DiagnosticKind.Syntax, (Pcre2ErrorCode)code, NativeOffset: nativeOffset));
    }
    private readonly ReadOnlySpan<int> OldChildren(int id) => _syntax.Children.Slice(_syntax.Nodes[id].Children.Offset, _syntax.Nodes[id].Children.Count);
    internal bool Lower(out Pcre2Program? executable, out Pcre2Diagnostic diagnostic)
    {
        ResolveReferences();
        ValidateAssertions();
        NormalizeClasses();
        TransformTree();
        var profile = Study(out var hints, out var bitmap);
        var plan = new Pcre2PlanData(_nodes.Span, _children.Span, _classes.Span, _intervals.Span, _groups.Span,
            _syntax.Names, _numbers.Span, _references.Span, _assertions.Span, _conditions.Span, _callouts.Span,
            _syntax.Controls, _syntax.Marks, _syntax.ScanReferences, _syntax.Units, _syntax.SourceMap,
            _behind.Span, hasLookbehindMetadata: true);
        var ok = Pcre2Program.TryCreate(profile, Pcre2PlanStage.Executable, _syntax.SourceLength, _syntax.NativePatternLength,
            _syntax.SourceUnit, _syntax.GroupCount, plan, _syntax.PatternLimits, ref _budget, out executable, out diagnostic,
            _syntax.ArgumentFlags, _syntax.PatternMatchFlags, hints, bitmap, hasExplicitCrOrLf: _syntax.HasExplicitCrOrLf,
            hasDuplicateNameOptionChange: _syntax.HasDuplicateNameOptionChange, hasBackslashC: _syntax.HasBackslashC);
        return ok;
    }
}
