#!/usr/bin/env bash
# Fetches the public, pinned inputs of the Oniguruma corpus and differential into artifacts/oniguruma/inputs:
#
#   tm-grammars/   the tm-grammars npm package (patterns: grammars/*.json, metadata: index.js, LICENSE, NOTICE),
#                  verified against its npm integrity
#   samples/       the per-language sample files of shikijs/textmate-grammars-themes (MIT) at a pinned commit,
#                  verified by commit SHA; the repository's LICENSE is copied beside them
#
#   tools/oracles/oniguruma/fetch-inputs.sh [<tm-grammars-version> <npm-integrity> <samples-commit>]
#
# Defaults are the pins recorded in test/corpora/oniguruma/README.md.
set -euo pipefail

version="${1:-1.32.22}"
integrity="${2:-sha512-VKWL+UiqlGqbGHonRbrBc0kkqDIHUUiVO/A2rBTBz5LBXKA8Fe2f34O1YiBI7ED37tTUlT3MGmLf9+0F/H4/Ww==}"
commit="${3:-8f1828ebccc57930cf0adde9b35ae929584fe191}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
out="$root/artifacts/oniguruma/inputs"
mkdir -p "$out"

tarball="$out/tm-grammars-$version.tgz"
curl -fsSL -o "$tarball" "https://registry.npmjs.org/tm-grammars/-/tm-grammars-$version.tgz"
actual="sha512-$(openssl dgst -sha512 -binary "$tarball" | openssl base64 -A)"
if [ "$actual" != "$integrity" ]; then
  echo "error: tm-grammars $version is $actual, expected $integrity" >&2
  exit 1
fi
rm -rf "$out/tm-grammars"
mkdir "$out/tm-grammars"
tar -xzf "$tarball" -C "$out/tm-grammars" --strip-components 1

repo="$out/textmate-grammars-themes"
if [ ! -d "$repo/.git" ]; then
  git init --quiet "$repo"
  git -C "$repo" remote add origin https://github.com/shikijs/textmate-grammars-themes
fi
git -C "$repo" fetch --quiet --depth 1 origin "$commit"
git -C "$repo" -c advice.detachedHead=false checkout --quiet --force FETCH_HEAD
if [ "$(git -C "$repo" rev-parse 'HEAD^{commit}')" != "$commit" ]; then
  echo "error: textmate-grammars-themes checkout is not $commit" >&2
  exit 1
fi
rm -rf "$out/samples"
cp -R "$repo/samples" "$out/samples"
cp "$repo/LICENSE" "$out/samples/LICENSE"
echo "tm-grammars $version and textmate-grammars-themes samples $commit in $out"
