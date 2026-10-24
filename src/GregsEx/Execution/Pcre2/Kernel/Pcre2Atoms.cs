#nullable enable
using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Kernel;

internal readonly partial struct Pcre2Kernel
{
    private const int Accepted = 4;
    private static Pcre2Step Atom<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, Pcre2Node node, Pcre2Storage storage,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        bool matches;
        var width = 0;
        // Invalid-UTF fragments change end_subject, not true_end_subject.
        // Scan-substring windows change both (pcre2_match.c5805–5811).
        var anchorEnd = input.Length;
        if (node.Kind is Pcre2NodeKind.AbsoluteEnd or Pcre2NodeKind.BeforeFinalNewline or Pcre2NodeKind.End)
        {
            anchorEnd = TrueEnd(program, input.Length, ref machine, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
            {
                return Resource();
            }
        }
        switch (node.Kind)
        {
            case Pcre2NodeKind.Empty:
                matches = true;
                break;
            case Pcre2NodeKind.AbsoluteStart:
                matches = machine.Position == 0;
                break;
            case Pcre2NodeKind.StartOffset:
                matches = machine.Position == machine.SearchStart;
                break;
            case Pcre2NodeKind.AbsoluteEnd:
                matches = machine.Position == anchorEnd;
                if (matches && Partial(flags, ref machine, ref state, true))
                {
                    return new(Pcre2StepKind.Partial);
                }

                break;
            case Pcre2NodeKind.Start:
                matches = machine.Position == 0 ? (flags & Pcre2MatchFlags.Notbol) == 0 :
                    (node.Flags & Pcre2CompileFlags.Multiline) != 0 &&
                    (machine.Position < machine.VisibleEnd || (node.Flags & Pcre2CompileFlags.AltCircumflex) != 0) &&
                    AfterNewline<TUnit, TReader>(input, program.Profile, ref machine, ref state);
                break;
            case Pcre2NodeKind.End:
            case Pcre2NodeKind.BeforeFinalNewline:
                var multiline = node.Kind == Pcre2NodeKind.End && (node.Flags & Pcre2CompileFlags.Multiline) != 0;
                var newline = NewlineAt<TUnit, TReader>(input, machine.Position, machine.VisibleEnd, program.Profile, ref state);
                matches = multiline ? machine.Position == machine.VisibleEnd ? (flags & Pcre2MatchFlags.Noteol) == 0 : newline > 0 :
                    (node.Kind != Pcre2NodeKind.End || (flags & Pcre2MatchFlags.Noteol) == 0) &&
                    (machine.Position == anchorEnd || newline > 0 && machine.Position + newline == anchorEnd &&
                        (node.Kind != Pcre2NodeKind.End || (node.Flags & Pcre2CompileFlags.DollarEndonly) == 0));
                if (matches && (!multiline || machine.Position == machine.VisibleEnd) &&
                    Partial(flags, ref machine, ref state, !multiline))
                {
                    return new(Pcre2StepKind.Partial);
                }

                if (!matches && program.Profile.Newline == Pcre2Newline.Crlf && machine.Position == machine.VisibleEnd - 1 &&
                    default(TReader).Read(input, machine.Position, machine.VisibleEnd, true, program.Profile, ref state).Value == 13 &&
                    Partial(flags, ref machine, ref state, true))
                {
                    return new(Pcre2StepKind.Partial);
                }

                break;
            case Pcre2NodeKind.WordBoundary:
            case Pcre2NodeKind.NotWordBoundary:
                bool previous = false, current = false;
                if (machine.Position > ScanStart(ref state))
                {
                    var before = default(TReader).ReadPrevious(input, machine.Position, program.Profile, ref state);
                    if (before.Status == Pcre2ReadStatus.Value && machine.Position - before.Width >= ScanStart(ref state))
                    {
                        if (!RecordEarliestRead(machine.Position - before.Width, ref machine, ref state))
                        {
                            return Resource();
                        }

                        previous = Pcre2CharacterOps.Word(before.Value, node.Flags, node.ExtraFlags, data);
                    }
                }
                var after = default(TReader).Read(input, machine.Position, machine.VisibleEnd, false, program.Profile, ref state);
                if (after.Status == Pcre2ReadStatus.Value)
                {
                    if (!UpdateLastUsedPosition(machine.Position + after.Width, ref machine, ref state))
                    {
                        return Resource();
                    }

                    current = Pcre2CharacterOps.Word(after.Value, node.Flags, node.ExtraFlags, data);
                }
                if (after.Status == Pcre2ReadStatus.End && Partial(flags, ref machine, ref state))
                {
                    return new(Pcre2StepKind.Partial);
                }

                matches = (previous != current) == (node.Kind == Pcre2NodeKind.WordBoundary);
                break;
            case Pcre2NodeKind.Literal:
            case Pcre2NodeKind.Class:
            case Pcre2NodeKind.Dot:
            case Pcre2NodeKind.CodeUnit:
            case Pcre2NodeKind.Newline:
                var previousFailure = state.Failure;
                var interiorRead = false;
                var read = default(TReader).Read(input, machine.Position, machine.VisibleEnd,
                    node.Kind == Pcre2NodeKind.CodeUnit || Pcre2CharacterOps.LiteralUsesUnit(node, data), program.Profile, ref state);
                if (program.HasBackslashC && program.Profile.Utf && read.Status == Pcre2ReadStatus.InvalidBarrier &&
                    previousFailure.Code == RegexExecutionFailureCode.None && state.Failure.Code == RegexExecutionFailureCode.InvalidEncoding)
                {
                    // A preceding \C can leave the interpreter inside a valid
                    // scalar. GETCHAR treats an interior unit as its own value;
                    // code-unit admission still proves the original scalar valid.
                    state.Failure = previousFailure;
                    read = default(TReader).Read(input, machine.Position, machine.VisibleEnd, true, program.Profile, ref state);
                    interiorRead = read.Status == Pcre2ReadStatus.Value;
                    if (read.Status == Pcre2ReadStatus.Value && node.Kind == Pcre2NodeKind.Dot &&
                        program.Profile.Width == Pcre2CodeUnitWidth.Eight)
                    {
                        var units = 1;
                        while (machine.Position + units < machine.VisibleEnd)
                        {
                            var nextUnit = default(TReader).Read(input, machine.Position + units, machine.VisibleEnd, true, program.Profile, ref state);
                            if (nextUnit.Status != Pcre2ReadStatus.Value || (nextUnit.Value & 0xC0) != 0x80)
                            {
                                break;
                            }

                            units++;
                        }
                        read = read with { Width = units };
                    }
                }
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return Resource();
                }

                if (read.Status == Pcre2ReadStatus.End)
                {
                    if (Partial(flags, ref machine, ref state))
                    {
                        return new(Pcre2StepKind.Partial);
                    }

                    Fail(ref state);
                    return Continued(machine.Node);
                }
                if (read.Status != Pcre2ReadStatus.Value)
                { Fail(ref state); return Continued(machine.Node); }
                width = read.Width;
                // A terminal CR may become CRLF when more subject arrives.
                // OP_ANY and OP_ANYNL record that ambiguity even before consuming it.
                if (read.Value == 13 && machine.Position + width == machine.VisibleEnd &&
                    (node.Kind == Pcre2NodeKind.Newline || node.Kind == Pcre2NodeKind.Dot &&
                        (node.Flags & Pcre2CompileFlags.Dotall) == 0 && program.Profile.Newline == Pcre2Newline.Crlf) &&
                    Partial(flags, ref machine, ref state, true))
                {
                    return new(Pcre2StepKind.Partial);
                }

                matches = node.Kind switch
                {
                    Pcre2NodeKind.Literal => Pcre2CharacterOps.Equal(node.Operand, read.Value, node.Flags, node.ExtraFlags, data,
                        ref machine, ref state),
                    Pcre2NodeKind.Class => Pcre2CharacterOps.Class(program, node, read.Value, data, storage.UnicodeScratch,
                        ref machine, ref state),
                    Pcre2NodeKind.Dot => interiorRead || (node.Flags & Pcre2CompileFlags.Dotall) != 0 ||
                        NewlineAt<TUnit, TReader>(input, machine.Position, machine.VisibleEnd, program.Profile, ref state) == 0,
                    Pcre2NodeKind.CodeUnit => true,
                    _ => RNewline(read.Value, program.Profile.Bsr)
                };
                if (matches && node.Kind == Pcre2NodeKind.Newline && read.Value == 13 && machine.Position + width < machine.VisibleEnd)
                {
                    var next = default(TReader).Read(input, machine.Position + width, machine.VisibleEnd, false, program.Profile, ref state);
                    if (next.Status == Pcre2ReadStatus.Value && next.Value == 10)
                    {
                        width += next.Width;
                    }
                }
                break;
            default:
                throw new InvalidOperationException("Unknown regular opcode.");
        }
        if (state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0)
        {
            return Resource();
        }

        if (!matches)
        { Fail(ref state); return Continued(machine.Node); }
        machine.Position += width;
        CompleteNode(machine.Node, ref machine, ref state);
        return Continued(machine.Node);
    }

    internal static bool RecordEarliestRead(int position, ref Pcre2VmState machine, ref RunState state)
    {
        if (position < ScanStart(ref state) || position > machine.SubjectLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        state.Buffers.States[EarliestRead] = Math.Min(state.Buffers.States[EarliestRead], position);
        return true;
    }
    // A true return requests immediate hard-partial handback. False also covers
    // soft remembering or sticky denial; callers inspect the common failure first.
    internal static bool RememberPartial(Pcre2Program program, Pcre2MatchFlags flags,
        ref Pcre2VmState machine, ref RunState state, bool force = false)
    {
        flags = EffectiveContextFlags(program, flags, ref machine, ref state);
        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return false;
        }

        return Partial(flags, ref machine, ref state, force);
    }
    private static bool Partial(Pcre2MatchFlags flags, ref Pcre2VmState machine, ref RunState state, bool force = false)
    {
        if ((flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) == 0)
        {
            return false;
        }

        if (!force && machine.Position <= state.Buffers.States[EarliestRead] && state.Buffers.States[EmptyPartial] == 0)
        {
            return false;
        }

        if (!machine.HasPartial)
        { machine.HasPartial = true; machine.PartialStart = machine.CandidateStart; machine.PartialEnd = machine.VisibleEnd; }
        if ((flags & Pcre2MatchFlags.PartialHard) == 0)
        {
            return false;
        }

        machine.SourceReturnCode = (int)Pcre2ErrorCode.Partial;
        return true;
    }
    private static bool RNewline(int cp, Pcre2Bsr bsr) => cp is 10 or 13 ||
        bsr == Pcre2Bsr.Unicode && cp is 11 or 12 or 0x85 or 0x2028 or 0x2029;
    private static int NewlineAt<TUnit, TReader>(ReadOnlySpan<TUnit> input, int position, int end,
        Pcre2Profile profile, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var read = default(TReader).Read(input, position, end, false, profile, ref state);
        if (state.Failure.Code != RegexExecutionFailureCode.None || read.Status != Pcre2ReadStatus.Value)
        {
            return 0;
        }

        if (read.Value == 13 && profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Any or Pcre2Newline.Anycrlf)
        {
            var next = default(TReader).Read(input, position + read.Width, end, false, profile, ref state);
            if (next.Status == Pcre2ReadStatus.Value && next.Value == 10)
            {
                return read.Width + next.Width;
            }

            return profile.Newline == Pcre2Newline.Crlf ? 0 : read.Width;
        }
        return profile.Newline switch
        {
            Pcre2Newline.Cr => read.Value == 13 ? read.Width : 0,
            Pcre2Newline.Lf => read.Value == 10 ? read.Width : 0,
            Pcre2Newline.Nul => read.Value == 0 ? read.Width : 0,
            Pcre2Newline.Anycrlf => read.Value is 10 or 13 ? read.Width : 0,
            Pcre2Newline.Any => RNewline(read.Value, Pcre2Bsr.Unicode) ? read.Width : 0,
            _ => 0
        };
    }
    private static bool AfterNewline<TUnit, TReader>(ReadOnlySpan<TUnit> input, Pcre2Profile profile,
        ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var floor = ScanStart(ref state);
        if (machine.Position <= floor)
        {
            return false;
        }

        var before = default(TReader).ReadPrevious(input, machine.Position, profile, ref state);
        if (before.Status != Pcre2ReadStatus.Value || machine.Position - before.Width < floor)
        {
            return false;
        }

        var begin = machine.Position - before.Width;
        if (before.Value == 10 && profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Any or Pcre2Newline.Anycrlf && begin > floor)
        {
            var earlier = default(TReader).ReadPrevious(input, begin, profile, ref state);
            if (earlier.Status == Pcre2ReadStatus.Value && earlier.Value == 13 && begin - earlier.Width >= floor)
            {
                begin -= earlier.Width;
            }
        }
        return NewlineAt<TUnit, TReader>(input, begin, machine.VisibleEnd, profile, ref state) == machine.Position - begin;
    }

    public static Pcre2MatchResult Publish(Pcre2Program program, ref Pcre2VmState machine,
        ref RunState state, Span<Pcre2OffsetPair> pairs)
    {
        if (!state.TryCharge(0, machine.Position))
        {
            return Failure(ref machine, ref state);
        }

        if (machine.SourceReturnCode != 0 && machine.SourceReturnCode != (int)Pcre2ErrorCode.Partial)
        {
            return Failure(ref machine, ref state);
        }

        if (state.Buffers.States[Accepted] == 0)
        {
            if (machine.HasPartial)
            {
                var source = machine.SourceReturnCode;
                machine.SourceReturnCode = 0;
                if (!Pcre2Storage.Charge(ref machine, ref state, pairs.IsEmpty ? 0 : 1, machine.Position))
                {
                    return Failure(ref machine, ref state);
                }

                machine.SourceReturnCode = source;
                if (!pairs.IsEmpty)
                {
                    pairs[0] = new(true, machine.PartialStart, machine.PartialEnd);
                }

                return new(Pcre2MatchStatus.Partial, (int)Pcre2ErrorCode.Partial, machine.PartialStart, machine.MarkIndex);
            }
            return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, machine.CandidateStart, machine.MarkIndex);
        }
        var count = Math.Min(program.GroupCount, pairs.Length);
        if (!Pcre2Storage.Charge(ref machine, ref state, count, machine.Position))
        {
            return Failure(ref machine, ref state);
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount);
        for (var group = 0; group < count; group++)
        {
            pairs[group] = storage.Capture(group);
        }

        return new(Pcre2MatchStatus.Match, pairs.Length < machine.CaptureTop ? 0 : machine.CaptureTop,
            machine.CandidateStart, machine.MarkIndex);
    }
    internal static Pcre2MatchResult Failure(ref Pcre2VmState machine, ref RunState state)
    {
        var failure = state.Failure;
        var status = failure.Code switch
        {
            RegexExecutionFailureCode.Cancelled => Pcre2MatchStatus.Cancelled,
            RegexExecutionFailureCode.TimedOut => Pcre2MatchStatus.TimedOut,
            RegexExecutionFailureCode.None => machine.SourceReturnCode is (int)Pcre2ErrorCode.Matchlimit or
                (int)Pcre2ErrorCode.Depthlimit or (int)Pcre2ErrorCode.Heaplimit ? Pcre2MatchStatus.LimitExceeded : Pcre2MatchStatus.Error,
            RegexExecutionFailureCode.InvalidEncoding or RegexExecutionFailureCode.InvalidStartBoundary => Pcre2MatchStatus.Error,
            _ => Pcre2MatchStatus.LimitExceeded
        };
        return new(status, failure.Code == RegexExecutionFailureCode.None ? machine.SourceReturnCode : 0,
            machine.CandidateStart, machine.MarkIndex, failure);
    }
}
