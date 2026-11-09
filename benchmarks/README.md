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

`Oniguruma/OnigurumaScannerBenchmarks` measures the scanner's selection alone: every pattern of one grammar of the
Oniguruma corpus (shellscript 142, typescript 361, csharp 319, html 618) as one interpreted scanner, driven as a
tokenizer drives it, from match end to match end, over the grammar's six sample lines as strings (`Strings`, whose
results the scanner keeps per string), as arrays (`Arrays`, searched afresh each call) and joined into one line of
1,700 to 3,900 characters (`LongLine`). Setup fails unless each walk selects what searching every pattern to the end
of the line selects. A tokenizer's scanners hold fewer patterns and change with its rule stack, so these are not
tokenizer throughputs.

```bash
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter '*OnigurumaScannerBenchmarks*'
```

Before and after the scanner walks the starts once for all its patterns (`ONIG_REGSET_POSITION_LEAD`,
[FL-ONIG-005](../docs/specs/flavors.md)) instead of searching each pattern in turn: Intel Xeon 2.1 GHz cloud VM (4
cores), Ubuntu 24.04.5, .NET 10.0.14 (SDK 10.0.403), X64 RyuJIT x86-64-v4, BenchmarkDotNet 0.15.8 ShortRun (1 launch,
3 warmups, 3 iterations), 2026-11-09, the two builds back to back with nothing else running (1-minute load average
4.3 when the first started, left by the build before it, and 1.0 at the end of each). Mean, with BenchmarkDotNet's error (half the 99.9% confidence interval, wide over three iterations);
every row allocates nothing.

| Grammar | Method | Before | After |
| --- | --- | ---: | ---: |
| csharp | Strings | 35.8 us ± 72.4 | 19.7 us ± 10.8 |
| csharp | Arrays | 76.6 us ± 156.6 | 22.3 us ± 11.6 |
| csharp | LongLine | 482.0 us ± 701.8 | 286.5 us ± 103.7 |
| html | Strings | 28.6 us ± 32.9 | 26.2 us ± 55.0 |
| html | Arrays | 129.4 us ± 6.6 | 21.5 us ± 38.4 |
| html | LongLine | 356.8 us ± 479.7 | 364.4 us ± 295.0 |
| shellscript | Strings | 4.56 us ± 4.20 | 3.07 us ± 1.91 |
| shellscript | Arrays | 6.24 us ± 22.19 | 3.22 us ± 1.40 |
| shellscript | LongLine | 62.1 us ± 56.7 | 41.1 us ± 91.3 |
| typescript | Strings | 53.0 us ± 113.3 | 40.5 us ± 42.8 |
| typescript | Arrays | 351.0 us ± 234.9 | 46.9 us ± 94.5 |
| typescript | LongLine | 943.8 us ± 1,040.4 | 824.7 us ± 1,172.0 |

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
