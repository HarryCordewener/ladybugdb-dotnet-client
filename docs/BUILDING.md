# Building and testing

This is for building `LadybugDb.Client` itself, not for consuming it. If you just want to use the
client in your own project, see the [README](../README.md) and [USAGE.md](USAGE.md) instead.

- [Prerequisites](#prerequisites)
- [First build: fetch the native binaries](#first-build-fetch-the-native-binaries)
- [Running tests](#running-tests)
- [Regenerating interop](#regenerating-interop)
- [How native binaries are pinned and verified](#how-native-binaries-are-pinned-and-verified)

## Prerequisites

- **.NET 10 SDK**, exactly `10.0.300` or a version `rollForward: latestFeature` will accept —
  `global.json` pins this. It also sets `test.runner` to `Microsoft.Testing.Platform`, which is
  what makes `dotnet test` work at all here; without it, `dotnet test` falls back to VSTest and
  won't discover TUnit's tests correctly.
- **`python3`** on `PATH`, used by `scripts/fetch-liblbug.sh` to extract release assets on every
  platform. `.zip` assets (Windows) go through `python3`'s `zipfile` module because `unzip` is
  absent from GitHub's `windows-latest` runner image and from Git Bash, and Git Bash's own `tar`
  can't read zip containers either. `.tar.gz` assets (Linux/macOS) also go through `python3`'s
  `tarfile` module rather than the `tar` binary: upstream ships the canonical library name
  (`liblbug.so`, `liblbug.dylib`) as a symlink chain inside the archive, which Git Bash's `tar`
  cannot recreate on `windows-latest` — `tarfile` resolves the chain from the archive's own member
  metadata instead, so no symlink is ever created on disk. Either way, `python3`'s stdlib modules
  need no OS-specific branching and are preinstalled everywhere this project builds.
- **`clang`** on `PATH`, needed only if you're regenerating the interop layer (see below). Not
  required for a normal build.

## First build: fetch the native binaries

```console
bash scripts/fetch-liblbug.sh
dotnet build
```

Native binaries are **never committed** to this repository. Building `LadybugDb.Client.Native`
without running the fetch script first fails **on purpose** — its `.csproj` has a
`FailIfNativesMissing` target that checks for `runtimes/<rid>/native/...` before `Build` and
`Pack` run, and errors out with a pointer back to this script if any RID is missing. That's
deliberate: a silent skip would produce a package that looks fine locally and then throws
`DllNotFoundException` for whoever installs it.

The script downloads the pinned `liblbug` release for all six supported RIDs, verifies each
archive's SHA256 against `LadybugDb.Client.Native/liblbug.lock`, and extracts the library file
into `runtimes/<rid>/native/`. Re-run it any time `liblbug.version` changes.

## Running tests

Two test projects, and they need different commands:

```console
# Unit tests — no real engine involved.
dotnet test LadybugDb.Client.Tests -c Release

# Integration tests — run against the real liblbug, so fetch it first.
bash scripts/fetch-liblbug.sh
dotnet test LadybugDb.Client.IntegrationTests -c Release
```

`LadybugDb.Client.Tests` also includes `PackagingTests`, which inspects built `.nupkg` files
directly, so it needs real packages on disk first:

```console
dotnet pack -c Release
dotnet test LadybugDb.Client.Tests -c Release
```

**Filtering to one test class or method:** this project runs on Microsoft.Testing.Platform, not
classic VSTest, so the familiar `--filter ClassName` silently does nothing —
it reports `Zero tests ran` (exit code 5) instead of an error, which makes it easy to mistake for
"no tests matched" when actually no filter was applied at all. Use `--treenode-filter` instead,
with a `/assembly/namespace/class/method` glob:

```console
dotnet test LadybugDb.Client.Tests -c Release --treenode-filter "/*/*/PackagingTests/*"
dotnet test LadybugDb.Client.IntegrationTests -c Release --treenode-filter "/*/*/DatabaseLifecycleTests/ConcurrentWrite_ThrowsLadybugWriteConflictException"
```

## Regenerating interop

The raw P/Invoke layer (`LadybugDb.Client/Native/LbugNative.g.cs`) is generated from the pinned
`lbug.h` C header via [ClangSharpPInvokeGenerator](https://github.com/dotnet/ClangSharp), pinned
in `.config/dotnet-tools.json`. It's committed, not built on the fly, so any change to the
targeted `liblbug` version needs a regeneration:

```console
bash scripts/regen-interop.sh
```

This downloads `lbug.h` for the pinned version into `third-party/`, runs the generator with the
project's specific flags (internal visibility, macro/helper-type generation, the 12 `*_to_tm` /
`*_from_tm` functions excluded — there's no portable `struct tm` layout across all six target
RIDs), and then mechanically rewrites the generator's classic `[DllImport]`/`static extern`
output into the source-generated `[LibraryImport]`/`static partial` shape this codebase requires.
Every generated entry point is fully blittable (raw pointers, byte-backed `_Bool`, primitive
numerics, pointer-sized enums), so that rewrite is a safe, deterministic text substitution, not a
hand-tweak of generator output.

CI enforces that the committed file matches what regeneration produces (the `interop-drift` job):
it re-runs `scripts/regen-interop.sh` and fails the build on any diff against
`LadybugDb.Client/Native/LbugNative.g.cs` or `third-party/lbug.h`. If you change
`liblbug.version`, run the script and commit the regenerated file in the same change.

## How native binaries are pinned and verified

`LadybugDb.Client.Native/liblbug.version` names the exact upstream release tag (currently
`v0.18.3`). `LadybugDb.Client.Native/liblbug.lock` pins the SHA256 of every release asset the
fetch script downloads. `scripts/fetch-liblbug.sh` refuses to proceed if a downloaded asset's hash
doesn't match its lockfile entry — this is the only thing standing between "we redistribute
upstream's official binary" and "we redistribute whatever a compromised release asset happened to
contain."

To bump the pinned version: update `liblbug.version`, then run
`bash scripts/fetch-liblbug.sh --update-lock` to redownload every asset and rewrite the lockfile
with fresh hashes (it only overwrites the lockfile after every asset has downloaded and hashed
successfully, so a network blip or a renamed asset can't leave a half-written lockfile behind).
Review the hash diff like you would any other dependency bump, then regenerate interop if the C
API surface changed.
