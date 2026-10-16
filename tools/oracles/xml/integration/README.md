# XML corpus join procedures

Run from repository root. These fixed no-argument development procedures neither parse options nor run counters, processors or product matching.

```
python3 tools/oracles/xml/integration/prepare.py
python3 tools/oracles/xml/integration/verify.py
dotnet restore tools/oracles/xml/integration/SchemaCheck.csproj --ignore-failed-sources --disable-parallel
dotnet run --project tools/oracles/xml/integration/SchemaCheck.csproj -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false
```

Preparation writes only the owned combined manifest and index. Verification is read-only and recomputes their exact bytes, complete index partitions and every retained child artifact digest. The formal checker assembles an in-memory union from the unchanged original files and evaluates the frozen Draft2020-12 schema, including the schema's own meta-schema; it writes no corpus. Six `GregsEx.Tests.Conformance.Xml.Integration` checks use `JoinedCorpus.Read()` to resolve every entry to the original typed `XmlFixture`, verify artifacts, check seed/permitted-set equality, map all inventory roles and diagnostic sidecars, compare exact observed/expected disagreement structures, and preserve original query skip identities. Negative index checks reject incomplete/duplicate/misdirected/unknown-comparison entries.

`index.json` is `xml-joined-index-1`: id, lane and ordinal resolve a recipe; oracleComparison is agreement, reviewed-difference or source-only; diagnosticPolicy is copied verbatim from that lane's sidecar, null for success. `manifest.json` is `xml-joined-manifest-1`: frozen interchange/data identity, complete per-lane locations/counts/revisions, index identity, artifact path/byte-length/SHA256 locks, original QT3 accounting and ratified/downstream policies. These are task-local join formats, without changing the shared fixture schema. Original recipe/provenance and sidecar data remain authoritative. All paths are repository-relative; read from the repository root, reject traversal/absolute paths when consuming.

The consumer checks standard errors' code/namespace/domain/native units/bounds through the existing frozen `XmlFixture.Validate()`. A product runner must compare the actual product result against the full permitted set and apply the explicit diagnostic policies. Source witnesses and whole-argument envelopes must never be converted into fabricated parser pinpoint expectations. Frozen seed diagnostics and first-offending-native offsets stay exact. Real parser-coordinate tests, every XML product recipe and cumulative resource/native/allocation/AOT gates remain downstream. No new processor execution or product pass is implied by this join.
