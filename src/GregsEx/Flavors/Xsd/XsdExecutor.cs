#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.IR;

namespace GregsEx.Flavors.Xsd;

internal enum XsdOp { Accept, Literal, Class, Split }
internal readonly record struct XsdInstruction(XsdOp Op, int Value, int X, int Y);

internal sealed class XsdExecutorFactory : IXmlExecutorFactory
{
    private struct Frame(int node, int target)
    {
        internal int Node = node, Target = target, Stage, Right, Remaining, Next, Split;
    }
    public bool TryCreate(XmlProgram program, IXmlUnicodeData unicode, ref CompileBudget budget,
        out IXmlExecutor? executor, out RegexDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(unicode);
        executor = null;
        diagnostic = default;
        var source = new SourceRange(0, program.PatternLength, RegexOffsetUnit.Utf16CodeUnits);
        if (program.Dialect != XmlDialect.Xsd || unicode.DataId != program.Profile.DataId)
        { diagnostic = new(RegexDiagnosticCode.InvalidProgram, 0, program.PatternLength, source.Unit); return false; }
        var nodes = program.Nodes;
        // One expansion-size slot and one lowering frame per validated node.
        var scratch = 96L + 48L * nodes.Length;
        if (!budget.TryReserveBytes(scratch, source, out diagnostic))
        {
            return false;
        }

        try
        {
            var sizes = new long[nodes.Length];
            var frames = new Frame[nodes.Length];
            for (var i = 0; i < nodes.Length; i++)
            {
                var n = nodes[i];
                if (!budget.TryCharge(1, n.Source, out diagnostic))
                {
                    return false;
                }
                // Saturation rejects resource excess before multiplication overflow.
                long cap = budget.Limits.MaxInstructions;
                var size = n.Kind switch
                {
                    XmlNodeKind.Empty => 0,
                    XmlNodeKind.Literal or XmlNodeKind.Class => 1,
                    XmlNodeKind.Sequence => sizes[n.X] + sizes[n.Y],
                    XmlNodeKind.Alternative => sizes[n.X] + sizes[n.Y] + 1,
                    XmlNodeKind.Repeat => n.Max == -1 ?
                        Product(sizes[n.X], (long)n.Min + 1, cap) + 1 :
                        Product(sizes[n.X], n.Max, cap) + (long)n.Max - n.Min,
                    _ => cap
                };
                if (size >= cap)
                { diagnostic = new(RegexDiagnosticCode.ProgramLimitExceeded, n.Source.Offset, n.Source.Length, source.Unit); return false; }
                sizes[i] = size;
            }
            var count = checked((int)sizes[program.Root] + 1);
            var bytes = 96L + 16L * count;
            if (bytes > budget.Limits.MaxPlanBytes - (budget.ReservedBytes - scratch))
            { diagnostic = new(RegexDiagnosticCode.PlanLimitExceeded, 0, program.PatternLength, source.Unit); return false; }
            if (!budget.TryReserveBytes(bytes, source, out diagnostic))
            {
                return false;
            }

            var published = false;
            try
            {
                var code = new XsdInstruction[count];
                code[0] = new(XsdOp.Accept, 0, -1, -1);
                int used = 1, top = 1, result = 0;
                frames[0] = new(program.Root, 0);
                while (top > 0)
                {
                    ref var f = ref frames[top - 1];
                    var n = nodes[f.Node];
                    if (!budget.TryCharge(1, n.Source, out diagnostic))
                    {
                        return false;
                    }

                    switch (n.Kind)
                    {
                        case XmlNodeKind.Empty:
                            result = f.Target;
                            top--;
                            break;
                        case XmlNodeKind.Literal:
                        case XmlNodeKind.Class:
                            result = used;
                            code[used++] = new(n.Kind == XmlNodeKind.Literal ? XsdOp.Literal : XsdOp.Class, n.Value, f.Target, -1);
                            top--;
                            break;
                        case XmlNodeKind.Sequence:
                            if (f.Stage == 0)
                            { f.Stage = 1; frames[top++] = new(n.Y, f.Target); }
                            else if (f.Stage == 1)
                            { f.Stage = 2; frames[top++] = new(n.X, result); }
                            else
                            {
                                top--;
                            }

                            break;
                        case XmlNodeKind.Alternative:
                            if (f.Stage == 0)
                            { f.Stage = 1; frames[top++] = new(n.Y, f.Target); }
                            else if (f.Stage == 1)
                            { f.Stage = 2; f.Right = result; frames[top++] = new(n.X, f.Target); }
                            else
                            { code[used] = new(XsdOp.Split, 0, result, f.Right); result = used++; top--; }
                            break;
                        case XmlNodeKind.Repeat:
                            if (f.Stage == 0)
                            {
                                f.Next = f.Target;
                                f.Remaining = n.Max == -1 ? n.Min : n.Max;
                                if (n.Max == -1)
                                {
                                    f.Split = used;
                                    code[used++] = new(XsdOp.Split, 0, -1, f.Target);
                                    f.Stage = 1;
                                    frames[top++] = new(n.X, f.Split);
                                    break;
                                }
                                f.Stage = 2;
                            }
                            if (f.Stage == 1)
                            { code[f.Split] = code[f.Split] with { X = result }; f.Next = f.Split; f.Stage = 2; }
                            else if (f.Stage == 3)
                            {
                                if (n.Max != -1 && f.Remaining > n.Min)
                                { code[used] = new(XsdOp.Split, 0, result, f.Next); f.Next = used++; }
                                else
                                {
                                    f.Next = result;
                                }

                                f.Remaining--;
                                f.Stage = 2;
                            }
                            if (f.Remaining == 0)
                            { result = f.Next; top--; }
                            else
                            { f.Stage = 3; frames[top++] = new(n.X, f.Next); }
                            break;
                    }
                }
                if (used != code.Length)
                {
                    throw new InvalidOperationException("XSD lowering size mismatch.");
                }

                executor = new XsdExecutor(program, code, result, bytes);
                published = true;
                return true;
            }
            finally
            {
                if (!published)
                {
                    budget.ReleaseBytes(bytes);
                }
            }
        }
        finally { budget.ReleaseBytes(scratch); }
    }
    private static long Product(long size, long count, long cap) => count != 0 && size > cap / count ? cap : size * count;
}

// Whole-language NFA: alternatives/quantifier priority cannot affect XSD's
// boolean membership or group0. All epsilon/state/class comparisons are charged.
// No mutable fields, input retention, recursion, framework regex or runtime codegen.
internal sealed class XsdExecutor(XmlProgram program, XsdInstruction[] code, int entry, long bytes) : IXmlExecutor
{
    public XmlProgram Program { get; } = program;
    internal long RetainedBytes { get; } = bytes;
    internal int InstructionCount => code.Length;
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation, int frameCapacity)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameCapacity);
        return new(checked(2 * code.Length), 0, checked(code.Length + frameCapacity));
    }
    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, int frameCapacity, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, IXmlTextReader<TUnit>
    {
        match = default;
        if (startAt != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startAt), "XSD membership starts at zero.");
        }

        if (!(typeof(TUnit) == typeof(char) && Program.InputKind == RegexInputKind.Utf16 ||
            typeof(TUnit) == typeof(byte) && Program.InputKind == RegexInputKind.Utf8))
        {
            throw new ArgumentException("Subject storage differs from the XSD plan.", nameof(input));
        }

        var required = GetRequiredCapacity(operation, frameCapacity);
        if (operation == SearchOperation.Captures && groups.Length < 1)
        {
            return Fail(ref state, RegexExecutionFailureCode.DestinationTooSmall, 0, 1);
        }

        if (operation == SearchOperation.Captures)
        {
            groups[0] = default;
        }

        if (!state.Buffers.Capacity.Contains(required))
        {
            return Fail(ref state, RegexExecutionFailureCode.WorkspaceCapacityExceeded, 0);
        }

        var status = default(TReader).Validate(input, 0, ref state);
        if (status != RegexSearchStatus.NoMatch)
        {
            return status;
        }

        var current = state.Buffers.States[..code.Length];
        var next = state.Buffers.States.Slice(code.Length, code.Length);
        var seen = state.Buffers.Scratch[..code.Length];
        var stack = state.Buffers.Scratch.Slice(code.Length, frameCapacity);
        if (!state.TryCharge(seen.Length, 0))
        {
            return Failure(ref state);
        }

        seen.Clear();
        var currentCount = 0;
        if (!Closure(entry, current, ref currentCount, seen, stack, 0, ref state))
        {
            return Failure(ref state);
        }

        var at = 0;
        while (at < input.Length)
        {
            if (!state.TryCharge(1, at))
            {
                return Failure(ref state);
            }

            if (!default(TReader).TryRead(input, at, out var scalar, out var width))
            {
                return Fail(ref state, RegexExecutionFailureCode.InvalidEncoding, at);
            }

            if (!state.TryCharge(seen.Length, at))
            {
                return Failure(ref state);
            }

            seen.Clear();
            var nextCount = 0;
            for (var i = 0; i < currentCount; i++)
            {
                if (!state.TryCharge(1, at))
                {
                    return Failure(ref state);
                }

                var instruction = code[current[i]];
                var accepts = instruction.Op == XsdOp.Literal && instruction.Value == scalar;
                if (instruction.Op == XsdOp.Class)
                {
                    var slice = Program.Sets[instruction.Value];
                    int lo = 0, hi = slice.Count - 1;
                    while (lo <= hi)
                    {
                        if (!state.TryCharge(1, at))
                        {
                            return Failure(ref state);
                        }

                        var mid = lo + ((hi - lo) >> 1);
                        var range = Program.Intervals[slice.Offset + mid];
                        if (scalar < range.First)
                        {
                            hi = mid - 1;
                        }
                        else if (scalar > range.Last)
                        {
                            lo = mid + 1;
                        }
                        else
                        { accepts = true; break; }
                    }
                }
                if (accepts && !Closure(instruction.X, next, ref nextCount, seen, stack, at + width, ref state))
                {
                    return Failure(ref state);
                }
            }
            at += width;
            var swap = current;
            current = next;
            next = swap;
            currentCount = nextCount;
            if (currentCount == 0)
            {
                return RegexSearchStatus.NoMatch;
            }
        }
        for (var i = 0; i < currentCount; i++)
        {
            if (!state.TryCharge(1, at))
            {
                return Failure(ref state);
            }

            if (code[current[i]].Op != XsdOp.Accept)
            {
                continue;
            }

            if (operation == SearchOperation.Captures)
            {
                if (!state.TryCharge(1, at))
                {
                    return Failure(ref state);
                }

                groups[0] = new(true, 0, input.Length);
            }
            match = new(0, input.Length);
            return RegexSearchStatus.Match;
        }
        return RegexSearchStatus.NoMatch;
    }
    private bool Closure(int seed, Span<int> output, ref int count, Span<int> seen, Span<int> stack, int offset, ref RunState state)
    {
        if (seen[seed] != 0)
        {
            return state.TryCharge(1, offset);
        }

        var top = 1;
        stack[0] = seed;
        seen[seed] = 1;
        while (top > 0)
        {
            if (!state.TryCharge(1, offset))
            {
                return false;
            }

            var id = stack[--top];
            while (true)
            {
                var instruction = code[id];
                if (!state.TryCharge(1, offset))
                {
                    return false;
                }

                if (instruction.Op != XsdOp.Split)
                { output[count++] = id; break; }
                var right = instruction.Y;
                if (seen[right] == 0)
                {
                    if (top == stack.Length)
                    { Fail(ref state, RegexExecutionFailureCode.WorkspaceCapacityExceeded, offset); return false; }
                    seen[right] = 1;
                    stack[top++] = right;
                }
                var left = instruction.X;
                if (seen[left] != 0)
                {
                    break;
                }

                seen[left] = 1;
                id = left;
            }
        }
        return true;
    }
    private static RegexSearchStatus Fail(ref RunState state, RegexExecutionFailureCode code, int offset, long required = 0)
    {
        state.Failure = new(code, offset, state.Budget.WorkConsumed, required);
        return code == RegexExecutionFailureCode.DestinationTooSmall ? RegexSearchStatus.DestinationTooSmall :
            code == RegexExecutionFailureCode.InvalidEncoding ? RegexSearchStatus.InvalidInput : RegexSearchStatus.LimitExceeded;
    }
    private static RegexSearchStatus Failure(ref RunState state) => state.Failure.Code switch
    {
        RegexExecutionFailureCode.Cancelled => RegexSearchStatus.Cancelled,
        RegexExecutionFailureCode.TimedOut => RegexSearchStatus.TimedOut,
        _ => RegexSearchStatus.LimitExceeded
    };
}
