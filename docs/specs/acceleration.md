# Acceleration and backend contracts

Accelerators preserve each profile's admitted language, captures, diagnostics,
and resource behavior. They change execution strategy, not semantics.

## Modes and backend reporting

| Mode | Selection |
| --- | --- |
| `Interpreted` | Reference flavor executors, without semantic accelerators |
| `Auto` | Eligible literal, one-pass, bounded-backtracking, lazy-DFA, and table strategies; optional guarded compilation |

`RegexExecutionBackend` has two values. Non-emitting accelerators report
`Interpreted`; runtime-compiled and source-generated kernels report `Compiled`.
Internal `AcceleratorKind` identifies the selected strategy for tests.

`IAcceleratedExecutor : IRegularExecutor` exposes `Kind` and `Reference`.
`AcceleratorAdmission` and `AcceleratorFallbackReason` record eligibility,
retained storage, and rejection reasons. Known ineligibility is not an error.

Accelerated scans charge through `AcceleratorWork.TryReserveScan` in chunks of
at most `ScanChunkUnits` (1024), polling cancellation and deadlines. All work
uses the original `RunState`. Cache exhaustion or unsupported context can retry
the original input/start/operation on `Reference` without resetting its budget.
Limits, cancellation, timeouts, capacity failures, and invalid input never become
`NoMatch`. Test forced acceleration, reference execution, fallback, and limits.

## Compiled backend

The public [compilation boundary](compilation.md) is strict; `Compile()` does not
silently return interpretation. A null result from
`CompileFast<TDelegate>(ifFastFailedReturnNull: true)` is known ineligibility;
defective trees, invalid IL, and resource exhaustion remain failures.

`Utf16SearchKernel` and `ByteSearchKernel` use direct `ReadOnlySpan<char|byte>`,
`startAt`, `SearchOperation`, `ref RunState`, `Span<RegexCapture>`, and
`out RegexMatchRange` parameters. No DynamicInvoke, object-array dispatch,
boxing, input copies, or captured subject/workspace is permitted. Closures retain
immutable plan data only.

`ICompiledPlanLowering.TryLower` receives `LoweringLimits`. Defaults bound nodes
to 8192, nesting to 64, and estimated code to 256 KiB before FEC runs.
Compiler implementation lives in `Execution/Compiled/` and public entry points
in `Public/Compilation/`.

Auto compilation requires dynamic-code support and the
`GregsEx.RuntimeCompilation.Auto` AppContext switch. Package build assets enable
the switch only for untrimmed JIT applications. Absent means false; trimmed and
NativeAOT consumers keep safe interpreted/accelerated execution.

FEC 5.4.1 is an internal private source dependency. No public FEC API, FEC DLL,
or transitive FEC dependency ships. Its MIT notice is distributed in
`THIRD-PARTY-NOTICES.txt`. `Compile()` is explicitly trim-unsafe, not proof that
arbitrary FEC reflection metadata survives trimming.

Lowering fixtures cover span/ref/out parameters, loops, branches, and helper
calls. Use value-returning helpers rather than ref-returning span indexers in
expression trees. Lower loops with void labels and explicit result variables:
a discarded value-typed loop can produce invalid FEC IL that fails only at first
invocation. Every lowered shape needs a first-invocation test.

## Measurement

[release-manifest.json](../benchmarks/release-manifest.json) pins deterministic
corpora, cohorts, comparators, host metadata, and per-case thresholds. A candidate
wins only below `min(0.90, 1 - 2 * noise)` and regresses above
`max(1.05, 1 + 2 * noise)`, using baseline relative dispersion for that case and
cohort. Results between those bounds are informational.

Use BenchmarkDotNet with `MemoryDiagnoser` and `ShortRunJob`. Compare equivalent
operations against the interpreted baseline, BCL interpreted/compiled/generated
variants, and Jint's QuickJS-derived engine on the shared ECMAScript subset.
Missing comparators are gaps, not fabricated results. Measure tree construction,
delegate compilation, first use, warmed execution, and retained memory separately.
