using System.Buffers;
using System.Runtime.CompilerServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;

namespace GregsEx.Execution.Posix;

internal enum PosixFastKind : byte
{
    None = 0,
    Literal,
    LiteralAlternation,
    SingleSetRepeat,
    SetRepeatSuffixLiteral,
    AtomicSequence
}

internal readonly struct PosixSeqStep
{
    internal readonly bool IsLiteral;
    internal readonly byte[]? LiteralBytes;
    internal readonly PosixByteSet Set;
    internal readonly int Min;
    internal readonly int Max;
    internal readonly int CaptureGroup;

    internal PosixSeqStep(byte[] literalBytes, int captureGroup)
    {
        IsLiteral = true;
        LiteralBytes = literalBytes;
        Set = default;
        Min = literalBytes.Length;
        Max = literalBytes.Length;
        CaptureGroup = captureGroup;
    }

    internal PosixSeqStep(PosixByteSet set, int min, int max, int captureGroup)
    {
        IsLiteral = false;
        LiteralBytes = null;
        Set = set;
        Min = min;
        Max = max;
        CaptureGroup = captureGroup;
    }
}

internal sealed class PosixAccelerator
{
    private readonly int _groupCount;
    internal readonly bool IsStartAnchored;
    internal readonly bool IsEndAnchored;
    internal readonly byte[]? RequiredBytes;
    internal readonly byte[]? PrefixBytes;
    internal readonly SearchValues<byte>? FirstByteSv;
    internal readonly int RequiredEndByte = -1;

    private readonly PosixFastKind _fastKind;
    private readonly byte[]? _literalBytes;
    private readonly byte[][]? _altBytes;
    private readonly PosixByteSet _repeatSet;
    private readonly SearchValues<byte>? _repeatSv;
    private readonly int _repeatMin;
    private readonly int _repeatMax;
    private readonly byte[]? _suffixBytes;
    private readonly bool _suffixAllInSet;
    private readonly PosixSeqStep[]? _seqSteps;
    private readonly int _seqAnchorIndex = -1;
    private readonly int _seqPrefixFixedLen = -1;

    private PosixAccelerator(
        int groupCount,
        bool isStartAnchored,
        bool isEndAnchored,
        byte[]? requiredBytes,
        byte[]? prefixBytes,
        SearchValues<byte>? firstByteSv,
        PosixFastKind fastKind = PosixFastKind.None,
        byte[]? literalBytes = null,
        byte[][]? altBytes = null,
        PosixByteSet repeatSet = default,
        SearchValues<byte>? repeatSv = null,
        int repeatMin = 0,
        int repeatMax = 0,
        byte[]? suffixBytes = null,
        bool suffixAllInSet = false,
        PosixSeqStep[]? seqSteps = null,
        int seqAnchorIndex = -1,
        int seqPrefixFixedLen = -1,
        int requiredEndByte = -1)
    {
        _groupCount = groupCount;
        IsStartAnchored = isStartAnchored;
        IsEndAnchored = isEndAnchored;
        RequiredBytes = requiredBytes;
        PrefixBytes = prefixBytes;
        FirstByteSv = firstByteSv;
        RequiredEndByte = requiredEndByte;
        _fastKind = fastKind;
        _literalBytes = literalBytes;
        _altBytes = altBytes;
        _repeatSet = repeatSet;
        _repeatSv = repeatSv;
        _repeatMin = repeatMin;
        _repeatMax = repeatMax;
        _suffixBytes = suffixBytes;
        _suffixAllInSet = suffixAllInSet;
        _seqSteps = seqSteps;
        _seqAnchorIndex = seqAnchorIndex;
        _seqPrefixFixedLen = seqPrefixFixedLen;
    }

    internal static PosixAccelerator? TryCreate(PosixProgram program)
    {
        if (program.HasBackreferences)
        {
            return null;
        }

        if ((program.Profile.Flags & (PosixCompileFlags.IgnoreCase | PosixCompileFlags.Newline)) != 0)
        {
            return null;
        }

        var spine = new List<int>();
        FlattenSequence(program, program.Root, spine);
        if (spine.Count == 0)
        {
            return null;
        }

        var isStartAnchored = false;
        var isEndAnchored = false;
        var startIdx = 0;
        var endIdx = spine.Count;

        if (program.Nodes[spine[0]].Kind == PosixNodeKind.Start)
        {
            isStartAnchored = true;
            startIdx++;
        }
        if (endIdx > startIdx && program.Nodes[spine[endIdx - 1]].Kind == PosixNodeKind.End)
        {
            isEndAnchored = true;
            endIdx--;
        }

        var body = spine.GetRange(startIdx, endIdx - startIdx);
        if (body.Count == 0)
        {
            return null;
        }

        // Extract required literal run along the top-level spine
        var requiredBytes = ExtractBestLiteralRun(program, body, out var prefixBytes);
        var requiredEndByte = isEndAnchored && TryGetRequiredTerminalByte(program, body[^1], out var termByte) ? termByte : -1;

        // 1. Whole-pattern Literal
        if (program.GroupCount == 1 && TryReadLiteralNodes(program, body, out var wholeLit) && wholeLit is { Length: > 0 })
        {
            return new(1, isStartAnchored, isEndAnchored, wholeLit, wholeLit, null,
                fastKind: PosixFastKind.Literal, literalBytes: wholeLit);
        }

        // 2. Whole-pattern LiteralAlternation
        if (program.GroupCount == 1 && !isStartAnchored && !isEndAnchored && body.Count == 1 &&
            program.Nodes[body[0]].Kind == PosixNodeKind.Alternative &&
            TryExtractLiteralAlternation(program, body[0], out var altBytes, out var firstSv))
        {
            return new(1, false, false, null, null, firstSv,
                fastKind: PosixFastKind.LiteralAlternation, altBytes: altBytes);
        }

        // 3. Whole-pattern SingleSetRepeat
        if (program.GroupCount == 1 && body.Count == 1 &&
            program.Nodes[body[0]].Kind == PosixNodeKind.Repeat)
        {
            var rNode = program.Nodes[body[0]];
            var child = program.Nodes[rNode.X];
            if (rNode.Min >= 1 && child.Kind == PosixNodeKind.Class)
            {
                var set = program.Sets[child.Value];
                var sv = CreateByteSearchValues(set);
                var max = rNode.Max < 0 ? int.MaxValue : rNode.Max;
                return new(1, isStartAnchored, isEndAnchored, requiredBytes, null, sv,
                    fastKind: PosixFastKind.SingleSetRepeat,
                    repeatSet: set, repeatSv: sv, repeatMin: rNode.Min, repeatMax: max);
            }
        }

        // 4. Whole-pattern SetRepeatSuffixLiteral ([class]+literal)
        if (program.GroupCount == 1 && !isStartAnchored && !isEndAnchored && body.Count >= 2 &&
            program.Nodes[body[0]].Kind == PosixNodeKind.Repeat)
        {
            var rNode = program.Nodes[body[0]];
            var child = program.Nodes[rNode.X];
            if (rNode.Min == 1 && rNode.Max < 0 && child.Kind == PosixNodeKind.Class)
            {
                var tailNodes = body.GetRange(1, body.Count - 1);
                if (TryReadLiteralNodes(program, tailNodes, out var suffix) && suffix is { Length: > 0 })
                {
                    var set = program.Sets[child.Value];
                    var allInSet = true;
                    foreach (var b in suffix)
                    {
                        if (!set.Contains(b))
                        { allInSet = false; break; }
                    }
                    var sv = CreateByteSearchValues(set);
                    return new(1, false, false, suffix, null, sv,
                        fastKind: PosixFastKind.SetRepeatSuffixLiteral,
                        repeatSet: set, repeatSv: sv, repeatMin: 1, repeatMax: int.MaxValue,
                        suffixBytes: suffix, suffixAllInSet: allInSet);
                }
            }
        }

        // 5. Whole-pattern AtomicSequence (e.g., ([0-9]{4})-([0-9]{2})-([0-9]{2}), ([a-z]+)=([a-z0-9]+), [a-q][^u-z]{13}x)
        if (!isStartAnchored && !isEndAnchored &&
            TryMatchAtomicSequence(program, body, requiredBytes, out var seqAcc))
        {
            return seqAcc;
        }

        // Prefilter-only accelerator
        SearchValues<byte>? leadSv = null;
        if (prefixBytes is null && body.Count > 0)
        {
            var firstNode = program.Nodes[body[0]];
            if (firstNode.Kind == PosixNodeKind.Class)
            {
                leadSv = CreateByteSearchValues(program.Sets[firstNode.Value]);
            }
            else if (firstNode.Kind == PosixNodeKind.Repeat && firstNode.Min >= 1 &&
                     program.Nodes[firstNode.X].Kind == PosixNodeKind.Class)
            {
                leadSv = CreateByteSearchValues(program.Sets[program.Nodes[firstNode.X].Value]);
            }
        }

        if (isStartAnchored || requiredBytes is not null || prefixBytes is not null || leadSv is not null || requiredEndByte >= 0)
        {
            return new(program.GroupCount, isStartAnchored, isEndAnchored, requiredBytes, prefixBytes, leadSv, requiredEndByte: requiredEndByte);
        }

        return null;
    }

    private static bool TryGetRequiredTerminalByte(PosixProgram program, int nodeIdx, out byte terminalByte)
    {
        terminalByte = 0;
        var node = program.Nodes[nodeIdx];
        switch (node.Kind)
        {
            case PosixNodeKind.Literal:
                terminalByte = (byte)node.Value;
                return true;
            case PosixNodeKind.Capture:
                return TryGetRequiredTerminalByte(program, node.X, out terminalByte);
            case PosixNodeKind.Repeat:
                return node.Min >= 1 && TryGetRequiredTerminalByte(program, node.X, out terminalByte);
            case PosixNodeKind.Sequence:
                return TryGetRequiredTerminalByte(program, node.Y, out terminalByte);
            case PosixNodeKind.Alternative:
                if (TryGetRequiredTerminalByte(program, node.X, out var b1) &&
                    TryGetRequiredTerminalByte(program, node.Y, out var b2) &&
                    b1 == b2)
                {
                    terminalByte = b1;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static void FlattenSequence(PosixProgram program, int nodeIdx, List<int> list)
    {
        var node = program.Nodes[nodeIdx];
        if (node.Kind == PosixNodeKind.Sequence)
        {
            FlattenSequence(program, node.X, list);
            FlattenSequence(program, node.Y, list);
        }
        else
        {
            list.Add(nodeIdx);
        }
    }

    private static bool TryReadLiteralNodes(PosixProgram program, List<int> nodeIds, out byte[]? bytes)
    {
        bytes = null;
        var list = new List<byte>(nodeIds.Count);
        foreach (var id in nodeIds)
        {
            var n = program.Nodes[id];
            if (n.Kind != PosixNodeKind.Literal)
            {
                return false;
            }

            list.Add((byte)n.Value);
        }
        bytes = list.ToArray();
        return bytes.Length > 0;
    }

    private static byte[]? ExtractBestLiteralRun(PosixProgram program, List<int> body, out byte[]? prefixBytes)
    {
        prefixBytes = null;
        List<byte>? best = null;
        var current = new List<byte>();
        var inPrefix = true;
        var prefix = new List<byte>();

        foreach (var id in body)
        {
            var n = program.Nodes[id];
            if (n.Kind == PosixNodeKind.Literal)
            {
                var b = (byte)n.Value;
                current.Add(b);
                if (inPrefix)
                {
                    prefix.Add(b);
                }

                if (best is null || current.Count > best.Count)
                {
                    best = new List<byte>(current);
                }
            }
            else
            {
                current.Clear();
                inPrefix = false;
            }
        }
        if (prefix.Count > 0)
        {
            prefixBytes = prefix.ToArray();
        }

        return best?.ToArray();
    }

    private static bool TryExtractLiteralAlternation(
        PosixProgram program, int altRoot, out byte[][]? altBytes, out SearchValues<byte>? firstSv)
    {
        altBytes = null;
        firstSv = null;
        var branches = new List<int>();
        FlattenAlternatives(program, altRoot, branches);
        if (branches.Count < 2 || branches.Count > 64)
        {
            return false;
        }

        var result = new List<byte[]>(branches.Count);
        var firstSet = new HashSet<byte>();
        foreach (var br in branches)
        {
            var seq = new List<int>();
            FlattenSequence(program, br, seq);
            if (!TryReadLiteralNodes(program, seq, out var lit) || lit is null || lit.Length == 0)
            {
                return false;
            }

            result.Add(lit);
            firstSet.Add(lit[0]);
        }
        altBytes = result.ToArray();
        firstSv = SearchValues.Create(firstSet.ToArray());
        return true;
    }

    private static void FlattenAlternatives(PosixProgram program, int nodeIdx, List<int> branches)
    {
        var node = program.Nodes[nodeIdx];
        if (node.Kind == PosixNodeKind.Alternative)
        {
            FlattenAlternatives(program, node.X, branches);
            FlattenAlternatives(program, node.Y, branches);
        }
        else
        {
            branches.Add(nodeIdx);
        }
    }

    private static SearchValues<byte> CreateByteSearchValues(PosixByteSet set)
    {
        Span<byte> buf = stackalloc byte[256];
        var count = 0;
        for (var b = 1; b <= 255; b++)
        {
            if (set.Contains((byte)b))
            {
                buf[count++] = (byte)b;
            }
        }
        return SearchValues.Create(buf[..count]);
    }

    private static bool TryMatchAtomicSequence(
        PosixProgram program, List<int> body, byte[]? requiredBytes, out PosixAccelerator? acc)
    {
        acc = null;
        var steps = new List<PosixSeqStep>();
        var i = 0;
        while (i < body.Count)
        {
            var nodeIdx = body[i];
            var n = program.Nodes[nodeIdx];
            if (n.Kind == PosixNodeKind.Literal)
            {
                var lit = new List<byte>();
                while (i < body.Count && program.Nodes[body[i]].Kind == PosixNodeKind.Literal)
                {
                    lit.Add((byte)program.Nodes[body[i]].Value);
                    i++;
                }
                steps.Add(new PosixSeqStep(lit.ToArray(), 0));
                continue;
            }
            if (TryReadClassStep(program, nodeIdx, 0, out var classStep))
            {
                steps.Add(classStep);
                i++;
                continue;
            }
            if (n.Kind == PosixNodeKind.Capture)
            {
                var group = n.Value;
                var innerSeq = new List<int>();
                FlattenSequence(program, n.X, innerSeq);
                if (innerSeq.Count == 1 && TryReadClassStep(program, innerSeq[0], group, out var capStep))
                {
                    steps.Add(capStep);
                    i++;
                    continue;
                }
                if (TryReadLiteralNodes(program, innerSeq, out var capLit) && capLit is { Length: > 0 })
                {
                    steps.Add(new PosixSeqStep(capLit, group));
                    i++;
                    continue;
                }
            }
            return false;
        }

        if (steps.Count < 2)
        {
            return false;
        }

        var anchorIdx = -1;
        var bestLen = 0;
        for (var s = 0; s < steps.Count; s++)
        {
            if (steps[s].IsLiteral && steps[s].LiteralBytes!.Length >= bestLen)
            {
                bestLen = steps[s].LiteralBytes!.Length;
                anchorIdx = s;
            }
        }
        if (anchorIdx < 0)
        {
            return false;
        }

        var prefixFixedLen = 0;
        for (var s = 0; s < anchorIdx; s++)
        {
            if (steps[s].Min != steps[s].Max)
            {
                if (s == 0 && anchorIdx == 1 && !steps[0].IsLiteral &&
                    steps[0].Min == 1 && steps[0].Max == int.MaxValue &&
                    !steps[0].Set.Contains(steps[anchorIdx].LiteralBytes![0]))
                {
                    prefixFixedLen = -1;
                    break;
                }
                return false;
            }
            prefixFixedLen += steps[s].Min;
        }

        for (var s = anchorIdx + 1; s < steps.Count; s++)
        {
            if (steps[s].Min != steps[s].Max && s != steps.Count - 1)
            {
                return false;
            }
        }

        var stepArr = steps.ToArray();
        acc = new(program.GroupCount, false, false, requiredBytes ?? stepArr[anchorIdx].LiteralBytes, null, null,
            fastKind: PosixFastKind.AtomicSequence,
            seqSteps: stepArr,
            seqAnchorIndex: anchorIdx,
            seqPrefixFixedLen: prefixFixedLen);
        return true;
    }

    private static bool TryReadClassStep(PosixProgram program, int nodeIdx, int captureGroup, out PosixSeqStep step)
    {
        step = default;
        var n = program.Nodes[nodeIdx];
        if (n.Kind == PosixNodeKind.Class)
        {
            step = new PosixSeqStep(program.Sets[n.Value], 1, 1, captureGroup);
            return true;
        }
        if (n.Kind == PosixNodeKind.Repeat && n.Min >= 1 && program.Nodes[n.X].Kind == PosixNodeKind.Class)
        {
            var max = n.Max < 0 ? int.MaxValue : n.Max;
            step = new PosixSeqStep(program.Sets[program.Nodes[n.X].Value], n.Min, max, captureGroup);
            return true;
        }
        return false;
    }

    internal bool TryExecuteFastPath(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        status = RegexSearchStatus.NoMatch;
        if (_fastKind == PosixFastKind.None)
        {
            return false;
        }

        return _fastKind switch
        {
            PosixFastKind.Literal => ExecuteLiteral(input, startAt, operation, ref state, groups, out match, out status),
            PosixFastKind.LiteralAlternation => ExecuteLiteralAlternation(input, startAt, operation, ref state, groups, out match, out status),
            PosixFastKind.SingleSetRepeat => ExecuteSingleSetRepeat(input, startAt, operation, ref state, groups, out match, out status),
            PosixFastKind.SetRepeatSuffixLiteral => ExecuteSetRepeatSuffixLiteral(input, startAt, operation, ref state, groups, out match, out status),
            PosixFastKind.AtomicSequence => ExecuteAtomicSequence(input, startAt, operation, ref state, groups, out match, out status),
            _ => false
        };
    }

    private bool ExecuteLiteral(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var lit = _literalBytes.AsSpan();
        if (IsStartAnchored)
        {
            if (startAt > 0 || input.Length < lit.Length || (IsEndAnchored && input.Length != lit.Length))
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            state.TryCharge(lit.Length + 2, 0);
            if (!input.Slice(0, lit.Length).SequenceEqual(lit))
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, lit.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (IsEndAnchored)
        {
            var pos = input.Length - lit.Length;
            if (pos < startAt)
            {
                state.TryCharge(input.Length - startAt + 1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            state.TryCharge(lit.Length + 2, pos);
            if (!input.Slice(pos, lit.Length).SequenceEqual(lit))
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(pos, lit.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        var idx = input.Slice(startAt).IndexOf(lit);
        if (idx < 0)
        {
            state.TryCharge(input.Length - startAt + 1, startAt);
            status = RegexSearchStatus.NoMatch;
            return true;
        }
        var matchStart = startAt + idx;
        state.TryCharge(idx + lit.Length + 2, startAt);
        PublishGroup0(matchStart, lit.Length, operation, groups, out match);
        status = RegexSearchStatus.Match;
        return true;
    }

    private bool ExecuteLiteralAlternation(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var alts = _altBytes!;
        var sv = FirstByteSv!;
        var pos = startAt;
        while (pos < input.Length)
        {
            var idx = input.Slice(pos).IndexOfAny(sv);
            if (idx < 0)
            {
                break;
            }

            pos += idx;
            var rem = input.Slice(pos);
            var bestLen = -1;
            foreach (var alt in alts)
            {
                if (alt.Length > bestLen && rem.StartsWith(alt))
                {
                    bestLen = alt.Length;
                }
            }
            if (bestLen >= 0)
            {
                state.TryCharge(pos - startAt + bestLen + 2, startAt);
                PublishGroup0(pos, bestLen, operation, groups, out match);
                status = RegexSearchStatus.Match;
                return true;
            }
            pos++;
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteSingleSetRepeat(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        status = RegexSearchStatus.NoMatch;
        var sv = _repeatSv!;
        if (IsStartAnchored && IsEndAnchored)
        {
            if (startAt > 0 || input.Length < _repeatMin || input.Length > _repeatMax)
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            var badIdx = input.IndexOfAnyExcept(sv);
            state.TryCharge(badIdx >= 0 ? badIdx + 1 : input.Length + 2, 0);
            if (badIdx >= 0)
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, input.Length, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (IsStartAnchored)
        {
            if (startAt > 0 || input.Length < _repeatMin)
            {
                state.TryCharge(1, startAt);
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            var maxScan = Math.Min(input.Length, _repeatMax);
            var badIdx = input.Slice(0, maxScan).IndexOfAnyExcept(sv);
            var runLen = badIdx < 0 ? maxScan : badIdx;
            state.TryCharge(runLen + 2, 0);
            if (runLen < _repeatMin)
            {
                status = RegexSearchStatus.NoMatch;
                return true;
            }
            PublishGroup0(0, runLen, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }

        if (IsEndAnchored)
        {
            return false;
        }

        var pos = startAt;
        while (pos <= input.Length - _repeatMin)
        {
            var rel = input.Slice(pos).IndexOfAny(sv);
            if (rel < 0)
            {
                break;
            }

            var matchStart = pos + rel;
            if (matchStart > input.Length - _repeatMin)
            {
                break;
            }

            var maxRem = Math.Min(input.Length - matchStart, _repeatMax);
            var badRel = input.Slice(matchStart, maxRem).IndexOfAnyExcept(sv);
            var runLen = badRel < 0 ? maxRem : badRel;
            if (runLen >= _repeatMin)
            {
                state.TryCharge(matchStart - startAt + runLen + 2, startAt);
                PublishGroup0(matchStart, runLen, operation, groups, out match);
                status = RegexSearchStatus.Match;
                return true;
            }
            pos = matchStart + Math.Max(1, runLen);
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteSetRepeatSuffixLiteral(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var set = _repeatSet;
        var lit = _suffixBytes.AsSpan();
        var searchPos = startAt + 1;
        while (searchPos <= input.Length - lit.Length)
        {
            var idx = input.Slice(searchPos).IndexOf(lit);
            if (idx < 0)
            {
                break;
            }

            var litPos = searchPos + idx;
            if (!set.Contains(input[litPos - 1]))
            {
                searchPos = litPos + 1;
                continue;
            }
            var matchStart = litPos - 1;
            while (matchStart > startAt && set.Contains(input[matchStart - 1]))
            {
                matchStart--;
            }

            var matchEnd = litPos + lit.Length;
            if (_suffixAllInSet)
            {
                var nextSearch = litPos + 1;
                while (nextSearch <= input.Length - lit.Length)
                {
                    var nextIdx = input.Slice(nextSearch).IndexOf(lit);
                    if (nextIdx < 0)
                    {
                        break;
                    }

                    var nextLitPos = nextSearch + nextIdx;
                    var connected = true;
                    for (var k = matchEnd; k < nextLitPos; k++)
                    {
                        if (!set.Contains(input[k]))
                        { connected = false; break; }
                    }
                    if (!connected)
                    {
                        break;
                    }

                    matchEnd = nextLitPos + lit.Length;
                    nextSearch = nextLitPos + 1;
                }
            }
            state.TryCharge(matchEnd - startAt + 2, startAt);
            PublishGroup0(matchStart, matchEnd - matchStart, operation, groups, out match);
            status = RegexSearchStatus.Match;
            return true;
        }
        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private bool ExecuteAtomicSequence(
        ReadOnlySpan<byte> input, int startAt, SearchOperation operation,
        ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match, out RegexSearchStatus status)
    {
        match = default;
        var steps = _seqSteps!;
        var anchorLit = steps[_seqAnchorIndex].LiteralBytes.AsSpan();
        var saveCaptures = operation == SearchOperation.Captures;

        if (_seqPrefixFixedLen >= 0)
        {
            var searchPos = startAt + _seqPrefixFixedLen;
            while (searchPos <= input.Length - anchorLit.Length)
            {
                var idx = input.Slice(searchPos).IndexOf(anchorLit);
                if (idx < 0)
                {
                    break;
                }

                var anchorPos = searchPos + idx;
                var matchStart = anchorPos - _seqPrefixFixedLen;

                var cur = matchStart;
                var ok = true;
                for (var s = 0; s < _seqAnchorIndex; s++)
                {
                    ref readonly var step = ref steps[s];
                    var stepStart = cur;
                    if (step.IsLiteral)
                    {
                        if (!input.Slice(cur, step.Min).SequenceEqual(step.LiteralBytes.AsSpan()))
                        { ok = false; break; }
                        cur += step.Min;
                    }
                    else
                    {
                        var set = step.Set;
                        for (var k = 0; k < step.Min; k++)
                        {
                            if (!set.Contains(input[cur + k]))
                            { ok = false; break; }
                        }
                        if (!ok)
                        {
                            break;
                        }

                        cur += step.Min;
                    }
                    if (saveCaptures && step.CaptureGroup > 0)
                    {
                        groups[step.CaptureGroup] = new(true, stepStart, cur - stepStart);
                    }
                }
                if (!ok)
                {
                    searchPos = anchorPos + 1;
                    continue;
                }

                if (saveCaptures && steps[_seqAnchorIndex].CaptureGroup > 0)
                {
                    groups[steps[_seqAnchorIndex].CaptureGroup] = new(true, anchorPos, anchorLit.Length);
                }

                cur = anchorPos + anchorLit.Length;

                if (VerifySuffixSteps(input, steps, _seqAnchorIndex + 1, ref cur, saveCaptures, groups))
                {
                    state.TryCharge(cur - startAt + 2, startAt);
                    match = new(matchStart, cur - matchStart);
                    if (saveCaptures)
                    {
                        groups[0] = new(true, matchStart, cur - matchStart);
                    }

                    status = RegexSearchStatus.Match;
                    return true;
                }
                searchPos = anchorPos + 1;
            }
        }
        else
        {
            var set0 = steps[0].Set;
            var searchPos = startAt + 1;
            while (searchPos <= input.Length - anchorLit.Length)
            {
                var idx = input.Slice(searchPos).IndexOf(anchorLit);
                if (idx < 0)
                {
                    break;
                }

                var anchorPos = searchPos + idx;
                if (!set0.Contains(input[anchorPos - 1]))
                {
                    searchPos = anchorPos + 1;
                    continue;
                }
                var matchStart = anchorPos - 1;
                while (matchStart > startAt && set0.Contains(input[matchStart - 1]))
                {
                    matchStart--;
                }

                if (saveCaptures)
                {
                    if (steps[0].CaptureGroup > 0)
                    {
                        groups[steps[0].CaptureGroup] = new(true, matchStart, anchorPos - matchStart);
                    }

                    if (steps[1].CaptureGroup > 0)
                    {
                        groups[steps[1].CaptureGroup] = new(true, anchorPos, anchorLit.Length);
                    }
                }
                var cur = anchorPos + anchorLit.Length;
                if (VerifySuffixSteps(input, steps, 2, ref cur, saveCaptures, groups))
                {
                    state.TryCharge(cur - startAt + 2, startAt);
                    match = new(matchStart, cur - matchStart);
                    if (saveCaptures)
                    {
                        groups[0] = new(true, matchStart, cur - matchStart);
                    }

                    status = RegexSearchStatus.Match;
                    return true;
                }
                searchPos = anchorPos + 1;
            }
        }

        if (saveCaptures)
        {
            groups[.._groupCount].Clear();
        }

        state.TryCharge(input.Length - startAt + 1, startAt);
        status = RegexSearchStatus.NoMatch;
        return true;
    }

    private static bool VerifySuffixSteps(
        ReadOnlySpan<byte> input, PosixSeqStep[] steps, int startStep,
        ref int cur, bool saveCaptures, Span<RegexCapture> groups)
    {
        for (var s = startStep; s < steps.Length; s++)
        {
            ref readonly var step = ref steps[s];
            var stepStart = cur;
            if (step.IsLiteral)
            {
                var lit = step.LiteralBytes.AsSpan();
                if (cur + lit.Length > input.Length || !input.Slice(cur, lit.Length).SequenceEqual(lit))
                {
                    return false;
                }

                cur += lit.Length;
            }
            else if (step.Min == step.Max)
            {
                if (cur + step.Min > input.Length)
                {
                    return false;
                }

                var set = step.Set;
                for (var k = 0; k < step.Min; k++)
                {
                    if (!set.Contains(input[cur + k]))
                    {
                        return false;
                    }
                }
                cur += step.Min;
            }
            else
            {
                var set = step.Set;
                var count = 0;
                while (cur < input.Length && set.Contains(input[cur]))
                {
                    cur++;
                    count++;
                }
                if (count < step.Min)
                {
                    return false;
                }
            }
            if (saveCaptures && step.CaptureGroup > 0)
            {
                groups[step.CaptureGroup] = new(true, stepStart, cur - stepStart);
            }
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void PublishGroup0(int index, int length, SearchOperation operation, Span<RegexCapture> groups, out RegexMatchRange match)
    {
        match = new(index, length);
        if (operation == SearchOperation.Captures)
        {
            groups[0] = new(true, index, length);
            for (var g = 1; g < _groupCount; g++)
            {
                groups[g] = default;
            }
        }
    }
}
