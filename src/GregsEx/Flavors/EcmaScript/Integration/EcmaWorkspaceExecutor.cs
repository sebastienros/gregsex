#nullable enable
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.EcmaScript;

namespace GregsEx.Flavors.EcmaScript.Integration;

// One dispatch per operation. The VM owns all character, capture and budget loops.
internal sealed class EcmaWorkspaceExecutor(IEcmaExecutor executor) : IRegularExecutor
{
    internal IEcmaExecutor Executor { get; } = executor;
    internal const int DefaultFrames = 4096;
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
        => Executor.GetRequiredCapacity(operation, DefaultFrames);

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups,
        out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if (typeof(TUnit) != typeof(char))
        {
            throw new ArgumentException("ECMAScript requires UTF-16 input.", nameof(input));
        }

        return Executor.Search(MemoryMarshal.Cast<TUnit, char>(input), startAt,
            (Executor.Program.Profile.Flags & EcmaScriptFlags.Sticky) != 0,
            EcmaStartPolicy.LowerBound, operation, ref state, groups, out match);
    }
}
