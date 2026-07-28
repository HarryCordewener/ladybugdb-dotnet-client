# LadybugDb.Client Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the foundation of a .NET client for LadybugDB — native binary acquisition, library resolution, generated P/Invoke interop, safe native-handle ownership — ending with a working `QueryAsync` that opens a real database, runs Cypher, and reads a string result.

**Architecture:** Three layers. A ClangSharp-generated `internal` interop layer over `lbug.h`; a hand-written `Interop/` layer of `SafeHandle` types that own native resources; and a small public API (`LadybugDatabase`, `LadybugConnection`, `LadybugQueryResult`). The managed package ships zero native binaries — a companion `LadybugDb.Client.Native` package carries six RIDs fetched from pinned upstream releases and verified by SHA256.

**Tech Stack:** .NET 10, C# 13, ClangSharpPInvokeGenerator, TUnit, LadybugDB 0.18.3 C API.

## Global Constraints

- Target framework: `net10.0`. Pin the SDK in `global.json` to `10.0.300` with `rollForward: latestFeature` — an 11.0 preview is present on some machines and must not be picked up implicitly.
- `LadybugDb.Client` ships **zero** native binaries. Any `runtimes/**` content in its `.nupkg` is a defect.
- Ladybug version is pinned in `LadybugDb.Client.Native/liblbug.version`. Only `v0.18.3` is in scope for this plan.
- Native binaries are **never committed**. `.gitignore` already excludes `LadybugDb.Client.Native/runtimes/`.
- All primary keys used in tests and samples must be `INT64`. A STRING primary key costs 4.8× an INT64 one at identical row count (see spec § Risks) — never model an example on a string key.
- Generated interop lives in `LadybugDb.Client/Native/` and is `internal`. It must never appear in the public API surface.
- `TreatWarningsAsErrors` is on for all projects.
- Every native call returning `lbug_state` must have its result checked. `LbugSuccess = 0`, `LbugError = 1`.
- Every `char*` returned by the C API must be freed with `lbug_destroy_string`. There is exactly one helper for this; no call site may skip it.

## Supported RIDs

| RID | Upstream release asset | Library file |
|---|---|---|
| `linux-x64` | `liblbug-linux-x86_64.tar.gz` | `liblbug.so` |
| `linux-arm64` | `liblbug-linux-aarch64.tar.gz` | `liblbug.so` |
| `osx-x64` | `liblbug-osx-x86_64.tar.gz` | `liblbug.dylib` |
| `osx-arm64` | `liblbug-osx-arm64.tar.gz` | `liblbug.dylib` |
| `win-x64` | `liblbug-windows-x86_64.zip` | `lbug_shared.dll` |
| `win-arm64` | `liblbug-windows-arm64.zip` | `lbug_shared.dll` |

Asset URL format: `https://github.com/LadybugDB/ladybug/releases/download/<tag>/<asset>`

> **Task 2 Step 1 establishes the real archive layout and library filenames.** The names above are the expected values; if an archive's actual contents differ, the values recorded in `liblbug.lock` by Task 2 win, and later tasks use those.

## File Structure

```
global.json                                  SDK pin
Directory.Build.props                        shared properties, warnings-as-errors
LadybugDb.Client.slnx                        solution

LadybugDb.Client/
  LadybugDb.Client.csproj
  Native/LbugNative.g.cs                     GENERATED, internal, committed
  Native/NativeLibraryResolver.cs            ModuleInitializer + probing
  Interop/LbugStructHandle.cs                base: owns a NativeMemory-allocated struct
  Interop/LbugDatabaseHandle.cs
  Interop/LbugConnectionHandle.cs
  Interop/LbugQueryResultHandle.cs
  Interop/NativeString.cs                    the single lbug_destroy_string helper
  LadybugDatabase.cs                         public entry point
  LadybugConnection.cs
  LadybugQueryResult.cs
  LadybugConfig.cs
  LadybugException.cs

LadybugDb.Client.Native/
  LadybugDb.Client.Native.csproj             packs runtimes/**, no managed code
  liblbug.version                            "v0.18.3"
  liblbug.lock                               sha256 per asset

LadybugDb.Client.Tests/                      unit, no engine
LadybugDb.Client.IntegrationTests/           real liblbug required

scripts/fetch-liblbug.sh                     download + verify + extract
scripts/regen-interop.sh                     ClangSharp invocation
.github/workflows/ci.yml
```

---

### Task 1: Repository scaffold that builds and tests

**Files:**
- Create: `global.json`, `Directory.Build.props`, `LadybugDb.Client.slnx`
- Create: `LadybugDb.Client/LadybugDb.Client.csproj`
- Create: `LadybugDb.Client.Tests/LadybugDb.Client.Tests.csproj`
- Create: `.github/workflows/ci.yml`
- Test: `LadybugDb.Client.Tests/ScaffoldTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: a buildable solution; `LadybugDb.Client` assembly with `InternalsVisibleTo("LadybugDb.Client.Tests")`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.Tests/ScaffoldTests.cs`:

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class ScaffoldTests
{
    [Test]
    public async Task ClientAssembly_IsReferencedAndLoadable()
    {
        var asm = typeof(LadybugDb.Client.LadybugConfig).Assembly;
        await Assert.That(asm.GetName().Name).IsEqualTo("LadybugDb.Client");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test`
Expected: FAIL — the solution does not exist yet, then once created, `LadybugConfig` is not defined (CS0234).

- [ ] **Step 3: Create the scaffold**

`global.json`:

```json
{
  "sdk": {
    "version": "10.0.300",
    "rollForward": "latestFeature"
  }
}
```

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Authors>Harry Cordewener</Authors>
    <PackageProjectUrl>https://github.com/HarryCordewener/ladybugdb-dotnet-client</PackageProjectUrl>
    <RepositoryUrl>https://github.com/HarryCordewener/ladybugdb-dotnet-client</RepositoryUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
  </PropertyGroup>
</Project>
```

`LadybugDb.Client/LadybugDb.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <IsPackable>true</IsPackable>
    <PackageId>LadybugDb.Client</PackageId>
    <Description>.NET client for LadybugDB, an embedded graph database with Cypher. Managed assembly only; install LadybugDb.Client.Native for the embedded engine.</Description>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="LadybugDb.Client.Tests" />
    <InternalsVisibleTo Include="LadybugDb.Client.IntegrationTests" />
  </ItemGroup>
</Project>
```

`LadybugDb.Client/LadybugConfig.cs`:

```csharp
namespace LadybugDb.Client;

/// <summary>Runtime configuration for opening a <see cref="LadybugDatabase"/>.</summary>
public sealed record LadybugConfig
{
    /// <summary>Max buffer pool size in bytes. 0 selects the engine default.</summary>
    public ulong BufferPoolSize { get; init; }

    /// <summary>Max threads used during query execution. 0 selects the engine default.</summary>
    public ulong MaxThreads { get; init; }

    /// <summary>Compress supported types on disk.</summary>
    public bool EnableCompression { get; init; } = true;

    /// <summary>Open read-only. No write transaction is permitted on the database.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Max database size in bytes. 0 selects the engine default.</summary>
    public ulong MaxDbSize { get; init; }
}
```

`LadybugDb.Client.Tests/LadybugDb.Client.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="0.25.21" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\LadybugDb.Client\LadybugDb.Client.csproj" />
  </ItemGroup>
</Project>
```

`LadybugDb.Client.slnx`:

```xml
<Solution>
  <Project Path="LadybugDb.Client/LadybugDb.Client.csproj" />
  <Project Path="LadybugDb.Client.Tests/LadybugDb.Client.Tests.csproj" />
</Solution>
```

`.github/workflows/ci.yml`:

```yaml
name: ci
on:
  push:
    branches: [main]
  pull_request:

jobs:
  build:
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false
      matrix:
        os: [ubuntu-latest, windows-latest]
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Restore
        run: dotnet restore
      - name: Build
        run: dotnet build --no-restore -c Release
      - name: Unit tests
        run: dotnet test LadybugDb.Client.Tests --no-build -c Release
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test`
Expected: PASS, 1 test.

- [ ] **Step 5: Commit**

```bash
git add global.json Directory.Build.props LadybugDb.Client.slnx LadybugDb.Client LadybugDb.Client.Tests .github
git commit -m "build: scaffold solution, projects and CI"
```

---

### Task 2: Fetch and verify native binaries

**Files:**
- Create: `scripts/fetch-liblbug.sh`
- Create: `LadybugDb.Client.Native/liblbug.version`
- Create: `LadybugDb.Client.Native/liblbug.lock`

**Interfaces:**
- Consumes: nothing.
- Produces: `LadybugDb.Client.Native/runtimes/<rid>/native/<libfile>` for all six RIDs; `liblbug.lock` mapping asset name to SHA256.

- [ ] **Step 1: Discover the real archive layout**

Do this before writing the script — the library filename inside each archive must be observed, not assumed.

```bash
cd /tmp && curl -sL -o probe.tar.gz \
  https://github.com/LadybugDB/ladybug/releases/download/v0.18.3/liblbug-linux-x86_64.tar.gz
tar -tzf probe.tar.gz
curl -sL -o probe.zip \
  https://github.com/LadybugDB/ladybug/releases/download/v0.18.3/liblbug-windows-x86_64.zip
unzip -l probe.zip
```

Record the actual library filenames. If they differ from the table in Global Constraints, use the observed names throughout and note the correction in `liblbug.lock` as a comment.

- [ ] **Step 2: Write the fetch script**

`scripts/fetch-liblbug.sh`:

```bash
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

# rid|asset
ASSETS="
linux-x64|liblbug-linux-x86_64.tar.gz
linux-arm64|liblbug-linux-aarch64.tar.gz
osx-x64|liblbug-osx-x86_64.tar.gz
osx-arm64|liblbug-osx-arm64.tar.gz
win-x64|liblbug-windows-x86_64.zip
win-arm64|liblbug-windows-arm64.zip
"

WRITE_LOCK=0
[ "${1:-}" = "--update-lock" ] && WRITE_LOCK=1
[ "$WRITE_LOCK" = 1 ] && : > "$LOCK"

echo "$ASSETS" | while IFS='|' read -r RID ASSET; do
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
  find "$EX" -type f \( -name '*.so' -o -name '*.dylib' -o -name '*.dll' \) \
    -exec cp {} "$DEST/" \;
  ls -1 "$DEST"
done

echo "done: $VERSION"
```

`LadybugDb.Client.Native/liblbug.version`:

```
v0.18.3
```

- [ ] **Step 3: Generate the lockfile and verify it**

```bash
chmod +x scripts/fetch-liblbug.sh
./scripts/fetch-liblbug.sh --update-lock
cat LadybugDb.Client.Native/liblbug.lock
```

Expected: six lines, each `<asset>  <64 hex chars>`.

- [ ] **Step 4: Prove verification actually fails on tampering**

A checksum check that never fails is not a check. Verify it rejects a bad hash:

```bash
cp LadybugDb.Client.Native/liblbug.lock /tmp/lock.bak
sed -i '1s/  [0-9a-f]\{64\}$/  0000000000000000000000000000000000000000000000000000000000000000/' \
  LadybugDb.Client.Native/liblbug.lock
./scripts/fetch-liblbug.sh; echo "exit=$?"
cp /tmp/lock.bak LadybugDb.Client.Native/liblbug.lock
```

Expected: `FATAL: checksum mismatch`, non-zero exit. Then re-run `./scripts/fetch-liblbug.sh` and expect a clean pass.

- [ ] **Step 5: Commit**

```bash
git add scripts/fetch-liblbug.sh LadybugDb.Client.Native/liblbug.version LadybugDb.Client.Native/liblbug.lock
git commit -m "build: fetch pinned liblbug binaries with checksum verification"
```

---

### Task 3: The native package

**Files:**
- Create: `LadybugDb.Client.Native/LadybugDb.Client.Native.csproj`
- Create: `LadybugDb.Client.Native/THIRD-PARTY-NOTICES.md`
- Modify: `LadybugDb.Client.slnx`
- Test: `LadybugDb.Client.Tests/PackagingTests.cs`

**Interfaces:**
- Consumes: `runtimes/<rid>/native/*` from Task 2.
- Produces: `LadybugDb.Client.Native` package laying binaries at `runtimes/<rid>/native/`, the path Task 4's resolver probes.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.Tests/PackagingTests.cs`:

```csharp
using System.IO.Compression;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "LadybugDb.Client.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string? FindPackage(string id) =>
        Directory.EnumerateFiles(RepoRoot(), $"{id}.*.nupkg", SearchOption.AllDirectories)
            .FirstOrDefault(p => !p.Contains(".symbols.", StringComparison.Ordinal));

    [Test]
    public async Task ManagedPackage_ShipsNoNativeBinaries()
    {
        var pkg = FindPackage("LadybugDb.Client");
        await Assert.That(pkg).IsNotNull();

        using var zip = ZipFile.OpenRead(pkg!);
        var natives = zip.Entries.Where(e => e.FullName.StartsWith("runtimes/")).ToList();
        await Assert.That(natives).IsEmpty();
    }

    [Test]
    public async Task NativePackage_ShipsAllSixRuntimeIdentifiers()
    {
        var pkg = FindPackage("LadybugDb.Client.Native");
        await Assert.That(pkg).IsNotNull();

        using var zip = ZipFile.OpenRead(pkg!);
        string[] rids = ["linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64"];
        foreach (var rid in rids)
        {
            var has = zip.Entries.Any(e => e.FullName.StartsWith($"runtimes/{rid}/native/"));
            await Assert.That(has).IsTrue();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet pack -c Release && dotnet test LadybugDb.Client.Tests --filter PackagingTests`
Expected: FAIL — `LadybugDb.Client.Native` package not found (assertion on null).

- [ ] **Step 3: Create the native project**

`LadybugDb.Client.Native/LadybugDb.Client.Native.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>LadybugDb.Client.Native</PackageId>
    <Description>Native liblbug binaries for LadybugDb.Client, for six runtime identifiers. Install alongside LadybugDb.Client to run the embedded engine.</Description>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NoWarn>$(NoWarn);NU5128</NoWarn>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <IsPackable>true</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <None Include="runtimes/**/*"
          Pack="true"
          PackagePath="runtimes"
          CopyToOutputDirectory="PreserveNewest" />
    <None Include="THIRD-PARTY-NOTICES.md" Pack="true" PackagePath="" />
  </ItemGroup>

  <Target Name="FailIfNativesMissing" BeforeTargets="Build;Pack">
    <ItemGroup>
      <_Natives Include="runtimes/**/*" />
    </ItemGroup>
    <Error Condition="'@(_Natives)' == ''"
           Text="No native binaries found. Run scripts/fetch-liblbug.sh first." />
  </Target>
</Project>
```

`LadybugDb.Client.Native/THIRD-PARTY-NOTICES.md`:

```markdown
# Third-party notices

This package redistributes compiled binaries of **LadybugDB**.

- Source: https://github.com/LadybugDB/ladybug
- Version: see `liblbug.version` in the source repository (v0.18.3)
- License: MIT

LadybugDB is MIT licensed, the same licence as this package, so the redistributed
binaries carry no additional restrictions.
```

Add to `LadybugDb.Client.slnx`:

```xml
  <Project Path="LadybugDb.Client.Native/LadybugDb.Client.Native.csproj" />
```

- [ ] **Step 4: Run test to verify it passes**

```bash
./scripts/fetch-liblbug.sh
dotnet pack -c Release
dotnet test LadybugDb.Client.Tests --filter PackagingTests
```

Expected: PASS, 2 tests. If `LadybugDb.Client` ever gains a `runtimes/` entry, the first test fails — that is its job.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client.Native LadybugDb.Client.slnx LadybugDb.Client.Tests/PackagingTests.cs
git commit -m "build: add LadybugDb.Client.Native package with packaging guards"
```

---

### Task 4: Native library resolver

**Files:**
- Create: `LadybugDb.Client/Native/NativeLibraryResolver.cs`
- Test: `LadybugDb.Client.Tests/NativeLibraryResolverTests.cs`

**Interfaces:**
- Consumes: `runtimes/<rid>/native/` layout from Task 3.
- Produces: `internal static class NativeLibraryResolver` with `internal const string LibraryName = "lbug"`, `internal static IEnumerable<string> ProbePaths(string rid, string fileName)`, and a `[ModuleInitializer] internal static void Initialize()`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.Tests/NativeLibraryResolverTests.cs`:

```csharp
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class NativeLibraryResolverTests
{
    [Test]
    public async Task ProbePaths_IncludeRuntimesLayout()
    {
        var paths = NativeLibraryResolver.ProbePaths("linux-x64", "liblbug.so").ToList();
        var hasRuntimes = paths.Any(p =>
            p.Replace('\\', '/').Contains("runtimes/linux-x64/native/liblbug.so"));
        await Assert.That(hasRuntimes).IsTrue();
    }

    [Test]
    public async Task ProbePaths_IncludeAppLocalFallback()
    {
        var paths = NativeLibraryResolver.ProbePaths("win-x64", "lbug_shared.dll").ToList();
        var hasFlat = paths.Any(p => p.EndsWith("lbug_shared.dll", StringComparison.Ordinal)
                                     && !p.Contains("runtimes"));
        await Assert.That(hasFlat).IsTrue();
    }

    [Test]
    public async Task MissingLibrary_ThrowsMessageNamingTheNativePackage()
    {
        var ex = NativeLibraryResolver.CreateMissingLibraryException();
        await Assert.That(ex.Message).Contains("LadybugDb.Client.Native");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test LadybugDb.Client.Tests --filter NativeLibraryResolverTests`
Expected: FAIL — `NativeLibraryResolver` does not exist (CS0246).

- [ ] **Step 3: Implement the resolver**

`LadybugDb.Client/Native/NativeLibraryResolver.cs`:

```csharp
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LadybugDb.Client.Native;

/// <summary>
/// Resolves the liblbug native library from the layout NuGet produces for
/// LadybugDb.Client.Native, so the managed package can ship no binaries of its own.
/// </summary>
internal static class NativeLibraryResolver
{
    internal const string LibraryName = "lbug";

    [ModuleInitializer]
    internal static void Initialize() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, Resolve);

    internal static string CurrentRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            var other => other.ToString().ToLowerInvariant(),
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"osx-{arch}";
        return $"linux-{arch}";
    }

    internal static string CurrentFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "lbug_shared.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "liblbug.dylib";
        return "liblbug.so";
    }

    internal static IEnumerable<string> ProbePaths(string rid, string fileName)
    {
        var roots = new List<string>();
        var asmDir = Path.GetDirectoryName(typeof(NativeLibraryResolver).Assembly.Location);
        if (!string.IsNullOrEmpty(asmDir)) roots.Add(asmDir);
        if (!string.IsNullOrEmpty(AppContext.BaseDirectory)) roots.Add(AppContext.BaseDirectory);

        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            yield return Path.Combine(root, "runtimes", rid, "native", fileName);
            yield return Path.Combine(root, fileName);
        }
    }

    internal static DllNotFoundException CreateMissingLibraryException() =>
        new($"""
             Could not load the LadybugDB native library ({CurrentFileName()}) for {CurrentRid()}.

             LadybugDb.Client ships no native binaries by design. Add the companion package:

                 dotnet add package LadybugDb.Client.Native

             If you supply the library yourself, place it at
             runtimes/{CurrentRid()}/native/{CurrentFileName()} next to the application,
             or alongside the application binary.
             """);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return IntPtr.Zero;

        foreach (var candidate in ProbePaths(CurrentRid(), CurrentFileName()))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // Fall back to the OS loader (system install, LD_LIBRARY_PATH, etc.).
        return NativeLibrary.TryLoad(LibraryName, assembly, searchPath, out var sys)
            ? sys
            : throw CreateMissingLibraryException();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test LadybugDb.Client.Tests --filter NativeLibraryResolverTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Native/NativeLibraryResolver.cs LadybugDb.Client.Tests/NativeLibraryResolverTests.cs
git commit -m "feat: resolve liblbug from the native package layout"
```

---

### Task 5: Generated interop with a CI drift check

**Files:**
- Create: `scripts/regen-interop.sh`
- Create: `LadybugDb.Client/Native/LbugNative.g.cs` (generated, committed)
- Create: `third-party/lbug.h` (pinned copy of the header)
- Modify: `.github/workflows/ci.yml`
- Test: `LadybugDb.Client.Tests/InteropSurfaceTests.cs`

**Interfaces:**
- Consumes: `NativeLibraryResolver.LibraryName`.
- Produces: `internal static partial class LbugNative` with `[LibraryImport("lbug")]` entry points, and the structs `lbug_database`, `lbug_connection`, `lbug_prepared_statement`, `lbug_query_result`, `lbug_flat_tuple`, `lbug_value`, `lbug_system_config`, plus `enum lbug_state { LbugSuccess = 0, LbugError = 1 }`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.Tests/InteropSurfaceTests.cs`:

```csharp
using System.Reflection;
using LadybugDb.Client.Native;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class InteropSurfaceTests
{
    private static readonly Type Native =
        typeof(NativeLibraryResolver).Assembly.GetType("LadybugDb.Client.Native.LbugNative")!;

    [Test]
    public async Task LbugNative_ExposesCoreLifecycleEntryPoints()
    {
        string[] required =
        [
            "lbug_database_init", "lbug_database_destroy",
            "lbug_connection_init", "lbug_connection_destroy",
            "lbug_connection_query", "lbug_query_result_destroy",
            "lbug_query_result_is_success", "lbug_query_result_get_error_message",
            "lbug_query_result_has_next", "lbug_query_result_get_next",
            "lbug_destroy_string", "lbug_default_system_config",
        ];

        foreach (var name in required)
        {
            var m = Native.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            await Assert.That(m).IsNotNull();
        }
    }

    [Test]
    public async Task InteropTypes_AreNotPublic()
    {
        await Assert.That(Native.IsPublic).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test LadybugDb.Client.Tests --filter InteropSurfaceTests`
Expected: FAIL — `LbugNative` type not found, `Native` is null (NullReferenceException).

- [ ] **Step 3: Write the generation script and run it**

`scripts/regen-interop.sh`:

```bash
#!/usr/bin/env bash
# Regenerates the raw interop from the pinned lbug.h. Output is committed;
# CI re-runs this and fails if the result differs.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/LadybugDb.Client.Native/liblbug.version")"
HEADER="$ROOT/third-party/lbug.h"
OUT="$ROOT/LadybugDb.Client/Native/LbugNative.g.cs"

mkdir -p "$(dirname "$HEADER")"
curl -fsSL -o "$HEADER" \
  "https://raw.githubusercontent.com/LadybugDB/ladybug/$VERSION/src/include/c_api/lbug.h"

dotnet tool restore

dotnet tool run ClangSharpPInvokeGenerator \
  --file "$HEADER" \
  --output "$OUT" \
  --namespace LadybugDb.Client.Native \
  --methodClassName LbugNative \
  --libraryPath lbug \
  --config latest-codegen generate-macro-bindings log-potential-typedef-remappings \
           multi-file-directory-behavior=none unix-types \
  --with-access-specifier "*=Internal" \
  --file-directory "$ROOT/third-party"

echo "generated $OUT"
```

Register the tool:

```bash
dotnet new tool-manifest --force
dotnet tool install ClangSharpPInvokeGenerator --version 18.1.0
chmod +x scripts/regen-interop.sh
./scripts/regen-interop.sh
```

If the generator emits `public` members despite `--with-access-specifier`, add a post-generation `sed` step to the script rather than hand-editing the output — the file must be reproducible:

```bash
sed -i 's/^\( *\)public \(partial class LbugNative\)/\1internal \2/' "$OUT"
```

- [ ] **Step 4: Run test to verify it passes, then add the drift check**

Run: `dotnet build && dotnet test LadybugDb.Client.Tests --filter InteropSurfaceTests`
Expected: PASS, 2 tests.

Append to `.github/workflows/ci.yml` as a new job:

```yaml
  interop-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Regenerate interop
        run: ./scripts/regen-interop.sh
      - name: Fail if generated interop is stale
        run: |
          if ! git diff --exit-code -- LadybugDb.Client/Native/LbugNative.g.cs third-party/lbug.h; then
            echo "::error::Generated interop is out of date. Run scripts/regen-interop.sh and commit."
            exit 1
          fi
```

- [ ] **Step 5: Commit**

```bash
git add .config/dotnet-tools.json scripts/regen-interop.sh third-party/lbug.h \
        LadybugDb.Client/Native/LbugNative.g.cs LadybugDb.Client.Tests/InteropSurfaceTests.cs .github/workflows/ci.yml
git commit -m "feat: generate lbug interop with ClangSharp and guard against drift"
```

---

### Task 6: Native handle ownership

**Files:**
- Create: `LadybugDb.Client/Interop/LbugStructHandle.cs`
- Create: `LadybugDb.Client/Interop/LbugDatabaseHandle.cs`
- Create: `LadybugDb.Client/Interop/LbugConnectionHandle.cs`
- Create: `LadybugDb.Client/Interop/NativeString.cs`
- Create: `LadybugDb.Client/LadybugException.cs`
- Test: `LadybugDb.Client.Tests/HandleTests.cs`

**Interfaces:**
- Consumes: `LbugNative` from Task 5.
- Produces: `internal abstract class LbugStructHandle : SafeHandle` with `unsafe void* Pointer`; `LbugDatabaseHandle.Open(string path, in lbug_system_config)`; `LbugConnectionHandle.Open(LbugDatabaseHandle)`; `internal static class NativeString { unsafe string TakeOwnership(sbyte* native) }`; `public class LadybugException : Exception`.

> **Why this is not a plain `SafeHandle` over the native object:** the C API's handle types are **structs passed by pointer**, not opaque pointers — `typedef struct { void* _database; } lbug_database;` and `typedef struct { void* _query_result; bool _is_owned_by_cpp; } lbug_query_result;`. The caller allocates the struct and passes its address to `*_init`, and passes the same address to `*_destroy`. So each handle owns a **`NativeMemory`-allocated struct**, and `ReleaseHandle` must call destroy *and then* free that allocation. Freeing without destroying leaks the engine object; destroying without freeing leaks the struct.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.Tests/HandleTests.cs`:

```csharp
using LadybugDb.Client;
using LadybugDb.Client.Interop;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.Tests;

public class HandleTests
{
    [Test]
    public async Task NewHandle_IsInvalidBeforeAllocation()
    {
        using var h = new UnallocatedHandle();
        await Assert.That(h.IsInvalid).IsTrue();
    }

    [Test]
    public async Task LadybugException_CarriesTheFailingStatement()
    {
        var ex = new LadybugException("boom", "MATCH (n) RETURN n");
        await Assert.That(ex.Statement).IsEqualTo("MATCH (n) RETURN n");
        await Assert.That(ex.Message).Contains("boom");
    }

    private sealed class UnallocatedHandle : LbugStructHandle
    {
        protected override bool ReleaseHandle() => true;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test LadybugDb.Client.Tests --filter HandleTests`
Expected: FAIL — `LbugStructHandle` and `LadybugException` do not exist (CS0246).

- [ ] **Step 3: Implement the ownership layer**

`LadybugDb.Client/LadybugException.cs`:

```csharp
namespace LadybugDb.Client;

/// <summary>An error reported by the LadybugDB engine.</summary>
public class LadybugException : Exception
{
    public LadybugException(string message, string? statement = null)
        : base(statement is null ? message : $"{message}{Environment.NewLine}Statement: {statement}")
        => Statement = statement;

    /// <summary>The Cypher statement that produced this error, when one is known.</summary>
    public string? Statement { get; }
}

/// <summary>
/// Thrown when the engine refuses a write because another write transaction is active.
/// LadybugDB permits exactly one write transaction at a time and rejects rather than queueing,
/// so this is expected under contention and is safe to retry.
/// </summary>
public sealed class LadybugWriteConflictException : LadybugException
{
    public LadybugWriteConflictException(string message, string? statement = null)
        : base(message, statement) { }
}
```

`LadybugDb.Client/Interop/LbugStructHandle.cs`:

```csharp
using System.Runtime.InteropServices;

namespace LadybugDb.Client.Interop;

/// <summary>
/// Owns a natively allocated C API struct (for example <c>lbug_database</c>).
/// The C API allocates nothing for these structs itself: the caller provides storage,
/// passes its address to the matching <c>*_init</c>, and passes the same address to
/// <c>*_destroy</c>. Releasing therefore has two obligations — destroy, then free.
/// </summary>
internal abstract class LbugStructHandle : SafeHandle
{
    protected LbugStructHandle() : base(IntPtr.Zero, ownsHandle: true) { }

    public override bool IsInvalid => handle == IntPtr.Zero;

    public unsafe void* Pointer => (void*)handle;

    /// <summary>Allocates zeroed native storage of <paramref name="size"/> bytes and takes ownership.</summary>
    protected unsafe void* Allocate(nuint size)
    {
        var p = NativeMemory.AllocZeroed(size);
        SetHandle((IntPtr)p);
        return p;
    }

    /// <summary>Frees the struct storage. Call only after the matching <c>*_destroy</c>.</summary>
    protected unsafe void FreeStorage()
    {
        if (handle != IntPtr.Zero)
        {
            NativeMemory.Free((void*)handle);
            SetHandle(IntPtr.Zero);
        }
    }
}
```

`LadybugDb.Client/Interop/NativeString.cs`:

```csharp
using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// The single place a <c>char*</c> from the C API is converted and freed.
/// Every string the API returns must be released with <c>lbug_destroy_string</c>;
/// routing all of them through here is what keeps that guarantee checkable.
/// </summary>
internal static class NativeString
{
    /// <summary>Copies a native string into managed memory and frees the native buffer.</summary>
    internal static unsafe string TakeOwnership(sbyte* native)
    {
        if (native is null) return string.Empty;
        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)native) ?? string.Empty;
        }
        finally
        {
            LbugNative.lbug_destroy_string(native);
        }
    }
}
```

`LadybugDb.Client/Interop/LbugDatabaseHandle.cs`:

```csharp
using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugDatabaseHandle : LbugStructHandle
{
    internal static unsafe LbugDatabaseHandle Open(string path, lbug_system_config config)
    {
        var h = new LbugDatabaseHandle();
        var db = (lbug_database*)h.Allocate((nuint)sizeof(lbug_database));

        var utf8 = Marshal.StringToCoTaskMemUTF8(path);
        try
        {
            var state = LbugNative.lbug_database_init((sbyte*)utf8, config, db);
            if (state != lbug_state.LbugSuccess)
            {
                h.Dispose();
                throw new LadybugException($"Failed to open LadybugDB database at '{path}'.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }

        return h;
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_database_destroy((lbug_database*)handle);
        FreeStorage();
        return true;
    }
}
```

`LadybugDb.Client/Interop/LbugConnectionHandle.cs`:

```csharp
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugConnectionHandle : LbugStructHandle
{
    internal static unsafe LbugConnectionHandle Open(LbugDatabaseHandle database)
    {
        var h = new LbugConnectionHandle();
        var conn = (lbug_connection*)h.Allocate((nuint)sizeof(lbug_connection));

        var state = LbugNative.lbug_connection_init((lbug_database*)database.Pointer, conn);
        if (state != lbug_state.LbugSuccess)
        {
            h.Dispose();
            throw new LadybugException("Failed to open a LadybugDB connection.");
        }

        return h;
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_connection_destroy((lbug_connection*)handle);
        FreeStorage();
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test LadybugDb.Client.Tests --filter HandleTests`
Expected: PASS, 2 tests.

> If the generated struct names differ from `lbug_database` / `lbug_connection` / `lbug_system_config` (ClangSharp may apply a naming convention), use the generated names and keep everything else identical. Check `LbugNative.g.cs` for the actual type names before writing this code.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Interop LadybugDb.Client/LadybugException.cs LadybugDb.Client.Tests/HandleTests.cs
git commit -m "feat: own native lbug structs through SafeHandle with destroy-then-free"
```

---

### Task 7: Open a database and execute a statement

**Files:**
- Create: `LadybugDb.Client/LadybugDatabase.cs`
- Create: `LadybugDb.Client/LadybugConnection.cs`
- Create: `LadybugDb.Client/Interop/LbugQueryResultHandle.cs`
- Create: `LadybugDb.Client.IntegrationTests/LadybugDb.Client.IntegrationTests.csproj`
- Create: `LadybugDb.Client.IntegrationTests/DatabaseLifecycleTests.cs`
- Modify: `LadybugDb.Client.slnx`, `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `LbugDatabaseHandle`, `LbugConnectionHandle`, `NativeString`, `LadybugConfig`.
- Produces: `public sealed class LadybugDatabase : IDisposable` with `LadybugDatabase(string path, LadybugConfig? config = null)` and `ValueTask<LadybugConnection> ConnectAsync(CancellationToken = default)`; `public sealed class LadybugConnection : IAsyncDisposable` with `ValueTask<LadybugQueryResult> QueryAsync(string cypher, CancellationToken = default)`.

- [ ] **Step 1: Write the failing test**

`LadybugDb.Client.IntegrationTests/DatabaseLifecycleTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class DatabaseLifecycleTests
{
    private static string TempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"lbug-test-{Guid.NewGuid():N}");

    [Test]
    public async Task OpenDatabase_CreateTable_AndInsertRow()
    {
        var path = TempDbPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            // INT64 primary key: a STRING key costs ~4.8x at equal row count.
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 42, name: 'Limbo'})")) { }

            await using var result = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.HasNext).IsTrue();
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Test]
    public async Task InvalidCypher_ThrowsLadybugExceptionCarryingTheStatement()
    {
        var path = TempDbPath();
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();

            const string bad = "MATCH (o:NoSuchTable) RETURN o.nope";
            var ex = await Assert.ThrowsAsync<LadybugException>(
                async () => await conn.QueryAsync(bad));

            await Assert.That(ex!.Statement).IsEqualTo(bad);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
```

> **A LadybugDB database is a single file plus siblings, not a directory.** `Directory.Delete` alone silently does nothing, which leaves a stale catalog and makes the next run fail with "already exists in catalog". The cleanup above removes both forms deliberately.

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test LadybugDb.Client.IntegrationTests
```

Expected: FAIL — `LadybugDatabase` does not exist (CS0246).

- [ ] **Step 3: Implement the public entry points**

`LadybugDb.Client/Interop/LbugQueryResultHandle.cs`:

```csharp
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugQueryResultHandle : LbugStructHandle
{
    internal static unsafe LbugQueryResultHandle Allocate(out lbug_query_result* result)
    {
        var h = new LbugQueryResultHandle();
        result = (lbug_query_result*)h.Allocate((nuint)sizeof(lbug_query_result));
        return h;
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_query_result_destroy((lbug_query_result*)handle);
        FreeStorage();
        return true;
    }
}
```

`LadybugDb.Client/LadybugDatabase.cs`:

```csharp
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// An embedded LadybugDB database. Opening is a local file operation, so this type is
/// constructed and disposed synchronously; connections and results are async-disposable.
/// </summary>
public sealed class LadybugDatabase : IDisposable
{
    private readonly LbugDatabaseHandle _handle;

    /// <summary>
    /// Serializes write transactions. LadybugDB permits exactly one write transaction at a
    /// time and raises rather than queueing, so the client holds this rather than letting
    /// callers collide. Read paths do not take it.
    /// </summary>
    internal SemaphoreSlim WriteLock { get; } = new(1, 1);

    public LadybugDatabase(string path, LadybugConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = LbugDatabaseHandle.Open(path, BuildConfig(config ?? new LadybugConfig()));
    }

    internal LbugDatabaseHandle Handle => _handle;

    /// <summary>Opens a connection. Multiple connections may share one database.</summary>
    public ValueTask<LadybugConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return ValueTask.FromResult(new LadybugConnection(this, LbugConnectionHandle.Open(_handle)));
    }

    private static unsafe lbug_system_config BuildConfig(LadybugConfig config)
    {
        var native = LbugNative.lbug_default_system_config();
        if (config.BufferPoolSize != 0) native.buffer_pool_size = config.BufferPoolSize;
        if (config.MaxThreads != 0) native.max_num_threads = config.MaxThreads;
        if (config.MaxDbSize != 0) native.max_db_size = config.MaxDbSize;
        native.enable_compression = config.EnableCompression;
        native.read_only = config.ReadOnly;
        return native;
    }

    public void Dispose()
    {
        _handle.Dispose();
        WriteLock.Dispose();
    }
}
```

`LadybugDb.Client/LadybugConnection.cs`:

```csharp
using System.Runtime.InteropServices;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// A connection to a <see cref="LadybugDatabase"/>.
/// Methods are async-shaped but currently complete synchronously: the engine is embedded and
/// the work is CPU and local-disk bound, so offloading would add cost without benefit. The
/// signatures are async so genuine offloading can be added later without an API break.
/// </summary>
public sealed class LadybugConnection : IAsyncDisposable
{
    private readonly LadybugDatabase _database;
    private readonly LbugConnectionHandle _handle;

    internal LadybugConnection(LadybugDatabase database, LbugConnectionHandle handle)
    {
        _database = database;
        _handle = handle;
    }

    /// <summary>Executes a Cypher statement and returns its result.</summary>
    public ValueTask<LadybugQueryResult> QueryAsync(string cypher, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cypher);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        return ValueTask.FromResult(Execute(cypher));
    }

    private unsafe LadybugQueryResult Execute(string cypher)
    {
        var handle = LbugQueryResultHandle.Allocate(out var result);
        var utf8 = Marshal.StringToCoTaskMemUTF8(cypher);
        try
        {
            var state = LbugNative.lbug_connection_query(
                (lbug_connection*)_handle.Pointer, (sbyte*)utf8, result);

            if (state != lbug_state.LbugSuccess || !LbugNative.lbug_query_result_is_success(result))
            {
                var message = NativeString.TakeOwnership(
                    LbugNative.lbug_query_result_get_error_message(result));
                handle.Dispose();
                throw message.Contains("one write transaction", StringComparison.OrdinalIgnoreCase)
                    ? new LadybugWriteConflictException(message, cypher)
                    : new LadybugException(
                        string.IsNullOrEmpty(message) ? "Query failed." : message, cypher);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }

        return new LadybugQueryResult(handle);
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

`LadybugDb.Client/LadybugQueryResult.cs`:

```csharp
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>The result of a Cypher statement.</summary>
public sealed class LadybugQueryResult : IAsyncDisposable
{
    private readonly LbugQueryResultHandle _handle;

    internal LadybugQueryResult(LbugQueryResultHandle handle) => _handle = handle;

    internal LbugQueryResultHandle Handle => _handle;

    public unsafe bool IsSuccess =>
        LbugNative.lbug_query_result_is_success((lbug_query_result*)_handle.Pointer);

    public unsafe bool HasNext =>
        LbugNative.lbug_query_result_has_next((lbug_query_result*)_handle.Pointer);

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

`LadybugDb.Client.IntegrationTests/LadybugDb.Client.IntegrationTests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TUnit" Version="0.25.21" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\LadybugDb.Client\LadybugDb.Client.csproj" />
    <ProjectReference Include="..\LadybugDb.Client.Native\LadybugDb.Client.Native.csproj" />
  </ItemGroup>
</Project>
```

Add to `LadybugDb.Client.slnx`:

```xml
  <Project Path="LadybugDb.Client.IntegrationTests/LadybugDb.Client.IntegrationTests.csproj" />
```

- [ ] **Step 4: Run test to verify it passes**

```bash
./scripts/fetch-liblbug.sh
dotnet test LadybugDb.Client.IntegrationTests
```

Expected: PASS, 2 tests. This is the first proof the whole stack works end to end — resolver finds the library, handles own the structs, Cypher executes.

Add the integration job to `.github/workflows/ci.yml`:

```yaml
  integration:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json
      - name: Fetch native binaries
        run: ./scripts/fetch-liblbug.sh
      - name: Integration tests
        run: dotnet test LadybugDb.Client.IntegrationTests -c Release
```

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/LadybugDatabase.cs LadybugDb.Client/LadybugConnection.cs \
        LadybugDb.Client/LadybugQueryResult.cs LadybugDb.Client/Interop/LbugQueryResultHandle.cs \
        LadybugDb.Client.IntegrationTests LadybugDb.Client.slnx .github/workflows/ci.yml
git commit -m "feat: open a database, execute Cypher, and surface engine errors"
```

---

### Task 8: Read a value, and prove there is no leak

**Files:**
- Modify: `LadybugDb.Client/LadybugQueryResult.cs`
- Create: `LadybugDb.Client/Interop/LbugFlatTupleHandle.cs`
- Create: `LadybugDb.Client/Interop/LbugValueHandle.cs`
- Create: `LadybugDb.Client.IntegrationTests/ValueReadTests.cs`
- Create: `LadybugDb.Client.IntegrationTests/LeakTests.cs`

**Interfaces:**
- Consumes: everything from Task 7.
- Produces: `LadybugQueryResult.ReadStringAsync(ulong columnIndex, CancellationToken = default)` returning `ValueTask<string?>`, advancing one row per call. This is the seam Milestone 2 replaces with full typed value marshalling.

- [ ] **Step 1: Write the failing tests**

`LadybugDb.Client.IntegrationTests/ValueReadTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class ValueReadTests
{
    [Test]
    public async Task ReadString_ReturnsTheStoredValue()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lbug-val-{Guid.NewGuid():N}");
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 7, name: 'Master Room'})")) { }

            await using var result = await conn.QueryAsync(
                "MATCH (o:Obj) WHERE o.dbref = 7 RETURN o.name");

            var name = await result.ReadStringAsync(0);
            await Assert.That(name).IsEqualTo("Master Room");
        }
        finally { Cleanup(path); }
    }

    internal static void Cleanup(string path)
    {
        foreach (var p in new[] { path, path + ".wal", path + ".shadow", path + ".lock", path + ".tmp" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }
}
```

`LadybugDb.Client.IntegrationTests/LeakTests.cs`:

```csharp
using LadybugDb.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace LadybugDb.Client.IntegrationTests;

public class LeakTests
{
    /// <summary>
    /// The C API has ten distinct destroy/free entry points and every returned string must be
    /// released. A leak here is invisible in functional tests and fatal in a long-running server,
    /// so it gets a test that fails when it regresses.
    /// </summary>
    [Test]
    public async Task RepeatedQueries_DoNotGrowProcessMemory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lbug-leak-{Guid.NewGuid():N}");
        try
        {
            using var db = new LadybugDatabase(path);
            await using var conn = await db.ConnectAsync();
            await using (var _ = await conn.QueryAsync(
                "CREATE NODE TABLE Obj(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
            await using (var _ = await conn.QueryAsync(
                "CREATE (o:Obj {dbref: 1, name: 'seed'})")) { }

            for (var i = 0; i < 500; i++)
            {
                await using var warm = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
                _ = await warm.ReadStringAsync(0);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var baseline = Environment.WorkingSet;

            for (var i = 0; i < 5_000; i++)
            {
                await using var r = await conn.QueryAsync("MATCH (o:Obj) RETURN o.name");
                _ = await r.ReadStringAsync(0);
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var after = Environment.WorkingSet;

            var growthMb = (after - baseline) / 1024.0 / 1024.0;
            await Assert.That(growthMb).IsLessThan(32);
        }
        finally { ValueReadTests.Cleanup(path); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test LadybugDb.Client.IntegrationTests --filter ValueReadTests`
Expected: FAIL — `ReadStringAsync` is not defined (CS1061).

- [ ] **Step 3: Implement value reading**

`LadybugDb.Client/Interop/LbugFlatTupleHandle.cs`:

```csharp
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugFlatTupleHandle : LbugStructHandle
{
    internal static unsafe LbugFlatTupleHandle Allocate(out lbug_flat_tuple* tuple)
    {
        var h = new LbugFlatTupleHandle();
        tuple = (lbug_flat_tuple*)h.Allocate((nuint)sizeof(lbug_flat_tuple));
        return h;
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_flat_tuple_destroy((lbug_flat_tuple*)handle);
        FreeStorage();
        return true;
    }
}
```

`LadybugDb.Client/Interop/LbugValueHandle.cs`:

```csharp
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugValueHandle : LbugStructHandle
{
    internal static unsafe LbugValueHandle Allocate(out lbug_value* value)
    {
        var h = new LbugValueHandle();
        value = (lbug_value*)h.Allocate((nuint)sizeof(lbug_value));
        return h;
    }

    protected override unsafe bool ReleaseHandle()
    {
        LbugNative.lbug_value_destroy((lbug_value*)handle);
        FreeStorage();
        return true;
    }
}
```

Append to `LadybugDb.Client/LadybugQueryResult.cs`, inside the class:

```csharp
    /// <summary>
    /// Advances one row and reads the column at <paramref name="columnIndex"/> as a string.
    /// Returns <see langword="null"/> when there are no more rows.
    /// </summary>
    /// <remarks>
    /// Milestone 2 replaces this with full typed value marshalling; it exists now to prove the
    /// tuple and value ownership chain end to end.
    /// </remarks>
    public ValueTask<string?> ReadStringAsync(ulong columnIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ReadString(columnIndex));
    }

    private unsafe string? ReadString(ulong columnIndex)
    {
        var result = (lbug_query_result*)_handle.Pointer;
        if (!LbugNative.lbug_query_result_has_next(result)) return null;

        using var tupleHandle = LbugFlatTupleHandle.Allocate(out var tuple);
        if (LbugNative.lbug_query_result_get_next(result, tuple) != lbug_state.LbugSuccess)
            throw new LadybugException("Failed to advance to the next row.");

        using var valueHandle = LbugValueHandle.Allocate(out var value);
        if (LbugNative.lbug_flat_tuple_get_value(tuple, columnIndex, value) != lbug_state.LbugSuccess)
            throw new LadybugException($"Failed to read column {columnIndex}.");

        sbyte* raw;
        if (LbugNative.lbug_value_get_string(value, &raw) != lbug_state.LbugSuccess)
            throw new LadybugException($"Column {columnIndex} is not a string.");

        return NativeString.TakeOwnership(raw);
    }
```

Add `using LadybugDb.Client.Interop;` to the file if not already present.

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test LadybugDb.Client.IntegrationTests
```

Expected: PASS, 4 tests. The leak test takes roughly 10–30 seconds.

> If the leak test fails, the fault is a missing destroy, not a threshold that needs raising. Check that every `Allocate` is paired with a `using`, and that every `char*` goes through `NativeString.TakeOwnership`. Do not raise the 32 MB bound to make it pass.

- [ ] **Step 5: Commit**

```bash
git add LadybugDb.Client/Interop/LbugFlatTupleHandle.cs LadybugDb.Client/Interop/LbugValueHandle.cs \
        LadybugDb.Client/LadybugQueryResult.cs LadybugDb.Client.IntegrationTests
git commit -m "feat: read string values from query results, with a leak regression test"
```

---

## Milestone complete

At this point the library opens a real LadybugDB database, executes Cypher, surfaces engine errors as typed exceptions, reads values back, and proves it does not leak. Both packages build and pack correctly, and CI enforces interop drift and the no-native-binaries rule.

## What Milestone 2 covers

A second plan, written once the interop patterns above are proven against the real library:

- Full typed value marshalling dispatching on `lbug_data_type_get_id` — bool, integer widths, float/double, string, `DateOnly`, `TimeSpan`, `DateTime`, `DateTimeOffset`, `byte[]`, node, rel, list, struct, map.
- Prepared statements with all **20** `lbug_prepared_statement_bind_*` variants, including the eight signed and unsigned integer widths, each covered by a test — a mis-sized integer marshal corrupts data silently rather than throwing.
- `LadybugQueryResult : IAsyncEnumerable<LadybugRow>` and `NextResultAsync()` over `lbug_query_result_get_next_query_result`.
- Cypher-driven transactions (`BeginTransactionAsync` issuing `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK`, rolling back on undisposed-uncommitted), wired to `LadybugDatabase.WriteLock`.
- Establishing whether `enable_multi_writes` lifts the single-writer constraint, which decides
  whether the write lock stays. It is a real field on `lbug_system_config` (confirmed in the
  header, alongside `auto_checkpoint`, `checkpoint_threshold`, `throw_on_wal_replay_failure`,
  `enable_checksums` and `enable_default_hash_index`) — `LadybugConfig` should expose the useful
  ones once their semantics are established by test. `enable_default_hash_index` is worth
  measuring against the INT64-vs-STRING key finding in the spec.
- Release and publish workflows.
