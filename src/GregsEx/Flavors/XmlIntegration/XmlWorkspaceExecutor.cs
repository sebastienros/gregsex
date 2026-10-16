using System.Runtime.InteropServices;
using GregsEx.Contracts;
using GregsEx.Contracts.Xml;
using GregsEx.Flavors.XmlShared;

namespace GregsEx.Flavors.XmlIntegration;

// One dispatch per call, with static native readers inside the verified kernels.
internal sealed class XmlWorkspaceExecutor(IXmlExecutor executor) : IRegularExecutor
{
    internal const int Frames = 4096;
    internal IXmlExecutor Inner => executor;
    public RegexWorkspaceCapacity GetRequiredCapacity(SearchOperation operation)
        => executor.GetRequiredCapacity(operation, Frames);

    public RegexSearchStatus Search<TUnit, TReader>(ReadOnlySpan<TUnit> input, int startAt,
        SearchOperation operation, ref RunState state, Span<RegexCapture> groups, out RegexMatchRange match)
        where TUnit : unmanaged where TReader : struct, ITextReader<TUnit>
    {
        if (typeof(TUnit) == typeof(char))
        {
            return executor.Search<char, XmlCharTextReader>(MemoryMarshal.Cast<TUnit, char>(input),
                startAt, operation, Frames, ref state, groups, out match);
        }

        if (typeof(TUnit) == typeof(byte))
        {
            return executor.Search<byte, XmlByteTextReader>(MemoryMarshal.Cast<TUnit, byte>(input),
                startAt, operation, Frames, ref state, groups, out match);
        }

        throw new ArgumentException("XML input requires UTF-16 or UTF-8 units.", nameof(input));
    }
}
