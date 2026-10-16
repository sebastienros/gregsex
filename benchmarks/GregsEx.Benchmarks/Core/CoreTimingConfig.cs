using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Horology;

namespace GregsEx.Benchmarks.Core;

// Transform each class's required ShortRun job instead of adding a second job.
// This informational local cohort avoids generated dotnet builds under the lease.
public sealed class CoreTimingConfig : ManualConfig
{
    public CoreTimingConfig() => AddJob(Job.Default
        .WithToolchain(InProcessEmitToolchain.Instance)
        .WithIterationTime(TimeInterval.FromMilliseconds(150))
        .AsMutator());
}
