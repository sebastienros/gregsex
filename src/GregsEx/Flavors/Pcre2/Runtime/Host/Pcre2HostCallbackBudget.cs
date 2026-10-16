namespace GregsEx.Flavors.Pcre2.Runtime.Host;

// The operation/session owns this small counter. Match invocations have separate
// machines, so a wrapper retains the cumulative callback ceiling across them.
internal sealed class Pcre2HostCallbackBudget
{
    internal int Count;
    internal readonly int Maximum;
    internal bool Denied;
    internal Pcre2HostCallbackBudget(int maximum) => Maximum = maximum;
    internal bool Enter()
    { if (Count >= Maximum) { Denied = true; return false; } Count++; return true; }
    internal Pcre2CharCallbacks? Wrap(Pcre2CharCallbacks? callbacks)
    {
        if (callbacks?.Match is null)
        {
            return callbacks;
        }

        return callbacks with
        {
            Match = (in context, _) =>
            Enter() ? callbacks.Match(in context, callbacks.State) : (int)Pcre2ErrorCode.Matchlimit
        };
    }
    internal Pcre2ByteCallbacks? Wrap(Pcre2ByteCallbacks? callbacks)
    {
        if (callbacks?.Match is null)
        {
            return callbacks;
        }

        return callbacks with
        {
            Match = (in context, _) =>
            Enter() ? callbacks.Match(in context, callbacks.State) : (int)Pcre2ErrorCode.Matchlimit
        };
    }
}
