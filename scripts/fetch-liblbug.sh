#!/usr/bin/env bash
# Downloads pinned liblbug release assets, verifies SHA256, extracts per-RID.
# Binaries are never committed; CI runs this before build/pack.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NATIVE_DIR="$ROOT/LadybugDb.Client.Native"
VERSION="$(tr -d '[:space:]' < "$NATIVE_DIR/liblbug.version")"
LOCK="$NATIVE_DIR/liblbug.lock"
BASE="https://github.com/LadybugDB/ladybug/releases/download/$VERSION"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# rid|asset|libfile
#
# libfile is the canonical top-level library name we place under
# runtimes/<rid>/native/. Observed archive layout for v0.18.3 (see
# task-2-report.md for the full discovery record):
#   - linux/osx archives ship liblbug.so / liblbug.dylib as a SYMLINK
#     chain (liblbug.so -> liblbug.so.0 -> liblbug.so.0.18.3, same
#     pattern for .dylib) rather than a plain regular file.
#   - windows archives ship lbug_shared.dll (and lbug_shared.lib, which
#     we intentionally do not copy: it's a link-time import lib, not
#     needed for P/Invoke at runtime).
# We reference the canonical name directly and dereference with `cp -L`
# so the destination always gets real file content under the name the
# native resolver expects, regardless of the symlink indirection.
ASSETS="
linux-x64|liblbug-linux-x86_64.tar.gz|liblbug.so
linux-arm64|liblbug-linux-aarch64.tar.gz|liblbug.so
osx-x64|liblbug-osx-x86_64.tar.gz|liblbug.dylib
osx-arm64|liblbug-osx-arm64.tar.gz|liblbug.dylib
win-x64|liblbug-windows-x86_64.zip|lbug_shared.dll
win-arm64|liblbug-windows-arm64.zip|lbug_shared.dll
"

WRITE_LOCK=0
[ "${1:-}" = "--update-lock" ] && WRITE_LOCK=1
[ "$WRITE_LOCK" = 1 ] && : > "$LOCK"

# Read from a herestring, not a pipe, so the loop body runs in the current
# shell rather than a subshell: a failure inside the loop then always
# terminates the whole script under `set -e`, without depending on
# `pipefail`'s interaction with the pipeline's exit status.
while IFS='|' read -r RID ASSET LIBFILE; do
  [ -z "$RID" ] && continue
  echo "==> $RID  $ASSET"
  curl -fsSL -o "$WORK/$ASSET" "$BASE/$ASSET"
  ACTUAL="$(sha256sum "$WORK/$ASSET" | cut -d' ' -f1)"

  if [ "$WRITE_LOCK" = 1 ]; then
    echo "$ASSET  $ACTUAL" >> "$LOCK"
  else
    EXPECTED="$(awk -v a="$ASSET" '$1==a {print $2}' "$LOCK")"
    if [ -z "$EXPECTED" ]; then
      echo "FATAL: $ASSET missing from liblbug.lock" >&2; exit 1
    fi
    if [ "$ACTUAL" != "$EXPECTED" ]; then
      echo "FATAL: checksum mismatch for $ASSET" >&2
      echo "  expected $EXPECTED" >&2
      echo "  actual   $ACTUAL" >&2
      exit 1
    fi
  fi

  DEST="$NATIVE_DIR/runtimes/$RID/native"
  mkdir -p "$DEST"
  EX="$WORK/x-$RID"; mkdir -p "$EX"
  case "$ASSET" in
    *.tar.gz) tar -xzf "$WORK/$ASSET" -C "$EX" ;;
    *.zip)    unzip -qo "$WORK/$ASSET" -d "$EX" ;;
  esac

  if [ ! -e "$EX/$LIBFILE" ]; then
    echo "FATAL: expected library '$LIBFILE' not found in $ASSET" >&2
    exit 1
  fi
  cp -L "$EX/$LIBFILE" "$DEST/$LIBFILE"
  ls -1 "$DEST"
done <<< "$ASSETS"

echo "done: $VERSION"
