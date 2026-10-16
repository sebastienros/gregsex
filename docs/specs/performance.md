# Performance specification

Target: .NET 10. The [research note](../research/engine-techniques.md) records sources and candidate techniques; this specification defines the requirements for adopting them.

## Contracts and accounting

The normal execution mode is interpreted instructions/tables and must work under NativeAOT, trimming and disabled dynamic code. The same GregsEx assembly includes an optional dynamic compiler behind the annotated Compile() API; Auto may use it behind runtime feature guards. It may specialize the same validated plan when runtime capabilities permit it. Its result semantics, cancellation, limits and allocation guarantees are identical. Explicit unsupported-mode behavior and any automatic fallback must be documented; requesting compilation never authorizes dropping features. No source generator is required.

The selected compiler is FastExpressionCompiler.Internal.src, with bounded expression-tree lowering and direct strongly typed span delegates. Source inclusion removes the external compiler DLL; it does not remove tree construction, dynamic-code or metadata costs. Compilation allocations are allowed and measured; warmed matching still forbids input copies, boxing, DynamicInvoke and per-call closure/argument-array allocation. Keep unsupported-tree fallback under GregsEx control by disabling FEC's Expression.Compile fallback. Audit FEC's trimming restrictions before promotion as described in [the compilation specification](compilation.md).

Plans are immutable and shareable. Workspace is mutable and exclusive to one active operation. Callers may retain or pool prepared workspace; concurrent calls use distinct workspaces. Workspace admission determines the maximum input window, capture storage, VM frames and cache capacities before entering the strict search path. Reuse does not permit leaking state between patterns or invocations. Convenience APIs can own/rent workspace but must disclose their separate allocation behavior.

Measure warmed boolean search, first-match range, span-based iteration, and caller-buffer capture offsets using prepared plans, reusable workspaces, and sufficient output capacity. Include misses, empty matches, Unicode, and accelerator fallback. Reserved buffers remain stable; caches may fill or reset in place but must not grow unexpectedly. Controlled managed allocations are acceptable when bounded and justified by performance; absolute zero is a preferred optimization, not a universal requirement.

Pattern parsing/compilation, initial static initialization, workspace creation/growth, optional code generation, formatting, replacement-string production, materialized match/capture objects, enumerable boxing, diagnostics and exceptional resource-limit paths are measured separately. These exclusions are not permission to hide allocations in the strict APIs: capacity exhaustion returns the documented status or follows an explicitly allocating convenience path chosen by the caller. Native allocations and retained memory must be reported alongside managed allocations where applicable.

Use offsets/lengths and spans over the original input; avoid substring creation, temporary normalized strings, closures, LINQ, boxed enumerators, per-match arrays and repeated buffer rents. Captures must distinguish unset from empty without allocating. Capture histories, if exposed by a flavor, need separately prepared storage and a bounded capacity contract. Do not stackalloc an unbounded size derived from a pattern/input. A small stack fast path needs a documented limit and an admitted workspace fallback.

## Correctness before acceleration

Every plan records the flavor, flags, Unicode version, text domain, assertion rules, match/capture ordering and supported features. Dispatch cannot infer these from a pattern looking similar to another flavor. Unsupported syntax is a diagnostic; execution budget exhaustion is an explicit outcome; neither is no-match.

Accelerators require a semantics proof/eligibility predicate and differential tests against the flavor reference executor. Witnesses include alternative priority, greediness, captures in repeats, empty matches, final newlines, nonzero search bounds, word-boundary context, surrogate pairs and lone surrogates, case equivalences, POSIX ties and nonregular capture dependence. Tests force each engine and force each fallback at the same input bounds. Compare overall range, every participating capture, unset/empty state, histories where applicable, iteration progress and error/status outcomes.

Lazy DFA can quit or clear a bounded cache; visited-state backtracking can be ineligible because state × window capacity is too large; one-pass or full DFA construction can fail eligibility/build limits. Each is an optimization decision followed by a compatible reference path, not a changed language. Budgets cover the entire operation, including prefilters, forward/reverse passes, capture recovery and restarted searches. Optimization retries are bounded. Nonregular VM execution has no promised O(mn) time bound; regular single-search bounds must be stated with their pattern-size and capture/tag factors. Enumeration complexity is reported separately: repeated suffix searches can be quadratic even when one search is bounded.

## Benchmark matrix

Benchmarks have stable case IDs and persisted patterns/flags/input hashes or seeded generators. Real corpora record provenance/license and deterministic extraction. Test cases and semantic assertions are prepared outside the measured method. Scale families vary one factor at a time and include small inputs where dispatch dominates.

| Family | Representative inputs and variants | Required measures |
| --- | --- |
| Literal and prefilter | Exact literal, alternatives, rare/dense candidate prefixes, candidate absent, suffix/inner literals; hit at start/middle/end | Boolean/range/count, prefilter enabled/disabled, search throughput and short-input latency |
| Anchored capture | Date/key-value/token records, disjoint and ambiguous transitions; 0/1/8/32 groups | Capture offsets, one-pass eligibility, recovery pass cost |
| Unicode/classes | ASCII, mixed scripts, supplementary scalars, surrogate witnesses, XML name/property classes, simple-fold edge cases | Classification cost, compiled table/NFA size, fallback cost |
| Selection policy | Overlapping alternatives, greedy/lazy repeats, POSIX overall/subexpression ties, unset versus empty | Semantically equal range/capture workloads only |
| Adversarial regular | Nested/repeated alternations, late near misses, large bounded repeats, cache churn, long no-match input | Scaling, memory ceiling, regular executor work, cache fallback |
| Nonregular | Backreference/recursion/lookaround families enabled by the flavor; successful and late failing inputs | Bounded VM steps/depth/memory, timeout/cancellation and limit outcomes |
| Lifecycle | Tiny/large patterns, Unicode expansion, plan reuse counts, workspace creation/growth, optional compilation | Compile latency/bytes, first use, break-even search count, retained memory |
| Expression compiler | Bounded plan-to-tree construction, FEC compilation, first invocation/JIT, warmed char/byte kernels, unsupported trees | Separate phase and end-to-end latency/allocations, retained tree/delegate/native-code memory, break-even reuse count; equivalent Expression.Compile comparator only in benchmarks |
| Reuse/concurrency | Private warmed workspace; deliberate shared-plan concurrent searches with separate workspaces | Steady allocation, cache reuse, synchronization overhead, aggregate throughput |
| Enumeration/output | Dense and sparse matches, empty-match progress, capture offsets/history capacities | Count and output-normalized time; allocation scope stated explicitly |

Input sizes start with 0, 16, 256, 4096 and 1 Mi UTF-16 code units where the case is meaningful; analogous byte lengths are labeled separately. Capture and pattern counts are independent parameters. Do not convert UTF-16 units to UTF-8 bytes silently when reporting GB/s. Add larger scaling inputs only under an agreed time/memory limit.

Compare only against System.Text.RegularExpressions (interpreted, compiled and `[GeneratedRegex]` source-generated) and Jint's QuickJS-derived regex engine, as BenchmarkDotNet cases on the shared semantic subset (Jint only for the ECMAScript cohort). Native/external comparators (Rust, Go, PCRE2 JIT, V8, RE2/Vectorscan, NonBacktracking) are out of scope. A faster boolean answer cannot be compared to full captures as the same task. Unsupported cases are labeled and excluded from pairwise summaries, with exclusions visible.

## Reproducible measurements

All BenchmarkDotNet classes use `[MemoryDiagnoser]` and `[ShortRunJob]`.
Use its existing runner; new project-owned CLIs use System.CommandLine.

Prepare Release builds, deterministic inputs, semantic assertions, plans, and
workspace capacity before timing. Warm every admitted path outside measured
methods. Consume result counts or checksums to prevent dead-code elimination.
Separate lifecycle and owned-output benchmarks from steady matching.

Record the code revision, exact commands and case filters, SDK/runtime/BDN and
comparator versions, OS/CPU/architecture/ISA, power/thermal/load conditions,
GC/tiering/PGO configuration, input identities, and capacity policy. Use bounded
sequences on a quiet host; preparation and analysis are outside measurement.
Do not claim exclusive host conditions without establishing them. Preserve
completed results if a later measurement or analysis fails.

BenchmarkDotNet MemoryDiagnoser is the sole allocation authority. Retain
unrounded allocated-byte numerators and operation denominators, including
positive values. Assess costs with stable reserved capacity, bounded ownership,
semantic reuse, and separately measured setup/growth/output. Do not round small
positive totals to zero, invent thresholds, attribute an unproven origin, or
repeat warmup merely to force zero. Uncontrolled growth, violated limits, and
meaningful regressions require diagnosis.

Retain raw BDN artifacts and uncertainty with the measured revision outside Git,
for example under ignored `artifacts/`. Use ns/op for short operations and
explicit byte/character throughput units for scans; include B/op, plan/workspace
retention, and compilation costs. Compare identical participating cases and
publish regressions and exclusions alongside improvements. Noisy ShortRun
estimates do not establish a winner.

## Baselines and promotion thresholds

[release-manifest.json](../benchmarks/release-manifest.json) pins deterministic
corpora, cohorts, comparators, cases, and thresholds. Against the interpreted
baseline for the same case/cohort, a candidate wins only below
`min(0.90, 1 - 2 * noise)` and regresses above `max(1.05, 1 + 2 * noise)`,
where noise is baseline relative dispersion. Between those bounds is
informational. Never promote an unmeasured optimization or claim universal
superiority.

Use BCL interpreted/compiled/source-generated comparators and Jint's
QuickJS-derived engine on shared ECMAScript semantics. Missing comparators are
gaps, not placeholders. FEC's upstream Expression.Compile comparisons do not
establish GregsEx speedups: measure tree construction, delegate compilation,
first invocation/JIT, warmed matching, retained memory, and break-even reuse
separately. Auto selection must account for amortization.

## Acceptance IDs

| ID | Requirement |
| --- | --- |
| PERF-001 | Interpreted/table execution under NativeAOT, trimming and disabled dynamic code; published executable smoke evidence |
| PERF-002 | Immutable plan plus exclusive prepared workspace and explicit capacity/result semantics |
| PERF-003 | Controlled, stable managed allocation costs in scoped warmed APIs, BenchmarkDotNet MemoryDiagnoser authoritative with raw bytes/operation counts and resource/capacity bounds; no absolute-zero chase or new custom counters; expand cases as flavors arrive |
| PERF-004 | Compilation/state/table/workspace/VM limits use checked arithmetic and explicit exhaustion; whole-operation retry/step accounting |
| PERF-005 | Flavor-compatible accelerated/reference differential witnesses and forced fallback coverage |
| PERF-006 | Literal extraction/prefilter soundness, dense-candidate regression cases and safe disable path |
| PERF-007 | One-pass and bounded regular backtracker eligibility/admission with compatible fallback |
| PERF-008 | Lazy/reverse DFA bounds, cache churn, assertion-context-preserving capture recovery and explicit fallback |
| PERF-009 | POSIX tag precedence oracle precedes tagged acceleration; overall and every submatch tie witnesses |
| PERF-010 | .NET table/SearchValues/hash candidates benchmarked; static-data lowering verified and hash collisions sequence-checked |
| PERF-011 | Every BDN class has MemoryDiagnoser/ShortRunJob; new project CLIs use System.CommandLine |
| PERF-012 | Versioned benchmark manifests/raw artifacts, semantic pairwise comparisons and host/environment metadata |
| PERF-013 | Prepared bounded measurement sequences, recorded host conditions, cleanup, and preservation of completed results |
| PERF-014 | Relative speed informational pending measured baseline; no universal fastest claim or unmeasured promotion |
| PERF-015 | Annotated Compile() and guarded Auto using internal FEC source preserve outcomes/limits/allocation contract; bounded expression lowering, direct span delegates and disabled Expression.Compile fallback are verified; measure tree creation, delegate compilation, first use, warm execution, retained memory and break-even costs separately |
| PERF-016 | Cold setup, native/retained memory and convenience/output allocation measured separately from warmed scope |
