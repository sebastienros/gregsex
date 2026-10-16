# Pinned PCRE2 development oracle

This tool uses only retained PCRE2-10.46 source at b2bd4254, never an installed
unversioned library or any GregsEx matcher output. It is outside the shipped
package. Source archive, 488 member hashes, testdata/UCD originals and licensing
are retained in `tools/oracles/pcre2/source/`. Native executable/library/config/table
identities and raw results are retained in `test/corpora/pcre2/oracle/`.

These are fixed workflows with no project command-line argument parser:

```
python3 tools/oracles/pcre2/build.py
python3 tools/oracles/pcre2/upstream.py
python3 tools/oracles/pcre2/index-upstream.py
python3 tools/oracles/pcre2/observe.py
python3 tools/oracles/pcre2/normalize.py
python3 tools/oracles/pcre2/supplement.py
python3 tools/oracles/pcre2/supplement-upstream.py
python3 tools/oracles/pcre2/classify.py
python3 tools/oracles/pcre2/archive.py
python3 tools/oracles/pcre2/verify.py
```

`build.py` implements upstream NON-AUTOTOOLS-BUILD1–9 with Appleclang on this
host: native8/16, Unicode enabled, JIT/32/EBCDIC disabled. Extra native DFA,
conversion, serialization and POSIX wrapper objects are diagnostic-driver
requirements; their ABI/artifact equivalence is excluded from the managed
profile. They are never a production dependency. Generated configs/headers,
exact commands, compiler/platform and source/library/executable SHA256s are
recorded. No network retrieval or system-oracle substitution is needed.

`upstream.py` runs independently bounded numbered/native source families in both
widths with upstream RunTest selection. Exit0 with a skipped family is not a
semantic pass. `index-upstream.py` adds only diagnostic I/O observations around
upstream decoder/API boundaries, preserving original callback implementations.
It runs the same exact-golden selfchecks as the unmodified driver, snapshots
selected table payloads and replays skipped JIT mixed patterns in interpreter
mode and disabled-C errors with explicit NEVER_BACKSLASH_C. It executes original
Windows-localized recipes and records unavailable host-locale preconditions.
EBCDIC recipes are individually catalogued with their excluded alphabet/build.

`observe.c` is a native test client built once per width. `cases.py` emits
immutable numeric inputs and a static C header; it is not a regex/parser/matcher.
`observe.py` collects complete native compile/match/metadata/callback/substring/
iteration/substitution/case events, includes positive/negative/boundary cases,
and retains exact raw native JSONL. Input scripts explicitly register callbacks,
set signed returns and specify synthetic case outputs. The input/output format
is documented in `test/corpora/pcre2/FORMAT.md`. Undefined native fields stay
undefined; no-check validity and split-UTF continuation exclusions are source
supported and never used as managed success expectations.

`classify.py` annotates every reference modifier/control/member/native dispatch,
plus source lines not dispatched after native compile/modifier/locale errors.
It preserves logical obligations inside mixed native-only files. Raw byte/event
references point into the retained compressed archive. `archive.py` retains
source-linked native binaries/libraries/generated headers, raw logs and table
snapshots with per-member identities; `verify.py` checks source/protocol/meaningful
semantic witnesses and artifact identities without rerunning complete intake.

Re-execution is correctness preparation, not a performance measurement. No new
allocation counter, throughput/superiority claim, native physical-counter
promise, new RID or standalone .NET NativeAOT qualification follows. Keep useful previous
results when a later phase fails. Focused corrections do not justify automatic
whole-intake reruns. Avoid conflicting host work during measurements.

`capture-native.sh` forwards unchanged native arguments and copies actual output before RunTest cleanup. `capture-missing.py` repairs only missing retention; original full structured traces/matrix/builds stay preserved.

`fuzz-native.py` captures 256 deterministic bounded grammar cases across four
native domains; `reduce-native.py` captures 80 fixed reductions using the
fuzz workflow's prepared native libraries. Run them explicitly, in that order,
only when intentionally regenerating `test/corpora/pcre2/parity` observations.
They are development tools, not part of ordinary managed test execution.
Their move from the test project does not change retained expectations or
historical provenance.
