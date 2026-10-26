# Oniguruma corpus

Native Oniguruma 6.9.10 observations for the `Oniguruma-6.9.10-OnigurumaSyntax-Utf16-v1` profile with
`ONIG_OPTION_CAPTURE_GROUP` (`RegexProfiles.OnigurumaTextMate`). Tests read these files only; they need no native
engine. Regenerate them with [`tools/oracles/oniguruma`](../../../tools/oracles/oniguruma/README.md).

## Oracle

`tools/oracles/oniguruma/native/oracle.c` over Oniguruma tag v6.9.10 (commit 4ef89209a239c1aea328cf13c05a2807e5c146d1,
Onigwrap 1.0.11's `ONIGURUMA_REF`), built from source by `build-oracle.sh` with `not-code-range-underflow.patch` (a nested
negated class holding U+0000 gets its true complement, as UTF-8 Oniguruma and so vscode-oniguruma compute it): `onig_new` with UTF-16LE,
`ONIG_SYNTAX_DEFAULT` and `ONIG_OPTION_CAPTURE_GROUP`, then `onig_search` from each start to the end of the subject, as
Onigwrap's `onigwrap_create`/`onigwrap_search` do. Patterns reach Oniguruma as written. Observations were recorded on
macOS 15.8.1 arm64 (Apple clang 17.0.0, `-O2`; `liboniguruma-oracle.dylib` SHA-256
`333a19f298d8a8693da530f0dc7d025e71cd7981160bb9420a1a12cd73efc0bf`). Without the patch (SHA-256
`439e975452cf3ffd3304a2cc97ae58ec9e810054b537e850dd3c7d88af25398c`) the build reproduced byte for byte the expectations
recorded earlier with Onigwrap 1.0.11's prebuilt `libonigwrap.dylib` (SHA-256
`5922d0c6c3cd31bfdf9997ea03d48d7aff952d891e8d46c2b9bfccd8d8160665`); the patch changes one edge case and four CSS
grammar patterns (`[...[^\x{00}-\x{7F}]]` identifiers).

## Files

| File | Contents |
| --- | --- |
| `edge-cases.json` | Hand-written cases: `{ pattern, subjects, starts? }`; a subject is a string or `{ "units": [...] }` for UTF-16 JSON cannot carry (lone surrogates). Without `starts`, every start offset is searched. |
| `edge-cases.expected.json` | Per case: `null` when Oniguruma rejects the pattern, else per subject `[start, outcome]` pairs: `null` (no match), a negative Oniguruma error code (such as -17, the retry limit), or capture pairs (start, end; -1 when a group did not participate). |
| `grammar-cases.json.gz` | Every distinct begin, end, while and match pattern of the shellscript, typescript, javascript, html, json, yaml and csharp grammars and those they embed (css), each searched from offsets 0, 7 and 21 of the first six distinct lines (with `\n` appended) of its language's sample. |
| `grammar-cases.expected.json.gz` | As `edge-cases.expected.json`. |
| `csharp-subexp-calls.json` | Every pattern of VS Code's C# grammar holding a subexpression call (`\g<name>`), verbatim with the rule path of its first occurrence, and C# subject lines; `OnigurumaCallKernelTests` compares the kernels with the interpreter over them from every offset (no native observations: the interpreter is that oracle). From `extensions/csharp/syntaxes/csharp.tmLanguage.json` of microsoft/vscode `main` on 2026-10-26 (dotnet/csharp-tmLanguage commit c2719309e91d2e26be21fce0ecaaca86c7b48ce4, MIT). |
| `LICENSE-tm-grammars.txt` | tm-grammars' MIT license and the NOTICE sections of the grammars above. tm-grammars 1.32.22's NOTICE has no section for `yaml.json`. |

Grammar patterns are stored as TextMate hosts hand them to Oniguruma: with Onigwrap's `UnicodeCharEscape` rewrite
applied (`\xHH...` to `\x{HH...}`, `\x{7fffffff}` to `\x{7ffff}`). GZip headers carry no timestamp.

## Sources

Fetched by `tools/oracles/oniguruma/fetch-inputs.sh`:

- tm-grammars 1.32.22 from the npm registry (integrity
  `sha512-VKWL+UiqlGqbGHonRbrBc0kkqDIHUUiVO/A2rBTBz5LBXKA8Fe2f34O1YiBI7ED37tTUlT3MGmLf9+0F/H4/Ww==`): patterns from
  `grammars/*.json`, embedded grammars from `index.js`. MIT; notices in `LICENSE-tm-grammars.txt`.
- Lines: `samples/<language>.sample` of github.com/shikijs/textmate-grammars-themes at commit
  `8f1828ebccc57930cf0adde9b35ae929584fe191` (the tm-grammars 1.32.22 release commit). MIT, Copyright (c) 2021 Pine Wu,
  Copyright (c) 2023 Anthony Fu.

`edge-cases.json` is written by hand; `grammar-cases.json.gz` and `LICENSE-tm-grammars.txt` are written by
`oracle corpus`, the expectations by `oracle cases` (see the tools README).

## Integrity (SHA-256)

- `edge-cases.json`: `2494e1a1a668fdbc2337c062cd75746ec121bd02c36a33a6e141a00cb456daa7`
- `edge-cases.expected.json`: `784fcdf42c4089458d1dd077575c0a45ac7bd6ca6b773ae9ac6ae01a9480c000`
- `grammar-cases.json.gz`: `5e3473d86b417ee92d7d29433a9d7eabffabe1f32ca4c2c28576911c0fe4f2f7`
- `grammar-cases.expected.json.gz`: `1845c1d73b7b5897c7142c7a3a8cf43346b6fd905944b1b83774198a16cfcfb4`
- `LICENSE-tm-grammars.txt`: `e44aaac9beeffeaf1498b2111052ba2df4ac0986c3f4fffbefef89f23b007c67`
