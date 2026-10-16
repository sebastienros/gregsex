using BenchmarkDotNet.Attributes;
namespace AllocationPolicy;
[MemoryDiagnoser]
[ShortRunJob]
public class StrictBenchmarks
{
 public IEnumerable<string> Ids=>Registry.Cases.Select(c=>c.Id);
 [ParamsSource(nameof(Ids))] public string Case {get;set;}="";
 private Workload workload=null!;
 [GlobalSetup] public void Setup(){workload=Registry.Create(Case);workload.Prepare();}
 [Benchmark] public long Cycle()=>workload.Cycle();
 [GlobalCleanup] public void Cleanup()=>workload.Dispose();
}
[MemoryDiagnoser]
[ShortRunJob]
public class LifecycleBenchmarks
{
 public IEnumerable<string> Ids=>Lifecycle.Ids;
 [ParamsSource(nameof(Ids))] public string Case {get;set;}="";
 private Workload workload=null!;
 [GlobalSetup] public void Setup(){workload=Lifecycle.Create(Case);workload.Prepare();}
 [Benchmark] public long Cost()=>workload.Cycle();
 [GlobalCleanup] public void Cleanup()=>workload.Dispose();
}
