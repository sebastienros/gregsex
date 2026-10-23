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

`Oniguruma/TextMateScannerBenchmarks` replays the scanner calls TextMateSharp makes tokenizing the first 40 docs-heavy
fences of each language (`test/corpora/oniguruma/trace.json.gz`, tm-grammars 1.32.22) through native Oniguruma 6.9.10
via Onigwrap 1.0.11's `OnigScanner` (TextMateSharp 2.0.4's binding) and through `OnigurumaScanner`, and checks that
both select the same patterns and captures. Array-backed lines are Douze's case (TextMateSharp copies a line without
its newline into an array, which disables the per-regex cache); string-backed lines enable it in both.

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*TextMateScannerBenchmarks*"
```

Measured 2026-10-23 at f2fab34a (GregsEx rows re-measured after 5d4e7d4c, which stops reusing cached results of
`\G` patterns: within 4% of these): Apple M4 Pro (14 cores), macOS 15.8.1, .NET 10.0.11 Arm64 RyuJIT, BenchmarkDotNet
0.15.8 ShortRun, host otherwise idle (load average about 4 from other sessions). Per call is per
`FindNextMatchSync`/`FindNextMatch`; per line divides by the distinct traced lines. GregsEx allocates nothing.

| Language, lines | Calls | Lines | Onigwrap µs/call | GregsEx µs/call | Onigwrap µs/line | GregsEx µs/line | Ratio | Onigwrap B/call |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| bash, array-backed | 5616 | 258 | 2.58 | 0.45 | 56.2 | 9.7 | 0.17 | 331 |
| ts, array-backed | 6584 | 297 | 7.16 | 2.10 | 158.7 | 46.5 | 0.29 | 973 |
| js, array-backed | 8895 | 412 | 10.42 | 2.39 | 224.9 | 51.7 | 0.23 | 1370 |
| html, array-backed | 7080 | 234 | 1.16 | 0.23 | 35.0 | 6.9 | 0.20 | 357 |
| json, array-backed | 3091 | 299 | 0.48 | 0.10 | 4.9 | 1.0 | 0.20 | 283 |
| yaml, array-backed | 4712 | 364 | 3.26 | 0.49 | 42.2 | 6.3 | 0.15 | 397 |
| csharp, array-backed | 10424 | 666 | 8.83 | 4.91 | 138.2 | 76.9 | 0.56 | 481 |
| bash, string-backed | 5616 | 258 | 2.01 | 0.37 | 43.7 | 8.1 | 0.18 | 269 |
| ts, string-backed | 6584 | 297 | 4.70 | 1.27 | 104.2 | 28.2 | 0.27 | 739 |
| js, string-backed | 8895 | 412 | 7.08 | 1.55 | 152.9 | 33.5 | 0.22 | 1023 |
| html, string-backed | 7080 | 234 | 0.68 | 0.15 | 20.7 | 4.6 | 0.22 | 299 |
| json, string-backed | 3091 | 299 | 0.41 | 0.08 | 4.3 | 0.9 | 0.20 | 254 |
| yaml, string-backed | 4712 | 364 | 2.20 | 0.34 | 28.5 | 4.4 | 0.16 | 316 |
| csharp, string-backed | 10424 | 666 | 4.64 | 2.48 | 72.7 | 38.8 | 0.53 | 337 |

Per regex search, array-backed (both engines search the same patterns, each scanner stopping at a match at the
offset):

| Language | Searches | Onigwrap ns/search | GregsEx ns/search | Onigwrap B/search |
| --- | ---: | ---: | ---: | ---: |
| bash | 82536 | 176 | 30 | 23 |
| ts | 244118 | 193 | 57 | 26 |
| js | 352769 | 263 | 60 | 35 |
| html | 45264 | 181 | 36 | 56 |
| json | 17481 | 84 | 17 | 50 |
| yaml | 53098 | 290 | 43 | 35 |
| csharp | 336952 | 273 | 152 | 15 |

Onigwrap's cost includes its managed layer (a `Region`, an `OnigResult` and capture objects per search, a lock and
`Encoding.Unicode.GetByteCount` per call). With that layer removed (direct `onigwrap_search` calls, tiered JIT off)
GregsEx took 0.10x to 0.44x of native Oniguruma's time on the same calls during development.
