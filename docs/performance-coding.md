# String and byte performance rules

Required reading before changing pattern parsing, matching, character tables, workspace storage, or host output. Applies to `net10.0`; the [performance specification](specs/performance.md) defines product targets. These are implementation requirements, not claims of measured performance.

## Ownership and allocation boundaries

- Process transient input using `ReadOnlySpan<byte>` / `ReadOnlySpan<char>` and slices. Avoid `Substring`, `Split`, `ToArray`, `ToString`, interpolation, LINQ, boxing, and captured delegates inside token loops when a borrowed view or direct comparison suffices. Any necessary allocation on such a path must have a documented owner/lifetime and allocation measurement.
- Keep byte input as bytes where the profile permits it. Recognize ASCII pattern syntax without allocating token strings. Decode only under the active flavor's Unicode rules. Do not round-trip strings through UTF-8 simply to share a helper.
- Allocate owned pattern text, group names, immutable plans, host output, and diagnostics only at explicit persistence boundaries. Probe name/property tables using borrowed spans; bound all retained tables.
- Preserve native source offsets, whole-subject validation, capture selection, and flavor semantics when bulk-scanning. Fast paths cannot skip required checks or hide limit failures.
- Borrowed input must not survive its matching call or callback. `ReadOnlyMemory<T>` is a retainable view, not ownership of a rented buffer. Returned ranges retain offsets, not the subject or workspace storage.

## SearchValues and bulk scanning

For repeatedly searching a fixed delimiter/character set, evaluate cached `System.Buffers.SearchValues<byte>` or `SearchValues<char>` with span search operations. Create the search object once per immutable set, normally in a `static readonly` field; never per token. Its setup cost is separate from the scan's allocation budget. [SearchValues documentation](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.searchvalues-1?view=net-10.0)

```csharp
private static readonly SearchValues<byte> PatternMetacharacters =
    SearchValues.Create("[](){}.*+?|^$\\"u8);

int stop = remaining.IndexOfAny(PatternMetacharacters);
```

Use state-specific sets and preserve the handling of every character at the returned position. Compare cached SearchValues against a simple loop, `IndexOf`, and small-set `IndexOfAny` overloads. A tiny set or short token may favor the simpler operation. Prefer SearchValues where representative benchmarks demonstrate a benefit; record a measured reason when retaining a simpler path. Do not add custom SIMD before evaluating the runtime primitives.

## Immutable literals and tables

Use UTF-8 literals for fixed byte tokens and constant `ReadOnlySpan<T>` properties for suitable immutable tables:

```csharp
private static ReadOnlySpan<byte> NamedGroupPrefix => "(?<"u8;
private static ReadOnlySpan<byte> AsciiDigits => [0x30, 0x39];
```

These patterns can use compiler-emitted static data instead of managed arrays. Prefer collection expressions for constant tables. A span return type alone does not prove allocation freedom: runtime-valued elements, mutable span results, and different element types/lowerings need inspection. `static readonly` values are not compile-time constants. For .NET 10, verify actual generated code instead of applying the article's older-runtime limitations to all primitive types. Avoid rebuilding constant bytes through `Encoding.GetBytes` or `ToArray`. Check allocation measurements and, for lowering-dependent optimizations, IL/JIT or AOT output. [Andrew Lock's article](https://andrewlock.net/removingbyte-array-allocations-in-dotnet-framework-using-readonlyspan-t/)

## Matching a fixed ASCII vocabulary

For known regex vocabulary (Unicode property aliases, option names, or PCRE2 verbs), compare spans directly. Baseline a length-dispatched `SequenceEqual` implementation before adding hashing. For larger hot vocabularies, evaluate build-time generated constants/matchers with byte and character paths, inspired by [StackExchange.Redis AsciiHash](https://github.com/StackExchange/StackExchange.Redis/blob/main/eng/StackExchange.Redis.Build/AsciiHash.md). Its useful techniques are length dispatch, precomputed token metadata, and hash filtering followed by equality. Adoption of a generator/package is a separate decision; this guide does not add a Redis dependency.

Always verify length and full equality after a hash match. A hash is not token identity. Apply only the active flavor's alias, case-folding, and matching rules. Do not allocate upper/lower-case copies, fold punctuation with an unchecked bit trick, or treat Unicode case-insensitivity as ASCII equivalence.

Use slices of the original pattern or subject. Bound temporary token storage and reject overlong candidates explicitly rather than matching a truncated prefix. Never read beyond the valid span, regardless of padding or vector width.

Specialized fixed-token hashes are not automatically suitable for attacker-controlled intern/dictionary keys. Dynamic tables need collision resistance or bounded worst-case behavior and adversarial tests. Generated matchers stay internal, deterministic, and reproducible; test unknown tokens, shared prefixes, collision candidates, case variants, and non-ASCII input against a simple reference matcher.

## Scratch buffers and output

- Use a small, explicitly bounded stack buffer for short-lived scratch work. Never size stack allocation from unchecked input or repeatedly `stackalloc` inside a loop; include recursion/depth in the stack budget.
- For larger transient buffers, consider pooling with an explicit owner and `try/finally` return. Use only the initialized logical slice. Clear sensitive data when required; do not unnecessarily clear unrelated capacity. Pool misses and growth allocate and must be measured. Immutable plans must never retain returned pooled storage.
- Avoid repeated concatenation and rescanning in replacement and other host output. Write runs directly to bounded output storage; admit growth with checked arithmetic. Materialize a final string once when the API promises an owned string.
- Keep no-error paths free of formatted diagnostics. Benchmark invalid-input paths separately and bound their allocations. Do not introduce an allocation-saving optimization that increases retained memory without reporting that tradeoff.

## Evidence required for hot-path changes

Prefer allocation-free scan/match/slice primitives after documented initialization with bounded preallocated storage, but do not sacrifice performance to force absolute zero. Measure controlled costs using BenchmarkDotNet MemoryDiagnoser. Parsing, compilation, workspace creation/growth, owned output, pool warm-up, and static initialization have separate scopes.

Use the relevant benchmark project and retain the measured revision and results:

1. Compare the straightforward baseline and proposed optimization with identical semantics on the pinned Release .NET 10 SDK/runtime. Consume results; keep fixture generation outside timing.
2. Cover short/long inputs, hits/misses and their positions, candidate density, ASCII/multilingual content, captures, empty matches, nonzero starts, and malformed inputs. Fixed-token tests include maximum length plus one and common prefixes.
3. Report throughput/latency and allocation bytes/op using BenchmarkDotNet memory diagnostics. Measure cold initialization, warm steady state, and retained memory separately. For async work, per-thread allocation counters alone are insufficient.
4. Include an end-to-end representative matching or host-operation workload. Label isolated microbenchmark results accordingly. Record CPU/architecture, runtime settings, corpus, and raw results; validate hardware-sensitive claims on x64 and arm64 before release.
5. Keep independent correctness oracles, semantic checks, and reserved-capacity regressions alongside BDN allocation measurements. Include invalid Unicode, collision/unknown tokens, truncated input, workspace reuse, and culture independence as applicable. Inspect generated code when compiler/JIT lowering is the reason for the claimed benefit.
6. Benchmark classes have `[MemoryDiagnoser]` and `[ShortRunJob]` attributes.
7. Don't commit benchmark result logs and outputs, only summary tables and analysis.

Hot-path changes require relevant allocation measurements. Noisy timing on shared CI is informational; follow the performance specification's repeatability and regression rules. If an optimization does not produce a repeatable benefit, retain the simpler implementation and record the result.

## Provenance and applicability

Originally copied on 11 October 2026 from the clean local checkout of
[sebastienros/xemele](https://github.com/sebastienros/xemele),
`docs/performance-coding.md`, at commit
`2ac5a78d92e1ce0cafca317d3b193a475e8ab8bb`. The remote fetch was unavailable;
this identifies the inspected local source, not the latest upstream version.
The local adaptation now uses regex-specific examples and ownership boundaries
rather than DOM, namespace, serialization, or chunked-parser requirements.

GregsEx's [performance](specs/performance.md) and
[verification](specs/verification.md) specifications govern applicability.
Keep compact verification summaries in Git; preserve raw measurements outside
Git with durable identities and authorized storage, not only ephemeral ignored
output. This guide does not establish a passing benchmark. Record future changes
as an explicit local adaptation or with a source revision; do not overwrite local
guidance from a moving upstream file.
