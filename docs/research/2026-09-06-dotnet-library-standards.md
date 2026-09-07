# Research: what a trustworthy .NET native-wrapper database client looks like (2026)

Researched 2026-09-06 for the readiness review in
[`2026-09-06-production-readiness.md`](../2026-09-06-production-readiness.md). "Verified" means read on
the cited page that day; "inference" means reasoning not confirmed on a page.

## 0. Landscape: this is no longer the only .NET binding

| Package family | Owner | Latest | TFMs | Shape |
|---|---|---|---|---|
| `LadybugDB` + `LadybugDB.Native` (meta) + `LadybugDB.Native.{win-x64,linux-x64,linux-arm64,osx-x64,osx-arm64}` | `LadybugDB/ladybug-dotnet` (upstream org repo, created 2026-05-30 from upstream discussion #544) | 0.19.1 (2026-08-18) | `net10.0;netstandard2.0` | Managed package has no dependency on the native one; `[LibraryImport]` on net7+, `[DllImport]` on netstandard2.0; `IsAotCompatible` on net10.0; `NativeLibrary.SetDllImportResolver` from a `[ModuleInitializer]` trying `liblbug.so`/`liblbug`/`lbug_shared`; snupkg; `PackageReadmeFile`; Trusted Publishing; version = engine version + optional fourth segment for binding-only fixes; natives fetched from the upstream GitHub release per engine tag; "header diff is the ABI gate". Sync API only (`Database`, `Connection.Query/Prepare/Execute`, `QueryResult.Rows()` yielding `object?[]`), no typed mapping, no async, no cancellation, no thread cap, no benchmarks. |
| `Ladybug` + `Ladybug.Native` + `Ladybug.Extensions` | Knaackee/ladybug.net | 0.3.1 (2026-03-25, no pushes since) | `net8.0;net10.0` | `Ladybug.Native` contains no native binaries: it is an `INativeLibrary` abstraction the caller must implement. Not a turnkey binding. |
| `LadybugDb.Client` + `LadybugDb.Client.Native` | this repository | 0.1.0-alpha, unpublished | `net10.0` | See the readiness review. |

Upstream `LadybugDB/ladybug` v0.20.2 (2026-09-02) ships six dynamic-library RIDs; the official binding
packages five (no win-arm64); this client fetches all six.

Consequences: the README's "upstream ships no .NET binding" statement is now false and must be
replaced by a positioning statement. Everything the official binding already does (LibraryImport,
per-RID native packages, snupkg, Trusted Publishing, the AOT flag, an ABI gate) is the floor, not a
differentiator. Differentiators available to this client: typed values with every engine type,
`IAsyncEnumerable` rows, `Select<T>`, parameter objects, the transaction guard, the refcounted
lifetime model, cancellation via `lbug_connection_interrupt`, the Arrow chunk path, DI/health/telemetry,
a written thread-safety contract, package validation, public-API tracking, win-arm64.

Sources: upstream discussion #544; `LadybugDB/ladybug-dotnet` README, MAINTAINING.md, csproj, release
workflow, `Interop/Native.cs`; `Knaackee/ladybug.net`; nuget.org registration indexes; the v0.20.2
release asset list.

## 1. Packaging standards

**Microsoft package-authoring guidance (page updated 2025-10-31).** DO: SDK-style project, unique
`PackageId`, pre-release suffix while unstable, `Authors`, `Description`, `Copyright`,
`PackageProjectUrl`, `PackageReadmeFile`, several `PackageTags`, `PackageReleaseNotes` or a changelog
link, `PackageLicenseExpression`. CONSIDER: prefix reservation, SemVer, `PackageIcon` (128×128 PNG),
Source Link. DO NOT: `LicenseUrl`, `IconUrl`.

**Trusted Publishing, September 2026.** GitHub Actions and GitLab supported (not Azure DevOps).
`NuGet/login@v1`, `permissions: id-token: write`, the `user:` input is the nuget.org username. Temp key
valid one hour. Policy fields: owner, repository, workflow file name, optional environment. From
2026-08-17 new API keys are capped at 30 days and every key created before that date expires
2026-11-01, so Trusted Publishing is the only non-rotating path. The repository's `release.yml` already
matches this.

**Source Link, determinism, symbols.** .NET SDK 8+ includes Source Link for GitHub; no
`Microsoft.SourceLink.GitHub` reference is needed. `EmbedUntrackedSources` is on by default;
`PublishRepositoryUrl=true` is still the author's job. `ContinuousIntegrationBuild=true` only on CI.
Symbols: `IncludeSymbols` + `SymbolPackageFormat=snupkg` (Npgsql, the official binding) or embedded
PDBs (DuckDB.NET).

**Package validation.** `EnablePackageValidation=true` runs the compatible-framework and
compatible-runtime validators immediately and the baseline validator once
`PackageValidationBaselineVersion` names a shipped version. Suppressions live in
`CompatibilitySuppressions.xml`.

**Public API tracking.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` 5.6.0 (2026-07-02) with
`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`; Npgsql references it with `PrivateAssets="All"`.

**AOT and trimming.** `IsAotCompatible=true` implies `IsTrimmable`, the trim, single-file and AOT
analyzers. .NET 10 adds an assembly-level `IsAotCompatible` attribute and the opt-in
`VerifyReferenceAotCompatibility` check consumers can run over their dependency graph.

**.NET Foundation checklist** is governance (license, CLA, code of conduct, issue tracker, CI badge,
contribution guide, reproducible build script), not engineering.

## 2. Native-interop packaging

- Layout: `runtimes/{rid}/native/` for natives; the recommended managed split for native-bearing
  packages is `ref/{tfm}/` plus `runtimes/any/lib/{tfm}/`, which stops .NET Framework projects from
  being falsely marked compatible. .NET 8 host RID resolution uses the portable RID list only.
- `NativeLibrary.SetDllImportResolver`: one resolver per assembly, first in the chain; returning
  `IntPtr.Zero` falls back to default probing. Upstream ships `lbug_shared.dll` on Windows but
  `liblbug.{so,dylib}` elsewhere, so a resolver (or two import names) is mandatory. This client already
  has one.
- `[LibraryImport]` is Microsoft's stated default for .NET 7+ ("DO use LibraryImport, if possible").
  Benefits: compile-time stubs, AOT and trimming compatibility, `StringMarshalling.Utf8`, custom
  marshallers. `[DisableRuntimeMarshalling]` makes every P/Invoke blittable-only, the simplest ABI for
  a pure C API. DuckDB.NET 1.5.0 measured 17-41% improvements from LibraryImport plus
  `[SuppressGCTransition]` on trivial getters plus inlining. This client's generated interop is
  already `[LibraryImport]` (166 entry points); `DisableRuntimeMarshalling` and
  `SuppressGCTransition` are not applied.
- Peers: SQLitePCLRaw (core + provider + lib + bundle), DuckDB.NET (`Bindings` vs `Bindings.Full`,
  natives downloaded at pack time), LibGit2Sharp (`NativeBinaries` package), official LadybugDB
  (managed with no native dependency + per-RID packages + meta). This client's one-package-for-all-RIDs
  `LadybugDb.Client.Native` (roughly 130 MB of binaries) is the least flexible of these shapes.
- macOS arm64: upstream dylibs are ad-hoc signed by the linker; they load from a NuGet extraction
  (no quarantine attribute). A consumer who notarizes must re-sign. Do not sign upstream binaries
  yourself; it would break the SHA256 verification.
- Windows: `.deps.json` must be present for `runtimes/` probing; `lbug_shared.dll` needs the MSVC
  redistributable (`vcruntime140`, `msvcp140`) unless statically linked, and a missing redistributable
  surfaces as `DllNotFoundException`. Check with `dumpbin /dependents` in CI and document it.

## 3. Target framework strategy

Microsoft guidance (2026-04-13): start with `net8.0` or later; `netstandard2.0` only for .NET
Framework. .NET 8 and 9 leave support on 2026-11-10; .NET 10 is LTS to November 2028. Peers: Npgsql
10 targets net8/9/10 and its main branch is net10-only; Microsoft.Data.Sqlite 10 targets net8 +
netstandard2.0; DuckDB.NET 1.5.5 targets net8 + net10; the official binding targets net10 +
netstandard2.0. Multitargeting cost for this client: `Lock` is .NET 9+ (one `#if`),
`System.Linq.AsyncEnumerable` is in-box only on .NET 10 (package reference on net8). Assessment:
net10.0-only is defensible for a 0.x library two months before .NET 8's end of support; adding
net8.0 for a 1.0 costs one conditional and can be dropped after November 2026.

## 4. API conventions for database clients

- Sync core is honest for an embedded engine. Microsoft.Data.Sqlite documents that its async
  methods execute synchronously and tells callers to avoid them. Do not wrap sync work in
  `Task.Run` inside a library. Where async can be real: the C API has `lbug_connection_interrupt`
  and `lbug_connection_set_query_timeout`, so a `CancellationToken` can be honoured by registering
  `interrupt` while the native call runs on another thread.
- `ConfigureAwait(false)` on every library await; `ValueTask` only with evidence of frequent
  synchronous completion (true here).
- Thread-safe entry point plus non-thread-safe session objects is the ADO.NET shape.
- DI: the Aspire client-integration contract (`Add<Name>Client(builder, connectionName, configure)`,
  settings with `DisableHealthChecks/Tracing/Metrics`, `ActivitySource` and `Meter` names). Keep the
  core package free of `Microsoft.Extensions.*` except `Logging.Abstractions`; put DI, health checks
  and telemetry in an `Extensions` package.
- OpenTelemetry database semantic conventions are stable since 1.33.0: `db.system.name`,
  `db.query.text`, `db.namespace`, `db.operation.name`, `db.query.summary`, `error.type`; metric
  `db.client.operation.duration`.
- Expected at 1.0: thread-safety contract, `IDisposable` correctness, cancellation wired to
  `interrupt` on any async surface, options validation, logging hooks, an exception hierarchy
  carrying native error text. Nice: DI package, health check, telemetry, `DbConnection` adapter.

## 5. Thread-safety and lifetime documentation norms

StackExchange.Redis, Npgsql, Microsoft.Data.Sqlite and DuckDB all state per-type contracts in one
paragraph each. The Ladybug C API states only that each connection is thread-safe; database, result
and prepared-statement contracts are the binding's to state and test. This client's `docs/USAGE.md`
already does this in more detail than any peer.

SafeHandle parent/child patterns: `DangerousAddRef`/`DangerousRelease` with the child releasing the
parent only after its own native destroy (what this client does); the pitfalls are unmatched AddRef
(leak) and unmatched Release (use-after-free for correct code elsewhere). An alternative avoids
`Dangerous*` by keeping a managed parent reference and a client-side live-child counter.

## 6. Release and versioning

SemVer 2.0.0: 0.y.z may change at any time; 1.0.0 defines the public API. Tooling: MinVer 8.0.0
(tag-driven), Nerdbank.GitVersioning 3.10, or the official binding's scheme (package version = engine
version, fourth segment for binding-only releases). For a wrapper whose value is "which engine does
this wrap", an explicit engine-tracking scheme is the more informative choice. Changelog: Keep a
Changelog 1.1.0. Reproducibility: `DotNet.ReproducibleBuilds`, NuGet Package Explorer or
`dotnet-validate` in CI.

## 7. Benchmarking

BenchmarkDotNet 0.15.8 (2025-11-30) is current; `MemoryDiagnoser`, `[Params]`, one job per runtime.
DuckDB.NET and Npgsql keep benchmark projects in-tree and publish numbers in posts, not dashboards.
No published benchmark anywhere compares per-value C API reads against Arrow chunk reads for
DuckDB or Kuzu/Ladybug; the measurement in `LadybugDb.Client.Benchmarks` is new information.

## Checklist for 1.0

**Must**
1. README positions the library against the official `LadybugDB` package (differences, engine version matrix).
2. Trusted Publishing only (already in place).
3. `PackageIcon`, `PackageTags`, `PackageReleaseNotes`/changelog link; `PublishRepositoryUrl=true`.
4. `ContinuousIntegrationBuild` on CI, snupkg.
5. `EnablePackageValidation=true`; `PackageValidationBaselineVersion` from 1.0 on.
6. `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt` frozen at 1.0.
7. `IsAotCompatible=true` and a CI job that publishes a sample with `PublishAot=true`.
8. Native layout documented; Windows CRT dependency checked; macOS signing note.
9. Written thread-safety and disposal contract per type (already present).
10. SemVer, `CHANGELOG.md`, tagged releases, an engine-compatibility policy.
11. Exceptions carry native error text (already present).

**Should**
1. `[DisableRuntimeMarshalling]` and `[SuppressGCTransition]` on trivial getters.
2. `net8.0` alongside `net10.0` until November 2026.
3. `CancellationToken` honoured through `lbug_connection_interrupt`.
4. `LadybugDb.Client.Extensions`: `AddLadybugDb(...)`, `IHealthCheck`, `ActivitySource`, `Meter`.
5. Per-RID native packages plus a meta package.
6. Benchmark numbers in the README.

**Nice**
1. Arrow path behind an optional package depending on `Apache.Arrow`.
2. `DbConnection`/`DbCommand` adapter for Dapper.
3. macOS `codesign --verify` and Windows CRT checks in CI.
4. Prefix reservation on nuget.org.

## Sources

Microsoft Learn: package authoring best practices; trusted publishing; Source Link SDK 8 change;
library guidance (NuGet, Source Link, versioning, cross-platform targeting); package validation
overview; native library loading; P/Invoke source generation; disabled marshalling; interop best
practices; Native AOT; Microsoft.Data.Sqlite async. .NET blog: API key lifetime reduction; AOT-compatible
libraries; .NET 8/9 end of support. dotnet/sourcelink README; dotnet/roslyn PublicApiAnalyzers help;
dotnet/runtime discussion #101980; dotnet/foundation new-projects guidance. Npgsql `Directory.Build.props`
and 10.0 release notes; DuckDB.NET repository and the 1.5 performance post; SQLitePCLRaw v3 notes;
libgit2sharp.nativebinaries; Aspire client-integration docs; OpenTelemetry database semantic
conventions; Keep a Changelog 1.1.0; SemVer 2.0.0; MinVer and Nerdbank.GitVersioning on nuget.org;
BenchmarkDotNet releases; Apple developer forums threads 708552 and 670761; dotnet/runtime issue
63952; `LadybugDB/ladybug-dotnet` and `Knaackee/ladybug.net` repositories.
