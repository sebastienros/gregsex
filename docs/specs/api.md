# Public API and ownership contracts

Public APIs use immutable versioned profiles and reusable bounded workspaces. The [RegularCore contract](regular-core.md) specifies core defaults and failure precedence; each [flavor contract](flavors.md) defines its own grammar, representations, and host operations.

## Pattern and profile

Use `GregsEx.RegexPattern` to avoid ambiguity with the BCL's `Regex`. Parsing into an executable plan and optional runtime code generation are separate operations. A versioned profile chooses a dialect. Both execution backends ship in one runtime assembly. See the [compilation boundary](compilation.md).

```csharp
public sealed class RegexPattern
{
    public static RegexPattern Parse(string pattern, RegexProfile profile,
        RegexCompileOptions? options = null);
    public static bool TryParse(ReadOnlySpan<char> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic,
        RegexCompileOptions? options = null);
    public static bool TryParse(ReadOnlySpan<byte> pattern, RegexProfile profile,
        out RegexPattern? result, out RegexDiagnostic diagnostic,
        RegexCompileOptions? options = null);
    public RegexProfile Profile { get; }
    public RegexInputKind InputKind { get; }
    public RegexExecutionBackend ExecutionBackend { get; }
    public int GroupCount { get; } // includes group zero
    [RequiresDynamicCode(
        "Regex compilation requires dynamic code. Use the parsed pattern directly under NativeAOT.")]
    [RequiresUnreferencedCode(
        "FastExpressionCompiler is not supported in trimming scenarios. Use the parsed pattern directly in trimmed applications.")]
    public RegexPattern Compile();
    public RegexWorkspace CreateWorkspace(RegexWorkspaceOptions? options = null);
    public bool IsMatch(ReadOnlySpan<char> input);
    public bool IsMatch(ReadOnlySpan<byte> input);
}
public enum RegexInputKind { Utf16, Utf8, Bytes }
public enum RegexExecutionMode { Interpreted, Auto }
public enum RegexExecutionBackend { Interpreted, Compiled }
```

`RegexProfile` is a closed-by-default immutable descriptor (a public abstract base with restricted construction, or equivalent sealed discriminated representation). Delivered concrete descriptors carry typed flags and explicit version identifiers: `EcmaScriptProfile`, `XsdProfile`, `XPathProfile`, `PosixProfile`, `Pcre2Profile`. They do not use a shared bag of .NET `RegexOptions`. Duplicate incompatible flags and unsupported profile/input-kind combinations fail at compile time. The experimental core exposes the explicitly experimental `RegularCoreProfile` with modes UnicodeScalars, Utf16CodeUnits and RawBytes, fixed ASCII identity GregsEx-ASCII-1, and flags None/DotAll. Its complete subset inventory and representation matrix are in [regular-core.md](regular-core.md).

The concrete profile records carry their own flag enums and version types, plus Unicode identity; XSD carries its XML character/name policy, POSIX its immutable locale provider ID/data digest, and PCRE2 its width/UTF/UCP/newline/BSR configuration. Do not represent these as arbitrary strings checked on every character. Their immutable normalized internal descriptors are built once.

`UnicodeVersion` enumerates supported datasets: `Unicode16_0 = 1600` and
`Unicode17_0 = 1700`. `EcmaScriptProfile`, `XsdProfile`, and `XPathProfile`
expose a get-only `UnicodeVersion` property and accept `unicodeVersion` in
their constructors. ECMAScript/XPath flag-parsing overloads also accept the
version. Undefined enum values throw `ArgumentOutOfRangeException`; there is
no ambient or moving `Latest` selection. Existing constructors, flag parsing,
and all shared presets retain Unicode 16.0 defaults. Existing constructor
signatures remain available for binary callers.

The selected version determines `DataId`: ECMAScript uses
`Unicode-16.0.0+Emoji-16.0` or `Unicode-17.0.0+Emoji-17.0`; XSD/XPath use
`Unicode-16.0.0+XML-1.0-5e` or `Unicode-17.0.0+XML-1.0-5e`.
Programs retain the immutable profile and bind that version's data at
compilation boundaries; warmed matching uses the bound tables. Runtime
compilation and `[GeneratedPattern(..., UnicodeVersion = ...)]` preserve the
same selection, including interpreted generated-pattern fallback. Generated
patterns diagnose unsupported versions/flavor combinations as `GREGEX005`.
PCRE2 10.46, Oniguruma 6.9.10, POSIX and RegularCore do not acquire an arbitrary Unicode override.

Enum members may be marked obsolete individually in future releases, with a
documented migration period before removal. No member is currently obsolete.
Deprecation must not silently redirect an existing version to another dataset.
XSD/XPath convenience functions have overloads taking `UnicodeVersion`; their
existing overloads remain pinned to 16.0.

`RegexProfiles` exposes get-only static properties for shared immutable presets,
each returning its concrete profile type. The base and concrete profile types
do not expose duplicate preset properties.
Constructors remain available for custom configurations; presets do not intern
constructor results or make reference identity a semantic equality contract.
Each preset retains its profile's pinned version/data identities, never an
implicitly advancing "latest" version.

| Concrete return type | `RegexProfiles` properties and exact configuration |
| --- | --- |
| `RegularCoreProfile` | `RegularCoreUnicodeScalars`, `RegularCoreUtf16CodeUnits`, `RegularCoreRawBytes`: corresponding mode, flags `None` |
| `EcmaScriptProfile` | `EcmaScript`: flags `None` (legacy); `EcmaScriptUnicode`: only `Unicode`; `EcmaScriptUnicodeSets`: only `UnicodeSets` |
| `XsdProfile` | `Xsd`: XSD 1.1, Unicode 16.0.0, XML 1.0 Fifth Edition |
| `XPathProfile` | `XPath`: F&O 3.1, flags `None`, Unicode 16.0.0, XML 1.0 Fifth Edition |
| `PosixProfile` | `PosixBasic`, `PosixExtended`: corresponding dialect, flags `None`, `PosixLocale.C` |
| `Pcre2Profile` | `Pcre2Raw8`, `Pcre2Raw16`: corresponding width, flags `None`; `Pcre2Utf8`, `Pcre2Utf16`: corresponding width, only `Utf` (not `Ucp`). All use extra flags `None`, LF newlines, Unicode BSR, default character tables, full optimization and maximum variable lookbehind 255 |
| `OnigurumaProfile` | `Oniguruma`: `ONIG_OPTION_NONE`; `OnigurumaTextMate`: only `CaptureGroup` (`ONIG_OPTION_CAPTURE_GROUP`, TextMate's setting) |

Presets may be shared across patterns and threads. They contain no matcher
session or mutable workspace; those remain independently owned.

```csharp
public sealed record RegexCompileOptions
{
    public RegexCompileOptions(RegexInputKind inputKind = RegexInputKind.Utf16,
        RegexExecutionMode executionMode = RegexExecutionMode.Interpreted,
        RegexCompileLimits? limits = null, RegexMatchLimits? defaultMatchLimits = null);
    public RegexInputKind InputKind { get; }
    public RegexExecutionMode ExecutionMode { get; }
    public RegexCompileLimits Limits { get; }
    public RegexMatchLimits DefaultMatchLimits { get; }
}
public readonly record struct RegexMatchOptions
{
    public CancellationToken CancellationToken { get; init; }
    public TimeSpan? Timeout { get; init; }
    public RegexMatchLimits? Limits { get; init; }
}
public readonly record struct RegexDiagnostic(
    RegexDiagnosticCode Code, int Offset, int Length,
    RegexOffsetUnit LocationUnit, string? Message);
public readonly record struct RegexExecutionFailure(
    RegexExecutionFailureCode Code, int Offset, long WorkConsumed, long RequiredCapacity = 0);
```

These supporting types use validated immutable constructors and finite defaults frozen in RegularCore contract. RegexWorkspaceCapacity has StateSlots/CaptureSlots/ScratchSlots; RegexWorkspaceOptions has InitialCapacity and AllowGrowth, with no-growth as default. Reserved executor partitions use checked byte accounting. RegexCompileLimits separately bounds peak compilation memory and retained plans. RegexMatchOptions remains an init-only operation value validated at invocation. A default `RegexMatchOptions` inherits the pattern's bounded limits. Per-operation timeout/cancellation are checked throughout nested assertions, capture recovery and fallback; retries do not reset them. `RegexMatchLimits` overrides cannot silently exceed the workspace/pattern safety ceiling. Cancellation and deadline expiration have their own statuses and failure codes.

`RegexCompileOptions` includes input kind, mode, compile limits and default match limits. `RegexWorkspaceOptions` includes initial reservation and whether growth is permitted. Both execution backends ship in GregsEx.dll. `Interpreted` is the default; `Parse`, `TryParse`, workspace creation and ordinary matching do not carry `RequiresDynamicCode`. Their default paths never invoke runtime emission.

`Compile()` is the explicit strict opt-in and carries `System.Diagnostics.CodeAnalysis.RequiresDynamicCode`. Under AOT analysis a call produces IL3050 at build/publish time; the attribute is not a runtime warning or a mechanism that makes generated code work under NativeAOT. The method checks `RuntimeFeature.IsDynamicCodeSupported` and `RuntimeFeature.IsDynamicCodeCompiled`, throwing `PlatformNotSupportedException` when either requirement is unavailable. A backend-ineligible plan reports `NotSupportedException`; explicit Compile never silently returns an interpreted plan. It uses the retained compilation limits and returns a new immutable pattern sharing safe immutable data, leaving the original pattern and its workspaces unchanged. An already compiled pattern may return itself. Matching the returned object needs no dynamic-code annotation because generation is complete.

`ExecutionMode.Auto` remains an explicit, AOT-safe preference. It uses direct runtime feature guards and measured eligibility/cost rules, selects interpretation when dynamic compilation is unavailable or ineligible, and records the actual backend in `ExecutionBackend`. It must not catch arbitrary compiler defects or resource-limit failures and disguise them as successful fallback. Annotation analysis is validated on the actual SDK; any needed internal suppression is narrowly scoped to a proven unreachable AOT call and documented. There is no `ExecutionMode.Compiled` value: a method-level attribute cannot issue a diagnostic selectively for one argument value without affecting ordinary callers.

The internal FastExpressionCompiler source package implements optional compilation. Expression-tree and FEC types remain implementation details; callers still provide patterns and spans. Disable FEC's Expression.Compile fallback. An unsupported tree is backend ineligibility under the above strict/Auto rules. FEC 5.4.1 declares trimming restrictions, so Compile also carries RequiresUnreferencedCode and Auto compilation is switch-gated off in trimmed applications; runtime dynamic-code guards alone do not settle trim safety. The required ordinary interpreted/Auto NativeAOT surface remains unannotated and safe.

Parse copies only the data required for persistence; no borrowed pattern span is retained. Diagnostics carry a structured code, pattern offset/length, explicit location unit and optional owned message. Character-pattern locations use UTF-16 units; byte-pattern locations use bytes. A successful `TryParse` does not format a message. `TryParse` reports expected syntax/admission/resource failures, not programmer argument errors or arbitrary system failures. A default diagnostic is an explicit no-error value. `Parse` converts a failed diagnostic to `RegexParseException` carrying the same fields.

Byte-pattern parsing is admitted only for profiles with a defined 8-bit pattern language. For PCRE2 UTF-8, decode strictly while retaining original byte source locations; non-UTF8 byte patterns preserve each byte exactly. For a char-pattern convenience request targeting the PCRE2 raw8 profile, only code units 0..255 are admitted and map one-to-one; larger units fail with a pattern-domain diagnostic. Char patterns for PCRE2 UTF-8 require strict, explicitly documented encoding with source-location mapping. Exact native byte-pattern conformance uses the byte overload. Other profiles reject this overload until a corresponding encoding contract is specified. The internal flavor compiler needs the matching byte-pattern entry point; it must not normalize patterns through lossy text.

## Allocation-controlled search

```csharp
public readonly record struct RegexMatchRange(int Index, int Length);
public readonly record struct RegexCapture(bool Participated, int Index, int Length);
public enum RegexSearchStatus
{
    Match, NoMatch, InvalidInput, DestinationTooSmall, LimitExceeded,
    Cancelled, TimedOut
}
public sealed class RegexWorkspace : IDisposable
{
    public void Reserve(RegexWorkspaceCapacity capacity);
    public RegexSearchStatus IsMatch(ReadOnlySpan<char> input, int startAt = 0,
        RegexMatchOptions options = default);
    public RegexSearchStatus IsMatch(ReadOnlySpan<byte> input, int startAt = 0,
        RegexMatchOptions options = default);
    public RegexSearchStatus Find(ReadOnlySpan<char> input, int startAt,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public RegexSearchStatus Find(ReadOnlySpan<byte> input, int startAt,
        out RegexMatchRange match, RegexMatchOptions options = default);
    public RegexSearchStatus FindCaptures(ReadOnlySpan<char> input, int startAt,
        Span<RegexCapture> groups, out RegexMatchRange match, RegexMatchOptions options = default);
    public RegexSearchStatus FindCaptures(ReadOnlySpan<byte> input, int startAt,
        Span<RegexCapture> groups, out RegexMatchRange match, RegexMatchOptions options = default);
    public RegexExecutionFailure LastFailure { get; }
    public void Dispose();
}
```

Separate operations let boolean search stop once success is proven and avoid capture work. `Find` selects a range without allocating result objects. `FindCaptures` requires at least `GroupCount` entries, one final capture per group including zero. Unmatched and zero-length captures are different. Capture history is an explicit future capability unless a flavor adapter requires it; .NET's repeated capture collection is not silently promised. Duplicate named groups, if admitted, map to the profile's group-number rules and may need multiple indices; a name lookup must not discard this information.

On `DestinationTooSmall`, set match to default and leave the destination untouched; report required count without searching. For Match, initialize every logical group slot, including nonparticipating ones (`Participated=false, Index=0, Length=0`); leave excess capacity untouched. For NoMatch or other non-success, set match to default and clear the logical destination slice so stale captures cannot be mistaken for results. Check destination size before workspace capacity admission or input validation; the core contract freezes this ordering for runtime tests. DestinationTooSmall reports required GroupCount in LastFailure.RequiredCapacity; work is zero and the destination is untouched. Lifecycle/argument checks precede destination checks. No-growth capacity/memory admission precedes input validation; the operation polls cancellation/deadline even for empty input and immediate results. Full subject encoding validation precedes scalar start-boundary validation. The precise precedence is in RegularCore contract. Limit/input failures populate `LastFailure` with code and source position, reset at each operation. Any failure releases transient state and allows the workspace to be reused. Argument range errors, disposed use and reentrant use throw normal argument/lifecycle exceptions.

Pattern convenience `IsMatch` returns bool, but throws a typed `RegexExecutionException` for invalid input or limits. It must not conflate them with false. Its temporary workspace/allocation policy is documented separately from the allocation-controlled API. No global shared mutable scratch.

## Text and positions

Indices/lengths are always in the input representation's native units: UTF-16 code units for char input; bytes for UTF-8 and raw-byte input. Ranges borrow no storage and remain meaningful only relative to the original input. Extract text with `input.Slice(range.Index, range.Length)`; no implicit `Substring`, transcoding or capture strings.

For raw-byte mode, all values 0..255 are data and classes obey the admitted byte profile. UTF-8 is strict by default: malformed, overlong, surrogate and truncated encodings return InvalidInput, with no replacement decoding. Scalar-oriented UTF-16 profiles reject unpaired surrogates; ECMAScript has explicit code-unit/lone-surrogate semantics even under its Unicode modes, so it uses a different decoder policy. A byte API must not imply all flavors have a valid byte interpretation. Flags define scalar/code-unit consumption independently of the storage unit used for offsets.

`startAt` bounds the candidate match start, not the input visible to lookbehind, anchors or boundaries. Pass the original span plus an offset; do not slice the subject to fake a search window. It must be within [0, input.Length]. UTF-8 scalar mode rejects offsets inside an encoded scalar. Generic UTF-16 searches preserve the lower-bound rule even when the offset is inside a surrogate pair; their contract may reject such a Unicode-mode boundary. The ECMAScript host adapter implements the pinned specification's separate `lastIndex` conversion: under Unicode sticky/global matching an interior-surrogate index can refer back to the pair's start. For example `/./uy` over a single surrogate pair with `lastIndex=1` needs a pinned oracle fixture for returned index 0 and final `lastIndex=2`. It must not route that raw index through a lower-bound-only Find contract. Scalar XML offsets require boundaries. Empty-match iteration advancement is profile-defined, including ECMAScript's Unicode-aware AdvanceStringIndex. Failed searches terminate iteration; an empty match at the terminal boundary is emitted once.

UTF-8 `InvalidInput` is a subject-wide contract even if an early match exists. The first implementation validates the full subject before search. A combined decoder/scan may avoid a duplicate pass only if it still proves subject validity before returning Match or NoMatch. Benchmark validation separately and end to end. For repeated searching, a future validated-input lifetime token must bind to immutable input ownership; do not cache validation of mutable caller spans.

## Iteration and host adapters

Callers can loop around Find with a contract-tested `AdvanceAfterMatch` helper. A later `ref struct` enumerator may borrow span and workspace, must expose status/failure after iteration, and must not permit simultaneous workspace use. Do not hide pooled leases in copyable disposable value enumerators.

For XSD, boolean/range operations mean whole-value membership with `startAt=0`; successful Find returns the entire lexical value. GroupCount is one and grouping parentheses do not invent capture semantics. Nonzero startAt is an argument error.

ECMAScript's `lastIndex`, sticky/global behavior and JS replacement syntax belong to an explicit mutable `EcmaScriptMatcher` adapter over an immutable pattern. The library is not a JS object runtime. XSD offers whole-value `IsValid` semantics; XPath exposes `Matches`, `Replace`, `Tokenize` and the agreed `AnalyzeString` result shape/errors. POSIX adapters map compile/execution options and submatch ranges. PCRE2 adapters expose only the explicitly agreed host operations and callbacks. These adapters can allocate owned strings/collections; borrowed matching still uses the shared range contract. Required replacements must parse each dialect's replacement grammar, not reuse .NET replacement syntax.

No async, Stream, ReadOnlySequence or multi-pattern database API is required initially. Adding them later needs a separate retention, chunk-boundary and zero-width semantics contract. A public plugin ABI is also deferred; flavor modules are internal strategies initially so IR can evolve without third-party ABI obligations.

## Acceptance

- **API-01**: Compile-checked examples exercise parse, boolean/range/capture searches, input slicing and disposal without materializing matched text. Negative samples prove spans cannot escape and invalid profile/representation combinations fail deterministically.
- **API-02**: FindCaptures tests cover short/oversized destinations, unmatched/empty groups, no-match, error clearing and duplicate-name rules. No result references returned pooled storage.
- **API-03**: ASCII, supplementary Unicode, lone surrogates, invalid UTF-8 and raw-byte fixtures assert native offset units and profile-specific legal input.
- **API-04**: Iteration terminates and advances correctly for empty matches, end-of-input, sticky/global searches and original-input lookbehind/anchors.
- **API-05**: Convenience and workspace operations agree on matches; every invalid-input, capacity and limit status maps to a distinct documented outcome. Successful warm no-growth operations meet the performance requirements.
- **API-06**: The single assembly supports warning-free interpreted and Auto calls under NativeAOT; an explicit Compile() call yields IL3050 in a dedicated diagnostic fixture and throws PlatformNotSupportedException on unsupported runtimes. JIT compilation returns the same semantic results without mutating the original pattern, and ExecutionBackend reports the actual backend. Default use does not initialize or retain unreachable emitter machinery.
