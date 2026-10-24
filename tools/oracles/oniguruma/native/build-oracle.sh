#!/usr/bin/env bash
# Builds the native Oniguruma oracle (oracle.c over github.com/kkos/oniguruma at a tag) on macOS or Linux.
#
#   tools/oracles/oniguruma/native/build-oracle.sh <tag> <commit-sha>
#
# Clones the tag into artifacts/oniguruma/<tag>/src, fails unless it is <commit-sha>, and compiles Oniguruma's library
# sources and oracle.c with the system C compiler (no autotools or CMake) into
# artifacts/oniguruma/<tag>/liboniguruma-oracle.{dylib,so}. Prints the library path.
set -euo pipefail

tag="${1:?usage: build-oracle.sh <tag> <commit-sha>}"
sha="${2:?usage: build-oracle.sh <tag> <commit-sha>}"
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../../../.." && pwd)"
out="$root/artifacts/oniguruma/$tag"
src="$out/src"

if [ ! -d "$src/.git" ]; then
  mkdir -p "$out"
  git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$tag" https://github.com/kkos/oniguruma "$src"
fi
actual="$(git -C "$src" rev-parse 'HEAD^{commit}')"
if [ "$actual" != "$sha" ]; then
  echo "error: $tag is $actual, expected $sha" >&2
  exit 1
fi

case "$(uname -s)" in
  Darwin) lib="$out/liboniguruma-oracle.dylib"; shared=(-dynamiclib) ;;
  Linux) lib="$out/liboniguruma-oracle.so"; shared=(-shared -fPIC) ;;
  *) echo "error: unsupported host $(uname -s)" >&2; exit 1 ;;
esac

# What Oniguruma's configure derives on 64-bit macOS and Linux (src/config.h.cmake.in).
cat > "$out/config.h" <<'H'
#define HAVE_ALLOCA 1
#define HAVE_ALLOCA_H 1
#define HAVE_STDINT_H 1
#define HAVE_SYS_TIMES_H 1
#define HAVE_SYS_TIME_H 1
#define HAVE_SYS_TYPES_H 1
#define HAVE_UNISTD_H 1
#define HAVE_INTTYPES_H 1
#define SIZEOF_INT 4
#define SIZEOF_LONG 8
#define SIZEOF_LONG_LONG 8
#define SIZEOF_VOIDP 8
H

# libonig_la_SOURCES of src/Makefile.am without the POSIX and GNU APIs and the non-Unicode encodings.
sources=(regparse.c regcomp.c regexec.c regenc.c regerror.c regext.c regsyntax.c regtrav.c regversion.c st.c
  unicode.c unicode_unfold_key.c unicode_fold1_key.c unicode_fold2_key.c unicode_fold3_key.c
  ascii.c utf8.c utf16_be.c utf16_le.c utf32_be.c utf32_le.c)
cc -O2 -w "${shared[@]}" -fvisibility=hidden -I"$out" -I"$src/src" -o "$lib" "${sources[@]/#/$src/src/}" "$here/oracle.c"
echo "$lib"
