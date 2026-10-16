using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Assertions;
using GregsEx.Execution.Pcre2.Kernel;
using GregsEx.Execution.Pcre2.Recursion;
using GregsEx.Execution.Pcre2.Unicode;

namespace GregsEx.Execution.Pcre2.Composition;

// Concrete module dispatch only. Control statuses are never successful matches.
// The caller owns candidate admission, observable notifications and publication.
internal static class Pcre2Vm
{
    internal static Pcre2Step Step<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchOptions options, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        var step = Pcre2Kernel.Step<TUnit, TReader>(program, input, data, options, ref machine, ref state);
        return Dispatch<TUnit, TReader>(program, input, data, options.Flags, step, ref machine, ref state);
    }

    internal static Pcre2Step RunCandidate<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchOptions options, ref Pcre2VmState machine, ref RunState state)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        Pcre2Step step;
        do
        {
            step = Step<TUnit, TReader>(program, input, data, options, ref machine, ref state);
        }
        while (step.Kind == Pcre2StepKind.Continue);
        return step;
    }

    // Controls can deliver a real scoped assertion ACCEPT as Return/forceAtomic.
    // Controls owns locating/unwinding that scope; Script KET must not be invented.
    internal static Pcre2Step Dispatch<TUnit, TReader>(Pcre2Program program, ReadOnlySpan<TUnit> input,
        Pcre2DataView data, Pcre2MatchFlags flags, Pcre2Step step,
        ref Pcre2VmState machine, ref RunState state, bool forceAssertionAtomic = false)
        where TUnit : unmanaged where TReader : struct, IPcre2TextReader<TUnit>
    {
        if (state.Failure.Code != RegexExecutionFailureCode.None)
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        if (machine.SourceReturnCode != 0)
        {
            return step.Kind is Pcre2StepKind.Partial or Pcre2StepKind.SourceAbort or Pcre2StepKind.ResourceFailure
                ? step : new(Pcre2StepKind.ResourceFailure);
        }

        switch (step.Kind)
        {
            case Pcre2StepKind.EnterAssertion:
                if (Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out _, out var caller) &&
                    caller.Kind == Pcre2FrameKind.Predicate &&
                    program.Nodes[caller.ScopeNode].Kind == Pcre2NodeKind.Conditional &&
                    program.Conditions[program.Nodes[caller.ScopeNode].Operand].AssertionNode == machine.Node)
                {
                    return Pcre2AssertionOps.EnterPredicate<TUnit, TReader>(program, input, ref machine, ref state);
                }

                if (Denied(ref machine, ref state))
                {
                    return new(Pcre2StepKind.ResourceFailure);
                }

                return Pcre2AssertionOps.Step<TUnit, TReader>(program, input, data, flags, ref machine, ref state);
            case Pcre2StepKind.EnterSubroutine:
                return Pcre2RecursionOps.Step(program, flags, ref machine, ref state);
            case Pcre2StepKind.Unicode:
                return Pcre2UnicodeOps.Step<TUnit, TReader>(program, input, data, flags, ref machine, ref state);
            case Pcre2StepKind.Return:
            case Pcre2StepKind.Backtrack:
                if (!Pcre2Kernel.ReadModuleContinuation(program, ref machine, ref state, out _, out var frame))
                {
                    if (Denied(ref machine, ref state))
                    {
                        return new(Pcre2StepKind.ResourceFailure);
                    }

                    if (step.Kind == Pcre2StepKind.Return)
                    {
                        throw new InvalidOperationException("Return lacks a module boundary.");
                    }

                    return step; // Exhausted candidate belongs to the search loop.
                }
                if (frame.ScopeNode != step.Node || frame.ScopeNode != machine.Node)
                {
                    throw new InvalidOperationException("Occurrence does not own the active module boundary.");
                }
                // Conditional uses ScriptRun for its selected branch and Predicate
                // for assertion truth. The semantic owner, not frame kind alone, routes it.
                switch (program.Nodes[frame.ScopeNode].Kind)
                {
                    case Pcre2NodeKind.Assertion:
                    case Pcre2NodeKind.ScanSubstring:
                        return Pcre2AssertionOps.Resume<TUnit, TReader>(program, input, step.Kind,
                            ref machine, ref state, forceAssertionAtomic);
                    case Pcre2NodeKind.Subroutine:
                    case Pcre2NodeKind.Conditional:
                        if (forceAssertionAtomic)
                        {
                            throw new InvalidOperationException("Scoped assertion ACCEPT requires an assertion owner.");
                        }

                        return Pcre2RecursionOps.Resume(program, step, ref machine, ref state);
                    case Pcre2NodeKind.ScriptRun:
                        if (forceAssertionAtomic)
                        {
                            throw new InvalidOperationException("ACCEPT bypasses script validation.");
                        }

                        return Pcre2UnicodeOps.Resume<TUnit, TReader>(program, input, data, step.Kind, ref machine, ref state);
                    default:
                        throw new InvalidOperationException("Unknown module boundary owner.");
                }
            default:
                return step;
        }
    }

    private static bool Denied(ref Pcre2VmState machine, ref RunState state) =>
        state.Failure.Code != RegexExecutionFailureCode.None || machine.SourceReturnCode != 0;
}
