using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using GregsEx.Execution.Backtracking;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.VM;

// Independently authored semantic plans, never sibling parser output or BCL Regex.
// Expected captures/ranges follow ECMA-262 2025 §22.2.2, not executor observations.
internal sealed class Plan
{
    internal readonly List<EcmaNode> Nodes = [];
    internal readonly List<EcmaSetSlice> Sets = [];
    internal readonly List<EcmaInterval> Intervals = [];
    internal readonly List<EcmaStringSlice> Strings = [];
    internal readonly List<int> Points = [];
    internal EcmaNameSlice[] Names = [];
    internal int[] Aliases = [];
    internal EcmaScriptFlags Flags;
    internal int Groups = 1;
    internal Plan(EcmaScriptFlags flags = EcmaScriptFlags.None) { Flags = flags; }
    internal int Node(EcmaNodeKind kind, int x = -1, int y = -1, int value = 0,
        int name = -1, int min = 0, int max = 0, bool lazy = false, EcmaScriptFlags? effective = null)
    {
        var first = kind == EcmaNodeKind.Capture ? value : 0;
        var count = kind == EcmaNodeKind.Capture ? 1 : 0;
        foreach (var child in new[] { x, y })
        {
            if (child < 0 || Nodes[child].CaptureCount == 0)
            {
                continue;
            }

            if (count == 0)
            {
                first = Nodes[child].FirstCapture;
            }

            count += Nodes[child].CaptureCount;
        }
        if (kind == EcmaNodeKind.Capture)
        {
            Groups = Math.Max(Groups, value + 1);
        }

        Nodes.Add(new(kind, x, y, value, name, min, max, lazy,
            effective ?? Flags & (EcmaScriptFlags.IgnoreCase | EcmaScriptFlags.Multiline | EcmaScriptFlags.DotAll),
            first, count, new(0, 0, RegexOffsetUnit.Utf16CodeUnits)));
        return Nodes.Count - 1;
    }
    internal int Lit(int point, EcmaScriptFlags? effective = null) => Node(EcmaNodeKind.Literal, value: point, effective: effective);
    internal int Empty() => Node(EcmaNodeKind.Empty);
    internal int Seq(int x, int y) => Node(EcmaNodeKind.Sequence, x, y);
    internal int Alt(int x, int y) => Node(EcmaNodeKind.Alternative, x, y);
    internal int Cap(int group, int child, int name = -1) => Node(EcmaNodeKind.Capture, child, value: group, name: name);
    internal int Ref(int group, int name = -1) => Node(EcmaNodeKind.Backreference, value: group, name: name);
    internal int Rep(int child, int min = 0, int max = -1, bool lazy = false) => Node(EcmaNodeKind.Repeat, child, min: min, max: max, lazy: lazy);
    internal int Assert(EcmaNodeKind kind, int child) => Node(kind, child);
    internal int Text(string text)
    {
        if (text.Length == 0)
        {
            return Empty();
        }

        var root = Lit(text[0]);
        for (var i = 1; i < text.Length; i++)
        {
            root = Seq(root, Lit(text[i]));
        }

        return root;
    }
    internal int Set(EcmaInterval[] intervals, params int[][] strings)
    {
        var io = Intervals.Count;
        var so = Strings.Count;
        Intervals.AddRange(intervals);
        foreach (var str in strings)
        { Strings.Add(new(Points.Count, str.Length)); Points.AddRange(str); }
        Sets.Add(new(io, intervals.Length, so, strings.Length));
        return Node(EcmaNodeKind.Set, value: Sets.Count - 1);
    }
    internal EcmaProgram Program(RegexCompileLimits? limits = null)
    {
        var budget = new CompileBudget(limits ?? new());
        Xunit.Assert.True(EcmaProgram.TryCreate(new(Flags), Nodes.Count - 1, Groups, 0, Nodes.ToArray(),
            Sets.ToArray(), Intervals.ToArray(), Strings.ToArray(), Points.ToArray(), Names, Aliases,
            ref budget, out var program, out var diagnostic), diagnostic.ToString());
        return program!;
    }
    internal IEcmaExecutor Build()
    {
        var program = Program();
        var budget = new CompileBudget(new());
        Xunit.Assert.True(new EcmaExecutorFactory().TryCreate(program, new TestData(), ref budget, out var executor, out var diagnostic), diagnostic.ToString());
        return executor!;
    }
}

internal sealed class TestData : IEcmaUnicodeData
{
    internal static readonly EcmaCaseTables Tables = CreateTables();
    private static EcmaCaseTables CreateTables()
    {
        var unicode = new List<EcmaCaseMapping>();
        var legacy = new List<EcmaCaseMapping>();
        for (var i = 0; i < 26; i++)
        { unicode.Add(new('A' + i, 'a' + i)); legacy.Add(new('a' + i, 'A' + i)); }
        unicode.Add(new(0x017F, 's'));
        unicode.Add(new(0x212A, 'k'));
        var budget = new CompileBudget(new());
        Xunit.Assert.True(EcmaCaseTables.TryCreate(unicode.ToArray(), legacy.ToArray(), ref budget, out var tables, out _));
        return tables!;
    }
    public string DataId { get; init; } = "Unicode-16.0.0+Emoji-16.0";
    public EcmaCaseTables CaseTables => Tables;
    // Any runtime provider use after construction is a test failure.
    public bool TryResolveProperty(ReadOnlySpan<char> name, ReadOnlySpan<char> value, bool unicodeSets, out EcmaProperty property) => throw new InvalidOperationException();
    public ReadOnlySpan<EcmaInterval> GetIntervals(EcmaProperty property) => throw new InvalidOperationException();
    public ReadOnlySpan<EcmaStringSlice> GetStrings(EcmaProperty property) => throw new InvalidOperationException();
    public ReadOnlySpan<int> GetStringCodePoints(EcmaProperty property) => throw new InvalidOperationException();
    public bool IsIdentifierStart(int point) => throw new InvalidOperationException();
    public bool IsIdentifierContinue(int point) => throw new InvalidOperationException();
}

internal sealed class Prepared
{
    internal readonly IEcmaExecutor Executor;
    internal readonly int[] States, Captures, Scratch;
    internal readonly RegexCapture[] Output;
    internal RegexExecutionFailure Failure;
    internal RegexMatchRange Match;
    internal long Work;
    internal Prepared(IEcmaExecutor executor, int frames = 128)
    {
        Executor = executor;
        var capacity = executor.GetRequiredCapacity(SearchOperation.Captures, frames);
        States = new int[capacity.StateSlots];
        Captures = new int[capacity.CaptureSlots];
        Scratch = new int[capacity.ScratchSlots];
        Output = new RegexCapture[executor.Program.GroupCount];
    }
    internal RegexSearchStatus Search(string input, int start = 0, bool sticky = false,
        EcmaStartPolicy policy = EcmaStartPolicy.LowerBound, SearchOperation operation = SearchOperation.Captures,
        RegexMatchLimits? limits = null, RegexMatchOptions options = default)
    {
        var state = new RunState(new(States, Captures, Scratch), limits ?? DefaultLimits, options);
        var status = Executor.Search(input, start, sticky, policy, operation, ref state, Output, out Match);
        Failure = state.Failure;
        Work = state.Budget.WorkConsumed;
        return status;
    }
    internal static readonly RegexMatchLimits DefaultLimits = new();
    internal void Expect(string input, int start, int length, params RegexCapture[] captures)
    {
        Assert.Equal(RegexSearchStatus.Match, Search(input));
        Assert.Equal(new(start, length), Match);
        Assert.Equal(new RegexCapture(true, start, length), Output[0]);
        Assert.Equal(captures, Output.Skip(1).ToArray());
    }
}
