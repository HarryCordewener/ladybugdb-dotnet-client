# Package hygiene and the Extensions package — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `LadybugDb.Client` look and behave like a package outside users trust (metadata, symbols, deterministic CI builds, package validation, AOT flag, changelog, versioning policy, CI on more platforms, a scheduled upstream-release check), and add `LadybugDb.Client.Extensions` with DI registration, options, and a health check.

**Architecture:** All hygiene lives in `Directory.Build.props`, the shipping csproj, `.github/workflows`, and repo-root docs. The Extensions package is a new project that depends only on `LadybugDb.Client` and `Microsoft.Extensions.*` abstractions; the core stays dependency-free.

**Tech Stack:** NuGet SDK properties, `Microsoft.DotNet.ApiCompat` (package validation), GitHub Actions, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Diagnostics.HealthChecks.Abstractions`.

**Spec:** `docs/research/2026-09-06-dotnet-library-standards.md` ("Checklist for 1.0") and the readiness report's checklist table.

## Global Constraints

- The core `LadybugDb.Client` gains no package dependencies (it has exactly one: `ExtendedNumerics.BigDecimal`).
- `TreatWarningsAsErrors` stays on; every new public member has XML docs.
- Do not enable `Microsoft.CodeAnalysis.PublicApiAnalyzers` in this plan (it is enabled once, at the end, after the LINQ surface lands, by whoever integrates the branches).
- CI must stay green on ubuntu-latest and windows-latest; new legs may be `continue-on-error: false` only once they pass.
- Commit after every task.

---

### Task H1: Package metadata, symbols, deterministic CI builds

**Files:** modify `Directory.Build.props`, `LadybugDb.Client/LadybugDb.Client.csproj`; create `LadybugDb.Client/icon.png` (128×128, a plain ladybug glyph rendered with Python/Pillow or an SVG converted with `rsvg-convert`; no third-party artwork); test `LadybugDb.Client.Tests/PackagingTests.cs`.

- [x] Test: open the nupkg and assert the nuspec has `<icon>icon.png</icon>`, `<tags>` containing `ladybugdb`, `graph`, `cypher`, `embedded`, `<repository ... commit="...">` with a 40-hex commit, and that a `.snupkg` exists next to the `.nupkg` after `dotnet pack -c Release`.
- [x] Implement: `PackageIcon=icon.png` (+ `<None Include="icon.png" Pack="true" PackagePath="" />`), `PackageTags`, `PublishRepositoryUrl=true`, `EmbedUntrackedSources=true`, `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`, `ContinuousIntegrationBuild` conditioned on `'$(GITHUB_ACTIONS)' == 'true'`, `PackageReleaseNotes` pointing at `CHANGELOG.md`.
- [x] Commit `build: package icon, tags, symbols, deterministic CI builds`.

### Task H2: Package validation and the AOT flag

- [x] `EnablePackageValidation=true` on the shipping csproj (no baseline yet: `PackageValidationBaselineVersion` is added at the first published version, note this in `docs/RELEASING.md`).
- [x] `IsAotCompatible=true`; run `dotnet build -c Release` and fix every IL2xxx/IL3xxx the analyzers raise (the reflective paths are already annotated `[RequiresUnreferencedCode]`; the `AssemblyMetadataAttribute` read in `EngineVersion` is fine; `ParameterBinder`/`RowMapper` may need `[DynamicallyAccessedMembers]` on `T`).
- [x] New project `samples/LadybugDb.Client.AotSample` (console: open a database, create a table, insert, query with typed accessors) and a CI job `aot-publish` on ubuntu-latest running `dotnet publish -c Release -r linux-x64 -p:PublishAot=true` and executing the binary. This needs `clang` on the runner (present on ubuntu-latest).
- [x] Commit `build: package validation and Native AOT compatibility`.

### Task H3: Changelog, versioning policy, release notes automation

- [x] `CHANGELOG.md` (Keep a Changelog 1.1.0): `Unreleased` with every change since `27a3e1c` grouped Added/Changed/Fixed, using `git log --oneline 27a3e1c..HEAD` as the source.
- [x] `docs/RELEASING.md` "Versioning" section: package versions are SemVer and independent of the engine; `LadybugDatabase.MinimumEngineVersion` and the README state which engine the release was generated against; a release bumps `third-party/liblbug.version` only when upstream has a `LadybugDB.Native` package for the new engine.
- [x] `.github/release.yml` with categories (Added / Changed / Fixed / Documentation / Dependencies) keyed on labels, and a `CONTRIBUTING.md` line asking for those labels.
- [x] Commit `docs: changelog, versioning policy, release-notes categories`.

### Task H4: CI on more platforms and a scheduled upstream check

- [x] `ci.yml`: add `macos-latest` (arm64) to the build matrix and an `integration-macos` job; add a `linux-arm64` integration leg using `ubuntu-24.04-arm` (GitHub-hosted arm runner). Pin `.slnx` explicitly on every `dotnet` command.
- [x] `.github/workflows/upstream-check.yml`: weekly cron; queries `https://api.nuget.org/v3-flatcontainer/ladybugdb.native/index.json`, compares the newest stable version to `third-party/liblbug.version`, and opens (or updates) an issue titled `Upstream LadybugDB.Native <version> is available` with the header diff link `https://github.com/LadybugDB/ladybug/compare/v<pinned>...v<new>`.
- [x] Commit `ci: macOS and arm64 legs; weekly upstream native-package check`.

### Task E1: `LadybugDb.Client.Extensions` — options and DI

**Files:** create `LadybugDb.Client.Extensions/LadybugDb.Client.Extensions.csproj` (net10.0, IsPackable, same metadata as the core via `Directory.Build.props`), `LadybugDbOptions.cs`, `ServiceCollectionExtensions.cs`; test project `LadybugDb.Client.Extensions.Tests`.

**Produces:**
```csharp
public sealed class LadybugDbOptions { public string DatabasePath { get; set; } = ""; public LadybugConfig Config { get; set; } = new(); public bool DisableHealthChecks { get; set; } }
public static class LadybugDbServiceCollectionExtensions
{
    public static IServiceCollection AddLadybugDb(this IServiceCollection services, string databasePath, Action<LadybugDbOptions>? configure = null);
    public static IServiceCollection AddLadybugDb(this IServiceCollection services, IConfiguration section);   // binds LadybugDbOptions
}
```
Registers `LadybugDatabase` as a singleton (opened on first resolve, disposed with the container), `LadybugConnection` as scoped (one per scope, disposed with the scope), and `IOptions<LadybugDbOptions>`. Validation: empty `DatabasePath` fails at `AddLadybugDb` with `OptionsValidationException`.

- [x] Tests: resolve `LadybugDatabase` twice → same instance; two scopes → different connections; disposing the provider disposes the database (subsequent `ConnectAsync` throws `ObjectDisposedException`); configuration binding from an in-memory `IConfiguration` with `LadybugDb:DatabasePath` and `LadybugDb:Config:MaxThreads`.
- [x] Implement; commit `feat(extensions): AddLadybugDb with options binding`.

### Task E2: Health check

- [x] `LadybugDbHealthCheck : IHealthCheck` running `RETURN 1` on a fresh connection with a 5-second timeout; `AddLadybugDb` registers it under the name `ladybugdb` unless `DisableHealthChecks`. Tests: healthy against an open database; unhealthy (with the exception in `Data`) after the database is disposed.
- [x] Commit `feat(extensions): health check`.

### Task E3: Docs and release wiring

- [x] README: an "ASP.NET Core / DI" subsection; `docs/USAGE.md` "Extensions" chapter with executed samples; `release.yml` packs and pushes both packages (`LadybugDb.Client`, `LadybugDb.Client.Extensions`) with the same version; `PackagingTests` asserts the Extensions nupkg depends on `LadybugDb.Client` with an exact-version range `[<version>]`.
- [x] Commit `docs: extensions package`.
