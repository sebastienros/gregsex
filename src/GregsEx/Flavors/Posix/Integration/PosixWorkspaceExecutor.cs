using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Posix;

namespace GregsEx.Flavors.Posix.Integration;

internal sealed class PosixWorkspaceExecutor(IPosixExecutor executor) : IRegularExecutor
{
    internal IPosixExecutor Executor => executor;
    internal bool NoSub => (executor.Program.Profile.Flags & PosixCompileFlags.NoSub) != 0;
    // A disjoint tail in existing owned Int32 storage, sized from the actual unmanaged
    // capture layout. No full-capture array is acquired per native partial-output call.
    internal int PublicationSlots => checked((Unsafe.SizeOf<RegexCapture>() * executor.Program.GroupCount + 3) / 4);
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
    {
        var capacity = executor.GetRequiredCapacity(operation, PosixExecutionCapacity.Default);
        return new(capacity.StateSlots, capacity.CaptureSlots, checked(capacity.ScratchSlots + PublicationSlots));
    }
    internal Span<RegexCapture> Publication(Span<int> scratch)
    {
        var start = executor.GetRequiredCapacity(SearchOperation.Captures, PosixExecutionCapacity.Default).ScratchSlots;
        return MemoryMarshal.Cast<int, RegexCapture>(scratch.Slice(start, PublicationSlots))[..executor.Program.GroupCount];
    }
    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if (typeof(TUnit) != typeof(byte))
        {
            throw new ArgumentException("POSIX subjects require bytes.", nameof(input));
        }

        return executor.Search(MemoryMarshal.Cast<TUnit, byte>(input), startAt, operation,
            PosixExecutionFlags.None, PosixExecutionCapacity.Default, ref state, groups, out match);
    }
}
