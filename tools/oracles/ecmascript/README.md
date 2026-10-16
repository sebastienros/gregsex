# Fixed-input ECMAScript oracle tools

All scripts operate on frozen repository inputs without a new argument parser.
They are development artifacts, not product engines or runtime dependencies.
The pinned runtime command is `env -u NODE_OPTIONS /opt/homebrew/bin/node`.

Acquire the exact Test262 archive from
`https://codeload.github.com/tc39/test262/tar.gz/6a73fa8f114611269ce4442e8f19a31239cb3827`
into `/tmp/gregsex-test262.tar.gz` and extract it into `/tmp/gregsex-test262`.
`intake.py` freezes original files/metadata/notice/harness identities. PyYAML6.0.3
is intake-only; replay has no Python/YAML dependency. Do not refetch a mutable tag.

`qualify.mjs` checks twelve required capabilities and records executable, compiled
config, build receipt, license and linked-library identities. Commit that pin
before running expectation emission. `guard.mjs` rejects a changed executable,
versions/platform/architecture, ICU/shared-library identity or NODE_OPTIONS.
The qualified runtime is one explicit darwin-arm64 oracle, not a release RID claim.

`test262.mjs` runs the entire frozen selection and preserves every variant outcome.
Invoke with `--experimental-vm-modules` for the retained module tests. Fresh VM
contexts isolate test mutations; original assertion/sta/includes and cross-realm
helpers are retained. `print` is a nonasserting shell output hook. `recheck.mjs`
selects exactly the initial failed files after that hook repair, preserving the
original report and successful variants. It does not broaden or loop retries.

`recipes.py` emits source-authored inputs and separate independent golden
assertions. `corpus.mjs` runs the qualified builtin adapter, retains discrepancies
and fails on any independent-golden mismatch. `import.mjs` adapts original passing
semantic Test262 assertions. `portable.py` freezes the shared contract vectors and
separate source-only project resource recipes. `dispositions.py` records every
intake file's edition and exact existing object-boundary failure disposition;
an unexplained failed file aborts it. `minimize.mjs` records standalone witnesses.

After intentional source changes, regenerate only affected recipes/corpora and
`freeze.py` before committing their tested revision. `freeze.py` verifies every
existing Unicode input/output identity and freezes adapter/corpus/source identities
plus the complete requirement matrix. `replay.mjs` then validates artifacts, exact
native/imported expectations, independent contract semantics and negative identity
rejections. Source-only limits are explicitly reported as integration obligations.
Run the focused C# corpus suite and Release build after changing adapter inputs.
Neither fixed replay nor full Test262 asserts product conformance or timing.
