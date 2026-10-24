#nullable enable
using GregsEx.Contracts;

namespace GregsEx;

public sealed partial class EcmaScriptMatcher
{
    private static void RequireSuccess(RegexSearchStatus status, ref RunState state)
    {
        if (status is not (RegexSearchStatus.Match or RegexSearchStatus.NoMatch))
        {
            throw new RegexExecutionException(state.Failure);
        }
    }
    private void RequireGlobal(ref RunState state)
    {
        if (!Global)
        {
            EcmaHostBudget.Fail(EcmaScriptHostFailureCode.GlobalFlagRequired, 0, ref state);
        }
    }
    private RegexCapture Named(ReadOnlySpan<char> name, ReadOnlySpan<RegexCapture> captures, ref RunState state)
    {
        foreach (var entry in Program.Names)
        {
            EcmaHostBudget.Charge(1L + Math.Min(name.Length, entry.Name.Length), ref state);
            if (!name.SequenceEqual(entry.Name))
            {
                continue;
            }

            foreach (var alias in Program.NameAliases.Slice(entry.Offset, entry.Length))
            {
                EcmaHostBudget.Charge(1, ref state);
                if (captures[alias].Participated)
                {
                    return captures[alias];
                }
            }
            return default;
        }
        return default;
    }
    private EcmaScriptExecResult Materialize(RegexMatchRange match, ref EcmaHostBudget host, ref RunState state)
    {
        host.Result(ref state);
        host.Reserve(96L + 32L + 16L * Program.GroupCount + 32L + 32L * Program.Names.Length, ref state);
        EcmaHostBudget.Charge(Program.GroupCount, ref state);
        var captures = new RegexCapture[Program.GroupCount];
        _hostCaptures.AsSpan().CopyTo(captures);
        var names = new EcmaScriptNamedCapture[Program.Names.Length];
        for (var n = 0; n < names.Length; n++)
        {
            var entry = Program.Names[n];
            names[n] = new(entry.Name, Named(entry.Name, captures, ref state));
        }
        return new(match, captures, names, (Program.Profile.Flags & EcmaScriptFlags.HasIndices) != 0);
    }
    private static string Text(string input, RegexCapture capture, ref EcmaHostBudget host, ref RunState state)
        => Text(input.AsSpan(capture.Index, capture.Length), ref host, ref state);
    private static string Text(ReadOnlySpan<char> value, ref EcmaHostBudget host, ref RunState state)
    {
        host.Units(value.Length, ref state);
        host.Reserve(32L + 2L * value.Length, ref state);
        return value.ToString();
    }
    /// <summary>String match: one captured result without g, otherwise bounded whole-match strings.</summary>
    public EcmaScriptMatchResult? Match(string input, RegexMatchOptions options = default)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            var state = CreateState(options);
            var host = new EcmaHostBudget(_hostLimits);
            try
            {
                EcmaHostBudget.Charge(0, ref state);
                if (!Global)
                {
                    var status = ExecCore(input, SearchOperation.Captures, _hostCaptures, out var range, ref state);
                    RequireSuccess(status, ref state);
                    if (status == RegexSearchStatus.NoMatch)
                    {
                        return null;
                    }

                    var single = Materialize(range, ref host, ref state);
                    host.Reserve(64, ref state);
                    return new(single);
                }
                _lastIndex = 0;
                var matches = EcmaHostList<string>.Create(8, ref host, ref state);
                while (true)
                {
                    var status = ExecCore(input, SearchOperation.Range, [], out var range, ref state);
                    RequireSuccess(status, ref state);
                    if (status == RegexSearchStatus.NoMatch)
                    {
                        break;
                    }

                    host.Result(ref state);
                    var text = Text(input.AsSpan(range.Index, range.Length), ref host, ref state);
                    matches.Add(text, ref host, ref state);
                    if (range.Length == 0)
                    {
                        _lastIndex = Advance(input, ToLength(_lastIndex), ref state);
                    }
                }
                if (matches.Count == 0)
                {
                    return null;
                }

                var array = matches.Finish(ref host, ref state);
                host.Reserve(128, ref state);
                return new(array);
            }
            finally { LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
    /// <summary>Search from zero, restoring the exact original numeric LastIndex in every outcome.</summary>
    public int Search(string input, RegexMatchOptions options = default)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            var state = CreateState(options);
            var original = _lastIndex;
            try
            {
                _lastIndex = 0;
                var status = ExecCore(input, SearchOperation.Range, [], out var range, ref state);
                RequireSuccess(status, ref state);
                return status == RegexSearchStatus.Match ? range.Index : -1;
            }
            finally { _lastIndex = original; LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
    /// <summary>ECMAScript string-template substitution with the builtin collection/state rules.</summary>
    public string Replace(string input, string replacement, RegexMatchOptions options = default)
        => ReplaceCore(input, replacement, options, false);
    /// <summary>Requires g, then uses the same builtin substitution rules as Replace.</summary>
    public string ReplaceAll(string input, string replacement, RegexMatchOptions options = default)
        => ReplaceCore(input, replacement, options, true);
    private string ReplaceCore(string input, string replacement, RegexMatchOptions options, bool requireGlobal)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(replacement);
            var state = CreateState(options);
            var host = new EcmaHostBudget(_hostLimits);
            try
            {
                if (requireGlobal)
                {
                    RequireGlobal(ref state);
                }

                EcmaHostBudget.Charge(0, ref state);
                if (Global)
                {
                    _lastIndex = 0;
                }

                var matches = EcmaHostList<EcmaScriptExecResult>.Create(8, ref host, ref state);
                while (true)
                {
                    var status = ExecCore(input, SearchOperation.Captures, _hostCaptures, out var range, ref state);
                    RequireSuccess(status, ref state);
                    if (status == RegexSearchStatus.NoMatch)
                    {
                        break;
                    }

                    matches.Add(Materialize(range, ref host, ref state), ref host, ref state);
                    if (!Global)
                    {
                        break;
                    }

                    if (range.Length == 0)
                    {
                        _lastIndex = Advance(input, ToLength(_lastIndex), ref state);
                    }
                }
                var output = EcmaHostText.Create(ref host, ref state);
                var end = 0;
                for (var m = 0; m < matches.Count; m++)
                {
                    EcmaHostBudget.Charge(1, ref state);
                    var match = matches[m];
                    var range = match.Range;
                    output.Append(input.AsSpan(end, range.Index - end), ref host, ref state);
                    Substitute(input, replacement, match, output, ref host, ref state);
                    end = range.Index + range.Length;
                }
                output.Append(input.AsSpan(end), ref host, ref state);
                return output.Finish(ref host, ref state);
            }
            finally { LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
    private void Substitute(string input, string replacement, EcmaScriptExecResult match,
        EcmaHostText output, ref EcmaHostBudget host, ref RunState state)
    {
        var literal = 0;
        for (var i = 0; i < replacement.Length; i++)
        {
            EcmaHostBudget.Charge(1, ref state);
            if (replacement[i] != '$' || i + 1 == replacement.Length)
            {
                continue;
            }

            EcmaHostBudget.Charge(1, ref state);
            var token = replacement[i + 1];
            var consumed = 2;
            RegexCapture capture = default;
            var captureToken = false;
            ReadOnlySpan<char> value;
            switch (token)
            {
                case '$':
                    value = replacement.AsSpan(i, 1);
                    break;
                case '&':
                    value = input.AsSpan(match.Range.Index, match.Range.Length);
                    break;
                case '`':
                    value = input.AsSpan(0, match.Range.Index);
                    break;
                case '\'':
                    value = input.AsSpan(match.Range.Index + match.Range.Length);
                    break;
                case '<' when Program.Names.Length != 0:
                    var close = i + 2;
                    while (close < replacement.Length)
                    {
                        EcmaHostBudget.Charge(1, ref state);
                        if (replacement[close] == '>')
                        {
                            break;
                        }

                        close++;
                    }
                    if (close == replacement.Length)
                    {
                        continue;
                    }

                    consumed = close - i + 1;
                    capture = Named(replacement.AsSpan(i + 2, close - i - 2), match.Captures, ref state);
                    captureToken = true;
                    value = [];
                    break;
                default:
                    if (token is not (>= '0' and <= '9'))
                    {
                        continue;
                    }

                    var number = token - '0';
                    if (i + 2 < replacement.Length)
                    {
                        EcmaHostBudget.Charge(1, ref state);
                        var second = replacement[i + 2];
                        if (second is >= '0' and <= '9')
                        {
                            var pair = number * 10 + second - '0';
                            if (pair > 0 && pair < match.Captures.Length)
                            { number = pair; consumed = 3; }
                        }
                    }
                    if (number == 0 || number >= match.Captures.Length)
                    {
                        continue;
                    }

                    EcmaHostBudget.Charge(1, ref state);
                    capture = match.Captures[number];
                    captureToken = true;
                    value = [];
                    break;
            }
            output.Append(replacement.AsSpan(literal, i - literal), ref host, ref state);
            if (captureToken)
            {
                value = capture.Participated ? input.AsSpan(capture.Index, capture.Length) : [];
            }

            output.Append(value, ref host, ref state);
            i += consumed - 1;
            literal = i + 1;
        }
        output.Append(replacement.AsSpan(literal), ref host, ref state);
    }
    /// <summary>Split using an independent sticky session. Absent captures become null.</summary>
    public string?[] Split(string input, double? limit = null, RegexMatchOptions options = default)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(input);
            var state = CreateState(options);
            var host = new EcmaHostBudget(_hostLimits);
            try
            {
                EcmaHostBudget.Charge(0, ref state);
                var countLimit = limit.HasValue ? ToUint32(limit.Value) : uint.MaxValue;
                var entries = EcmaHostList<string?>.Create(8, ref host, ref state);
                if (countLimit == 0)
                {
                    return entries.Finish(ref host, ref state);
                }

                using var clone = new EcmaScriptMatcher(_executor, _limits, frameCapacity: _frames, hostLimits: _hostLimits);
                state = new RunState(new WorkspaceBuffers(clone._states, clone._registers, clone._scratch),
                    options.Resolve(_limits), options)
                { Budget = state.Budget, Failure = state.Failure };
                if (input.Length == 0)
                {
                    var status = clone.ExecCore(input, SearchOperation.Captures, clone._hostCaptures, out _, ref state, true);
                    RequireSuccess(status, ref state);
                    if (status == RegexSearchStatus.NoMatch)
                    {
                        AddSplit(input, new(true, 0, 0), entries, ref host, ref state);
                    }

                    return entries.Finish(ref host, ref state);
                }
                var p = 0;
                long q = 0;
                while (q < input.Length)
                {
                    clone._lastIndex = q;
                    var status = clone.ExecCore(input, SearchOperation.Captures, clone._hostCaptures, out _, ref state, true);
                    RequireSuccess(status, ref state);
                    var e = (int)Math.Min(ToLength(clone._lastIndex), input.Length);
                    if (status == RegexSearchStatus.NoMatch || e == p)
                    { q = Advance(input, q, ref state); continue; }
                    AddSplit(input, new(true, p, (int)q - p), entries, ref host, ref state);
                    if ((uint)entries.Count == countLimit)
                    {
                        return entries.Finish(ref host, ref state);
                    }

                    p = e;
                    for (var g = 1; g < Program.GroupCount; g++)
                    {
                        EcmaHostBudget.Charge(1, ref state);
                        AddSplit(input, clone._hostCaptures[g], entries, ref host, ref state);
                        if ((uint)entries.Count == countLimit)
                        {
                            return entries.Finish(ref host, ref state);
                        }
                    }
                    q = p;
                }
                AddSplit(input, new(true, p, input.Length - p), entries, ref host, ref state);
                return entries.Finish(ref host, ref state);
            }
            finally { LastFailure = state.Failure; }
        }
        finally { Exit(); }
    }
    private static void AddSplit(string input, RegexCapture capture, EcmaHostList<string?> entries,
        ref EcmaHostBudget host, ref RunState state)
    {
        host.Result(ref state);
        var text = capture.Participated ? Text(input, capture, ref host, ref state) : null;
        entries.Add(text, ref host, ref state);
    }
    internal static uint ToUint32(double value)
    {
        if (!double.IsFinite(value) || value == 0)
        {
            return 0;
        }

        var remainder = Math.Truncate(value) % 4_294_967_296d;
        if (remainder < 0)
        {
            remainder += 4_294_967_296d;
        }

        return (uint)remainder;
    }
}
