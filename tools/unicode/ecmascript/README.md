# ECMAScript Unicode 16/17 and Emoji 16/17 data

`python3 tools/unicode/ecmascript/generate.py` is a fixed-input offline generator,
without a CLI argument parser, network access or host Unicode dependency. It checks
all retained input lengths/SHA256 hashes before writing. A fixed loop processes
Unicode16/Emoji16 in this directory and Unicode17/Emoji17 in `17.0.0/`.
Each directory has its own `inputs.sha256.json` and `outputs.json`, recording
source URLs, original byte lengths/SHA256, generator/output identities and counts.
`python3 tools/unicode/ecmascript/audit.py` independently enumerates source sets and
compares every emitted category/script/scx/binary/string member, exact alias,
generated test-inventory entry and case mapping for both datasets. It also checks
normalized interval/slice coverage, string ordering, static-size accounting and
output manifests; it does not import the generator.
Neither script imports ambient Python Unicode category/case data.

Original16 inputs/manifests remain unchanged. Original17 inputs are retained in
`17.0.0/inputs/`, together with the original UCD archive at `17.0.0/UCD.zip`.
Both generation and audit verify that every retained17 UCD file exactly matches
its archive member. Official17 locations are
`https://www.unicode.org/Public/17.0.0/ucd/UCD.zip`,
`https://www.unicode.org/Public/17.0.0/ucd/` and
`https://www.unicode.org/Public/17.0.0/emoji/` (not `/Public/emoji/17.0/`).
The archive is 9,101,877 bytes with SHA256
`2066d1909b2ea93916ce092da1c0ee4808ea3ef8407c94b4f14f5b7eb263d28e`.
Scoped `.gitattributes` preserves original LF bytes and binary archive bytes.
The exact UCD source files, headers and current official Unicode License V3 are
retained; license bytes are pinned (copyright 1991–2026) rather than refetched on
regeneration. The license is also copied beside runtime source. Package notice
composition is requested from the integration owner. The edition-16 admission
JSON contains exact code cells from ECMA tables69–71 and full retrieved-page
length/SHA256/source URL. Facts in those tables determine admissible property names;
the complete, version-specific UCD alias files determine admissible property values.
The edition16 JSON is byte-identical for both datasets: this does not admit extra
Unicode17 binary property names beyond the existing53-property whitelist.

Generated17 uses `GeneratedUnicode17Tables` and `Unicode17Inventory`;16 retains
`GeneratedUnicodeTables` and `UnicodeInventory`. Corresponding runtime classes
have identical member APIs. The generator preserves the checked-in16 C# bytes,
including its formatting, and16 inventory bytes. Output-manifest generator hashes
advance when tooling changes; the earlier16 manifest's C# byte/hash entry is
corrected to the already-formatted checked-in source.

| Dataset | Properties | Script values | Intervals | C/S folds | Legacy uppercase | RGI emoji | Static image bytes | Literal bytes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Unicode16 / Emoji16 | 442 | 172 | 22,925 | 1,484 | 1,166 | 3,790 | 327,968 | 24,686 |
| Unicode17 / Emoji17 | 450 | 176 | 23,205 | 1,512 | 1,169 | 3,953 | 340,816 | 25,042 |

Both datasets have38 category values,53 binary and7 string properties.
Unicode17 contains5,520 finite strings and22,392 string code points (16:
5,208 and20,426).17 string-member counts are Basic_Emoji1,400, keycap12,
flag259, modifier665, tag3, ZWJ1,614, and RGI union3,953.

Concrete17 additions, independently covered by the complete source audit:
- `Script=Beria_Erfe` / `Berf`: U+16EA0 BERIA ERFE CAPITAL LETTER ARKAB,
  folding to U+16EBB; the XML full upper/lower mapping is the same pair.
- `Script=Sidetic` / `Sidt`: U+10940; `Script=Tai_Yo` / `Tayo`: U+1E6C0;
  `Script=Tolong_Siki` / `Tols`: U+11DB0. All are letters, previously unassigned.
- U+A7CE/U+A7CF LATIN PHARYNGEAL VOICED FRICATIVE is a new BMP case pair;
  Unicode folding maps capital to small and legacy uppercase maps small to capital.
- Basic_Emoji U+1FAEA DISTORTED FACE; modifier sequence
  `1F46F 1F3FB` (people with bunny ears, light skin tone); ZWJ sequence
  `1F468 1F3FB 200D 1F430 200D 1F468 1F3FC`
  (men with bunny ears, light/medium-light skin tones). These are absent from16.

Runtime entry: `EcmaUnicodeData.TryCreate(ref CompileBudget, out data, out diagnostic)`.
Cold admission charges static-image bytes, conservative literal retention, gate/wrapper
and case copies once, before copies. Later calls share immutable data and charge32B
for their reference. Primitive constant spans use static image storage; views cast
only reference-free sequential two-Int32 contract structs. CaseTables are the frozen
budgeted defensive-copy object and expose direct readonly mappings for VM binding.
Compilation owners must charge their bounded property/identifier queries in their
own compile budget; the frozen query interface has no budget parameter. Alias lookup
uses ordinal span equality over a finite fixed vocabulary, with no hashing experiment
or speed claim. No table/case lookup consults .NET categories or culture.

GC includes surrogate code points and aggregated categories. Script_Extensions
replaces Script defaults only on explicit overrides; all172/176 exact values are admitted,
including the empty Katakana_Or_Hiragana value. Binary inventory has exactly53 entries;
other UCD properties are rejected. Emoji strings derive from sequences/ZWJ files, never
emoji-test status or blocks; singletons become intervals, longer strings are distinct
and longest-first with lexicographic tie ordering. The RGI union contains exactly3790/3953
members for16/17. C/S folding excludes full/Turkic folding. Legacy canonicalization applies
UnicodeData simple uppercase overridden by unconditional SpecialCasing, retaining
identity for multi-unit uppercase or non-ASCII-to-ASCII results. No normalization.

`smoke/Smoke.csproj` references actual GregsEx.dll and uses the existing nonshipping
GregsEx.Tests friend assembly identity. It checks real table membership/admission
under NativeAOT without shared source/build edits. It establishes only this Unicode
lane's native execution; complete parser/VM/host binding remains an integration gate.
There is no command-line parser or runtime dynamic generation in the smoke.
