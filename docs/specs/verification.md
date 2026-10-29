# Verification requirements

Verify actual product behavior, not merely declaration compilation or fixture shape.
Keep existing semantic guarantees covered when adding a feature or optimization.

## Tests and oracles

Tests live in `test/GregsEx.Tests`, with profile-specific contracts, parser/runtime
checks, integration cases, conformance suites, and resource/lifetime tests.
Versioned corpora live in `test/corpora`; development-only oracle adapters and
Unicode generators live in `tools/oracles` and `tools/unicode`.

A fixture records its ID, requirements, source/license provenance, profile and
data versions, flags, input representation, operation, bounds, expected status,
ranges, capture participation, and diagnostic. Use numeric units for raw bytes
and lone surrogates. Preserve unmatched versus matched-empty captures and label
all offsets. Expected results come from standards or pinned independent oracles,
never from copying GregsEx output into golden data.

Pin oracle versions, build options, Unicode/locale data, source revisions, and
artifact hashes. A BCL baseline is an oracle only for an explicitly shared
semantic subset. Compare every observable result, not just match success.
Classify disagreements and unspecified behavior with minimized reproductions;
do not silently skip failures or count exclusions as passes.

Use deterministic fuzz seeds and adversarial inputs. Force reference and eligible
accelerated paths, including fallback and small-capacity failures. Cover capture
rollback, empty-match progress, surrogate handling, case folding, class algebra,
anchors, nonzero starts, lookbehind, POSIX ambiguity, and PCRE2 callbacks,
control verbs, and recursion.

## Resources, allocation, and lifetime

Follow [PERF-001 through PERF-016](performance.md). BenchmarkDotNet
`MemoryDiagnoser` is the sole allocation authority, retaining raw bytes and
operation counts. Controlled costs are acceptable with stable prepared capacity,
bounded ownership, and performance preservation. Do not round positive totals
to zero or rerun solely to force zero. Cold setup, growth, compilation, and owned
output have separate measurement scopes.

Use deterministic resource budgets and small imposed limits, not only timed
sleeps. Verify lifecycle/argument, destination, capacity, initial cancellation,
whole-subject validity, and scalar-boundary precedence. Empty inputs and immediate
results still poll the operation budget. Test checked overflow, temporary and
retained compilation admission, cache churn, catastrophic VM cases, cancellation,
pool return/reuse, concurrent separate workspaces, and immutable plan safety.

## NativeAOT, trimming, and package boundaries

Publish and run real consumers; analyzer-only builds do not establish deployment
compatibility. `test/GregsEx.AotSmoke` exercises ordinary interpreted and Auto
operations. `test/GregsEx.AotDiagnostics` checks expected explicit `Compile()`
diagnostics. `test/GregsEx.TrimSmoke` checks trimmed JIT behavior.
[aot.yml](../../.github/workflows/aot.yml) runs them on linux-x64 before each release
publish and on demand, not on pull requests or main.

The shared `UnicodeVersionSmoke` probe runs under unit tests, NativeAOT and
trimmed JIT. It covers both supported Unicode versions, version-specific new
letters/casing, native UTF-8 offsets, Auto selection and Unicode 17 generated
ECMAScript/XSD/XPath kernels. Default Unicode 16 corpora keep their original
expected results and source pins. Audit each retained version independently
with the ECMAScript/XML Unicode generation and audit scripts before changing
its manifest or generated data.

Every assembly is strong-named with the repository key `GregsEx.snk` (public key token `172acb106fb17aad`,
pinned by `StrongNameTests`): .NET Framework refuses to load an unsigned assembly from a strong-named consumer such
as TextMateSharp. The key pair is checked in on purpose, since strong names give identity, not security; keep it
across releases, and give friend assemblies `InternalsVisibleTo` with `$(GregsExPublicKey)`.

Ordinary interpreted/Auto consumers must remain warning-free. Explicit
`Compile()` carries `RequiresDynamicCode` and `RequiresUnreferencedCode`;
dedicated fixtures check IL3050/IL2026 and unsupported-runtime
`PlatformNotSupportedException`. Do not suppress warnings on ordinary APIs.
Inspect published artifacts to verify unreachable compiler/FEC code is removed.
Dynamic-code support alone does not prove metadata preservation after trimming.

Test direct char/byte span delegates, by-reference outputs, loops, branches,
capture updates, budget checks, unsupported lowering, and first invocation.
Disable FEC's Expression.Compile fallback. Compiler defects and resource
exhaustion remain failures, not successful fallback.

Inspect the package for a single runtime assembly, optional generator/build
assets, no exported FEC types, no FEC DLL or transitive source dependency,
and distributed license/data notices.

## Commands

Run from the repository root with the pinned SDK:

```sh
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --minimum-expected-tests 1
dotnet pack -c Release --no-build -o artifacts
```

`GregsEx.csproj` targets `netstandard2.0`, `net8.0` and `net10.0`; the Release
build must be warning-free for all three. A test executable cannot target
netstandard2.0, so the same suite also runs on the net10.0 host bound to the
net8.0 and netstandard2.0 assets (each builds under ignored `artifacts/GregsEx.Tests-<tfm>/`):

```sh
dotnet test --project test/GregsEx.Tests -c Release -p:GregsExTargetFramework=net8.0 --minimum-expected-tests 1
dotnet test --project test/GregsEx.Tests -c Release -p:GregsExTargetFramework=netstandard2.0 --minimum-expected-tests 1
```

Those runs load the shipped net8.0 or polyfilled netstandard2.0 `GregsEx.dll` (the latter
has no FastExpressionCompiler and uses scalar search fallbacks) on any OS. Tests of explicit runtime compilation and
netstandard-absent APIs are excluded there with `GREGSEX_NETSTANDARD_ASSET`; every other
result must match the net10.0 run. It does not exercise the .NET Framework runtime itself, which rejects some
collection behavior that .NET accepts (such as updating a dictionary value while enumerating it).

`test/GregsEx.Framework.Tests` (net472) runs the netstandard2.0 asset and `GregsEx.Generator.dll` on the .NET Framework
runtime: a compile-and-find per flavor, Oniguruma subexpression calls, and `KernelSource` emission. It is part of the
solution, so `dotnet test` from the repository root runs it on Windows; other hosts build it but skip it, since they have
no .NET Framework runtime. To run it alone on Windows:

```sh
dotnet test --project test/GregsEx.Framework.Tests -c Release --minimum-expected-tests 1
```

Use focused test selectors when appropriate. Deployment-specific commands are
defined by the relevant consumer projects and CI workflow. Packaging does not
authorize publication.

CI builds use `-p:ContinuousIntegrationBuild=True`. The test executable disables
deterministic source-path remapping because its `CallerFilePath`-based fixture
loaders need real checkout paths. The library and generator retain their normal
CI source-path mapping. To reproduce CI fixture loading locally, build with this
property before running tests with `--no-build`.

The .NET 10 SDK uses native Microsoft.Testing.Platform via `global.json`.
xUnit runs test collections in parallel by default; process-wide switch and
allocation-sensitive collections explicitly opt out. Keep those exclusions.
Use numeric code units for lone-surrogate theory arguments and construct their
strings inside the test, avoiding lossy metadata encoding and duplicate test IDs.

XML, POSIX and ECMAScript corpus integrity checks require the exact LF bytes pinned by the manifests.
The corresponding inputs, pinned source code, licenses, and imported oracle and
Unicode data have LF checkout rules in `.gitattributes`; do not regenerate
hashes to accommodate checkout line-ending conversion.

Independent XML, POSIX, ECMAScript, PCRE2 public-host, and frozen PCRE2 fuzz
recipes are individual `MemberData` rows keyed by stable fixture IDs. Keep
separate inventory/count checks, share only read-only corpus data, and create
mutable workspaces inside each test. The ECMAScript native consumer retains its
bulk runner. PCRE2 per-case reports are written under
`artifacts/conformance/pcre2/public-host-corpus/<id>.json` and
`artifacts/conformance/pcre2/fuzz-comparison/<id>.json`.
Do not split stateful upstream replay sequences, warmed-use loops, or exhaustive
fingerprint calculations into independent rows. Collection-level parallelism
does not run rows of the same class concurrently.

Rerun a single recipe by its displayed fixture ID, for example:

```sh
dotnet test --project test/GregsEx.Tests -c Release --no-build \
  --filter-display-name '*EveryIndependentPublicRecipe(id: "POSIX-C001")' \
  --minimum-expected-tests 1
```

If Microsoft.Testing.Platform reports zero tests because dynamic extension
loading is disabled, invoke the test application explicitly:

```sh
dotnet run -c Release --no-build --project test/GregsEx.Tests -- --enable-dynamic-extensions --minimum-expected-tests 1
```

Zero discovered tests is a failed verification, not a passing suite.

Record the tested revision, command, exit status, environment, case counts,
failures/skips, and input identities with retained validation output. Generated
test and benchmark output belongs in ignored `artifacts/` or a durable external
artifact store. Never overwrite checked-in oracle observations during tests.

## Acceptance

- **VERIFY-01**: Fixtures preserve representation, profile, results, and provenance; positive, negative, and boundary cases execute for every released capability.
- **VERIFY-02**: Standard-derived and pinned-oracle checks pass or have an explicit profile exclusion; disagreements retain minimized reproductions.
- **VERIFY-03**: Adversarial limits, invalid inputs, allocation, lifetime, and concurrency checks preserve explicit failures.
- **VERIFY-04**: Published consumers establish safe NativeAOT/Auto behavior and compiler removal; dedicated fixtures verify explicit compilation diagnostics and failures.
- **VERIFY-05**: Combined regression checks and package/license/API inspection cover the tested code revision and preserve earlier guarantees.
- **VERIFY-06**: Performance comparisons retain host conditions, artifact identities, and applicable thresholds; absent or noisy measurements establish no speed claim.
