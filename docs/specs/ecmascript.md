# ECMAScript contract v1

## Editions and data

`EcmaScript2025` means edition 16 grammar and matching, with an explicit
`Unicode-16.0.0+Emoji-16.0` (default) or `Unicode-17.0.0+Emoji-17.0` data identity,
selected by the immutable profile's `UnicodeVersion`. This is a GregsEx reproducibility
choice. [ECMA §2](https://tc39.es/ecma262/2025/multipage/conformance.html) and
[§3](https://tc39.es/ecma262/2025/multipage/normative-references.html) reference
Unicode without an annual pin; this contract does not claim 16.0 is their
mandated minimum or the latest Unicode. An oracle using other tables must be
classified, never silently substituted. Choosing Unicode 17 does not change the
ECMAScript grammar edition or property-name admission whitelist; property values,
classification, identifiers, casing and Emoji strings come from the selected data.

Admit all eight flags `d g i m s u v y`; legacy means neither u nor v. Admit
[§22.2](https://tc39.es/ecma262/2025/multipage/text-processing.html#sec-regexp-regular-expression-objects)
in full, including modifiers and disjoint duplicate names.
[Annex B.1.2](https://tc39.es/ecma262/2025/multipage/additional-ecmascript-features-for-web-browsers.html#sec-regular-expressions-patterns)
is enabled for legacy and does not extend u/v. No subset may receive the stable
profile claim. The profile contract alone is not an executable pattern.

Only UTF-16 char patterns/subjects are admitted, including lone surrogates.
UTF-8 and raw-byte compile options and byte-pattern overloads fail
UnsupportedRepresentation before parsing. No transcoding extension is implied.
Native offsets remain UTF-16 units. The generic workspace lower-bound search
and JS lastIndex conversion are separate entry points; core scalar validation,
nullable capture pruning and absolute anchors must not supply ECMAScript behavior.

## Complete admission inventory

These IDs are stable fixture coverage keys. Every admitted row requires positive,
negative where applicable, boundary, resource and regression coverage. Normative
productions/algorithms at the linked edition are authoritative within each row;
examples are not a substitute for the complete production inventory.

| ID | Frozen coverage | Legacy | u | v |
| --- | --- | --- | --- | --- |
| ES-001 | Flags, duplicate/unknown/uv rejection; normalized identity | all flags except u/v modes | u, not v | v, not u |
| ES-002 | Empty pattern/branches, concatenation, ordered disjunction, pattern characters | yes | yes | yes |
| ES-003 | Dot, anchors, four line terminators, scoped i/m/s add/remove | yes | yes | yes |
| ES-004 | Greedy/lazy finite/unbounded quantifiers, empty iterations, capture reset | yes | yes | yes |
| ES-005 | Capturing/noncapturing/named groups, Unicode names, disjoint duplicate names | yes | yes | yes |
| ES-006 | Numeric/named/forward/unmatched backreferences and capture rollback | yes | yes | yes |
| ES-007 | Positive/negative lookahead and unbounded reverse lookbehind, assertion captures | yes | yes | yes |
| ES-008 | Character/control/hex/Unicode/null/identity escapes and mode early errors | BMP | Unicode | Unicode |
| ES-009 | Empty/negated/range classes, predefined d/D/w/W/s/S, boundaries b/B | yes | yes | yes |
| ES-010 | p/P exact aliases: general category, Script, Script_Extensions, permitted binary properties | identity rules | yes | yes |
| ES-011 | Nested class sets, union, intersection, subtraction, reserved syntax and q string disjunction | no | no | yes |
| ES-012 | All seven specified properties of strings, string ordering, complement restrictions | no | no | yes |
| ES-013 | Mode-specific canonicalization, complement/fold order and string-set folding | uppercase | simple/common | simple/common |
| ES-014 | Annex B decimal/octal/control/identity/brace/class extensions and quantified lookahead | yes | no | no |
| ES-015 | Exec/Test, indices, names, lastIndex, sticky/global and empty exec | yes | yes | yes |
| ES-016 | Match/MatchAll/Search, iterator snapshots, empty advancement | yes | yes | yes |
| ES-017 | Replace/ReplaceAll templates, numbered/named substitutions, prefix/suffix | yes | yes | yes |
| ES-018 | Split limit, independent sticky clone, captures including absent/empty | yes | yes | yes |
| ES-019 | Bounded allocating host results/output with explicit exhaustion | required | required | required |
| ES-020 | Limits/cancellation/deadline/lifetime/zero-allocation/AOT for admitted core operations | required | required | required |

Do not admit PCRE atomic/possessive/conditional/recursion/verbs, .NET balancing
groups, XML escapes or alternate syntax under this profile. Valid ECMAScript
cannot become UnsupportedFeature in the completed profile. While implementing,
report feature ID/source span in the preview capability report; syntax errors
and unsupported representations remain distinct from resource failures.

## Unicode generation boundary

Unicode generation is deterministic and runtime tables are checked in. Sources are versioned
[UCD](https://www.unicode.org/Public/16.0.0/ucd/ReadMe.txt) and
[Emoji](https://www.unicode.org/Public/emoji/16.0/ReadMe.txt).
Unicode 17 inputs, source archive and hashes are separately retained under
`tools/unicode/ecmascript/17.0.0/`; generation and independent audit cover both
versions without replacing the Unicode 16 source snapshots or expected corpora.
Record SHA256/length for every input, generator revision, output digest and
Unicode terms/notices before claiming generated data. Required UCD inputs cover
UnicodeData, SpecialCasing (unconditional default uppercase), CaseFolding C/S,
PropertyAliases, PropertyValueAliases, Scripts, ScriptExtensions, PropList,
DerivedCoreProperties, DerivedNormalizationProps and relevant extracted/property
files including DerivedBinaryProperties and emoji-data. Audit the edition's
permitted property tables against actual data keys; loose alias matching and
ambient .NET categories/culture/casing are forbidden. ID_Start/ID_Continue plus
the edition's identifier additions govern capture names.

The seven string-property names are Basic_Emoji, Emoji_Keycap_Sequence,
RGI_Emoji_Modifier_Sequence, RGI_Emoji_Flag_Sequence, RGI_Emoji_Tag_Sequence,
RGI_Emoji_ZWJ_Sequence and RGI_Emoji. Use emoji-sequences/emoji-zwj-sequences and
the specified union, not an approximate emoji block or emoji-test status filter.
Input code points include surrogate values; normalization is never performed.
The Unicode implementation supplies budget-admitted immutable EcmaCaseTables
with sorted identity-omitted legacy Canonicalize and Unicode C/S mappings.
The executor factory binds this object once; VM loops use its direct readonly
spans with charged comparisons, never per-character interface dispatch or host
tables. Parser property/identifier queries use IEcmaUnicodeData at compile time.
Table retention is explicit (128 + 8 per mapping); once-shared generated tables
are charged once at their cold admission and disclosed separately from per-plan
retention. Factory wrappers charge their table reference; no per-plan hidden copy.

## Parser/runtime ABI

`Contracts/EcmaScript` is separate from core SemanticProgram/ProfileDescriptor.
The parser emits immutable budget-admitted bottom-up nodes: sequence,
alternative, repeat, capture, assertion, literal, resolved set and reference.
Child IDs precede parents; the root is the final node. Balance long binary
sequence/alternative lists without reordering; depth admission bounds the final
tree and syntax nesting independently. Nodes retain native source spans,
effective i/m/s flags, repeats without mandatory expansion, assertion direction,
capture subtree clearing ranges, and numeric or named reference identity.
FirstCapture/CaptureCount includes the node's own capture when applicable.
The admission factory validates bottom-up contiguous source-order capture ranges,
cycle prevention, source units, names/aliases, set slices and ordered finite string
storage before defensive copying. Repeat counts beyond the admitted Int32
representation fail ProgramLimitExceeded, never numeric wrap or InvalidSyntax;
this finite resource policy also applies to valid very large decimal counts.
Group zero is supplied by the executor, not a syntactic capture node. Names map
to every numbered alias in source order; duplicate-name legality is a parser
early-error obligation. Resolved sets contain ordered canonical intervals and
distinct finite code-point strings, including empty strings when admitted.
Folding/complement/algebra is resolved in parser-owned code under the node's
effective flags. Matching string sets prefers longer elements and preserves the
normative continuation behavior; unordered HashSet enumeration is insufficient.

The runtime lowers these nodes into its explicit-stack VM after bounded
compile admission, retaining the original node/source identity. No recursion
proportional to pattern/input, no unbudgeted repeat expansion, and no Thompson
deduplication based solely on input/program position for capture-sensitive paths.
Sequence evaluates X then Y forward and Y then X backward; alternative priority
remains X before Y in both directions. Positive assertions retain the selected
assertion captures while restoring the outer position, with the edition's
assertion choice-point boundary; negative assertions publish no participating
internal captures. Failed paths restore captures/position before trying the next
choice. Repeat resets its child's capture range before each attempted iteration,
and implements the normative empty-progress guard rather than RegularCore's first-visit
nullable pruning. Reverse class-string matching consumes the chosen element in
reverse while preserving its longest-first priority. These rules apply to Boolean
too: registers needed for references/assertions cannot be omitted merely because
the caller did not request output captures.
Runtime/compiler interfaces borrow spans only during calls. No plan retains the
caller pattern, input, scratch or destination. A shared plan exposes readonly
spans over privately owned copies and is reusable across independent workspaces.

The runtime reports prepared State/Capture/Scratch slot requirements using core
RegexWorkspaceCapacity; all VM frame, rollback, assertion, repeat and set-string
scratch live in those charged Int32 partitions. A deterministic caller-selected
frame capacity is admitted before search; exhaustion is WorkspaceCapacityExceeded,
never an allocating retry/no-match. The same RunState and cumulative RunBudget
survive candidates, nested assertions, backreferences and host loops. Every
native-unit inspection, node/VM visit, set comparison and register/frame copy is
charged before doing it. Poll even empty success; source-order budget precedence
and transactional reserve preserve the core safety/lifecycle requirements. Per-host-call
loops share one budget, including replacement/split matching. Output materialization
is separate from warmed boolean/range/caller-buffer capture/iteration allocation.

## Host contract

EcmaScriptMatcher implements the typed host operations below. Numeric LastIndex is double with
ToLength semantics (NaN/negative to zero, floor positive, clamp infinity/large
values); property coercion/JS objects are excluded. String/span inputs are already
typed. The immutable RegexPattern never stores LastIndex. Sessions own exclusive
workspaces; concurrent/reentrant use and disposed use follow core lifecycle rules.

| Operation | Builtin state and result obligations |
| --- | --- |
| Exec/Test | g/y read/write LastIndex; failure resets for g/y; non-g/y ignores and preserves it. Exec returns UTF-16 ranges/final captures/name aliases and optional d indices; raw empty success does not advance. Test follows Exec state but may omit result materialization. |
| Match | Non-g Exec-shaped result; global collects whole matches, resets LastIndex and advances empty matches. |
| MatchAll | String operation requires g; independent matcher clone seeded from ToLength LastIndex, source session unchanged; retains captures/indices and advances empty matches. |
| Search | Temporarily sets LastIndex to zero, restores the numeric original even on match/no-match; returns index or -1; runtime failure remains explicit. |
| Replace | Collect builtin matches with global/empty rules; GetSubstitution tokens $$, $&, $`, $', $n/$nn, $<name>. Unmatched captures substitute empty; unknown/invalid tokens follow literal fallback. Named duplicate aliases select the participating group. |
| ReplaceAll | Requires g for typed pattern overload, then builtin Replace behavior. |
| Split | Independent sticky clone retaining other flags; ToUint32 limit/default 2^32-1, empty input/zero-length/end cases, insert numbered captures including absent values. Does not mutate source session. |

Borrowed Exec writes caller capture slots and returns RegexSearchStatus/LastFailure;
ExecNext is a library iteration helper over the session: require g or y, use
the same builtin position conversion, advance after empty success, and let the
next failed search reset g/y state. Nonempty success keeps its end. A terminal
empty result is emitted once before the next NoMatch. Each caller-loop step is
a distinct operation; MatchAll's complete owned enumerator keeps a single budget
and exposes terminal failure, with no silent partial-success materialization.
Materialized EcmaScriptExecResult owns immutable numbered/name ranges and optional
d indices; EcmaScriptMatchResult explicitly distinguishes Single from GlobalMatches.
No object-typed result or mutable caller array becomes an ownership escape.
Materialized operations throw RegexExecutionException on match failures instead of
returning partial success. Iterator terminal status is explicit. Unicode interior
surrogate LastIndex conversion requires the existing V-ECMA-08 witness; generic
Find keeps its advertised lower bound and must not silently return an earlier start.
LowerBound Unicode-aware searches reject a start inside a valid surrogate pair
with InvalidInput/InvalidStartBoundary; lone surrogate units remain legal. The
BuiltinLastIndex path performs the pinned JS position mapping instead. No
whole-subject scalar-validity rejection is used for ECMAScript; inspected units
and pair/boundary checks still charge the cumulative budget.

The existing V-ECMA-08 / ECMA-C018 witness has an explicitly reviewed provenance
qualification: for `/./uy` on UTF-16 `[0xD83D, 0xDE00]` with LastIndex 1, this
project retains index 0, length 2 and final LastIndex 2. The pinned Node24.4.1
oracle agrees. Edition16 RegExpBuiltinExec maps the matcher input position but
retains the original lastIndex when constructing the result index/start; a
literal reading therefore differs for this interior-pair case. This is an
existing project-contract resolution, not a claim that the published algorithm
directly specifies index 0 or a resolved TC39 erratum. Compatibility documentation
must disclose this discrepancy. Preserve this witness unless the versioned profile
contract is explicitly changed.
Replacement and materialized arrays may allocate; that never
relaxes the prepared strict matcher search contract. Annex B RegExp.compile,
source literal parsing, JS coercions/accessors/overrides/species/Symbol dispatch and
replacer callbacks/general JS runtime interoperability are outside the typed adapter
product scope. MatchAll/ReplaceAll without g throws EcmaScriptHostException with
GlobalFlagRequired, rather than a pattern syntax or no-match result.

EcmaScriptHostLimits defaults to 1,000,000 result entries, 16,777,216 UTF-16
output units and 64 MiB live output memory. Positive finite overrides are chosen
at session construction; there is no unlimited sentinel. Charge all copied output
units/entries before work and reserve output storage before growth. Memory
accounting includes result wrappers, array/string headers (conservative 32 bytes
each), references (8 bytes each), captures (16 bytes each), strings (2 bytes per
unit), builders and old plus new buffers while both are live; increase constants
if the actual implementation is larger. Count split captures and retained MatchAll
entries too. Template prefix/suffix expansion cannot escape these ceilings.
ResultLimitExceeded/OutputLimitExceeded/MemoryLimitExceeded are distinct typed host
failures carrying RequiredCapacity/WorkConsumed; no partial output is returned.
Matching, empty advancement, template parsing, capture-name lookup and output
copying share one RunState/budget/deadline per complete host operation. MatchAll
holds it for the full iterator lifetime, including polls on every MoveNext.
On host-limit failure, cleanup releases admitted transient output; observable
LastIndex reflects the builtin matching steps already completed, not a fabricated
success or rolled-back JS state. Search always restores its numeric original
in a finally boundary, including failures. Iterator disposal relinquishes its
clone and owned scratch; subsequent source-session operations remain usable.

## Diagnostics and corpus

core RegexDiagnostic/RegexExecutionFailure codes and native units are retained.
EcmaScriptDiagnostic adds a stable ES-nnn feature/early-error identity and
Pattern/Flags source domain without modifying shared core enums. TryParseFlags is
a usable descriptor factory with original flag offsets; compiler output carries
pattern-domain detail. Integration's generic TryParse exposes the shared Diagnostic;
the typed host factory exposes full detail on failure. InvalidProgram denotes internal malformed
plan/ABI, never user syntax. Unknown/duplicate/uv flags are early errors with
original flag-string offsets; invalid enum flags passed directly are argument
errors. Undefined/empty captures must remain distinct in results and serialization.

Nonshipping fixture v1 stores schema/profile/data identity, capability IDs,
numeric UTF-16 pattern/input/replacement units, flags, operation, numeric LastIndex
before/after (special doubles encoded as strings), lower bound/destination,
structured result sequences/names/indices/text/split undefined entries and
diagnostic/failure details plus original lower-bound/destination and finite
compile/match/frame/host limit overrides. Source-derived, imported and discovered cases carry
distinct provenance, exact edition clauses and license/oracle metadata.
WorkConsumed may be explicitly null in a portable expected-error recipe when no
executor-specific deterministic count has been pinned; actual observed failures
always record their real consumed counter. Zero is never a substitute for an
unasserted counter. Error/capture/nullable output fields are required JSON members,
even when null; the strict loader rejects missing/unknown fields and versions.

Pin source-derived vectors, Test262 revisions/per-file licenses, and runtime binary/build/Unicode identities before generating oracle expectations. Required cases may not be silently excluded; retain a minimized disposition for every mismatch.
