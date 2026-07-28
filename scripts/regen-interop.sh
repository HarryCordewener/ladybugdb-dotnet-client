#!/usr/bin/env bash
# Regenerates the raw interop from the pinned lbug.h. Output is committed;
# CI re-runs this and fails if the result differs.
#
# Requires: a `clang` binary on PATH (used only to locate the Clang resource
# directory containing builtin headers like stddef.h - ClangSharpPInvokeGenerator
# does not reliably auto-detect this), and libclang available for the .NET
# tool to load at runtime (present system-wide on most dev machines and on
# GitHub's ubuntu-latest/windows-latest/macos-latest runners).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/LadybugDb.Client.Native/liblbug.version")"
HEADER_DIR="$ROOT/third-party"
HEADER="$HEADER_DIR/lbug.h"
OUT="$ROOT/LadybugDb.Client/Native/LbugNative.g.cs"

mkdir -p "$HEADER_DIR"
curl -fsSL -o "$HEADER" \
  "https://raw.githubusercontent.com/LadybugDB/ladybug/$VERSION/src/include/c_api/lbug.h"

cd "$ROOT"
dotnet tool restore

# ClangSharpPInvokeGenerator's Clang resource-directory auto-detection doesn't
# find the system Clang install on this stack, and without it parsing fails
# outright (stddef.h and friends don't resolve). Ask the system `clang` for
# its own resource directory rather than hardcoding a version-specific path.
RESOURCE_DIR="$(clang -print-resource-dir)"

# --with-access-specifier's bare `*` wildcard only reliably catches type-level
# declarations (structs/enums/the method-class itself) in this generator
# version; free functions and struct fields need their own catch-all pattern
# to also land as Internal (verified empirically - each pattern below covers
# a distinct declaration-kind bucket that the bare `*` misses on its own).
# Everything in this header is `lbug_`-prefixed except the embedded Arrow C
# Data Interface types (ArrowSchema/ArrowArray, matched by the bare `*` and
# `*.*`) and the ARROW_* flag macros.
#
# The 12 `*_to_tm`/`*_from_tm` helpers take/return `struct tm` by value or by
# pointer. `tm` comes from system <time.h>, which this generation run never
# traverses (it's out of scope for lbug.h's own declarations), so the type
# is unresolvable and the build fails without help. It also can't be safely
# remapped to a fixed C# layout: POSIX's struct tm (glibc, two extra fields)
# and the Windows CRT's struct tm are different sizes and layouts, so no
# single struct is ABI-correct across every RID this package ships for.
# Excluded here rather than hand-patched post-generation; none of the
# required core lifecycle entry points depend on them.
#
# The tool exits with the diagnostic count as its process exit code (here,
# 178 - one per `LBUG_C_API`/__attribute__((visibility(...))) export macro
# it doesn't understand), not 0/1 for success/failure, even though generation
# completes and $OUT is written in full. So `set -e` is suspended around this
# call and success is instead verified directly: the output must exist and
# must contain no `Error:` diagnostic (the tool's actual fatal-failure marker,
# e.g. on a header parse failure, which also leaves $OUT missing or empty).
set +e
GEN_LOG="$(dotnet tool run ClangSharpPInvokeGenerator \
  --file "lbug.h" \
  --file-directory "third-party" \
  --output "$OUT" \
  --namespace LadybugDb.Client.Native \
  --method-class-name LbugNative \
  --library-path lbug \
  --language c \
  --resource-directory "$RESOURCE_DIR" \
  --config codegen=latest types=unix file=single \
  --generate macro-bindings helper-types \
  --log potential-typedef-remappings \
  --with-access-specifier "*=Internal" "lbug_*=Internal" "*.*=Internal" "ARROW_*=Internal" \
  --exclude "*_to_tm" "*_from_tm" 2>&1)"
set -e
echo "$GEN_LOG"

if [ ! -s "$OUT" ] || grep -q '^Error:' <<< "$GEN_LOG"; then
  echo "FATAL: ClangSharpPInvokeGenerator failed to generate $OUT" >&2
  exit 1
fi

# This ClangSharpPInvokeGenerator version only emits the classic
# [DllImport]/`static extern` shape; it has no switch to opt into the
# source-generated [LibraryImport]/`static partial` shape our interop is
# required to use. Every generated entry point here is fully blittable
# (raw pointers, byte-backed `_Bool`, primitive numerics, pointer-sized
# enums) with no strings, arrays-by-value, or non-pointer bool parameters,
# so the substitution below is a safe mechanical rewrite - not a hand
# tweak of the generator's actual output shape, and it re-runs identically
# on every regeneration.
perl -0777 -pi -e '
  s/\[DllImport\("lbug", CallingConvention = CallingConvention\.Cdecl, ExactSpelling = true\)\]\n((?:\s*\[return:[^\n]*\]\n)?)(\s*)internal static extern /[LibraryImport("lbug")]\n${2}[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]\n$1${2}internal static partial /g;
  s/^using System\.Runtime\.InteropServices;\n/using System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\n/m;
' "$OUT"

echo "generated $OUT"
