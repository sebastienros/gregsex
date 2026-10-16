using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Controls;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Text;
using GregsEx.Flavors.Pcre2.Runtime.Kernel;

namespace GregsEx.Flavors.Pcre2.Runtime.Controls;

// Concrete internal search composition. Workspace/lifecycle and host adapters
// supply prepared arrays, immutable control context and one cumulative RunState.
internal static class Pcre2ControlSearch
{
    internal static Pcre2MatchResult Find(Pcre2ControlPlan plan, ReadOnlySpan<char> input,
        Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy,
        Pcre2CharCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs) =>
        Find<char, Pcre2CharTextReader, Pcre2CharUnits, Pcre2CharCallbacks, Pcre2CharCalloutDriver>(
            plan, input, data, options, policy, callbacks, callbackPairs, ref machine, ref state, pairs);

    internal static Pcre2MatchResult Find(Pcre2ControlPlan plan, ReadOnlySpan<byte> input,
        Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy,
        Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs) =>
        Find<byte, Pcre2ByteTextReader, Pcre2ByteUnits, Pcre2ByteCallbacks, Pcre2ByteCalloutDriver>(
            plan, input, data, options, policy, callbacks, callbackPairs, ref machine, ref state, pairs);

    private static Pcre2MatchResult Find<TUnit, TReader, TUnits, TCallbacks, TDriver>(Pcre2ControlPlan plan,
        ReadOnlySpan<TUnit> input, Pcre2DataView data, Pcre2MatchOptions options, Pcre2ValidationPolicy policy,
        TCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
        where TUnits : struct, IPcre2Units<TUnit> where TCallbacks : class
        where TDriver : struct, IPcre2CalloutDriver<TUnit, TCallbacks>
    {
        ArgumentNullException.ThrowIfNull(plan);
        var program = plan.Program;
        if (!program.IsExecutable || input.Length != machine.SubjectLength || program.Profile.Width != TUnits.Width)
        {
            throw new ArgumentException("Validated executable plan, matching width and original subject are required.", nameof(plan));
        }

        if (callbackPairs.Length < program.GroupCount)
        {
            throw new ArgumentException("Full prepared callback pair storage is required.", nameof(callbackPairs));
        }

        var controls = new Pcre2ControlState();
        try
        {
            if (!Pcre2KernelOperation.PollEntry(program, ref machine, ref state))
            {
                return Failure(ref controls, ref machine, ref state);
            }

            if (options.OffsetLimit is not null && (program.Profile.Flags & Pcre2CompileFlags.UseOffsetLimit) == 0)
            {
                machine.SourceReturnCode = (int)Pcre2ErrorCode.Badoffsetlimit;
                return Failure(ref controls, ref machine, ref state);
            }
            var maxLookbehind = program.SearchHints?.MaximumLookbehind ?? 0;
            if (program.SearchHints is not null && state.Budget.IsUnconstrained(program.Nodes.Length + 4))
            {
                if (!Pcre2Storage.Charge(ref machine, ref state, program.Nodes.Length, machine.SearchStart))
                {
                    return Failure(ref controls, ref machine, ref state);
                }
            }
            else
            {
                foreach (var node in program.Nodes)
                {
                    if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.SearchStart))
                    {
                        return Failure(ref controls, ref machine, ref state);
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
            }
            var flags = Pcre2KernelOperation.EffectiveFlags(program, options.Flags);
            if ((flags & (Pcre2MatchFlags.PartialHard | Pcre2MatchFlags.PartialSoft)) != 0 &&
                ((flags & Pcre2MatchFlags.Endanchored) != 0 || (program.Profile.Flags & Pcre2CompileFlags.Endanchored) != 0))
            {
                return new(Pcre2MatchStatus.Error, (int)Pcre2ErrorCode.Badoption, 0);
            }

            var fragment = Pcre2TextFragments<TUnit, TUnits>.Bind(input, machine.SearchStart, maxLookbehind,
                program.Profile, flags, policy, ref state);
            if (Denied(ref machine, ref state))
            {
                return Failure(ref controls, ref machine, ref state);
            }

            if (!fragment.Validation.Valid)
            {
                return new(Pcre2MatchStatus.Error, fragment.Validation.NativeError, fragment.Validation.Failure.Offset,
                    Failure: fragment.Validation.Failure);
            }
            // Source cb.subject_length7257 is frozen after initial fragment
            // selection; the original pointer survives later fragments/scans.
            controls.CalloutSubjectLength = fragment.VisibleEnd;
            var anchored = (flags & Pcre2MatchFlags.Anchored) != 0 || (program.Profile.Flags & Pcre2CompileFlags.Anchored) != 0;
            var firstline = !anchored && (program.Profile.Flags & Pcre2CompileFlags.Firstline) != 0;
            bool attempted = false, firstFragment = true;
            while (fragment.Available)
            {
                if (!Pcre2KernelOperation.ApplyFragment(fragment, ref machine, ref state))
                {
                    return Failure(ref controls, ref machine, ref state);
                }

                var fragmentOptions = options with { Flags = flags | fragment.FragmentFlags };
                var admission = new Pcre2CandidatePolicy();
                var floor = firstFragment ? 0 : fragment.ScanStart;
                while (true)
                {
                    if (!admission.Admit<TUnit, TReader, TUnits>(program, input, data, fragmentOptions.Flags,
                        anchored, firstline, ref machine, ref state))
                    {
                        if (Denied(ref machine, ref state))
                        {
                            return Failure(ref controls, ref machine, ref state);
                        }

                        break;
                    }
                    if (options.OffsetLimit is { } limit && machine.CandidateStart > limit)
                    {
                        break;
                    }

                    if (!Pcre2Kernel.BeginCandidate(program, floor, ref machine, ref state))
                    {
                        return Failure(ref controls, ref machine, ref state);
                    }

                    attempted = true;
                    controls.Signal = Pcre2ControlSignal.None;
                    controls.SkipArgumentCount = 0;
                    var step = Pcre2Controls.RunCandidate<TUnit, TReader, TCallbacks, TDriver>(plan, input, data,
                        fragmentOptions, callbacks, callbackPairs, ref controls, ref machine, ref state);
                    if (controls.CallbackAborted)
                    {
                        return new(Pcre2MatchStatus.Error, machine.SourceReturnCode, 0, controls.NomatchMarkIndex);
                    }

                    if (step.Kind is Pcre2StepKind.Accept or Pcre2StepKind.ResourceFailure or Pcre2StepKind.SourceAbort)
                    {
                        return Publish(program, ref controls, ref machine, ref state, pairs);
                    }

                    if (step.Kind == Pcre2StepKind.Partial || controls.Signal == Pcre2ControlSignal.Commit)
                    {
                        break;
                    }

                    if (step.Kind != Pcre2StepKind.Backtrack)
                    {
                        throw new InvalidOperationException("Unresolved control boundary cannot be published.");
                    }
                    // Unmatched SKIP:name reruns exactly this candidate, ignoring
                    // the executed prefix of named SKIPs; one shared budget/time.
                    if (controls.Signal == Pcre2ControlSignal.SkipArgument)
                    { controls.IgnoreSkipArguments = controls.SkipArgumentCount; continue; }
                    controls.IgnoreSkipArguments = 0;
                    if (anchored || machine.CandidateStart == fragment.VisibleEnd)
                    {
                        break;
                    }

                    if (firstline && Pcre2CandidatePolicy.AtNewline<TUnit, TReader>(program, input, machine.CandidateStart, fragment.VisibleEnd, ref state))
                    {
                        break;
                    }

                    if (Denied(ref machine, ref state))
                    {
                        return Failure(ref controls, ref machine, ref state);
                    }

                    int next;
                    if (controls.Signal == Pcre2ControlSignal.Skip && controls.SkipPosition > machine.CandidateStart)
                    {
                        next = controls.SkipPosition;
                    }
                    else
                    {
                        var advance = TReader.Read(input, machine.CandidateStart, fragment.VisibleEnd, false, program.Profile, ref state);
                        if (Denied(ref machine, ref state))
                        {
                            return Failure(ref controls, ref machine, ref state);
                        }

                        if (advance.Status != Pcre2ReadStatus.Value)
                        {
                            throw new InvalidOperationException("Candidate must advance by a checked native character.");
                        }

                        next = machine.CandidateStart + advance.Width;
                    }
                    if (!program.HasExplicitCrOrLf && next > machine.SearchStart && next < fragment.VisibleEnd &&
                        program.Profile.Newline is Pcre2Newline.Crlf or Pcre2Newline.Any or Pcre2Newline.Anycrlf)
                    {
                        if (!Pcre2Storage.Charge(ref machine, ref state, 2, next))
                        {
                            return Failure(ref controls, ref machine, ref state);
                        }

                        if (TUnits.Unit(input, next - 1) == 13 && TUnits.Unit(input, next) == 10)
                        {
                            next++;
                        }
                    }
                    if (next <= machine.CandidateStart || next > fragment.VisibleEnd)
                    {
                        throw new InvalidOperationException("Control bumpalong must make bounded progress.");
                    }

                    machine.CandidateStart = next;
                }
                if (Denied(ref machine, ref state) && machine.SourceReturnCode != (int)Pcre2ErrorCode.Partial)
                {
                    return Failure(ref controls, ref machine, ref state);
                }

                if (fragment.VisibleEnd == input.Length)
                {
                    break;
                }

                fragment = Pcre2TextFragments<TUnit, TUnits>.Next(input, fragment, program.Profile, ref state);
                firstFragment = false;
                if (state.Failure.Code != RegexExecutionFailureCode.None)
                {
                    return Failure(ref controls, ref machine, ref state);
                }

                machine.HasPartial = false;
                if (machine.SourceReturnCode == (int)Pcre2ErrorCode.Partial)
                {
                    machine.SourceReturnCode = 0;
                }
            }
            if (!attempted)
            {
                return new(Pcre2MatchStatus.NoMatch, (int)Pcre2ErrorCode.Nomatch, 0, controls.NomatchMarkIndex);
            }

            return Publish(program, ref controls, ref machine, ref state, pairs);
        }
        finally { Pcre2KernelOperation.ReleaseTransient(ref machine); }
    }

    private static Pcre2MatchResult Publish(Pcre2Program program, ref Pcre2ControlState controls,
        ref Pcre2VmState machine, ref RunState state, Span<Pcre2OffsetPair> pairs)
    {
        var result = Pcre2Kernel.Publish(program, ref machine, ref state, pairs);
        return result.Status == Pcre2MatchStatus.Match ? result : result with { MarkIndex = controls.NomatchMarkIndex };
    }
    private static Pcre2MatchResult Failure(ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state) =>
        Pcre2Kernel.Failure(ref machine, ref state) with { MarkIndex = controls.NomatchMarkIndex };
    private static bool Denied(ref Pcre2VmState machine, ref RunState state) =>
        machine.SourceReturnCode != 0 || state.Failure.Code != RegexExecutionFailureCode.None;
}
