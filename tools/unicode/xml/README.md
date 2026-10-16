# Frozen Unicode16/17 and XML1.0 Fifth Edition data

Fixed-input, offline generation: `python3 tools/unicode/xml/generate.py`.
The standard command processes both fixed configurations: Unicode16 in this
directory and Unicode17 in `17.0.0/`, each with its own original `inputs/`,
`inputs.json`, `emitted.json` and `outputs.json`. Every original input is retained,
with its exact source URL, byte length/SHA256 and license in `inputs.json`, and
verified before generation. Original16 inputs and their manifest remain unchanged.
Unicode16 UnicodeData, SpecialCasing and ReadMe were copied from the
pinned ECMAScript Unicode inputs, then independently matched to the XML source hashes.
Blocks.txt and XML Recommendation were retrieved from their dated official URIs
and matched to that same register. Unicode License V3 is retained, including its
copyright/permission terms, beside both the originals and generated runtime source.
XML Recommendation use follows the linked W3C document license. No host Unicode
category/case API supplies generated or production behavior.
Unicode17 UCD files exactly match members of the retained official archive at
`../ecmascript/17.0.0/UCD.zip`; their individual official source URLs are under
`https://www.unicode.org/Public/17.0.0/ucd/`. The dated XML Recommendation and
Unicode License V3 bytes are unchanged between datasets. Scoped `.gitattributes`
preserves the pinned original LF bytes on checkout.

`python3 tools/unicode/xml/audit.py` independently reads the original inputs without
importing the generator, enumerates every XML scalar's category, verifies all exact
blocks and legacy aliases, full lower/upper sequences and sparse identity partners,
and independently checks XML [2]/[4]/[4a] ranges, for both datasets. It checks emitted C# constants and
lookup slices as well as the sidecar dataset; a digest alone is not completeness.
Runtime tests separately compare the actual provider to original Unicode files.
No fixture expectations use live sibling outputs or GregsEx regex engines.
The audit also verifies complete table inventories, sparse row uniqueness,
static-size accounting and output-manifest identities.

Generated17 uses `GeneratedXml17Tables` with exactly the member API of
`GeneratedXmlTables`; the16 generated C# and sidecar bytes remain unchanged.
Both datasets use the same XML1.0 Fifth Edition Char/NameStart/NameChar domain;
selecting Unicode17 does not advance the XML policy.

| Dataset | Categories | Blocks, including3 legacy aliases | Case rows | Expansions | Intervals | Static image bytes | Literal bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Unicode16 | 36 | 341 | 2,981 | 73 | 7,706 | 97,420 | 22,184 |
| Unicode17 | 36 | 349 | 3,037 | 73 | 7,793 | 98,788 | 22,708 |

Every audit enumerates all1,112,033 XML-legal scalar values. Concrete17 witnesses:
U+16EA0/U+16EBB (Beria Erfe ARKAB, upper/lower); U+A7CE/U+A7CF (Latin
pharyngeal voiced fricative); letters U+10940 (Sidetic), U+11DB0 (Tolong Siki)
and U+1E6C0 (Tai Yo). All were unassigned in16.
The8 new blocks, with exact source bounds, are:

| Block | Bounds |
| --- | --- |
| Sidetic | U+10940–U+1095F |
| Sharada Supplement | U+11B60–U+11B7F |
| Tolong Siki | U+11DB0–U+11DEF |
| Beria Erfe | U+16EA0–U+16EDF |
| Tangut Components Supplement | U+18D80–U+18DFF |
| Miscellaneous Symbols Supplement | U+1CEC0–U+1CEFF |
| Tai Yo | U+1E6C0–U+1E6FF |
| CJK Unified Ideographs Extension J | U+323B0–U+3347F |

Runtime block spelling removes spaces/underscores, preserving the existing case
and hyphen rules (for example `BeriaErfe` and `CJKUnifiedIdeographsExtensionJ`).

Runtime types in GregsEx.Flavors.XmlShared:
- XmlUnicodeData.TryCreate charges the complete immutable static image, conservative
  literal metadata and provider object for EVERY admission. Fixed image charge is
  disclosed by RetainedBytes; no invisible cache or runtime table copy. Consumers
  charge lookup work once at compile boundaries; matching binds readonly spans.
- XmlCharTextReader and XmlByteTextReader implement IXmlTextReader, whole-subject XML
  validity with native work/offsets, initial polls and deferred scalar start check.
  TryRead rejects malformed/out-of-domain values; executor charges its reads.
- XmlClassAlgebra implements normalized union/subtract/complement/direct case variants.
  It clips inputs to XML Char and performs iterative charged heap sorts. All scratch
  arrays and final exact output copies are pre-admitted; failure releases scratch,
  successful retained output bytes stay reserved in the same caller budget.
  Budget units are documented in its source. Missing sparse case rows mean identity;
  no transitive closure or multi-character consumption is added. Apply literal/range
  case expansion before class negation/subtraction; property escapes are unchanged.

Maintained primitive benchmarks live in `benchmarks/GregsEx.Benchmarks/Xml`,
using the standard BenchmarkDotNet runner:

```sh
dotnet run -c Release --project benchmarks/GregsEx.Benchmarks -- --filter "*XmlSharedBenchmarks*"
```

The two UTF-16/UTF-8 cases retain their semantic fixture, 8192 warmup cycles,
MemoryDiagnoser/ShortRunJob, and in-process job mutator
(one launch, three warmups, three measured iterations, 150 ms each).
Historical control/diagnostic and host-specific isolated runners are retired.
Serialize measurements and keep unrelated host work outside the interval.

`probe/Probe.csproj` links the same fixture for a counter-free semantic smoke
check and can also publish under NativeAOT. Unicode generators and audits remain
independent of the benchmark runner. These primitive checks do not establish
complete XML regex parser/executor/public binding conformance.
