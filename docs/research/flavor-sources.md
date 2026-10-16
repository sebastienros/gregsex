# Flavor source register

Research date: 11 October 2026. This register separates authoritative references from proposed GregsEx choices. It contains links and short paraphrases; no upstream code or test corpus has been imported. Proposed profiles and observable requirement IDs are in [flavors.md](../specs/flavors.md).

## Primary references

| Key | Versioned primary source | Role and limits |
| --- | --- | --- |
| ECMA-REGEXP | [ECMA-262 2025 §22.2](https://tc39.es/ecma262/2025/multipage/text-processing.html#sec-regexp-regular-expression-objects) | Normative grammar and matching algorithms for proposed edition 16; includes Unicode sets, modifiers, captures and regex/string operation behavior. A live draft must not silently replace this pin. |
| ECMA-STRING | [ECMA-262 2025 §6.1.4](https://tc39.es/ecma262/2025/multipage/ecmascript-data-types-and-values.html#sec-ecmascript-language-types-string-type) | Defines UTF-16 string values, including unpaired surrogates. This domain differs from validated UTF-16 and from XML strings. |
| ECMA-ANNEX-B | [ECMA-262 2025 Annex B](https://tc39.es/ecma262/2025/multipage/additional-ecmascript-features-for-web-browsers.html#sec-regular-expressions-patterns) | Browser legacy additions require an explicit profile policy. Annex B support is a proposed decision, not an automatic consequence of a .NET ECMAScript option. |
| XSD-REGEXP | [XSD 1.1 Part 2, 5 April 2012, Appendix G](https://www.w3.org/TR/2012/REC-xmlschema11-2-20120405/#regexs) and [errata](https://www.w3.org/XML/2012/04/xmlschema-11-datatypes-errata.html) | Regex language and whole-value membership; class subtraction, XML name escapes and category/block definitions. The errata snapshot still needs classification before the contract freezes. |
| XPATH-REGEXP | [F&O 3.1, 21 March 2017, §5.6](https://www.w3.org/TR/2017/REC-xpath-functions-31-20170321/#string.match) | Regex extensions, flags, `matches`, `replace`, `tokenize`, `analyze-string`, and error contracts. Pin corrected Recommendation text and applicable errata during corpus intake. |
| POSIX-REGEXP | [Issue 7, 2018 edition, XBD §9](https://pubs.opengroup.org/onlinepubs/9699919799/basedefs/V1_chap09.html) | Proposed normative BRE/ERE baseline. Distinguish invalid constructs from undefined/unspecified behavior and optional extensions. |
| POSIX-API | [Issue 7 regex interfaces](https://pubs.opengroup.org/onlinepubs/9699919799/functions/regcomp.html) | Compile/match flags, return codes and offset rules. Direct retrieval failed in this research pass; complete normative verification is an implementation prerequisite. |
| POSIX-LOCALE | [Issue 8 locale environment definitions](https://pubs.opengroup.org/onlinepubs/9799919799/basedefs/V1_chap08.html) and [Issue 7 locale chapter](https://pubs.opengroup.org/onlinepubs/9699919799/basedefs/V1_chap07.html) | Locale decoding/classification/collation cannot be replaced with an arbitrary culture identifier. Issue 7 locale details still need a full audit; Issue 8 material is contextual, not authority for the proposed 2018 contract. |
| POSIX-2024-RATIONALE | [Issue 8 rationale A.9](https://pubs.opengroup.org/onlinepubs/9799919799/xrat/V4_xbd_chap01.html#tag_21_09) | Informative discussion documents changes, including minimal matching and BRE optional escape extensions. It is not the normative contract. |
| PCRE-PATTERN | [PCRE2 10.46 pattern manual](https://raw.githubusercontent.com/PCRE2Project/pcre2/pcre2-10.46/doc/pcre2pattern.3) | Tag-pinned syntax/semantics. Variable-length lookbehind has bounded lengths; unbounded repetition remains outside its accepted syntax. |
| PCRE-UNICODE | [PCRE2 10.46 Unicode manual](https://raw.githubusercontent.com/PCRE2Project/pcre2/pcre2-10.46/doc/pcre2unicode.3) | Code-unit widths, UTF/UCP distinction and validity handling; native build data must also be recorded. |
| PCRE-API | [PCRE2 10.46 API manual](https://raw.githubusercontent.com/PCRE2Project/pcre2/pcre2-10.46/doc/pcre2api.3) | Compile/match context, option and error inventory, resource budgets, callouts and match-data surface. Limits depend on build/context, so reference defaults are not universal capacities. |
| PCRE-LIMITS | [PCRE2 10.46 limits manual](https://raw.githubusercontent.com/PCRE2Project/pcre2/pcre2-10.46/doc/pcre2limits.3) | Capacity audit starting point; short/raw rendering needs checking against release source before fixtures pin numerical limits. |

These are sensible proposed baselines, **not claims that they are the newest editions**. 2025 ECMAScript and 10.46 PCRE2 were selected for reproducible existing references. The latest PCRE2 documentation and GitHub release page were inspected only to find versioned material; neither determines the contract.

## Corpus and oracle plan

| Candidate | Source and provenance to pin | Proposed usage |
| --- | --- | --- |
| Test262 | [official repository](https://github.com/tc39/test262), [license](https://github.com/tc39/test262/blob/main/LICENSE) | Select regex cases applicable to the pinned edition, preserving original IDs, metadata and harness requirements. The upstream license is BSD-style; retain notices and verify third-party/per-file terms before import. Tests exercise a JavaScript host, so classify host-only behavior rather than treating it as pattern-library behavior. |
| W3C QT3 | [official QT3 repository/catalog](https://github.com/w3c/qt3tests/blob/master/catalog.xml) | Select F&O function cases with their language/version/dependency metadata. Preserve permitted alternative results and errors. Freeze a commit, inspect package/per-file license and environment requirements before copying. No license clearance is claimed here. |
| W3C XML Schema tests | [official suite index](https://www.w3.org/XML/2004/xml-schema-test-suite/) | Extract relevant datatype/pattern fixtures and track 1.0 versus 1.1 applicability. Schema-validation wrappers must control whitespace/datatype facets so outcomes are attributable to regex membership. Review test-suite and contributor notices before import. |
| PCRE2 | [10.46 source/testdata](https://github.com/PCRE2Project/pcre2/tree/pcre2-10.46/testdata), [release license](https://github.com/PCRE2Project/pcre2/blob/pcre2-10.46/LICENCE.md) | Run tag-pinned `pcre2test`/standard match harness by width/build. The license identifies software/docs as `BSD-3-Clause WITH PCRE2-exception` and testdata as public domain; retain provenance and distinguish generated output from source data. |
| POSIX | [normative specification](https://pubs.opengroup.org/onlinepubs/9699919799/basedefs/V1_chap09.html) plus independently authored vectors | Design a small C harness using `regcomp`/`regexec` in pinned glibc and a second libc, with fixed locale files. Preserve oracle identity and discrepancy reports. Any future libc test imports need their own license review; no libc corpus has been copied. |

Each oracle manifest must contain the full executable/library version and digest, source commit/tag, adapter revision, machine/runtime, compile flags, input encoding, Unicode dataset revision, locale provider/data digest, capabilities and limits. A version range or an unpinned container tag is insufficient. Use the checked-in corpus pins for Test262/QT3 and runtime identities; smoke-test any replacement before generating expectations.

Oracle agreements are evidence, not specification authority. Keep source-derived vectors, imported corpus cases and differential discoveries distinguishable. Reduce divergences to small reproductions and record whether the cause is an oracle bug, profile mismatch, unspecified permitted behavior or GregsEx defect. Exclude one engine's nonstandard extensions from normative assertions.

## Material research limitations

- Full ECMA edition landing pages exceeded the browser fetch size limit. The official TC39 edition-pinned multipage pages were accessible and used instead. The versioned profile contracts pin their Unicode data independently.
- POSIX Issue 8 normative regex/interface pages failed direct retrieval; its official rationale was accessible. Issue 7 §9 was available through the official indexed page, but direct `regcomp` retrieval also failed. The proposed baseline stays Issue 7 (2018). Resolve the interface audit before implementation; no claim of completed Issue 8 support or audit is made.
- W3C recommendations were accessible; applicable errata, exact Unicode/XML domain decisions, unknown-block edge behavior and implementation-defined capture choices need frozen corpus decisions. This setup does not claim full XSD validator or XPath evaluator conformance.
- Native PCRE build character/Unicode tables, all extra option combinations, and fine-grained capacity defaults have not been experimentally measured. A later pinned harness must establish them. The setup imports neither PCRE code nor a native dependency into the library.
- Test suites and candidate reference engines have not been downloaded, licensed as a package, run or checked against GregsEx. No conformance pass rate can be reported at this stage. License review applies when content is actually imported, not merely linked.

Resolve source limitations under FL-ORACLE-001/002 and the flavor contracts; do not convert them into silent compatibility exclusions.
