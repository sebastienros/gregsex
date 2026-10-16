using GregsEx.Contracts;
using GregsEx.Contracts.Pcre2;
using GregsEx.Execution.Pcre2.Kernel;

namespace GregsEx.Execution.Pcre2.Controls;

// Real callout execution, separate from the frozen notification interface.
internal static class Pcre2Callouts
{
    internal static Pcre2Step Invoke(Pcre2Program program, ReadOnlySpan<char> input,
        Pcre2CharCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        var site = Site(program, ref machine);
        if (callbacks?.Match is not { } callback)
        {
            return Complete(ref machine, ref state);
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount, callbackPairs);
        if (!storage.BeginCallout(ref machine, ref state) || !storage.PrepareCallbackPairs(true, ref machine, ref state))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        var mark = controls.NomatchMarkIndex < 0 ? default : program.NativeChars(program.Marks[controls.NomatchMarkIndex]);
        var text = site.IsString ? program.NativeChars(site.StringUnits) : default;
        var context = new Pcre2CharCalloutContext(input, storage.CallbackPairs, mark, text,
            Info(site, controls.CalloutSubjectLength ?? input.Length, controls.NomatchMarkIndex, ref machine));
        var result = callback(in context, callbacks.State);
        return Result(result, ref controls, ref machine, ref state);
    }

    internal static Pcre2Step Invoke(Pcre2Program program, ReadOnlySpan<byte> input,
        Pcre2ByteCallbacks? callbacks, Span<Pcre2OffsetPair> callbackPairs,
        ref Pcre2ControlState controls, ref Pcre2VmState machine, ref RunState state)
    {
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        var site = Site(program, ref machine);
        if (callbacks?.Match is not { } callback)
        {
            return Complete(ref machine, ref state);
        }

        var storage = new Pcre2Storage(state.Buffers, machine.Capacity, program.GroupCount, callbackPairs);
        if (!storage.BeginCallout(ref machine, ref state) || !storage.PrepareCallbackPairs(true, ref machine, ref state))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        var mark = controls.NomatchMarkIndex < 0 ? default : program.NativeBytes(program.Marks[controls.NomatchMarkIndex]);
        var text = site.IsString ? program.NativeBytes(site.StringUnits) : default;
        var context = new Pcre2ByteCalloutContext(input, storage.CallbackPairs, mark, text,
            Info(site, controls.CalloutSubjectLength ?? input.Length, controls.NomatchMarkIndex, ref machine));
        var result = callback(in context, callbacks.State);
        return Result(result, ref controls, ref machine, ref state);
    }

    private static Pcre2CalloutSite Site(Pcre2Program program, ref Pcre2VmState machine)
    {
        var node = program.Nodes[machine.Node];
        if (node.Kind != Pcre2NodeKind.Callout)
        {
            throw new InvalidOperationException("Callout requires an actual callout occurrence.");
        }

        return program.Callouts[node.Operand];
    }

    private static Pcre2CalloutInfo Info(Pcre2CalloutSite site, int length, int mark, ref Pcre2VmState machine) =>
        new(site.Number, site.IsString, mark >= 0, length, machine.CaptureTop, machine.CaptureLast,
            machine.CandidateStart, machine.Position, site.PatternPosition, site.NextItemLength,
            site.IsString ? site.StringSourceOffset : 0, site.IsString ? site.StringUnits.Count : 0, machine.PendingFlags);

    private static Pcre2Step Result(int result, ref Pcre2ControlState controls,
        ref Pcre2VmState machine, ref RunState state)
    {
        // Source do_callout332 clears only after an actual user invocation.
        machine.PendingFlags = Pcre2CalloutFlags.None;
        if (!Pcre2Storage.Charge(ref machine, ref state, 0, machine.Position))
        {
            return new(Pcre2StepKind.ResourceFailure);
        }

        if (result < 0)
        {
            controls.CallbackAborted = true;
            machine.SourceReturnCode = result;
            return new(Pcre2StepKind.SourceAbort, machine.Node, result);
        }
        if (result > 0)
        {
            Pcre2Kernel.Fail(ref state);
            return new(Pcre2StepKind.Continue, machine.Node);
        }
        return Complete(ref machine, ref state);
    }

    private static Pcre2Step Complete(ref Pcre2VmState machine, ref RunState state)
    {
        Pcre2Kernel.CompleteNode(machine.Node, ref machine, ref state);
        return new(Pcre2StepKind.Continue, machine.Node);
    }
}
