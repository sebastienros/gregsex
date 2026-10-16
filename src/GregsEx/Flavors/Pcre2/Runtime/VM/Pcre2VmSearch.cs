using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Composition;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;

namespace GregsEx.Flavors.Pcre2.Runtime.VM;

// Prepared, unoptimized composition search. Not a public executor/factory.
// Controls owns callbacks, verbs, observable study/prefilter behavior and retries.
// hasExplicitCrOrLf is source HASCRORLF metadata supplied by the plan/controls
// owner, never inferred from arbitrary classes here. It has no default.
internal static class Pcre2VmSearch
{
    internal static Pcre2MatchResult FindWithoutControls(Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy, bool hasExplicitCrOrLf,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs) =>
        Find<char, Pcre2CharTextReader, Pcre2CharUnits>(program, input, data, options, policy,
            hasExplicitCrOrLf, ref machine, ref state, pairs);

    internal static Pcre2MatchResult FindWithoutControls(Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy, bool hasExplicitCrOrLf,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs) =>
        Find<byte, Pcre2ByteTextReader, Pcre2ByteUnits>(program, input, data, options, policy,
            hasExplicitCrOrLf, ref machine, ref state, pairs);

    private static Pcre2MatchResult Find<TUnit, TReader, TUnits>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy, bool hasExplicitCrOrLf,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
        where TUnits : struct, IPcre2Units<TUnit>
    {
        if (!program.IsExecutable || input.Length != machine.SubjectLength)
        {
            throw new ArgumentException("Executable plan and original subject are required.", nameof(program));
        }

        try
        {
            // Caller prepares one machine and one reduced positive RunState. Host
            // retries keep the same state/time/budget. No constructor/reset here.
            if (!Pcre2KernelOperation.PollEntry(program, ref machine, ref state))
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            if (options.OffsetLimit is not null && (program.Profile.Flags & Pcre2CompileFlags.UseOffsetLimit) == 0)
            {
                machine.SourceReturnCode = (int)Pcre2ErrorCode.Badoffsetlimit;
                return Pcre2Kernel.Failure(ref machine, ref state);
            }
            var maxLookbehind = program.SearchHints?.MaximumLookbehind ?? 0;
            foreach (var node in program.Nodes)
            {
                if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.SearchStart))
                {
                    return Pcre2Kernel.Failure(ref machine, ref state);
                }

                if (node.Kind is Pcre2NodeKind.Callout or Pcre2NodeKind.ResetStart or Pcre2NodeKind.Verb ||
                    (program.Profile.Flags & Pcre2CompileFlags.AutoCallout) != 0)
                {
                    throw new NotSupportedException("This internal search requires plans without controls/callouts. Use the VM control handoff.");
                }

                if (node.Kind == Pcre2NodeKind.Assertion)
                {
                    maxLookbehind = Math.Max(maxLookbehind, program.Assertions[node.Operand].MaximumLength);
                }

                if (node.Kind is Pcre2NodeKind.WordBoundary or Pcre2NodeKind.NotWordBoundary)
                {
                    maxLookbehind = Math.Max(maxLookbehind, 1);
                }
            }
            var flags = Pcre2KernelOperation.EffectiveFlags(program, options.Flags);
            var fragment = Pcre2TextFragments<TUnit, TUnits>.Bind(input, machine.SearchStart, maxLookbehind,
                program.Profile, flags, policy, ref state);
            if (state.Failure.Code != RegexExecutionFailureCode.None)
            {
                return Pcre2Kernel.Failure(ref machine, ref state);
            }

            if (!fragment.Validation.Valid)
            {
                return new(Pcre2MatchStatus.Error, fragment.Validation.NativeError,
                    fragment.Validation.Failure.Offset, Failure: fragment.Validation.Failure);
            }

            var anchored = (flags & Pcre2MatchFlags.Anchored) != 0 ||
                (program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0;
            var firstline = !anchored && (program.Profile.Flags & Pcre2CompileFlags.Firstline) != 0;
            var attempted = false;
            var firstFragment = true;
            while (fragment.Available)
            {
                if (!Pcre2KernelOperation.ApplyFragment(fragment, ref machine, ref state))
                {
                    return Pcre2Kernel.Failure(ref machine, ref state);
                }

                var fragmentOptions = options with { Flags = flags | fragment.FragmentFlags };
                // Native ordinary check_subject is a validation window, not
                // start_subject (match.c7266/extuni.c127). RI parity can inspect
                // valid prefix before the operation's validation start, also
                // for an initially valid MATCH_INVALID_UTF subject. Prefix reads
                // still use the checked reader; an invalid prefix is a barrier,
                // never trusted as validated text. Later fragments have a real
                // invalid-UTF delimiter and retain its checked floor.
                var executionFloor = firstFragment ? 0 : fragment.ScanStart;
                while (true)
                {
                    if (options.OffsetLimit is { } limit && machine.CandidateStart > limit)
                    {
                        if (!attempted)
                        {
                            return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, machine.CandidateStart);
                        }

                        break;
                    }
                    if (!Pcre2Kernel.BeginCandidate(program, executionFloor, ref machine, ref state))
                    {
                        return Pcre2Kernel.Failure(ref machine, ref state);
                    }

                    attempted = true;
                    var step = Pcre2Vm.RunCandidate<TUnit, TReader>(program, input, data, fragmentOptions, ref machine, ref state);
                    if (step.Kind == Pcre2StepKind.Accept)
                    {
                        return Pcre2Kernel.Publish(program, ref machine, ref state, pairs);
                    }

                    if (step.Kind is Pcre2StepKind.ResourceFailure or Pcre2StepKind.SourceAbort)
                    {
                        return Pcre2Kernel.Publish(program, ref machine, ref state, pairs);
                    }

                    if (step.Kind == Pcre2StepKind.Partial)
                    {
                        break;
                    }

                    if (step.Kind != Pcre2StepKind.Backtrack)
                    {
                        throw new InvalidOperationException("Unresolved VM boundary cannot be published as a match.");
                    }

                    if (anchored || machine.CandidateStart == fragment.VisibleEnd)
                    {
                        break;
                    }

                    if (firstline && AtNewline<TUnit, TReader>(program, input, machine.CandidateStart, fragment.VisibleEnd, ref state))
                    {
                        break;
                    }

                    if (state.Failure.Code != RegexExecutionFailureCode.None)
                    {
                        return Pcre2Kernel.Failure(ref machine, ref state);
                    }
                    // This is matcher bumpalong, not host empty-match iteration:
                    // AdvanceAfterEmpty always joins CRLF and would hide an
                    // explicitly matchable LF even when HASCRORLF is true.
                    var advance = TReader.Read(input, machine.CandidateStart, fragment.VisibleEnd, false, program.Profile, ref state);
                    if (state.Failure.Code != RegexExecutionFailureCode.None)
                    {
                        return Pcre2Kernel.Failure(ref machine, ref state);
                    }

                    if (advance.Status != Pcre2ReadStatus.Value)
                    {
                        throw new InvalidOperationException("Validated candidate requires a readable native character.");
                    }

                    var next = machine.CandidateStart + advance.Width;
                    // match.c7905–7920: source bumpalong skips the interior of CRLF
                    // only when no explicit CR/LF match is recorded by compilation.
                    if (!hasExplicitCrOrLf && next > machine.SearchStart && next < fragment.VisibleEnd &&
                        program.Profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Any or Pcre2Newline.Anycrlf)
                    {
                        if (!Pcre2Storage.Charge(ref machine, ref state, 2, next))
                        {
                            return Pcre2Kernel.Failure(ref machine, ref state);
                        }

                        if (TUnits.Unit(input, next - 1) == 13 && TUnits.Unit(input, next) == 10)
                        {
                            next++;
                        }
                    }
                    if (next <= machine.CandidateStart || next > fragment.VisibleEnd)
                    {
                        throw new InvalidOperationException("Candidate advancement must make bounded progress.");
                    }

                    machine.CandidateStart = next;
                }
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return Pcre2Kernel.Failure(ref machine, ref state);
                }

                if (fragment.VisibleEnd == input.Length)
                {
                    return Pcre2Kernel.Publish(program, ref machine, ref state, pairs);
                }

                fragment = Pcre2TextFragments<TUnit, TUnits>.Next(input, fragment, program.Profile, ref state);
                firstFragment = false;
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return Pcre2Kernel.Failure(ref machine, ref state);
                }
                // FRAGMENT_RESTART discards previous partials, including when no
                // nonempty fragment remains. Work/deadline/cancellation never reset.
                machine.HasPartial = false;
                if (machine.SourceReturnCode == (int)Pcre2ErrorCode.Partial)
                {
                    machine.SourceReturnCode = 0;
                }
            }
            return Pcre2Kernel.Publish(program, ref machine, ref state, pairs);
        }
        finally { Pcre2KernelOperation.ReleaseTransient(ref machine); }
    }

    private static bool AtNewline<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        int position, int end, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        if (position == end)
        {
            return false;
        }

        var read = TReader.Read(input, position, end, false, program.Profile, ref state);
        if (read.Status != Pcre2ReadStatus.Value)
        {
            return false;
        }

        if (program.Profile.Newline == Pcre2Newline.Crlf)
        {
            if (read.Value != 13 || position + read.Width == end)
            {
                return false;
            }

            var next = TReader.Read(input, position + read.Width, end, false, program.Profile, ref state);
            return next.Status == Pcre2ReadStatus.Value && next.Value == 10;
        }
        return program.Profile.Newline switch
        {
            Pcre2Newline.Cr => read.Value == 13,
            Pcre2Newline.Lf => read.Value == 10,
            Pcre2Newline.Nul => read.Value == 0,
            Pcre2Newline.Anycrlf => read.Value is 10 or 13,
            Pcre2Newline.Any => read.Value is 10 or 13 or 11 or 12 or 0x85 or 0x2028 or 0x2029,
            _ => false
        };
    }
}
