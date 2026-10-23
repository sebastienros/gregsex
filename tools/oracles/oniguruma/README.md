# Oniguruma oracle tools

Development-only tools for the Oniguruma flavor (`Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1`). Their retained
outputs live in [`test/corpora/oniguruma`](../../../test/corpora/oniguruma/README.md) and
`src/GregsEx/Flavors/Oniguruma/Tables`.

## Unicode tables

`generate.py` reads a checkout of github.com/kkos/oniguruma at tag v6.9.10
(4ef89209a239c1aea328cf13c05a2807e5c146d1) and writes `OnigurumaUnicodeData.g.cs`: Oniguruma's own Unicode 16.0
property ranges and names (`unicode_property_data.c`), simple and multi-code-point case folds
(`unicode_fold_data.c`) and Grapheme_Cluster_Break ranges (`unicode_egcb_data.c`), with the SHA-256 of each source
in the header. Oniguruma's BSD-2-Clause notice is `src/GregsEx/Flavors/Oniguruma/ONIGURUMA-NOTICE.txt`.

```bash
git clone --branch v6.9.10 https://github.com/kkos/oniguruma artifacts/oniguruma
python3 -I tools/oracles/oniguruma/generate.py artifacts/oniguruma src/GregsEx/Flavors/Oniguruma/Tables/OnigurumaUnicodeData.g.cs
```

## Corpus

1. Inputs: `build_corpus.py <textmate-dir> <fences.json> <raw-trace.json> test/corpora/oniguruma`, where
   `textmate-dir` is Douze's `Douze/Highlighting/textmate.zip` extracted, `fences.json` maps each language to the
   fences of `node generate.mjs 400 <dir>` (Douze's docs-heavy scenario), and `raw-trace.json` is
   `TextMateSharp.Benchmarks --trace` output from a TextMateSharp build whose `OnigScanner` records calls.
   `edge-cases.json` is written by hand.
2. Observations: `oracle/` calls Onigwrap 1.0.11's native `libonigwrap` directly (it does not reference GregsEx):

```bash
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- cases test/corpora/oniguruma/edge-cases.json test/corpora/oniguruma/edge-cases.expected.json
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- cases test/corpora/oniguruma/grammar-cases.json.gz test/corpora/oniguruma/grammar-cases.expected.json.gz
dotnet run -c Release --project tools/oracles/oniguruma/oracle -- trace test/corpora/oniguruma/trace.json.gz test/corpora/oniguruma/trace.expected.json.gz
```

3. Update the SHA-256 list in the corpus README.
