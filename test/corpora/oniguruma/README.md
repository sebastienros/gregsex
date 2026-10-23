# Oniguruma corpus

Native Oniguruma 6.9.10 observations for the `Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1` profile with
`ONIG_OPTION_CAPTURE_GROUP` (`RegexProfiles.OnigurumaTextMate`). Tests read these files only; they need no native
engine. Regenerate them with [`tools/oracles/oniguruma`](../../../tools/oracles/oniguruma/README.md).

## Oracle

Onigwrap 1.0.11 (the binding TextMateSharp 2.0.4 uses), whose native library is built from Oniguruma tag v6.9.10
(commit 4ef89209a239c1aea328cf13c05a2807e5c146d1, Onigwrap's `ONIGURUMA_REF`). Observations were recorded on macOS
arm64 with `runtimes/osx-arm64/native/libonigwrap.dylib`, SHA-256 `5922d0c6c3cd31bfdf9997ea03d48d7aff952d891e8d46c2b9bfccd8d8160665`, calling `onigwrap_create` (UTF-16LE,
`ONIG_SYNTAX_DEFAULT`, `ONIG_OPTION_CAPTURE_GROUP`) and `onigwrap_search` directly: patterns reach Oniguruma as
written, without Onigwrap's managed `\xHH` rewrite.

## Files

| File | Contents |
| --- | --- |
| `edge-cases.json` | Hand-written cases: `{ pattern, subjects, starts? }`; a subject is a string or `{ "units": [...] }` for UTF-16 JSON cannot carry (lone surrogates). Without `starts`, every start offset is searched. |
| `edge-cases.expected.json` | Per case: `null` when Oniguruma rejects the pattern, else per subject `[start, outcome]` pairs: `null` (no match), a negative Oniguruma error code (such as -17, the retry limit), or capture pairs (start, end; -1 when a group did not participate). |
| `grammar-cases.json.gz` | Every distinct begin, end, while and match pattern of the shellscript, typescript, javascript, html, json, yaml and csharp grammars and those they embed (css), each searched from offsets 0, 7 and 21 of six lines of its language. |
| `grammar-cases.expected.json.gz` | As `edge-cases.expected.json`. |
| `trace.json.gz` | Per language, the scanner calls TextMateSharp makes tokenizing 40 docs-heavy fences: `patterns`, `scanners` (pattern indices in priority order), `lines` (with the newline TextMateSharp appends) and `calls` (flat triples: scanner, line, offset). |
| `trace.expected.json.gz` | Per language and call: `null`, or `[pattern index within the scanner, captures]` for the match `OnigScanner.FindNextMatchSync` selects (earliest start, first pattern on a tie, stopping at a match at the offset). |
| `LICENSE-tm-grammars.txt` | tm-grammars' MIT license and the NOTICE sections of the grammars above. tm-grammars 1.32.22's NOTICE has no section for `yaml.json`. |

Grammar and trace patterns are stored as TextMate hosts hand them to Oniguruma: with Onigwrap's `UnicodeCharEscape`
rewrite applied (`\xHH...` to `\x{HH...}`, `\x{7fffffff}` to `\x{7ffff}`). GZip headers carry no timestamp.

## Sources

- tm-grammars 1.32.22 (npm integrity `sha512-VKWL+UiqlGqbGHonRbrBc0kkqDIHUUiVO/A2rBTBz5LBXKA8Fe2f34O1YiBI7ED37tTUlT3MGmLf9+0F/H4/Ww==`),
  as Douze bundles it (`Douze/Highlighting/textmate.zip`, written by `scripts/update-textmate.cs`).
- Lines: Douze's docs-heavy scenario generator (`bench/ssg-comparison/scenarios/docs-heavy/generate.mjs`, commit
  `8e72eab72a0b743848d8497ac517850dd26f5618`), run as `node generate.mjs 400 <dir>`; fences are taken in page order per language.
- Trace: TextMateSharp (github.com/danipen/TextMateSharp at e0db45b, with GregsEx in place of Onigwrap; its tokens are
  byte-identical to Onigwrap's on these fences), `TextMateSharp.Benchmarks --trace` over the first 40 fences of each
  language, lines without their newline as Douze passes them.

## Integrity (SHA-256)

- `edge-cases.json`: `e1beba98eb381b7dd0578b744335057e78b5dc8ac7f4ff381f7a9dd7ff38be49`
- `edge-cases.expected.json`: `d9c4d0a8e5163e7f9fafe0b23183330c961dd357382e555b4c448a11359f0fea`
- `grammar-cases.json.gz`: `37fc413fc1df3cb9c6c07d5c7d89b4dba896d81d0c07716f3b570f251585f6d2`
- `grammar-cases.expected.json.gz`: `db531cec04efb1c4cabc9739f1eec3657b97254a938067f13172003ff191e31b`
- `trace.json.gz`: `838da5bc8544087aad6adc34de5d8745c2cf85d21d7db03e17401fd3bded78f3`
- `trace.expected.json.gz`: `5d80cea41d4d1676424a97ba59d994c3068dd4b832132a7f8c371a79f89e59c4`
- `LICENSE-tm-grammars.txt`: `e44aaac9beeffeaf1498b2111052ba2df4ac0986c3f4fffbefef89f23b007c67`
