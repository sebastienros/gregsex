using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;
using Xunit;

namespace GregsEx.Tests.Flavors.EcmaScript.Runtime.Host;

// Nonshipping reference executor: literal/empty/dot witnesses only. Expectations in
// HostSemanticsTests derive independently from edition16 host algorithms, not this executor.
internal sealed class ContractExecutor : IEcmaExecutor
{
    public EcmaProgram Program { get; }
    internal string? Word;
    internal bool Dot;
    internal RegexCapture[] Relative;
    internal Action? OnSearch;
    internal int LastStart;
    internal EcmaStartPolicy LastPolicy;
    internal bool OverflowCapacity;
    internal ContractExecutor(string? word, EcmaScriptFlags flags = 0, RegexCapture[]? captures = null,
        EcmaNameSlice[]? names = null, int[]? aliases = null)
    {
        Word = word;
        Relative = captures ?? [];
        var nodes = new List<EcmaNode>();
        var root = -1;
        for (var group = 1; group <= Relative.Length; group++)
        {
            var leaf = nodes.Count;
            nodes.Add(Node(EcmaNodeKind.Empty));
            var name = -1;
            if (names != null)
            {
                for (var n = 0; n < names.Length; n++)
                {
                    if (aliases!.AsSpan(names[n].Offset, names[n].Length).Contains(group))
                    {
                        name = n;
                    }
                }
            }

            var capture = nodes.Count;
            nodes.Add(Node(EcmaNodeKind.Capture, leaf, value: group, name: name, first: group, count: 1));
            if (root >= 0)
            { var next = nodes.Count; nodes.Add(Node(EcmaNodeKind.Sequence, root, capture, first: 1, count: group)); root = next; }
            else
            {
                root = capture;
            }
        }
        if (root < 0)
        { root = 0; nodes.Add(Node(EcmaNodeKind.Empty)); }
        var budget = new CompileBudget(new());
        Assert.True(EcmaProgram.TryCreate(new(flags), root, Relative.Length + 1, 0,
            nodes.ToArray(), [], [], [], [], names ?? [], aliases ?? [], ref budget, out var program, out var diagnostic), diagnostic.ToString());
        Program = program!;
    }
    private static EcmaNode Node(EcmaNodeKind kind, int x = -1, int y = -1, int value = 0,
        int name = -1, int first = 0, int count = 0)
        => new(kind, x, y, value, name, 0, 0, false, 0, first, count, new(0, 0, RegexOffsetUnit.Utf16CodeUnits));
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, int frameCapacity)
    {
        if (OverflowCapacity)
        {
            throw new OverflowException();
        }

        return new(checked(frameCapacity * 2 + 1), checked(2 * Program.GroupCount), checked(frameCapacity * 3));
    }
    public RegexSearchStatus Search(ReadOnlySpan<char> input, int startAt, bool sticky,
        EcmaStartPolicy startPolicy, SearchOperation operation, ref RunState state,
        Span<RegexCapture> groups, out RegexMatchRange match)
    {
        OnSearch?.Invoke();
        LastStart = startAt;
        LastPolicy = startPolicy;
        match = default;
        if (!state.TryCharge(1, startAt))
        {
            return Finish(operation, groups, ref state);
        }

        if (Program.Profile.IsUnicodeAware && startAt > 0 && startAt < input.Length)
        {
            if (!state.TryCharge(2, startAt))
            {
                return Finish(operation, groups, ref state);
            }

            if (char.IsLowSurrogate(input[startAt]) && char.IsHighSurrogate(input[startAt - 1]))
            {
                Assert.Equal(EcmaStartPolicy.BuiltinLastIndex, startPolicy);
                startAt--;
            }
        }
        for (var candidate = startAt; candidate <= input.Length; candidate++)
        {
            if (!state.TryCharge(1, candidate))
            {
                return Finish(operation, groups, ref state);
            }

            var found = Word != null;
            var length = Word?.Length ?? 0;
            if (Dot)
            { found = candidate < input.Length; length = found ? 1 : 0; }
            if (found && Dot && Program.Profile.IsUnicodeAware && char.IsHighSurrogate(input[candidate]) && candidate + 1 < input.Length)
            {
                if (!state.TryCharge(2, candidate))
                {
                    return Finish(operation, groups, ref state);
                }

                if (char.IsLowSurrogate(input[candidate + 1]))
                {
                    length = 2;
                }
            }
            if (found && !Dot)
            {
                found = candidate + length <= input.Length;
                for (var i = 0; found && i < length; i++)
                {
                    if (!state.TryCharge(1, candidate + i))
                    {
                        return Finish(operation, groups, ref state);
                    }

                    found = input[candidate + i] == Word![i];
                }
            }
            if (found)
            {
                if (!state.TryCharge(Program.GroupCount, candidate))
                {
                    return Finish(operation, groups, ref state);
                }

                match = new(candidate, length);
                if (operation == SearchOperation.Captures)
                {
                    groups[0] = new(true, candidate, length);
                    for (var g = 1; g < Program.GroupCount; g++)
                    {
                        var c = Relative[g - 1];
                        groups[g] = c.Participated ? new(true, candidate + c.Index, c.Length) : default;
                    }
                }
                return RegexSearchStatus.Match;
            }
            if (sticky)
            {
                break;
            }
        }
        if (operation == SearchOperation.Captures)
        {
            groups[..Program.GroupCount].Clear();
        }

        return RegexSearchStatus.NoMatch;
    }
    private RegexSearchStatus Finish(SearchOperation operation, Span<RegexCapture> groups, ref RunState state)
    {
        if (operation == SearchOperation.Captures)
        {
            groups[..Program.GroupCount].Clear();
        }

        return EcmaScriptMatcher.FailureStatus(state.Failure);
    }
    internal EcmaScriptMatcher Bind(RegexMatchLimits? limits = null, EcmaScriptHostLimits? host = null, int frames = 8)
        => new(this, limits, frameCapacity: frames, hostLimits: host);
}
