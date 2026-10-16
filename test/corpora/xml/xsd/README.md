# Independent XSD 1.1 intake

`corpus.json` is `xml-fixtures-1`, using the verified contract's exact numeric pattern/input units, profile/data identity, permitted structured outcomes and provenance. It contains 4,811 recipes authored from the ratified REC/XML/Unicode sources or imported from a pinned MIT-licensed independent suite. It does not contain GregsEx engine outputs. `coverage.json` maps all twelve capabilities to positive, negative and boundary witnesses and records every admitted category and block name. The generator has no product engine dependency.

`processor-pin.json` identifies actual retained xmlschema4.3.2 and elementpath5.1.3 wheels, byte lengths, SHA256 and licenses. Independent test source is the original upstream `tests/test_regex.py` at commit300f8bc21e1323294c756d8977a35cfa606e8f30 (releasev5.1.3); the exact original is retained under the vendor directory. Twenty-four imported membership cases preserve their method names and independently reviewable expected booleans. The upstream source, MIT notices and wheel content survive checkout cleanup; installation is disposable.

`inputs/UnicodeData.txt` is the pinned Unicode16 original, byte-checked against the XML source register. `Blocks.txt` was independently retrieved from the dated Unicode16 URI and matched the same contract SHA256. The Unicode license is retained. Generation expands First/Last records, derives implicit Cn and single-letter unions, clips samples to XML Char, and never asks ambient Unicode for an expected result. All338 current block names and the three explicitly ratified legacy aliases are represented; surrogate-only blocks receive negative/complement witnesses because the positive scalar domain is empty. New16 assignments, undefined scalars, private-use endpoints and XML supplementary noncharacters are included.

Original GregsEx recipes/tools are MIT under the retained repository license. Imported elementpath tests and oracle wheels retain their own MIT copyright notices. Unicode input data retains Unicode LicenseV3. W3C standards are authority references, not relicensed copied suites; the independently written recipes are attributed to the dated [XSD REC](https://www.w3.org/TR/2012/REC-xmlschema11-2-20120405/), [XML Fifth Edition](https://www.w3.org/TR/2008/REC-xml-20081126/) and the human-ratified contract. Contract seed records preserve their original provenance unchanged.

## Oracle isolation and disagreements

The runner invokes actual `xmlschema.XMLSchema11`, checks an XSD1.1-only assertion facet, constructs an xs:string restriction containing exactly one pattern, and evaluates its actual pattern facet on the supplied scalar string. It cross-checks the type's validation result, ensuring no other facet/whitespace behavior affects the result. Schema pattern attribute serialization uses numeric references for whitespace; each built facet is checked against the exact original pattern. Subjects are supplied directly as strings, without XML serialization, entity decoding or whitespace normalization. A separate host audit proves xs:string preservation versus xs:token collapse, same-restriction pattern union and literal entity text. Schema datatype processing is never substituted for direct membership.

Unicode16 is selected explicitly from elementpath's static versioned data; Python3.14.2's ambient Unicode happens also to be16 but does not derive golden data. Processor output records the actual interpreter executable/hash/platform, wheel identity, exact schema/translator options, Unicode selection and audited XML-name policy. `processor-results.json` retains every actual result and translated pattern. There are 4,771 executed oracle-applicable rows: 4,732 membership,34 rejected patterns and5 accepted parse-only rows. Forty rows are explicitly not applicable to this processor interface: native malformed encodings, XML-domain/pattern violations, representation and library resource contracts. They remain in the product corpus, uncounted as oracle passes.

`disagreements.json` retains all49 reduced disagreements, exact observed outcomes and source expectations. The six dispositions concern Python-style predefined escapes outside classes, supplementary XML names omitted from the processor, escaped range endpoint parsing, unknown-block U+10FFFF exclusion, a nonstandard NoBlock extension and permissive syntax admission. Every source expectation remains required on GregsEx; no capability exclusion or oracle-majority change occurs. Published-text dispositions D1/D2 remain the already ratified GregsEx choices, not invented W3C errata. These are audited oracle limitations, not new source conflicts.

Product tests must execute this whole corpus against actual GregsEx implementations, including all disagreement rows and source-only diagnostics/limits/group0. This intake's interchange tests and processor replay do not certify a product matcher, allocation, performance, AOT or release support. Schema/context-sensitive and lifecycle budget procedures beyond the fixture's existing override fields are detailed in `requirements.md`; no shared-schema change was made.

## Reproduction

Run from the repository root, using the fixed System.CommandLine development CLI:

```
dotnet restore tools/oracles/xml/xsd/XsdOracle.csproj --ignore-failed-sources --disable-parallel
dotnet build tools/oracles/xml/xsd/XsdOracle.csproj -c Release --no-restore --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet tools/oracles/xml/xsd/bin/Release/net10.0/XsdOracle.dll generate
dotnet tools/oracles/xml/xsd/bin/Release/net10.0/XsdOracle.dll schema
dotnet tools/oracles/xml/xsd/bin/Release/net10.0/XsdOracle.dll run
dotnet tools/oracles/xml/xsd/bin/Release/net10.0/XsdOracle.dll verify
```

`--python` explicitly chooses another Python executable; default is the inspected local `/opt/homebrew/bin/python3`. Python scripts are fixed no-argument internal jobs; all new CLI argument parsing belongs to System.CommandLine2.0.12. Oracle processes have a120second finite ceiling. No counters, timing or BenchmarkDotNet are invoked. On another platform processor/runtime identity fields can legitimately differ; results, Unicode selection, corpus digest and dispositions must still verify. Wheels reinstall locally from retained hash-checked artifacts, requiring no package network during replay.
