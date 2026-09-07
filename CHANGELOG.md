# Changelog

All notable changes to `LadybugDb.Client` and `LadybugDb.Client.Extensions` (which share a
version) are recorded here. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and versions follow
[SemVer 2.0.0](https://semver.org/spec/v2.0.0.html). Package versions are independent of the
engine version; each release states the engine it was generated against. See
[docs/RELEASING.md](docs/RELEASING.md#versioning) for the policy.

Nothing has been published to nuget.org yet, so every entry below is unreleased. The first tagged
version will rename this section and start a new `Unreleased` above it.

## [Unreleased]

Generated against LadybugDB engine **v0.19.1** (`third-party/liblbug.version`); an engine at
0.19 or newer is accepted at open time.

### Added

- Parameter objects: `QueryAsync(cypher, parameters)` and `ExecuteAsync(cypher, parameters)` on
  `LadybugConnection`, and `ExecuteAsync(parameters)` / `ExecuteNonQueryAsync(parameters)` on
  `LadybugPreparedStatement`. `parameters` is an anonymous object or any string-keyed
  dictionary; each value is dispatched to the typed `Bind` overload for its runtime type, `null`
  binds a typed NULL, and a type with no overload is an `ArgumentException` naming the parameter
  and the type. Values bind at their natural width; the engine range-checks the coercion.
- `ExecuteAsync(cypher)` on `LadybugConnection` and `ExecuteNonQueryAsync()` on
  `LadybugPreparedStatement`, for statements whose rows are not needed. They return nothing
  because the engine reports no affected-row count.
- `Select<T>(cypher, parameters)`: a streaming `IAsyncEnumerable<T>` that projects each row into
  a constructor whose parameter names match the returned columns (positional records need no
  attributes), or into a scalar for a single-column result. It owns and releases the underlying
  result on every exit path, including `break`. Plans are cached per (`T`, ordered column names).
- Lossless widening on read: an `INT32` column reads into a `long` target, `FLOAT` into `double`,
  unsigned into a wider signed target; nothing that can lose a bit, a digit or a sign is accepted.
- `LadybugDatabase.EngineVersion` (what was loaded) and `LadybugDatabase.MinimumEngineVersion`
  (what the interop was generated from). The constructor refuses an engine older than the pinned
  major.minor with a `LadybugException` naming the version to install, instead of an
  `EntryPointNotFoundException` from whichever call happened to be missing.
- `LadybugDb.Client.Benchmarks`: BenchmarkDotNet micro-benchmarks, a `--workload` mode mirroring
  `benchmarks/workload_bench.py`, and two read-path prototypes; results under `benchmarks/`.
- Package metadata for nuget.org: icon, tags, a release-notes link to this file, Source Link with
  the build commit, symbols as a `.snupkg`, deterministic builds on CI, and package validation at
  pack time.
- `IsAotCompatible=true`; `samples/LadybugDb.Client.AotSample` is published with
  `PublishAot=true` and executed by CI.
- `LadybugDb.Client.Extensions`, a second package for `Microsoft.Extensions.DependencyInjection`
  hosts: `AddLadybugDb(path, configure)` and `AddLadybugDb(IConfiguration section)` register a
  singleton `LadybugDatabase` (opened on first resolve), a scoped `LadybugConnection` and
  `IOptions<LadybugDbOptions>`, validating `DatabasePath` at registration; `LadybugDbHealthCheck`
  (`RETURN 1` on a fresh connection, 5-second timeout) is registered as `ladybugdb` unless
  `DisableHealthChecks`. Ships at the core's version and depends on it exactly.
- Documentation: the production-readiness review (`docs/2026-09-06-production-readiness.md`),
  three research appendices under `docs/research/`, the API-ergonomics and LINQ design specs.

### Changed

- The engine binaries come from upstream's `LadybugDB.Native` packages, chosen by the consumer.
  `LadybugDb.Client.Native`, `scripts/fetch-liblbug.sh` and the SHA256 lockfile are retired;
  `LadybugDb.Client` declares no native dependency and ships no `runtimes/` folder.
- The header pin moved to `third-party/liblbug.version` (v0.19.1) and the interop was
  regenerated; one new entry point, `lbug_connection_get_pushed_sql`.
- `AnalysisMode=Recommended` on the shipping library (zero findings), not on the test projects.
- README positions the client against the official `LadybugDB` binding and Knaackee's `Ladybug`.
- `docs/USAGE.md` documents parameter objects, `Select<T>`, the widening rule, and replaces the
  "zero conflicts under `EnableMultiWrites`" claim with the measured table.

### Fixed

- A nested `BEGIN TRANSACTION` is refused. The engine tears down the transaction already in
  flight on a nested `BEGIN`, so the writes inside it vanished with no error; the guard now also
  tracks a transaction opened by handing `BEGIN TRANSACTION` to `QueryAsync`.
- `LadybugTransaction` follows the engine when a raw `COMMIT` or `ROLLBACK` closes the
  transaction it opened, instead of later committing nothing or rolling back a transaction that
  no longer exists.
- A contended transaction gate deadlocked when the caller blocked on a single-threaded
  `SynchronizationContext` (WPF, WinForms, legacy ASP.NET). `ConfigureAwait(false)` on every
  genuine suspension point.
- `PrepareAsync` of a write statement classifies the single-writer refusal as
  `LadybugWriteConflictException` instead of throwing it raw, and the multi-writes wording
  "Write-write conflict of updating the same row" is classified as a write conflict too.
- `Select<T>` matches an unaliased column such as `o.dbref` to a constructor parameter by the
  text after its last dot; exact aliases still win.
- `TransactionStatement.Classify` no longer allocates for ordinary statements.
- The native resolver's `Assembly.Location` read is annotated for single-file and AOT apps, where
  it is empty and only `AppContext.BaseDirectory` is probed.

[Unreleased]: https://github.com/HarryCordewener/ladybugdb-dotnet-client/compare/27a3e1c...HEAD
