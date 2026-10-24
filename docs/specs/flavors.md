# Flavor contracts

These contracts specify observable regex behavior and compatibility boundaries, not a translation into .NET regex syntax. Source keys are in the [source register](../research/flavor-sources.md). `FL-*` identifiers name testable semantic requirements; a specification alone is not a conformance result.

## Profiles and claims

Every compiled pattern must carry a versioned profile, input representation, Unicode/table revision, syntax options, match-selection policy and capability record. A flavor enum alone cannot identify a reproducible contract. ECMAScript is frozen in [ECMAScript contract](ecmascript.md), XML in [XML contract](xml.md); POSIX is frozen in [POSIX contract](posix.md); PCRE2 is frozen in [PCRE2 contract](pcre2.md); Oniguruma is frozen in [Oniguruma contract](oniguruma.md). Pins are independent of whatever version an installed oracle happens to expose.

| Profile | Proposed reference | Admission and completion boundary |
| --- | --- | --- |
| `EcmaScript2025` | ECMA-262 edition 16 (2025), §22.2 and selected Annex B behavior | complete legacy/`u`/`v` grammar; legacy Annex B; selected Unicode/Emoji16 (default) or17; UTF-16 only; typed builtin host operations with string-template replacement. See ECMAScript contract inventory. |
| `Xsd11` | XSD 1.1 Part 2, Recommendation 5 April 2012, Appendix G | complete regex language membership on a supplied lexical value; XML1.0Fifth with selected Unicode16 (default) or17, strict scalar UTF-16/UTF-8 subjects. Does not claim a schema validator. |
| `XPath31` / `XQuery31` | F&O 3.1, Recommendation 21 March 2017, §5.6 | shared regex semantics with selected Unicode16 (default) or17, plus all forms of `matches`, `replace`, `tokenize`, and immutable typed `analyze-string`. Does not claim an XPath/XQuery evaluator. |
| `PosixBre2018-CByte` / `PosixEre2018-CByte` | POSIX Issue 7, 2018 edition, XBD §9/7.2/7.3 and regex interfaces | complete defined BRE/ERE in named managed C/POSIX byte-only profiles. Other locales/Issue 8 explicitly excluded; high-byte/strict/empty-pattern dispositions labelled. See POSIX contract. |
| `Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1` | Oniguruma tag v6.9.10, commit `4ef89209`, `ONIG_SYNTAX_ONIGURUMA`, UTF-16LE | complete syntax and matching of TextMate grammars: Ruby syntax, possessive/atomic groups, variable-length look-behind, subexpression calls, absent repeater, Oniguruma's Unicode 16.0 tables and multi-code-point folds; TextMate multi-pattern scanner. Callouts, absent expressions/stoppers, level backreferences and whole options are unsupported. See Oniguruma contract. |
| `Pcre2-10.46-ManagedStandard8_16-v1` | PCRE2 release10.46, commit`b2bd4254`, standard interpreter | complete defined8/16 language/matching, all controls/advanced/callback/partial/table/iteration/substitution obligations; typed native results plus explicit generic projection. NativeABI/JIT/DFA32/EBCDIC boundaries in PCRE2 contract. |

**FL-COMMON-001:** An unsupported valid feature produces a structured unsupported-capability diagnostic identifying the profile, feature, source span and capability. An invalid expression produces a syntax/option diagnostic. Never silently change dialect, normalize text, switch to `System.Text.RegularExpressions`, or return no-match for either failure.

**FL-COMMON-002:** A stable profile name means its declared contract is complete. A staged implementation must expose a preview capability manifest and enumerate gaps. A restricted regular subset, C-locale-only POSIX support, or PCRE2 without recursion is not marketed as unrestricted flavor support.

**FL-COMMON-003:** Compare more than success: match start/end, every group (including unmatched versus matched-empty), names/numbers, final capture versus optional capture history, iteration, replacement/split results, and error category. UTF-16 APIs report offsets in original UTF-16 code units; byte APIs report byte offsets. Code-point semantics do not change those coordinates. Host adapters may translate to native coordinates explicitly.

**FL-COMMON-004:** Resource exhaustion, cancellation and timeout are distinct from no-match and syntax errors. Accelerated, interpreted and compiled paths must agree on admitted-pattern results. The budget model need not reproduce a native engine's instruction count; compatibility documentation must state that limitation.

## ECMAScript

Source facts: ECMAScript strings are UTF-16 code-unit sequences and need not be well-formed Unicode (ECMA-STRING). Regex semantics distinguish legacy code units from Unicode-aware matching; `v` adds sets containing strings (ECMA-REGEXP). Annex B contains additional legacy rules (ECMA-ANNEX-B).

The [ECMAScript contract](ecmascript.md) specifies ES-001 through ES-020, Unicode generation, internal ABI, host limits, and fixture format.

- **FL-ECMA-001:** Build separate grammar/semantic coverage for legacy, `u`, and `v`; reject combined `uv`, duplicate/unknown flags, invalid property aliases, and mode-dependent malformed escapes. Track `d g i m s u v y`, scoped modifiers, lookaround including lookbehind, numeric/named backreferences, named groups and disjoint duplicate-name cases. Annex B B.1.2 compatibility is a required capability enabled only for legacy mode; its absence blocks a browser-compatible legacy claim.
- **FL-ECMA-002:** Preserve lone surrogates in patterns and inputs. Unicode-aware matching combines valid pairs and treats an unpaired surrogate as its own code point; a blanket scalar-value decoder that rejects or replaces it fails this profile. Keep category/property lookup and case-folding policies mode-specific, including complement order in `u` versus `v`.
- **FL-ECMA-003:** Preserve ordered alternative/backtracking behavior, capture clearing during repetition, unmatched-backreference behavior, assertion captures and reverse lookbehind capture behavior. Possessive/atomic optimizations require proof they preserve the profile.
- **FL-ECMA-004:** Separate immutable compiled pattern from a stateful compatibility session. Verify `lastIndex` updates/reset for `g`/`y`; a raw `exec` and an all-match iterator have different empty-match responsibilities. The iterator uses the profile's advancement rule, including surrogate pairs. A shared compiled object must not contain mutable `lastIndex`.
- **FL-ECMA-005:** Specify adapters for `exec`, `test`, match iteration, replace and split before claiming those operations. Include numbered/named replacement references and split capture insertion. The typed surface is Exec/Test, Match/MatchAll, Search, string-template Replace/ReplaceAll and Split with finite output/work/memory limits. Exclude replacer callbacks, RegExp.compile, JavaScript object coercions, overridden properties and `Symbol` dispatch from a pattern-library claim; full JavaScript object interoperability would be another product scope.

## XML Schema

[XML contract](xml.md) freezes XS-001–012 and XP-001–017, XML/Unicode policies, strict UTF-16/UTF-8 native offsets, diagnostic and independent-internal ABI, all four host functions and fixture v1. GregsEx interpretations of published-text ambiguities are not W3C errata. Original source identities are retained with the corpora. Byte patterns/raw subjects are rejected. XML default paths remain AOT-safe.

Source facts: Appendix G defines whole-string language membership; `^` and `$` are ordinary characters. Its syntax includes XML-name escapes and class subtraction, but no XPath-style anchors or backreferences (XSD-REGEXP).

- **FL-XSD-001:** Define validation as `value ∈ language(pattern)` over the supplied lexical value. Grouping serves language construction; no normative capture choice is invented. Expose validation directly. A generic search operation must either be unavailable for this profile or explicitly distinguish its library-extension semantics.
- **FL-XSD-002:** Cover every Appendix G production, nested subtraction, XML `\i`/`\c`, categories/blocks and quantifier boundaries. Test dot, `\s`, `\d`, `\w` using XSD tables rather than ECMAScript or .NET tables. Test XSD's treatment of unknown block names separately from XPath's error policy.
- **FL-XSD-003:** Pin XML character/name policy and Unicode data . Reject input outside the declared XML string domain with an input-domain diagnostic. Do not perform whitespace-facet normalization, XML entity decoding or XML parsing in the matcher; a schema host applies its datatype/facet processing first. Multiple schema facets and derivation are host responsibilities, not equivalent to adding `.*` or joining all facets with `|`.

## XPath and XQuery

Source facts: F&O regexes add anchors, reluctant quantifiers, captures/noncapturing groups and backreferences to XSD syntax; matching searches substrings by code point. Flags `s m i x q` may repeat. Unknown flags/patterns report `FORX0001`/`FORX0002`; empty-matching replace/tokenize patterns report `FORX0003`, and malformed replacement syntax reports `FORX0004`. Replacement and tokenization have their own contracts (XPATH-REGEXP).

- **FL-XPATH-001:** Use one regex profile for both languages and a separate function adapter. Cover LF anchor boundaries (including the terminal LF rule), CR/LF dot handling, case variants, whitespace removal outside classes, and literal `q` processing. Test `q` with `i`, with ineffective `m/s/x`, and with replacement text. Neither general collations nor .NET replacement grammar apply.
- **FL-XPATH-002:** Verify all signatures of `matches`, `replace`, and `tokenize`, including absent input and one-argument tokenization. Preserve separator-edge empty tokens and exclude separator captures. Apply the specification's zero-length-pattern check, not merely a check against the current input's matches.
- **FL-XPATH-003:** Cover `$0`, multi-digit group references, fallback digits, missing/unmatched groups and literal replacement escapes. Backreference numbering, forward-reference rejection, unmatched-group behavior and repeated captures require independent tests. Retain the `err` namespace URI with the `FORX` local code in adapter errors.
- **FL-XPATH-004:** The typed surface requires an `analyze-string` representation preserving nesting/participation; all four functions are required. A match/capture API may be useful before the XML result adapter exists, but cannot claim the complete F&O §5.6 function surface. Resolve ambiguous implementation-defined capture results using a documented permitted outcome set rather than copying one oracle.

## POSIX BRE and ERE

The [POSIX contract](posix.md) specifies PX-001 through PX-020, provider/data identity, byte representation, classification, selection, typed adapters, resources, and versioned fixtures.

Source facts: Issue 7 specifies the earliest match, then the longest whole match, and longest subpatterns consistent with that match. Empty participation differs from no participation. BRE includes backreferences; ERE's standard grammar does not. Bracket classes, equivalence classes and collating symbols depend on locale; ranges outside the POSIX locale have unspecified behavior (POSIX-REGEXP).

- **FL-POSIX-001:** Implement whole-match and submatch selection as an independent policy. Test nested/repeated groups and backreferences while preserving the winning whole match. Choosing the first successful backtracking path fails this profile even when the overall match sometimes agrees.
- **FL-POSIX-002:** Cover BRE escaped grouping/intervals and references 1–9 separately from ERE grouping, intervals and alternation. Create a register of invalid, undefined, unspecified and optional constructs. strict policy rejects undefined/unspecified forms, ERE backreferences and GNU/BSD extra escapes; do not call their behavior a POSIX-required error. Optional BRE anchors at first/last group positions are enabled.
- **FL-POSIX-003:** Certify only named PosixBre2018-CByte/PosixEre2018-CByte with Bytes subjects, managed deterministic C/POSIX tables and the frozen digest. Reject other locales, NUL and UTF subject requests explicitly. Complete locale support needs a versioned provider for decoding, `LC_CTYPE` classes/case and `LC_COLLATE` symbols/equivalence data. Never substitute .NET culture or Unicode category matching for a POSIX locale. Freeze the locale per compiled pattern/session; no process-global locale mutation on the hot path.
- **FL-POSIX-004:** Certify regex-interface options/offsets separately from shell utility behavior: newline processing, ignore-case, no-subexpression mode and beginning/end suppression flags; quantify pattern/interval capacity and embedded NUL policy. The profile requires immutable managed C/POSIX-only tables and all defined interface semantics in that named domain; both pinned libc-family audits and classified disagreements are mandatory. Broader locales/native providers/Issue8 require a explicit versioned extension, datasets and provider-specific corpus jobs. C-only certification stays labeled C-only.

The 2024 Issue 8 rationale records minimal matching and BRE extensions (POSIX-2024-RATIONALE). Adopting that edition needs a fresh normative audit and profile; none of its additions are silently included in the 2018 contract.

## PCRE2

The [PCRE2 contract](pcre2.md) specifies PC-001 through PC-024 and PH-001 through PH-012, native-width data, controls, callbacks, hosts, and resources.

Source facts: PCRE2 has 8/16/32-bit code-unit libraries; UTF and UCP are separate controls (PCRE-UNICODE). The 10.46 pattern manual covers recursion/subroutines, conditionals, branch reset, duplicate names, control verbs and bounded variable-length lookbehind (PCRE-PATTERN). Native compile/match contexts expose options, diagnostics, limits and callouts (PCRE-API).

- **FL-PCRE-001:** Certify `byte` 8-bit non-UTF, UTF-8, UTF-16 and 16-bit non-UTF modes independently. UTF does not imply UCP; non-UTF does not mean an automatic UTF-8 decode. Carry newline/BSR choices and character tables in the profile. Treat UTF validity, start offsets, `\C`, invalid-UTF matching and no-validation options as distinct capabilities. Invalid UTF must not be repaired implicitly.
- **FL-PCRE-002:** Build a per-feature parity matrix against the pinned standard matcher: escapes/assertions, `\K`, `\R`, `\X`, properties, option changes, atomic/possessive constructs, all group/name/number forms and replacement behavior if offered. Generate cross-mode vectors, including limits and malformed patterns.
- **FL-PCRE-003:** Full pattern parity requires backreferences, recursion/subroutine calls, recursion conditions, assertion conditions, branch reset, duplicate names, control verbs/marks, start-of-pattern directives and version-specific lookbehind restrictions. Stage these as advanced features, not permanent unannounced exclusions. bounded variable lookbehind default is255characters, configurable as in the reference. Unlimited lookbehind is invalid in the pinned profile.
- **FL-PCRE-004:** Preserve callout behavior: the managed manual/automatic callback/inspection adapter is mandatory. Until implemented, patterns needing callouts fail with unsupported capability. An explicit restricted profile may prohibit callouts, but full PCRE2 matching parity requires these capabilities. Callout count/order and auto-callouts need separate observable tests because optimizations affect them.
- **FL-PCRE-005:** Model compile nesting/pattern size, maximum variable lookbehind, match work, heap/depth and cancellation separately. Map native error identities/categories without promising byte-identical human messages. Pattern directives may reduce caller ceilings. Exhaustion never becomes a mismatch. Configuration inventory must include all relevant compile/match extras, partial matching and start/end anchoring modes, including options introduced by the pinned release.
- **FL-PCRE-006:** Explicit host boundaries: C ABI, native serialized-code bytes, native JIT stack/control APIs, EBCDIC builds, 32-bit inputs and `pcre2_dfa_match` are excluded from the named managed8/16 standard-matcher profile. Record them in the capability manifest with future parity/adapter decisions . Managed compiled execution is GregsEx's backend, not a claim to replicate native JIT artifacts. These boundaries must appear beside compatibility claims.

## Oniguruma

The [Oniguruma contract](oniguruma.md) specifies OG-001 through OG-007: syntax inventory, rejections, unsupported
constructs, semantics, limits, implementation and conformance evidence.

Source facts: TextMate grammars are written for Oniguruma's default syntax (doc/RE, `ONIG_SYNTAX_ONIGURUMA`) and run
with `ONIG_OPTION_CAPTURE_GROUP` by vscode-oniguruma (UTF-8) and Onigwrap (UTF-16LE); Onigwrap rewrites `\xHH` to
`\x{HH}` before compiling, since UTF-16 Oniguruma reads `\xHH` as an encoded byte.

- **FL-ONIG-001:** Pin the Oniguruma release, syntax, encoding and options in the profile; take Unicode data from
  that release's generated tables, not from another flavor.
- **FL-ONIG-002:** Agree with the pinned native engine on pattern validity and on every capture of every search,
  including quirks that grammars observe (look-behind right range, empty-loop capture checks, multi-code-point folds),
  against retained native observations. A nested negated class holding U+0000 is its true complement, as under UTF-8
  (vscode-oniguruma), not UTF-16's underflow to every code point; the oracle carries that one-line fix.
- **FL-ONIG-003:** Offer TextMate's scanner selection and per-pattern result cache with allocation-free warm searches,
  since grammar tokenization searches many patterns from each position.

## Representative acceptance vectors

These are small independently stated contract vectors, **not passing tests or evidence of implementation**. Run only when the requisite profile feature is admitted; otherwise expect the structured unsupported result required by FL-COMMON-001. Expand them into a versioned corpus before implementation. Patterns are regex text (one displayed backslash); strings use `\n`, `\r` and `\uXXXX` notation to describe exact code units. Ranges are zero-based half-open. XSD rows use whole-value validation; ECMA/PCRE/POSIX rows use search unless anchors are shown.

| Vector | Profile / operation | Pattern / options; input | Expected when supported |
| --- | --- | --- | --- |
| V-ECMA-01 | legacy / Unicode `u` / `v` | `^.$`; `\uD83D\uDE00` | false / true / true |
| V-ECMA-02 | ECMA `u` | `^.$`; lone `\uD800` | true; original range `[0,1)` |
| V-ECMA-03 | ECMA `v` | `[[a-z]--[aeiou]]+`; `aeibcd` | range `[3,6)` = `bcd` |
| V-ECMA-04 | ECMA `v` | `[\q{ab\|cd}]`; `xab` | `[1,3)` = `ab` |
| V-ECMA-05 | ECMA `u` versus `v`, both `i` | `\P{Lowercase_Letter}`; `a` | true / false |
| V-ECMA-06 | ECMA | `^(a\|(b))\2$`; `a` | true; group 2 unmatched |
| V-ECMA-07 | ECMA / match-all | empty pattern, `gu`; `\uD83D\uDE00` | empty matches at UTF-16 indices 0 and 2 |
| V-ECMA-08 | ECMA Unicode sticky exec | `.` with `uy`; `\uD83D\uDE00`, `lastIndex=1` | returned index 0, final `lastIndex=2`; adapter-specific position conversion |
| V-XSD-01 | XSD validation | `a`; `ba` | false |
| V-XSD-02 | XSD validation | `^a$`; `^a$` / `a` | true / false |
| V-XSD-03 | XSD validation | `[a-z-[aeiou]]+`; `bcdf` / `be` | true / false |
| V-XSD-04 | XSD validation | `\i\c*`; `a:b` | true under proposed XML 1.0 name policy |
| V-XPATH-01 | XPath matches | `a`; `ba` | true |
| V-XPATH-02 | XPath matches | `a$`; `a\n`, flags empty / `m` | false / true |
| V-XPATH-03 | XPath matches | `\p{Lu}`; `a`, flag `i` | false |
| V-XPATH-04 | XPath replace | pattern `(z)`, replacement `$12`; `z` | `z2` |
| V-XPATH-05 | XPath replace | pattern `x`, replacement `$`; `x`, flag `q` | `$` |
| V-XPATH-06 | XPath tokenize | separator `,`; `,a,` | sequence `("", "a", "")` |
| V-XPATH-07 | XPath tokenize | separator `,`; empty input | empty sequence |
| V-XPATH-08 | XPath replace/tokenize | pattern `a*`; any input | `err:FORX0003` |
| V-XPATH-09 | XPath matches / replace | invalid flag `z` / replacement `$` without `q` | `err:FORX0001` / `err:FORX0004` |
| V-POSIX-01 | ERE, C locale | `a\|aa`; `aa` | `[0,2)` = `aa` |
| V-POSIX-02 | ERE, C locale | `(a\|aa)(a?)`; `aa` | whole `aa`, group 1 `aa`, group 2 matched-empty |
| V-POSIX-03 | BRE, C locale | `\(ab\)\1`; `abab` | `[0,4)` = `abab`, group 1 `ab` |
| V-POSIX-04 | ERE, strict extension policy | `(ab)\1` | rejected as extension/undefined construct; no normative match claim |
| V-POSIX-05 | ERE, C locale | `[[.a.]]`; `a` | `[0,1)` = `a` |
| V-PCRE-01 | PCRE UTF-16 | `^\d+$`; `\u0661`, UCP off / on | false / true |
| V-PCRE-02 | PCRE non-UTF16 / UTF16 | `^.$`; `\uD83D\uDE00` | false / true |
| V-PCRE-03 | PCRE UTF16 | `.`; lone `\uD800`, default validity checks | input UTF error |
| V-PCRE-04 | PCRE | `foo\Kbar`; `foobar` | reported whole-match range `[3,6)` |
| V-PCRE-05 | PCRE | `(?<=a{1,2})b`; `aab` | `[2,3)` = `b` with default lookbehind limit |
| V-PCRE-06 | PCRE | `^(?<p>\((?:[^()]\|(?&p))*\))$`; `(())` | true, recursion required |

Locale vectors involving equivalence classes, multi-character collating elements and non-ASCII class/case behavior require a checked-in locale definition and provider identifier before an expected result is committed. Unspecified POSIX behavior has no single standard-derived expected answer.

## Conformance requirements

**FL-ORACLE-001:** Build a flavor corpus with requirement IDs, source section, profile/version/options, exact input units, operation, expected result or permitted result set, and provenance. Pin imported tests by commit/hash and license; store oracle executable/library hashes, platform, build flags, Unicode and locale tables. An oracle may disagree with its specification. Classify and reduce disagreements; do not majority-vote errors into the contract.

**FL-ORACLE-002:** Differential jobs use pinned JS runtimes and Test262 for ECMAScript; an XSD 1.1 processor for validation; a F&O 3.1 processor and QT3 tests for XML functions; at least two libc regex implementations in fixed locales for POSIX; and the exact PCRE2 10.46 standard interpreter at frozen b2bd4254 source/table/options/optimizer identities in widths8/16. `.NET Regex` is only a deliberately compatible-case baseline. Cross-check interpreted/compiled/accelerated GregsEx modes on every admitted vector. Release requires zero unexplained mismatches, no skipped required capability and a published capability and limitations documentation.
