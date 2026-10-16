# Pinned ECMAScript conformance inputs

This is independent nonshipping oracle preparation for ECMAScript2025 edition16,
legacy/u/v with Unicode16.0.0/Emoji16.0 and exact numeric UTF16 units. It is not a
claim that GregsEx implements the profile or that a finite corpus proves complete
specification parity.

`oracle-pin.json` freezes the actual Node24.4.1 executable, V8 build,
ICU77.1/Unicode16 versions and shared-library hashes, full compiled build config,
Homebrew bottle receipt, platform and licenses. Twelve qualification probes ran
before expectation generation. `artifacts.json` pins adapters, recipes, fixtures,
the shared frozen schema/loader, and every verified Unicode input/output/license.
No ambient .NET/Python Unicode tables produce expectations.

Test262 source is commit `6a73fa8f114611269ce4442e8f19a31239cb3827`, dated
2025-06-09. Every path matching RegExp/regexp/regular-expression plus all six String
prototype operation trees is retained: 2711 files, exact hashes/lengths, original
copyright headers, metadata and BSD license, and 12 harness sources.
`test262-dispositions.json` maps each file's original section and edition status.
Pending legacy-regexp proposals and staging tests are not normative edition16
requirements. The complete selected suite executes original assertions with
required sloppy/strict/module variants. Tests of literal parsing, JS coercions,
accessors/overrides/species/Symbol dispatch, static RegExp properties, compile and
callbacks are oracle audits; they do not imply a typed library supports JS objects.
Exact failing files have minimized, existing-contract exclusions rather than
being removed from run totals. No admitted regex grammar is excluded.

The fixture files use the unchanged `EcmaCorpus` / `EcmaFixture` v1 shape:

* `contract-corpus.json` retains all27 independently specified contract recipes
  unchanged, including C012's repaired subtraction and C018's disputed index.
* `oracle-corpus.json` covers flags, grammar, matching and all eight typed host
  operations, source/flags early errors, undefined/empty captures, every name
  alias, optional d indices, numeric state and all seven string properties.
  `recipes.json` retains independently authored assertions separately from oracle
  observations; disagreement fails instead of replacing a golden assertion.
* `imported-corpus.json` adapts builtin exec calls from every S15.10.2 algorithm
  file and the named-groups/lookBehind/lookbehind/dotall/unicodeSets trees. Every
  original selected-file assertion runs before adaptation. Exact duplicate calls
  within one file are deduplicated; there is no fixture cap. Imported cases link
  to the exact upstream commit/file and retain its notices. Other source files,
  including generated exhaustive Unicode16 assertions, remain in the full suite.
* `resource-corpus.json` prepares portable GregsEx category recipes. It is not
  executed through JavaScript and does not simulate GregsEx instruction counts,
  workspace admission, cancellation, deadlines or materialization limits. These
  source-only recipes must bind to actual bounded operations at integration.
* `raw-flag-boundary.json` preserves a lone-surrogate invalid flag in numeric
  units. The frozen v1 Flags JSON string is rejected by System.Text.Json before
  parser invocation for this input. A direct existing descriptor test and native
  SyntaxError witness preserve the required negative flag semantic; integration
  reconstructs the flag span from these numeric units. Shared shapes stay frozen.

The native adapter observes original builtin outputs/state. An independent shadow
with `d` obtains capture ranges and must return identical strings/state. Visible
`HasIndices` comes from the original flag. Named alias numbers are structurally
audited from capture parentheses, including duplicate names; the native names and
participating range must agree. Split undefined entries serialize as null and
matched-empty strings as empty unit arrays. Numeric -0/NaN/infinities retain exact
tokens. All indices are original UTF16 units; lone surrogates remain intact.

For collection/text operations, fixture `Match` means successful materialization;
MatchAll may have an empty match list, Split an empty text list, and Replace may
return the unchanged input. It is not the borrowed iterator's terminal status.
Search records its actual index via group zero, with no invented capture output.
Test records its boolean category/state and omits materialized capture records.

Native SyntaxError has no specified source position/feature/work counter. Flag
offsets are exact. Pattern error recipes preserve the whole-pattern source domain
and native category, not a fabricated point diagnostic. Integrators assert their
actual structured diagnostics separately: portable oracle comparisons select
category/domain, while exact GregsEx offsets/spans and real work counters are
retained in integration observations. Resource recipes likewise prescribe status,
code and finite policy boundary; null work is explicitly unasserted. Execution
locations/required-capacity zero follow the shared failure contract; actual source
positions are not fabricated by an external engine. Memory byte-accounting,
controlled deadline expiration, capacity precedence, representation rejection,
lifetime/concurrency, warmed allocation and NativeAOT remain explicit integration
checks. The deadline recipe must not become a wall-clock race assertion.

`coverage.json` retains all20 stable requirement rows and product-binding
obligations. Representative rows do not replace auditing every admitted normative
production/algorithm. There are no hidden skipped required semantics or product
passes inferred from loader checks. The [ECMAScript contract](../../../docs/specs/ecmascript.md) documents the C018 qualification; retained oracle observations and pins are in `oracle/` and `oracle-pin.json`.
