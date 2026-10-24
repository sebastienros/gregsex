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

## Oniguruma: TextMate scanners

The former `Oniguruma/TextMateScannerBenchmarks`, which replayed a TextMateSharp scanner trace through Onigwrap 1.0.11
and `OnigurumaScanner`, was removed with that trace: the trace needed a hook in TextMateSharp and inputs that are not
public. Scanner throughput is measured in TextMateSharp itself, against its tokenizer; GregsEx's correctness against
native Oniguruma is covered by the [Oniguruma oracle](../tools/oracles/oniguruma/README.md).

## ECMAScript: TextMate patterns

`EcmaScript/TextMateLineSearch` searches every match per line, lastIndex-driven, for TextMate C# grammar patterns
converted from Oniguruma to ES2025 (flags `dgv`, as Shiki's oniguruma-to-es converts them), against the source
pattern under GregsEx's Oniguruma flavor; setup fails if the two disagree on the match count. Set
`GREGSEX_TEXTMATE_CORPUS` to TextMateSharp's `bigfile.cs` for comparable numbers; without it the corpus is this
repository's `src/GregsEx` sources.

```bash
GREGSEX_TEXTMATE_CORPUS=/path/to/bigfile.cs dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter '*TextMateLineSearch*'
```

On bigfile.cs (5.8 MB, Apple M4 Pro, .NET 10, ShortRun), before and after the required-unit bound, the ASCII
first-unit filter and the follow-filtered greedy give-back:

| Pattern | ES before | ES after | Oniguruma |
| --- | ---: | ---: | ---: |
| element_access | 3,243.8 ms | 111.4 ms | 35.0 ms |
| lookahead_bracket `(?=\[)` | 141.9 ms | 4.2 ms | 1.2 ms |
| member_ident | 2,049.6 ms | 253.5 ms | 69.6 ms |
| identifier (`\p{Alpha}`) | 153.1 ms | 49.0 ms | 22.8 ms |
| identifier_ascii | 45.7 ms | 45.4 ms | 19.4 ms |
| comment | 90.2 ms | 18.6 ms | 9.5 ms |
| keywords | 56.9 ms | 59.5 ms | 31.5 ms |
