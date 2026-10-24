# Oniguruma oracle tools

Development-only tools for the Oniguruma flavor (`Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1`). Everything is built
from public, pinned inputs, and the Oniguruma version is a parameter, so adopting a release is a re-run (see the
[upgrade checklist](#upgrading-oniguruma)). Generated output stays in ignored `artifacts/oniguruma`; the retained
outputs are [`test/corpora/oniguruma`](../../../test/corpora/oniguruma/README.md) and
`src/GregsEx/Flavors/Oniguruma/Tables`. Run every command from the repository root, on macOS or Linux.

| Path | Role |
| --- | --- |
| `native/oracle.c`, `native/build-oracle.sh` | C shim over Oniguruma (`onig_new` with UTF-16LE, `ONIG_SYNTAX_DEFAULT`, `ONIG_OPTION_CAPTURE_GROUP`; `onig_search` from an offset; captures or error codes), compiled with Oniguruma's sources at a tag verified by commit SHA |
| `native/not-code-range-underflow.patch` | The profile's one change to Oniguruma: a nested negated class holding U+0000 gets its true complement (UTF-8 Oniguruma's, so vscode-oniguruma's), not every code point (`docs/specs/oniguruma.md`) |
| `fetch-inputs.sh` | tm-grammars by npm version and integrity, and the samples of shikijs/textmate-grammars-themes at a commit |
| `oracle/` | `corpus`, `cases` and `differential`; P/Invokes the shim |
| `generate.py` | The Unicode tables from the Oniguruma checkout |

## Build the oracle and fetch the inputs

```bash
tools/oracles/oniguruma/native/build-oracle.sh v6.9.10 4ef89209a239c1aea328cf13c05a2807e5c146d1
tools/oracles/oniguruma/fetch-inputs.sh
```

`build-oracle.sh` clones the tag into `artifacts/oniguruma/<tag>/src`, applies the patch, and writes
`artifacts/oniguruma/<tag>/liboniguruma-oracle.{dylib,so}` with the system C compiler (no autotools or CMake).
`fetch-inputs.sh [<tm-grammars-version> <npm-integrity> <samples-commit>]` writes `artifacts/oniguruma/inputs`; its
defaults are the pins of the corpus README. `oracle` options: `--oniguruma <tag>` (default `v6.9.10`) selects the
library, `--inputs <dir>` the fetched inputs.

## Unicode tables

`generate.py` reads the checkout and writes `OnigurumaUnicodeData.g.cs`: Oniguruma's own Unicode property ranges and
names (`unicode_property_data.c`), simple and multi-code-point case folds (`unicode_fold_data.c`) and
Grapheme_Cluster_Break ranges (`unicode_egcb_data.c`), with the SHA-256 of each source in the header. Oniguruma's
BSD-2-Clause notice is `src/GregsEx/Flavors/Oniguruma/ONIGURUMA-NOTICE.txt`.

```bash
python3 -I tools/oracles/oniguruma/generate.py artifacts/oniguruma/v6.9.10/src src/GregsEx/Flavors/Oniguruma/Tables/OnigurumaUnicodeData.g.cs
```

## Corpus

`edge-cases.json` is written by hand. The grammar cases come from the inputs, then the oracle records expectations:

```bash
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- corpus test/corpora/oniguruma
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- cases test/corpora/oniguruma/edge-cases.json test/corpora/oniguruma/edge-cases.expected.json
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- cases test/corpora/oniguruma/grammar-cases.json.gz test/corpora/oniguruma/grammar-cases.expected.json.gz
```

Then update the SHA-256 list and provenance in the corpus README (`CorpusTests` checks the hashes).

## Full differential

```bash
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- differential [--report artifacts/oniguruma/differential.json]
```

Every distinct begin, end, while and match pattern of every tm-grammars grammar (with the hosts' `\xHH` rewrite) is
compiled by both engines; patterns native rejects must be rejected by GregsEx and the reverse. Each pattern is then
searched from starts 0, 1, 2, 3, 5, 7, 11, 13, 21, 34, the middle and the last unit of every distinct line (with `\n`)
of each sample whose grammar closure contains it, and from 0, 9 and the middle of a cross-language mix (the first four
non-blank lines of every sample). Captures must agree exactly. Near the retry limit the engines' budgets differ by
design (OG-005 in `docs/specs/oniguruma.md`): where native reports a retry-limit error from some start of a subject,
`LimitExceeded` from any start of it, or a result where native reports the error, is counted, not a mismatch. It exits
1 on any mismatch; the report holds counts and up to 10,000 mismatches.

Run of 2026-10-24 (v6.9.10 with `not-code-range-underflow.patch`, tm-grammars 1.32.22, samples 8f1828eb; Apple M4 Pro, macOS 15.8.1, .NET 10.0.11, 24 s):
15,034 patterns from 260 grammars (83 rejected by both), 238 samples and a mix of 912 lines: 88,475,049 searches,
10,782,960 matches, 95 native retry-limit errors (92 `LimitExceeded`, 3 completed GregsEx searches), 0 mismatches.

The `Oniguruma differential` workflow (`.github/workflows/oniguruma-differential.yml`, manual and weekly) builds the
oracle on Linux and runs it.

## Upgrading Oniguruma

1. Read the Oniguruma release notes (`HISTORY`, the GitHub release) between the pinned tag and the new one for syntax,
   option, Unicode and matching changes, and decide: a change in observable results needs a new side-by-side profile,
   `Oniguruma-<version>-OnigurumaSyntax-Utf16-v1` (versioned flavor semantics: AGENTS.md, `docs/specs/flavors.md`),
   with the old profile kept as it is; only a release with no observable change may re-pin the existing profile, and
   then the differential below must still show zero mismatches against the old expectations.
2. Build the oracle for the new tag, giving its commit SHA (drop or rebase `not-code-range-underflow.patch` if the
   release fixes or moves `not_code_range_buf`) (`git ls-remote https://github.com/kkos/oniguruma <tag>`,
   peeled `^{}` for an annotated tag): `native/build-oracle.sh <tag> <sha>`.
3. Regenerate the Unicode tables: update the version and `PIN` in `generate.py`, run it on
   `artifacts/oniguruma/<tag>/src`, and review the diff (property names, ranges, folds, data identity in
   `docs/specs/oniguruma.md`).
4. Optionally move the inputs (`fetch-inputs.sh <version> <integrity> <commit>`: `npm view tm-grammars@<version>
   dist.integrity` and the textmate-grammars-themes commit that released it) and update the defaults in the script.
5. Re-record the corpus with `--oniguruma <tag>` (`corpus`, then both `cases`), review the expectation diffs against
   the release notes, and update hashes and provenance in the corpus README.
6. Run the full differential with `--oniguruma <tag>`; resolve every mismatch in GregsEx or in the contract.
7. Update `docs/specs/oniguruma.md` (versions, data identity, OG-007 evidence counts), `docs/specs/flavors.md`,
   `src/GregsEx/Flavors/Oniguruma/ONIGURUMA-NOTICE.txt` (the tag's `COPYING`), the pins here and in the CI workflow.
8. Release GregsEx and move consumers (TextMateSharp's GregsEx package version, and its profile if a new one was
   added).
