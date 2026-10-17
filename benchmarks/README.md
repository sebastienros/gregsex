# Benchmarks

All maintained measurements use the standard runner in
`GregsEx.Benchmarks`, with BenchmarkDotNet MemoryDiagnoser and ShortRunJob.
Generators and independent oracle tools remain under `tools/`.

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --list flat
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*AllocationPolicy*"
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*XmlSharedBenchmarks*"
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*Pcre2DataCosts*"
```

The README comparison uses `ReadmeBenchmarks`, not the historical cross-engine
summary or `Auto` backend selection:

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- \
  --filter "*ReadmeBenchmarks*" \
  --exporters json --iterationTime 200 --warmupCount 3 --iterationCount 3 --launchCount 1
```

Its 54 cases cover all six GregsEx flavors and BCL interpreted, runtime-compiled,
and source-generated execution where supported. All use `((a)|b)+c`, no flags,
`bbaaac` for short matching/captures, and 256 `a` characters followed by `c` for
long matching. POSIX uses equivalent ASCII bytes; other profiles use UTF-16.
XSD has non-capturing groups, so capture extraction is excluded for that flavor.
POSIX has no runtime-compiled or source-generated rows.

Setup checks actual GregsEx backends, profile/data identities, complete short
capture offsets/participation, matching and no-match results. Each case uses
its own fixed-capacity workspace, warms outside measurement, and checks that
capacity, reserved bytes and retained plan bytes remain unchanged. BCL captures
use `Match`/groups; GregsEx uses caller-owned capture buffers. Report their
allocation difference as an API/output difference, not as a universal engine
allocation claim. Keep raw MemoryDiagnoser byte/operation totals even when
the formatted allocation column rounds them to zero.

`AllocationPolicy` retains prepared core, ECMAScript, Thompson, VM, host,
reader/classification, Unicode, iteration, and lifecycle workloads. Their case
IDs and semantic setup checks are preserved; the separate preparation CLI and
retired-method inventory are removed. XML primitive and PCRE2 data benchmarks
also retain their original semantic checks. Historical diagnostic controls and
developer-specific runners are not part of the maintained suite.

Report missing comparators as gaps rather than comparing different case sets.

Prepare Release builds and bounded case filters before measuring on a quiet
host. Preserve raw BDN results and host conditions in durable storage.
Historical results are not new measurements of
the reorganized harness. See the [performance contract](../docs/specs/performance.md)
and [release report](../docs/release/performance.md).
