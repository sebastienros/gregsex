# POSIX development corpus and libc audit

Run from the repository root. This tool is not shipped or invoked by GregsEx. New option parsing uses System.CommandLine2.0.12. Fixed internal Python procedures accept no arguments.

```
dotnet restore tools/oracles/posix/PosixOracle.csproj
dotnet run --project tools/oracles/posix/PosixOracle.csproj -c Release --no-restore -- generate
dotnet run --project tools/oracles/posix/PosixOracle.csproj -c Release --no-restore -- prepare
dotnet run --project tools/oracles/posix/PosixOracle.csproj -c Release --no-restore -- verify
dotnet run --project tools/oracles/posix/PosixOracle.csproj -c Release --no-restore -- schema
```

`generate` writes only the source-authored expansion. It reads immutable contract identity and never imports native or GregsEx outputs. `prepare` joins exact recipes/raw observations and fails on a difference without an explicit source-reviewed resolution; it never modifies expectations. `verify` rechecks original contract locks, all artifact hashes, every native request/result, both locale tables, the complete comparison index and source evidence. `schema` validates both original corpora with the frozen Draft2020-12 schema. The committed gzip observations permit replay without Docker or network.

Actual native intake is pinned in `oracle-lock.json`. The helper is C with its own native build conventions. The prepared OCI image has an exact ID and is retained locally; `run` addresses that ID, not a mutable tag. To repeat an audit on that exact image:

```
dotnet run --project tools/oracles/posix/PosixOracle.csproj -c Release --no-restore -- run
```

This is a bounded untimed semantic job; it cannot claim controlled performance or allocation evidence. A fresh image can be built with `docker build --progress plain -t gregsex-m4-posix-oracles:local tools/oracles/posix/native`, but Debian package repositories can change. Fresh source/package/helper/library/image identities must match the recorded lock or be explicitly reviewed as a new intake before adopting results. A rebuild is not permission to silently change the lock. The Dockerfile pins the base digest and upstream musl version; actual distribution package/source versions and all observed archives/binaries are locked separately. Native `.so`/executables are not shipped. Original source archive URLs/hashes, actual regex sources and license notices are retained for audit. Future source re-intake may use the recorded immutable Debian file names or an appropriate upstream archive, with hash verification.

The original 138 seeds/schema/table and 7,127 expanded exact-numeric recipes use versioned join/index formats. `manifest.json` names both original files and locks every input; no shared schema was changed. All20 capabilities remain required. Defined disagreements, undefined/unspecified strict choices, high-byte dispositions and native-only limitations are explicit in `test/corpora/posix/oracle/disagreements.json`. No BCL/native forwarding, invented oracle-coordinate guarantee or majority voting is allowed.

Real product integration uses the test-only `GregsEx.Tests.Conformance.Posix.PosixConformanceRunner` in the existing test assembly:

- `PosixFixture[] ReadAll()` validates the join and returns every immutable seed and expansion in order.
- `int Run(Func<PosixFixture, PosixExpected> observe)` calls a real integration consumer exactly once per fixture; any exception or unmatched full permitted result fails with its ID.
- `bool Equivalent(PosixExpected expected, PosixExpected actual)` compares every asserted status, participation/capture/range/native endpoint, diagnostic/location/disposition and output-publication field. Only null expected GroupCount leaves metadata unspecified.

The callback must invoke actual typed Parse/TryParse, RegExec/RegError and generic workspaces, configure each recipe's representation/limits/cancellation/destination/startAt, and retain real prefilled output sentinels. It must emit only the operation's actual outputs, including group0-only range output, whole capture vectors, all requested native slots, destination-unchanged and exact RegError bytes/required count. Native-source-only means no comparable libc operation, not a product skip. Integration must execute these21 recipes too. The runner has no product/native placeholder and does not create an observation from its own expected values. Its eight artifact/comparator tests verify interchange and fail-closed comparisons; they do not establish POSIX engine correctness.
