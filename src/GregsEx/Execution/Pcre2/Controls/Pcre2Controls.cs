using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;

namespace GregsEx.Execution.Pcre2.Controls;

// Notify records caller-supplied real source occurrences. It does not execute a
// control node, invoke a callback or claim a successful candidate.
internal readonly partial struct Pcre2Controls : IPcre2Controls
{
    public static Pcre2Step Notify(Pcre2ExecutionEvent occurrence, Pcre2Program program,
        ReadOnlySpan<char> input, Pcre2CharCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2VmState machine, ref RunState state) => Notify(occurrence, ref machine, ref state);

    public static Pcre2Step Notify(Pcre2ExecutionEvent occurrence, Pcre2Program program,
        ReadOnlySpan<byte> input, Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2VmState machine, ref RunState state) => Notify(occurrence, ref machine, ref state);

    private static Pcre2Step Notify(Pcre2ExecutionEvent occurrence, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Enum.IsDefined(occurrence))
        {
            throw new ArgumentOutOfRangeException(nameof(occurrence));
        }

        if (!Pcre2Storage.Charge(ref machine, ref state, 1, machine.Position))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        if (occurrence == Pcre2ExecutionEvent.CandidateStart)
        {
            machine.PendingFlags |= Pcre2CalloutFlags.Startmatch;
        }
        else if (occurrence == Pcre2ExecutionEvent.Backtrack)
        {
            machine.PendingFlags |= Pcre2CalloutFlags.Backtrack;
        }

        return new(Pcre2StepKind.Continue, machine.Node);
    }
}
