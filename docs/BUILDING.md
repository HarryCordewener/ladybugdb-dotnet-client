# Building and testing

This is for building `LadybugDb.Client` itself, not for consuming it. If you just want to use the
client in your own project, see the [README](../README.md) and [USAGE.md](USAGE.md) instead.

- [Prerequisites](#prerequisites)
- [Building](#building)
- [Running tests](#running-tests)
- [Regenerating interop](#regenerating-interop)
- [How the engine version is pinned](#how-the-engine-version-is-pinned)

## Prerequisites

- **.NET 10 SDK**, exactly `10.0.300` or a version `rollForward: latestFeature` will accept —
  `global.json` pins this. It also sets `test.runner` to `Microsoft.Testing.Platform`, which is
  what makes `dotnet test` work at all here; without it, `dotnet test` falls back to VSTest and
  won't discover TUnit's tests correctly.
- **`clang`** on `PATH`, needed only if you're regenerating the interop layer (see below). Not
  required for a normal build.

## Building

```console
dotnet build
```

Nothing to fetch first. The engine binaries come from upstream's `LadybugDB.Native` package, which
the test, benchmark and crash-repro projects reference like any other package; `dotnet restore`
brings them in (about 130 MB for all five RIDs, cached by NuGet after the first restore). The
shipping `LadybugDb.Client` project references no native package at all, by design — see the
README's installation section for why the consumer makes that choice.

## Running tests

Two test projects, and they need different commands:

```console
# Unit tests — no real engine involved.
dotnet test LadybugDb.Client.Tests -c Release

# Integration tests — run against the real liblbug from the LadybugDB.Native package.
dotnet test LadybugDb.Client.IntegrationTests -c Release
```

`LadybugDb.Client.Tests` also includes `PackagingTests`, which inspects the built `.nupkg` directly,
so it needs a real package on disk first:

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
targeted engine version needs a regeneration:

```console
bash scripts/regen-interop.sh
```

This downloads `lbug.h` for the pinned version into `third-party/`, runs the generator with the
project's specific flags (internal visibility, macro/helper-type generation, the 12 `*_to_tm` /
`*_from_tm` functions excluded — there's no portable `struct tm` layout across the target RIDs),
and then mechanically rewrites the generator's classic `[DllImport]`/`static extern` output into
the source-generated `[LibraryImport]`/`static partial` shape this codebase requires. Every
generated entry point is fully blittable (raw pointers, byte-backed `_Bool`, primitive numerics,
pointer-sized enums), so that rewrite is a safe, deterministic text substitution, not a hand-tweak
of generator output.

CI enforces that the committed file matches what regeneration produces (the `interop-drift` job):
it re-runs `scripts/regen-interop.sh` and fails the build on any diff against
`LadybugDb.Client/Native/LbugNative.g.cs` or `third-party/lbug.h`. If you change
`third-party/liblbug.version`, run the script and commit the regenerated file in the same change.

## How the engine version is pinned

`third-party/liblbug.version` names the upstream release tag whose C header the interop was
generated from (currently `v0.19.1`). It is embedded into `LadybugDb.Client.dll` as assembly
metadata at build time, exposed as `LadybugDatabase.MinimumEngineVersion`, and checked once against
`lbug_get_version()` when a `LadybugDatabase` is opened: an engine older than the pin (by
major.minor) is refused with a `LadybugException` naming the version to install, because it would
be missing entry points this client calls; a newer engine is accepted, since the C API has only
ever grown between releases.

The binaries are not pinned here at all — the consumer chooses a `LadybugDB.Native` version, and
upstream's packages carry the engine's own provenance. The test projects reference the same version
as the pin so the suite runs against exactly the engine the interop was generated for.

To bump the pinned version: edit `third-party/liblbug.version`, bump the `LadybugDB.Native`
package reference in the test, benchmark and crash-repro projects to match, run
`bash scripts/regen-interop.sh`, review the interop diff (new entry points, changed structs), and
run the full suite and the benchmarks. Upstream publishes native packages a little after each
engine release, so the pin can only move to a version that has one.
