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
#     chain (e.g. liblbug.so -> liblbug.so.0 -> liblbug.so.0.18.3 on
#     linux; liblbug.dylib -> liblbug.0.dylib -> liblbug.0.18.3.dylib on
#     osx - note the two platforms don't even agree on where the version
#     number goes) rather than a plain regular file.
#   - windows archives ship lbug_shared.dll (and lbug_shared.lib, which
#     we intentionally do not copy: it's a link-time import lib, not
#     needed for P/Invoke at runtime).
# For .tar.gz assets we never materialize the symlinks on disk at all -
# `tar` under Git Bash on windows-latest can't create them and aborts
# the whole archive with a nonzero exit. Instead we walk the chain
# purely via tarfile's in-memory member metadata and write the resolved
# regular file's bytes straight to the canonical destination name. See
# the extraction step below for the actual walk.
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

# In --update-lock mode, hashes accumulate into a scratch file and are only
# moved over the real lockfile once ALL assets have downloaded and hashed
# successfully. Writing directly into $LOCK as we went would truncate/corrupt
# a previously-good lockfile the moment any later asset failed (bad asset
# name, network blip, etc.), leaving a half-written lockfile committed to the
# working tree with no way back short of `git checkout`.
LOCK_NEW="$WORK/liblbug.lock.new"
[ "$WRITE_LOCK" = 1 ] && : > "$LOCK_NEW"
CHANGED=0
TOTAL=0

# Read from a herestring, not a pipe, so the loop body runs in the current
# shell rather than a subshell: a failure inside the loop then always
# terminates the whole script under `set -e`, without depending on
# `pipefail`'s interaction with the pipeline's exit status. It also means
# CHANGED/TOTAL above are visible after the loop, for the summary below.
while IFS='|' read -r RID ASSET LIBFILE; do
  [ -z "$RID" ] && continue
  echo "==> $RID  $ASSET"
  curl -fsSL -o "$WORK/$ASSET" "$BASE/$ASSET"
  ACTUAL="$(sha256sum "$WORK/$ASSET" | cut -d' ' -f1)"

  if [ "$WRITE_LOCK" = 1 ]; then
    TOTAL=$((TOTAL + 1))
    OLD_HASH=""
    [ -f "$LOCK" ] && OLD_HASH="$(awk -v a="$ASSET" '$1==a {print $2}' "$LOCK")"
    echo "$ASSET  $ACTUAL" >> "$LOCK_NEW"
    if [ "$ACTUAL" != "$OLD_HASH" ]; then
      CHANGED=$((CHANGED + 1))
      if [ -z "$OLD_HASH" ]; then
        echo "    new:     $ASSET  $ACTUAL"
      else
        echo "    changed: $ASSET"
        echo "      old: $OLD_HASH"
        echo "      new: $ACTUAL"
      fi
    fi
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

  case "$ASSET" in
    *.tar.gz)
      # Resolve the (possibly multi-hop) symlink chain named $LIBFILE using
      # only tarfile's member metadata - no symlink is ever created on disk,
      # so this behaves identically on Linux, macOS, and Windows/Git Bash.
      python3 - "$WORK/$ASSET" "$LIBFILE" "$DEST/$LIBFILE" <<'PY'
import posixpath
import sys
import tarfile

archive, want, dest = sys.argv[1], sys.argv[2], sys.argv[3]
with tarfile.open(archive) as tf:
    members = {m.name: m for m in tf.getmembers()}
    name = want
    seen = set()
    while True:
        member = members.get(name)
        if member is None:
            sys.exit(f"FATAL: member '{name}' not found in {archive}")
        if member.isreg():
            src = tf.extractfile(member)
            if src is None:
                sys.exit(f"FATAL: could not read '{name}' from {archive}")
            with open(dest, "wb") as out:
                out.write(src.read())
            break
        if member.issym() or member.islnk():
            if name in seen:
                sys.exit(f"FATAL: symlink loop resolving '{want}' in {archive}")
            seen.add(name)
            name = posixpath.normpath(posixpath.join(posixpath.dirname(name), member.linkname))
            continue
        sys.exit(f"FATAL: '{name}' in {archive} is neither a regular file nor a symlink")
PY
      ;;
    *.zip)
      # `unzip` is absent from GitHub's windows-latest image (not bundled by
      # Git for Windows either - it's a separate MSYS2 package), and `tar`
      # can't be swapped in as a substitute: under `shell: bash`, Git Bash
      # puts its own GNU tar first on PATH, and GNU tar cannot read zip
      # containers at all - on Linux either, not just Windows. python3's
      # zipfile module needs no OS branching and is preinstalled on every
      # GitHub-hosted runner (including inside Git Bash on Windows, since
      # it's on the system PATH there too). Zip assets have no symlinks, so
      # a plain extract-then-copy is sufficient here.
      EX="$WORK/x-$RID"; mkdir -p "$EX"
      python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$WORK/$ASSET" "$EX"
      if [ ! -e "$EX/$LIBFILE" ]; then
        echo "FATAL: expected library '$LIBFILE' not found in $ASSET" >&2
        exit 1
      fi
      cp -L "$EX/$LIBFILE" "$DEST/$LIBFILE"
      ;;
  esac

  if [ ! -s "$DEST/$LIBFILE" ]; then
    echo "FATAL: '$LIBFILE' missing or empty after extracting $ASSET" >&2
    exit 1
  fi
  chmod 0755 "$DEST/$LIBFILE"
  ls -1 "$DEST"
done <<< "$ASSETS"

if [ "$WRITE_LOCK" = 1 ]; then
  mv "$LOCK_NEW" "$LOCK"
  echo "lockfile updated: $CHANGED of $TOTAL hashes changed"
fi

echo "done: $VERSION"
