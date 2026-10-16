# Pinned PCRE2 data projection

Fixed no-argument tools use the retained archive in `tools/oracles/pcre2/source/`,
not an installed library, .NET Unicode APIs, another flavor's tables or a network
download. Run from the repository root:

```sh
python3 tools/oracles/pcre2-data/source.py
python3 tools/oracles/pcre2-data/generate.py
python3 tools/oracles/pcre2-data/verify.py
```

Source intake validates the archive and all 488 members before writing a compact
manifest of all 13 actual Unicode inputs, generators, generated outputs, relevant
algorithms and license. Generation requires that durable intake. Verification
reproduces every managed product and the native-ID alias fixture, reruns all three
unmodified upstream Unicode generators in temporary storage, compares their
outputs byte-for-byte with the release, and compiles a read-only C table exporter.
The exporter verifies all 1,114,112 codepoint rows against the managed projection's
canonical SHA256. It never compiles or executes a regular expression. A C99 `cc`
compiler and Python 3 are the only source-tool dependencies. Temporary files are
bounded by the fixed source input and removed after the run.

`Pcre2Data.Instance` implements the frozen `IPcre2Data` in
`GregsEx.Flavors.Pcre2.Tables`. Call `Bind(profile.CharacterTables)` once and keep
its borrowed readonly spans. No per-character interface call is needed. Bind
reuses immutable Unicode owners and the descriptor's exact copied low-byte
payload. Records use the frozen native property IDs, 128-codepoint blocks,
signed OtherCaseDelta, counted cases without NOTACHAR, six-word script sets
and two-word boolean sets. Offsets are absolute word offsets. Source nonzero
script-extension sets are expanded from four words; a zero extension uses the
direct source script ID. DigitSet is the native 1-based digit-vector index,
zero for nondigits. Its presence splits nine source records and recompresses
stage1/2 without changing any source properties.

Concrete `TurkishDottedI` and `TurkishDotlessI` spans retain the source's two
unreferenced optional case sets. `DigitSetEnds` contains the native '9' values
without the leading count; `GraphemeNoBreakMasks` contains the source 15 adjacency
masks. Contextual regional-indicator/emoji rules and script-run algorithms belong
to the Unicode operation owner, not this data provider.

`OutputCases` is sorted by codepoint; an absent entry is identity. It projects
the pinned substitute.c default case_force behavior in Unicode mode, including
its documented category/OtherCase quirks. Title-first uses upper on the first
character, not Unicode title mappings. ASCII entries are the C-default source
behavior. The host owner must use the requested low-byte class/flip tables for
ASCII and for Unicode-disabled low bytes; imported payloads are never replaced
by these default entries. This data does not implement substitution or callbacks.
Entries are codepoints before native-width output conversion. In non-UTF native8
with UCP, the pinned source's PCRE2_UCHAR assignment truncates transformed values
to eight bits; the host must retain that source behavior rather than treat this
map as already encoded native output.

Property resolution supports the source's loose ASCII matching and actual510
aliases, with only source-supported explicit sc/scx/bc prefixes. PT_SC aliases
remain Script even under explicit scx. Native PT special IDs are retained rather
than their otherwise-zero native value. Syntax owns braces, negation, explicit
empty-value rejection, delimiters, source errors and compile-budget charging.
The helper accepts split name/value or unsplit `sc=Latin`; it uses fixed bounded
scratch and an ordinal binary search. It does not promise support for additional
Unicode long category/bidi names absent from the source alias table.

`IsValidGroupName` is the canonical lexical helper ratified at activation. It
uses actual L/Nd/underscore rules in UTF and C-default ctypes_word otherwise.
Syntax must check native-width MAX_NAME_SIZE and requested imported raw tables,
ASCII leading-digit prohibition and MAX_255 directly; it must never filter an
imported raw name through the C-default helper. No table/provider retains a
borrowed caller-owned buffer; imported descriptors copy exactly once and preserve
arbitrary 1088-byte payloads and their exact SHA256.

Primitive constant spans use compiler-emitted static data. Three private fixed
arrays own records, sparse output cases and aliases, initialized once. Their
sizes are manifest-bounded; no runtime data file, decompression, pool, Unicode
culture lookup or per-bind copy is used. Static setup and imported descriptor
construction are distinct from warmed table access; no speed claim follows.

The four maintained data benchmarks live in
`benchmarks/GregsEx.Benchmarks/Pcre2/Pcre2DataCosts.cs`. They measure prepared
binding, array-owner binding, hit/miss property resolution, and canonical
ASCII/Unicode name checks using BenchmarkDotNet 0.15.8, MemoryDiagnoser and
ShortRunJob:

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*Pcre2DataCosts*"
```

The standard runner uses the benchmark assembly's existing friend boundary and
ordinary out-of-process toolchain; no test-assembly impersonation or custom
project-selection generator is needed. Prepare Release builds before a bounded
measurement sequence on a quiet host. Managed allocation acceptance uses BDN
alone, preserves raw totals/operation counts and accepts controlled costs.
This command does not establish NativeAOT compatibility; deployment checks are separate.

The package must include generated BSD/Unicode notices; data generation alone does not verify package contents.
