# Performance and allocations

This document records the release performance and allocation qualification for `GregsEx` across all six public profiles (`RegularCore`, `ECMAScript`, `XSD`, `XPath`, `POSIX`, and `PCRE2`) and all execution tiers (`Interpreted`, `Auto` semantic acceleration, `Auto` guarded runtime compilation, `RegexPattern.Compile()`, and `[GeneratedPattern]`).

## Measurement Environment & Policy

- **Host:** Apple M4 Pro (14 logical / 14 physical cores, `osx-arm64`), macOS Sequoia 15.8.1 (`Darwin 24.6.0`).
- **Runtime & SDK:** .NET SDK `10.0.400`, .NET Runtime `10.0.11` (`Arm64 RyuJIT armv8.0-a`).
- **Harness:** `BenchmarkDotNet v0.15.8`, `[MemoryDiagnoser]`, `[ShortRunJob]` (`WarmupCount=3`, `IterationCount=3`, `LaunchCount=1`).
- **Allocation Authority (`PERF-003`, `PERF-016`):** `BenchmarkDotNet` `MemoryDiagnoser` is the sole allocation authority under the allocation contract. Raw allocated-byte numerators and operation denominators from `BenchmarkDotNet`'s `// GC:` diagnostics are preserved alongside workspace capacity (`ws.Capacity`), reserved bytes (`ws.ReservedBytes`), and plan retained bytes (`pattern.RetainedBytes`).
- **Comparators (`PERF-012`, `PERF-014`):** `System.Text.RegularExpressions` (`Interpreted`, `Compiled`, `[GeneratedRegex]`, and `RegexOptions.ECMAScript`) and Jint's QuickJS-derived regex engine (`jint-quickjs`) on the pinned 14-case release manifest (`docs/benchmarks/release-manifest.json`).

Sections 1-3 retain earlier measurements. The README now uses the separate,
explicitly forced execution-tier matrix in [section 4](#4-explicit-execution-tier-readme-comparison-readmebenchmarks).
Do not combine these runs into a single ranking.

---

## 1. End-to-End Allocation & Lifecycle Accounting (`ReleaseAllocationBenchmarks`)

Command:

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*ReleaseAllocationBenchmarks*"
```

| Method | Profile / Mode | Mean | Error | StdDev | Raw GC Numerator / Ops | BDN Allocated |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `RegularCoreAutoCaptures` | `RegularCore` (`Auto` -> `Compiled`) | `78.30 ns` | `10.01 ns` | `0.55 ns` | `0 B / 8,388,608 ops` | `0 B` (`-`) |
| `EcmaScriptAutoCaptures` | `ECMAScript` (`Auto` -> `Compiled`) | `67.51 ns` | `5.55 ns` | `0.30 ns` | `0 B / 8,388,608 ops` | `0 B` (`-`) |
| `XsdAutoIsMatch` | `XSD` (`Auto` -> `Compiled`) | `35.04 ns` | `5.86 ns` | `0.32 ns` | `0 B / 16,777,216 ops` | `0 B` (`-`) |
| `XPathAutoCaptures` | `XPath` (`Auto` -> `Compiled`) | `57.66 ns` | `4.79 ns` | `0.26 ns` | `0 B / 16,777,216 ops` | `0 B` (`-`) |
| `Pcre2AutoCaptures` | `PCRE2` (`Auto` -> `Compiled`) | `54.54 ns` | `25.87 ns` | `1.42 ns` | `0 B / 16,777,216 ops` | `0 B` (`-`) |
| `PosixAutoRegExec` | `POSIX ERE` (`Auto` -> `Interpreted`) | `45.31 ns` | `13.17 ns` | `0.72 ns` | `248 B / 16,777,216 ops` (`0.000015 B/op`)* | `0 B` (`-`) |
| `GeneratedRegularCoreCaptures` | `RegularCore` (`[GeneratedPattern]`) | `79.37 ns` | `24.52 ns` | `1.34 ns` | `0 B / 8,388,608 ops` | `0 B` (`-`) |
| `EcmaFallbackBackrefCaptures` | `ECMAScript` (`([a-z]+)=\1` VM fallback) | `1,469.13 ns` | `412.54 ns` | `22.61 ns` | `0 B / 524,288 ops` | `0 B` (`-`) |
| `LifecycleParseAutoAndCreateWorkspace` | Lifecycle (`Parse` + `Auto` FEC + `CreateWorkspace`) | `261,817.63 ns` | `2,095,143.73 ns` | `114,841.90 ns` | `173,174,720 B / 1,024 ops` | `169,116 B` |

\* `PosixAutoRegExec` recorded a one-off `248 B` runtime/JIT bookkeeping allocation across `16,777,216` operations (`0.0000148 B/op`); `ReleaseAllocationMatrixTests` verifies that `ws.Capacity` and `ws.ReservedBytes` remain completely unchanged across 4,096 warmed iterations.

### Workspace Capacity & Resource Stability (`ReleaseAllocationMatrixTests`)

`test/GregsEx.Tests/Performance/Release/ReleaseAllocationMatrixTests.cs` verifies across all 6 profiles (`RegularCore`, `ECMAScript`, `XSD`, `XPath`, `POSIX`, `PCRE2`) and all applicable execution modes (`Interpreted`, `Auto`, `Compile()`, `[GeneratedPattern]`, plus nonregular backreference fallback `([a-z]+)=\1`):

- Exact semantic checksum agreement across 4,096 warmed iterations of `IsMatch` (match and no-match), `Find`, and `FindCaptures`.
- Zero growth in `ws.Capacity`, `ws.ReservedBytes`, or `pattern.RetainedBytes` during warmed matching.
- Visible retained-memory accounting for compiled and source-generated plans (`auto.RetainedBytes > interp.RetainedBytes`, `compiled.RetainedBytes > interp.RetainedBytes`, `generated.RetainedBytes == interp.RetainedBytes + 4096`).

---

## 2. Release Manifest Cross-Engine Summary (`ComparisonBenchmarks`)

Means in microseconds (`µs`) across the 14 pinned release-manifest cases (`docs/benchmarks/release-manifest.json`; `-` = profile does not support the pattern):

These are not GregsEx source-generation measurements. The current harness's
unqualified GregsEx engine IDs do not request source generation or explicit
runtime compilation; `gregsex-core-auto` and `gregsex-core-compiled` are separate
opt-in configurations. This historical summary is not a complete execution-tier
matrix; section 4 verifies the backend for every new GregsEx row.

| Case : Operation | `gregsex-core` | `gregsex-ecma` | `gregsex-posix` | `gregsex-pcre2` | `bcl-interp` | `bcl-compiled` | `bcl-sourcegen` | `jint-quickjs` |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `alt-names-64k:count` | `6.39` | `1.91` | `2.89` | `6.79` | `4.83` | `1.91` | `1.91` | `849` |
| `anc-end-64k:is_match` | `2.15` | `0.004` | `1.13` | `0.092` | `0.075` | `0.032` | `0.008` | `4.14` |
| `anc-full-4k:is_match` | `0.324` | `0.171` | `0.268` | `0.252` | `3.12` | `0.17` | `0.17` | `33.5` |
| `bt-dotstar-4k:is_match` | `0.312` | `0.168` | `-` | `0.238` | `55.4` | `0.35` | `0.35` | `1,674,146` |
| `bt-overlap-alt:is_match` | `0.029` | `0.015` | `-` | `0.089` | `582,040` | `190,079` | `197,650` | `151,325` |
| `cap-date-log-64k:captures` | `57.8` | `42.1` | `84.1` | `126.7` | `164` | `114` | `105` | `874` |
| `cap-kv-64k:captures` | `154.2` | `135.7` | `236.4` | `341.6` | `478` | `307` | `281` | `793` |
| `cls-ing-64k:count` | `89.6` | `69.1` | `117.4` | `157.9` | `1,087` | `496` | `340` | `1,135` |
| `lit-dense-64k:count` | `44.6` | `39.7` | `57.3` | `88.8` | `40.1` | `17.1` | `15.8` | `54.9` |
| `lit-miss-64k:is_match` | `6.56` | `2.47` | `2.56` | `4.35` | `4.12` | `2.81` | `3.18` | `4.10` |
| `lit-rare-64k:find` | `5.90` | `2.58` | `2.44` | `4.43` | `3.83` | `2.46` | `2.81` | `3.73` |
| `lrg-alt-1m:count` | `67.8` | `29.7` | `43.7` | `79.3` | `75.4` | `28.6` | `32.6` | `13,216` |
| `lrg-lit-1m:find` | `103.6` | `42.0` | `42.4` | `67.8` | `64.8` | `43.0` | `49.8` | `64.4` |
| `uni-letters-64k:count` | `-` | `158.7` | `-` | `1,200` | `599` | `331` | `237` | `1,200` |

- **Allocations (`MemoryDiagnoser`):** Every `GregsEx` row allocates `0 B`. `System.Text.RegularExpressions` allocates `750,288 B` on `cap-date-log-64k` and `1,843,632 B` on `cap-kv-64k` (due to `Match` / `GroupCollection` heap materialization) and `0 B` on the non-capture cases. `jint-quickjs` allocates `144 B` to `1,614,432 B` across cases.

---

## 3. Compiled & Source-Generated Kernels (`CompiledShapeBenchmarks`)

For general multi-branch patterns that are not served by a specialized `StartScan` / flavor direct plan, lowered kernels (`RegexExecutionMode.Auto` under untrimmed JIT, explicit `RegexPattern.Compile()`, and build-time `[GeneratedPattern]`) accelerate matching by `7x–14x` over the reference interpreter with `0 B` steady-state allocation:

| Shape (64 KiB `Count`, `µs`) | Reference / Semantic | `Compile()` (FEC) | `[GeneratedPattern]` | `bcl-interpreted` | `bcl-compiled` | `jint-quickjs` |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `(the\|and\|of) ([a-z]+)` | `1,846` | `144` | **`114`** | `712` | `128` | `949` |
| `[a-z]+(ing\|ed)` | `4,187` | `626` | **`609`** | `2,540` | `514` | `1,314` |
| `(ERROR\|WARN) ([a-z]+)` | `839` | `63.5` | **`64.1`** | `126` | `54.6` | `764` |
| `(wi\|ri)n[a-z]*` | `679` | `58.6` | **`62.7`** | `295` | `85.0` | `781` |
| `t[a-z]*e` | `757` | `94.9` | **`80.8`** | `238` | `103` | `118` |
| `(a\|b)*c` | `4.46` | `4.35` | **`4.28`** | `338` | `53.2` | `965` |
| `([a-z]+)=([a-z0-9]+)` (direct plan) | **`35.1`** | declined (fast path) | **`36.4`** (`GREGEX003`) | `69.1` | `30.7` | `2,152` |

---

## 4. Explicit Execution-Tier README Comparison (`ReadmeBenchmarks`)

Measured on 16 October 2026 UTC using
[`ReadmeBenchmarks`](../../benchmarks/GregsEx.Benchmarks/Release/ReadmeBenchmarks.cs).
This is a matching-only microbenchmark, not release-wide speed qualification.

### Methodology and bounds

- **Host/runtime:** Apple M4 Pro, macOS 15.8.1, Arm64 RyuJIT, .NET 10.0.11,
  SDK 10.0.400, BenchmarkDotNet 0.15.8, concurrent workstation GC.
- **Job:** `[MemoryDiagnoser]`, `[ShortRunJob]`, one launch, three warmups,
  three measured iterations, target iteration time 200 ms. Runtime defaults
  were used; no tiering/PGO overrides were set.
- **Actual conditions:** shared host with other active applications; load
  averages before `5.37 / 5.48 / 5.31`, after `7.49 / 6.09 / 5.58`.
  No recorded thermal/performance warnings. High-priority scheduling was denied.
  These timings are informational, not evidence of close-case rankings or
  universal improvements.
- **Pattern/inputs:** `((a)|b)+c`, no flags; short subject `bbaaac` (6 units),
  long subject 256 `a` characters followed by `c` (257 units), negative setup
  witness `zzzzzz`. POSIX ERE uses ASCII bytes; other profiles use UTF-16.
  Selectable Unicode profiles use 16.0.0; PCRE2 keeps its own 10.46 data.
- **Tiers:** interpreted explicitly requests `RegexExecutionMode.Interpreted`;
  runtime-compiled calls `RegexPattern.Compile()`; source-generated calls
  `[GeneratedPattern]` factories. All actual GregsEx backends, profiles,
  data IDs, group counts and input kinds are checked in setup. No `Auto`
  selection, interpreted generated fallback, or Jint comparator is used.
  BCL uses `RegexOptions.None`, `RegexOptions.Compiled`, or `[GeneratedRegex]`,
  all with a 20-second match timeout, not `RegexOptions.ECMAScript`.
- **Shared semantics:** every positive subject is a whole match, so XSD
  membership and other profiles' search return the same boolean. Captures
  read the final participating offsets of group zero and both subgroups:
  `(0, 6)`, `(4, 1)`, `(4, 1)`, checksum `19`. This witness does not assert
  identical capture clearing/history or selection rules for other inputs.
- **Preparation:** pattern construction, code generation, owned strings/bytes,
  capture buffers, and 64 warmup calls are outside measurement. Each GregsEx
  case has a private workspace with `AllowGrowth=false`; cleanup asserts
  unchanged capacity, reserved bytes and retained plan bytes. All engines
  use a prepared delegate adapter in the measured method.
- **Exclusions:** POSIX has no runtime-compiled or source-generated tier.
  XSD grouping is non-capturing, so its three capture rows are excluded.
  There are **54 completed cases**, each with three measured iterations,
  across 19 engine/tier scenarios.
- **Warning:** the PCRE2 interpreted long-match case had a minimum observed
  iteration of 46.051 ms, below BDN's recommended 100 ms. Its result and
  positive allocation total are retained, not silently dropped or rerun.

Command:

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- \
  --filter "*ReadmeBenchmarks*" \
  --exporters json --iterationTime 200 --warmupCount 3 --iterationCount 3 --launchCount 1
```

Input SHA256 values, over the exact native representations:

| Input | UTF-16 little-endian | ASCII bytes (POSIX) |
| --- | --- | --- |
| Pattern | `621dfbc1fb29439d4a32fe69e4002da34f22af977a4bbf1187088409086fc090` | `8fab4e3a844b49a9e1aac9c4395e548e606625144abb0fd17214b61933d52491` |
| Short subject | `85cd78948da2e23408c4782130b8bf752966b9f09c702b092156a0cabe811b7f` | `16f44497121148552e5a6cca2deb44aac54216d86a953136fabbebdb6ac56d1d` |
| Long subject | `2090aa911d9616e0c4beaa3dbb3c49b9812b121e501fc8d09029fe5d49696dd0` | `2a9358698b3c242289ac7508de4c45df7811d1460f6c67108c4ed742d4a40210` |

### Complete measured results

Times are nanoseconds per operation; Error is half the 99.9% confidence interval.
Allocated B/op below is the raw MemoryDiagnoser byte total divided by its operation
count, preserving positive totals that the standard BDN column rounds to zero.
Capture extraction uses GregsEx caller-owned buffers versus BCL `Match`/group
materialization; it is an API/output comparison, not a universal allocation claim.

| Workload | Flavor / backend | Mean (ns) | Error (ns) | StdDev (ns) | Allocated B/op |
| --- | --- | ---: | ---: | ---: | ---: |
| ShortIsMatch | BCL / Compiled | 58.13 | 54.63 | 2.99 | 0 |
| LongIsMatch | BCL / Compiled | 1,858.44 | 204.94 | 11.23 | 0 |
| ShortCaptures | BCL / Compiled | 133.96 | 46.55 | 2.55 | 768 |
| ShortIsMatch | BCL / Interpreted | 210.07 | 71.85 | 3.94 | 0 |
| LongIsMatch | BCL / Interpreted | 5,311.35 | 170.48 | 9.34 | 0 |
| ShortCaptures | BCL / Interpreted | 279.66 | 109.05 | 5.98 | 768 |
| ShortIsMatch | BCL / SourceGenerated | 39.11 | 19.36 | 1.06 | 0 |
| LongIsMatch | BCL / SourceGenerated | 1,723.00 | 555.42 | 30.44 | 0 |
| ShortCaptures | BCL / SourceGenerated | 131.09 | 363.05 | 19.90 | 768 |
| ShortIsMatch | ECMAScript / Compiled | 64.06 | 3.51 | 0.19 | 0 |
| LongIsMatch | ECMAScript / Compiled | 298.85 | 110.69 | 6.07 | 0 |
| ShortCaptures | ECMAScript / Compiled | 100.75 | 18.58 | 1.02 | 0 |
| ShortIsMatch | ECMAScript / Interpreted | 259.97 | 118.96 | 6.52 | 0 |
| LongIsMatch | ECMAScript / Interpreted | 6,239.03 | 263.93 | 14.47 | 0 |
| ShortCaptures | ECMAScript / Interpreted | 341.79 | 10.75 | 0.59 | 0 |
| ShortIsMatch | ECMAScript / SourceGenerated | 62.18 | 13.86 | 0.76 | 0 |
| LongIsMatch | ECMAScript / SourceGenerated | 264.64 | 142.58 | 7.82 | 0 |
| ShortCaptures | ECMAScript / SourceGenerated | 92.58 | 4.24 | 0.23 | 0 |
| ShortIsMatch | PCRE2 / Compiled | 50.83 | 2.86 | 0.16 | 0 |
| LongIsMatch | PCRE2 / Compiled | 274.26 | 60.81 | 3.33 | 0 |
| ShortCaptures | PCRE2 / Compiled | 74.42 | 3.40 | 0.19 | 0 |
| ShortIsMatch | PCRE2 / Interpreted | 8,284.42 | 896.20 | 49.12 | 0.009579 |
| LongIsMatch | PCRE2 / Interpreted | 362,090.98 | 42,645.72 | 2,337.56 | 145.875 |
| ShortCaptures | PCRE2 / Interpreted | 8,349.68 | 161.21 | 8.84 | 0.010067 |
| ShortIsMatch | PCRE2 / SourceGenerated | 39.15 | 2.47 | 0.14 | 0 |
| LongIsMatch | PCRE2 / SourceGenerated | 230.50 | 21.39 | 1.17 | 0 |
| ShortCaptures | PCRE2 / SourceGenerated | 62.52 | 37.14 | 2.04 | 0 |
| ShortIsMatch | POSIX / Interpreted | 660.90 | 14.66 | 0.80 | 0.000821 |
| LongIsMatch | POSIX / Interpreted | 23,929.93 | 2,087.36 | 114.42 | 0.02958 |
| ShortCaptures | POSIX / Interpreted | 800.02 | 176.99 | 9.70 | 0.001027 |
| ShortIsMatch | RegularCore / Compiled | 58.85 | 18.46 | 1.01 | 0 |
| LongIsMatch | RegularCore / Compiled | 333.98 | 13.86 | 0.76 | 0 |
| ShortCaptures | RegularCore / Compiled | 99.87 | 13.56 | 0.74 | 0 |
| ShortIsMatch | RegularCore / Interpreted | 750.08 | 222.84 | 12.21 | 0 |
| LongIsMatch | RegularCore / Interpreted | 32,202.36 | 5,313.63 | 291.26 | 0 |
| ShortCaptures | RegularCore / Interpreted | 897.88 | 172.52 | 9.46 | 0 |
| ShortIsMatch | RegularCore / SourceGenerated | 57.95 | 9.12 | 0.50 | 0 |
| LongIsMatch | RegularCore / SourceGenerated | 273.91 | 56.68 | 3.11 | 0 |
| ShortCaptures | RegularCore / SourceGenerated | 99.68 | 7.99 | 0.44 | 0 |
| ShortIsMatch | XPath / Compiled | 38.62 | 26.61 | 1.46 | 0 |
| LongIsMatch | XPath / Compiled | 369.85 | 88.77 | 4.87 | 0 |
| ShortCaptures | XPath / Compiled | 59.53 | 10.08 | 0.55 | 0 |
| ShortIsMatch | XPath / Interpreted | 410.36 | 70.87 | 3.88 | 0.000491 |
| LongIsMatch | XPath / Interpreted | 12,691.39 | 3,331.29 | 182.60 | 0.016213 |
| ShortCaptures | XPath / Interpreted | 412.37 | 128.00 | 7.02 | 0.000503 |
| ShortIsMatch | XPath / SourceGenerated | 38.69 | 1.00 | 0.06 | 0 |
| LongIsMatch | XPath / SourceGenerated | 335.08 | 126.58 | 6.94 | 0 |
| ShortCaptures | XPath / SourceGenerated | 62.74 | 6.92 | 0.38 | 0 |
| ShortIsMatch | XSD / Compiled | 37.42 | 6.31 | 0.35 | 0 |
| LongIsMatch | XSD / Compiled | 397.94 | 8.87 | 0.49 | 0 |
| ShortIsMatch | XSD / Interpreted | 201.22 | 30.92 | 1.69 | 0.000258 |
| LongIsMatch | XSD / Interpreted | 7,132.51 | 62.63 | 3.43 | 0.008817 |
| ShortIsMatch | XSD / SourceGenerated | 37.52 | 6.22 | 0.34 | 0 |
| LongIsMatch | XSD / SourceGenerated | 328.69 | 4.73 | 0.26 | 0 |

### Exact positive allocation totals

All other new cases recorded zero raw allocated bytes. Positive totals are
reported without attributing an unproven origin or rerunning to obtain zero.
In particular, the PCRE2 interpreted long-match case recorded 18,672 bytes over
128 operations (145.875 B/op), despite unchanged reserved workspace storage.

| Workload | Flavor / backend | Raw bytes | Operations |
| --- | --- | ---: | ---: |
| ShortCaptures | BCL / Compiled | 1,199,210,496 | 1,561,472 |
| ShortCaptures | BCL / Interpreted | 533,090,304 | 694,128 |
| ShortCaptures | BCL / SourceGenerated | 1,179,156,480 | 1,535,360 |
| ShortIsMatch | PCRE2 / Interpreted | 240 | 25,056 |
| LongIsMatch | PCRE2 / Interpreted | 18,672 | 128 |
| ShortCaptures | PCRE2 / Interpreted | 240 | 23,840 |
| ShortIsMatch | POSIX / Interpreted | 248 | 301,888 |
| LongIsMatch | POSIX / Interpreted | 248 | 8,384 |
| ShortCaptures | POSIX / Interpreted | 248 | 241,376 |
| ShortIsMatch | XPath / Interpreted | 248 | 505,488 |
| LongIsMatch | XPath / Interpreted | 248 | 15,296 |
| ShortCaptures | XPath / Interpreted | 248 | 492,912 |
| ShortIsMatch | XSD / Interpreted | 248 | 962,864 |
| LongIsMatch | XSD / Interpreted | 248 | 28,128 |

### Stable reserved storage and validation

The following conservative API-reported byte counts were identical across
the corresponding measured workloads. They are not process RSS, native-code
size, or independently additive shared-table ownership measurements.

| Flavor / backend | Workspace reserved bytes | Pattern retained bytes |
| --- | ---: | ---: |
| ECMAScript / Compiled | 1,720,952 | 383,102 |
| ECMAScript / Interpreted | 1,720,952 | 376,126 |
| ECMAScript / SourceGenerated | 1,720,952 | 380,478 |
| PCRE2 / Compiled | 1,575,496 | 9,730 |
| PCRE2 / Interpreted | 1,575,496 | 3,394 |
| PCRE2 / SourceGenerated | 1,575,496 | 7,746 |
| POSIX / Interpreted | 1,737,024 | 1,292 |
| RegularCore / Compiled | 51,104 | 8,178 |
| RegularCore / Interpreted | 2,120 | 1,202 |
| RegularCore / SourceGenerated | 51,104 | 5,554 |
| XPath / Compiled | 1,180,152 | 127,392 |
| XPath / Interpreted | 1,180,152 | 121,056 |
| XPath / SourceGenerated | 1,180,152 | 125,408 |
| XSD / Compiled | 49,456 | 125,032 |
| XSD / Interpreted | 16,716 | 120,936 |
| XSD / SourceGenerated | 49,456 | 125,288 |

Preparation independently exercised all 19 scenarios for 1,024 repetitions,
checking complete short captures and fixed storage. The full regression suite
passed 24,461 tests, with no failures or skips. An isolated cold-start run of
`ReleaseAllocationMatrixTests` failed the existing ECMAScript assertion
`auto.RetainedBytes > interp.RetainedBytes`; that separate accounting/order
issue is not fixed or claimed passing by this benchmark change.

The tables above preserve measured summaries, exact allocation totals and
storage accounting. Use the command above to generate fresh BDN reports.

### Targeted PCRE2 interpreted confirmation

The unexpectedly high interpreted PCRE2 results were rerun separately on
16 October 2026 UTC with the same benchmark source, pattern, inputs, runtime,
and fixed-capacity semantic/storage checks. This was a timing-stability
investigation, not a rerun to force zero allocations. Original results and
the 54-case tables above are preserved without mixing job settings.

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks --no-build -- \
  --filter "*ReadmeBenchmarks*PCRE2:Interpreted*" \
  --exporters json --iterationTime 1000 --warmupCount 6 --iterationCount 10 --launchCount 2
```

Two fresh launches, six warmups and ten measured iterations per launch used
a 1-second target iteration time. The host remained shared and busy: load
averages before `4.20 / 5.69 / 5.81`, after `7.27 / 7.00 / 6.38`.
High-priority scheduling was again denied. The original minimum-iteration
warning did not recur; BDN instead warned that short-match timings were bimodal.
Its default outlier processing retained 19/18/18 observations from the configured
20 per case, and reported a removed 9.08-microsecond upper capture outlier.

Times below are microseconds per operation, not the nanoseconds used above.
Error is half the 99.9% confidence interval.

| Workload | Original mean (us) | Rerun mean (us) | Rerun error (us) | Rerun StdDev (us) | Retained samples |
| --- | ---: | ---: | ---: | ---: | ---: |
| ShortIsMatch | 8.284 | 8.104 | 0.227 | 0.252 | 19 |
| LongIsMatch | 362.091 | 245.356 | 6.640 | 7.105 | 18 |
| ShortCaptures | 8.350 | 8.266 | 0.182 | 0.195 | 18 |

The short-match and capture costs reproduced within about 2.2% and 1.0% of
their original means. The long-match mean was 32.2% lower, so its initial
estimate is sensitive to run configuration and/or host conditions. These
measurements do not isolate the cause of that change, or explain the
interpreted-versus-compiled gap; profiling is the next diagnostic step.

The BDN allocation column rounded these confirmation cases to zero, but every
launch recorded a positive raw total:

| Workload | Launch | Raw bytes | Operations |
| --- | ---: | ---: | ---: |
| ShortIsMatch | 1 | 240 | 126,240 |
| ShortIsMatch | 2 | 240 | 121,600 |
| LongIsMatch | 1 | 240 | 4,208 |
| LongIsMatch | 2 | 240 | 4,016 |
| ShortCaptures | 1 | 240 | 126,000 |
| ShortCaptures | 2 | 240 | 120,944 |

No reserved-workspace growth was observed. Positive totals are retained
without attributing an unproven origin. The tables above preserve both runs'
summaries and exact allocation totals; the command reproduces the targeted
measurement configuration.
