#nullable enable
namespace GregsEx.Execution.Pcre2.Controls;

internal enum Pcre2ControlSignal { None, Commit, Prune, Skip, SkipArgument, Then }

// Invocation-global source fields, distinct from the rollbackable VM snapshots.
// The caller constructs once and keeps this value through all candidate retries.
internal struct Pcre2ControlState
{
    // Default-initialized invocation state must mean no mark, including when
    // embedded in a workspace field before its first operation.
    private int nomatchMarkPlusOne;
    internal int NomatchMarkIndex
    {
        readonly get => nomatchMarkPlusOne - 1;
        set => nomatchMarkPlusOne = checked(value + 1);
    }
    internal int SkipArgumentCount, IgnoreSkipArguments;
    internal bool CallbackAborted;
    internal Pcre2ControlSignal Signal;
    internal int VerbNode, ArgumentMarkIndex, OriginRecursion, SkipPosition;
    internal int? CalloutSubjectLength;

}
