using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;
namespace XmlProbe;
// Mutate the required ShortRun job; never add a second job or generated build.
public sealed class XmlTimingConfig : ManualConfig
{
    public XmlTimingConfig() => AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance)
        .WithIterationTime(TimeInterval.FromMilliseconds(150)).AsMutator());
}
