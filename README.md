# LadybugDb.Client

[![ci](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml/badge.svg)](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml)

A .NET client for [LadybugDB](https://github.com/LadybugDB/ladybug) — an MIT-licensed embedded
property-graph database with Cypher, serializable ACID transactions, and vector/full-text indices.
Embedded means in-process: no server, no daemon, no separate install.

**Status:** pre-1.0, not yet published to NuGet. The API is functional and tested against the real
engine, but may change before 1.0.

## Requirements

- .NET 10 SDK
- One of the five platforms upstream ships binaries for (below)

## Installation

`LadybugDb.Client` is not on NuGet yet; build it from source:

```console
git clone https://github.com/HarryCordewener/ladybugdb-dotnet-client.git
cd ladybugdb-dotnet-client
dotnet pack -c Release
```

That produces one package, `LadybugDb.Client`, under `LadybugDb.Client/bin/Release`. It is the
managed client only. The engine binaries come from upstream's own native packages, which you add
alongside it:

```console
dotnet add package LadybugDB.Native            # every platform, or:
dotnet add package LadybugDB.Native.linux-x64  # one platform
```

The native package's version is the engine version. This build of the client was generated against
engine **v0.19.1** (`third-party/liblbug.version`) and accepts that version or newer; opening a
database against an older engine throws a `LadybugException` that says which version to install.
`LadybugDatabase.EngineVersion` reports what was actually loaded.

`LadybugDb.Client` declares no dependency on a native package, so the platform choice and the
engine version stay yours. Without one, the first call into the engine throws
`DllNotFoundException` naming the package to add.

Reference the client from a local feed, or add a project reference to
`LadybugDb.Client/LadybugDb.Client.csproj`. See [docs/BUILDING.md](docs/BUILDING.md) for details.

## Quick start

```csharp
using LadybugDb.Client;

using var db = new LadybugDatabase("./mydb");
await using var conn = await db.ConnectAsync();

await conn.ExecuteAsync(
    "CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
await conn.ExecuteAsync(
    "CREATE (o:Object {dbref: 42, name: 'Limbo'})");

await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.name");
await foreach (var row in result)
{
    Console.WriteLine(row.GetString(0)); // Limbo
}

// Or project straight into your own shape:
await foreach (var o in conn.Select<Room>(
    "MATCH (o:Object) WHERE o.dbref = $dbref RETURN o.dbref AS Dbref, o.name AS Name",
    new { dbref = 42L }))
{
    Console.WriteLine($"{o.Dbref}: {o.Name}"); // 42: Limbo
}

record Room(long Dbref, string Name);
```

[docs/USAGE.md](docs/USAGE.md) documents every public member with worked examples.

## Current features

**Database and connections**
Open, configure, and close a database (`LadybugDatabase`, `LadybugConfig`): buffer pool size, thread
count, compression, read-only mode, maximum size, and multi-writer mode. Multiple concurrent
connections per database.

**Queries and results**
Execute Cypher directly (`QueryAsync`) or as prepared statements (`PrepareAsync`). Read results with
`await foreach` over `IAsyncEnumerable<LadybugRow>`, addressing columns by position or name. Walk
multi-statement scripts with `NextResultAsync()`.

**Typed projection**
`conn.Select<T>(cypher, parameters)` streams rows projected into a type you define — a positional
`record` needs no attributes or settable properties — or into a scalar for a single-column result:

```csharp
record Person(long Dbref, string Name);

await foreach (var p in conn.Select<Person>(
    "MATCH (o:Object) WHERE o.dbref > $min RETURN o.dbref AS Dbref, o.name AS Name",
    new { min = 40L }))
{
    Console.WriteLine($"{p.Dbref}: {p.Name}");
}

var total = await conn.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync();
```

Nothing is materialized, and the underlying result is owned and released by the projection itself —
including when you `break` out early. Columns convert to their target type with lossless widening
(an `INT32` column reads into a `long`) but never narrowing, and a mismatch is a typed error naming
the column, its engine type, and the target — reported even for a query that returns no rows.

**Type coverage**
Every value type the engine returns marshals to a typed `LadybugValue`:

| Category | Types |
|---|---|
| Scalar | `BOOL`, all signed/unsigned integer widths, `INT128`, `FLOAT`, `DOUBLE`, `STRING`, `BLOB`, `UUID`, `SERIAL` |
| Decimal | `DECIMAL` — `AsDecimal()` within .NET's range, `AsBigDecimal()` lossless to the engine's full 38 digits |
| Temporal | `DATE`, `TIMESTAMP` (and `_SEC`/`_MS`/`_NS`/`_TZ` variants), `INTERVAL` |
| Container | `LIST`, `ARRAY`, `STRUCT`, `MAP`, `UNION` |
| Graph | `NODE`, `REL`, `RECURSIVE_REL` (variable-length paths), `INTERNAL_ID` |

**Parameterized queries**
23 binding methods: 19 typed `Bind` overloads (including `Guid`, `Int128`, and `BigDecimal`), three
timestamp-precision variants, and `BindNull`. A statement executed repeatedly is planned once.

Or pass every parameter at once, as an anonymous object or any string-keyed dictionary — one call for
a statement run once, and one per execution for a prepared one:

```csharp
await conn.ExecuteAsync(
    "CREATE (o:Object {dbref: $dbref, name: $name})", new { dbref = 42L, name = "Limbo" });

await using var stmt = await conn.PrepareAsync("CREATE (o:Object {dbref: $dbref, name: $name})");
await stmt.ExecuteNonQueryAsync(new { dbref = 43L, name = "The Void" });
```

Values bind at their natural width and the engine range-checks the coercion rather than truncating; a
value whose type has no `Bind` overload is an `ArgumentException` naming the parameter and the type.

**Transactions**
`BeginTransactionAsync` returns a `LadybugTransaction` wrapping `BEGIN`/`COMMIT`/`ROLLBACK`.
Disposing without committing rolls back automatically.

**Error handling**
`LadybugException` carries the failing statement. `LadybugWriteConflictException` identifies the
retryable write-conflict case.

**Lifetime safety**
Every native child handle holds a reference on its parent for its entire lifetime. Disposing a
database while a connection, result, or transaction is still open closes it to new work immediately
— subsequent calls throw `ObjectDisposedException` — and destroys the native database only once the
last dependent releases. Disposal order does not crash the process.

**Thread safety**
`LadybugConnection` is safe for concurrent use. `Bind` calls on a single `LadybugPreparedStatement`
are serialized internally. See [docs/USAGE.md](docs/USAGE.md#concurrency) for the full contract.

## Known limitations

- **Not published.** No NuGet package; build from source.
- **Pre-1.0 API.** Public surface may change.
- **No production use.** Tested extensively against the real engine, but not yet proven under a real
  workload.
- **Async methods complete synchronously.** Signatures are async-shaped so genuine offloading can be
  added later without a breaking change, but the work is CPU- and local-disk-bound today.
- **`POINTER` is unreachable.** An engine-internal type with no Cypher-level representation. It reads
  as `LadybugType.Unsupported`.
- **`AsTimeSpan()` on `INTERVAL` is lossy.** The engine converts months at 30 days each.
- **Raw-Cypher transactions are recognized, but only in their plain form.** `BEGIN TRANSACTION`,
  `BEGIN TRANSACTION READ ONLY`, `COMMIT`, and `ROLLBACK` issued through `QueryAsync` are tracked, so
  the client refuses a nested `BEGIN` rather than letting the engine destroy the transaction already
  in flight. Recognition is deliberately conservative: a multi-statement script such as
  `"BEGIN TRANSACTION; CREATE ...; COMMIT"` is not tracked, and a transaction opened that way stays
  invisible to the guard. Uncommitted work is discarded on dispose either way; what `BeginTransactionAsync`
  adds is a deterministic close at a point you choose, rather than whenever the connection is destroyed. See [docs/USAGE.md](docs/USAGE.md#transactions).
- **Temporal conversion functions are excluded.** The 12 `*_to_tm`/`*_from_tm` C API functions have
  no portable `struct tm` ABI across the supported platforms. Epoch-based equivalents are used
  throughout.

## Future features

- Publication to NuGet under Trusted Publishing ([docs/RELEASING.md](docs/RELEASING.md))
- Apache Arrow interop (`lbug_query_result_get_arrow_schema`, `get_next_arrow_chunk`)
- Genuine async offloading behind the existing async signatures
- Extension and registry management

ADO.NET `DbProviderFactory` conformance is explicitly out of scope — a graph engine is a poor fit for
that abstraction.

## Supported platforms

The platforms are whatever upstream's `LadybugDB.Native.<rid>` packages cover:

| RID | OS | This repository's CI |
|---|---|---|
| `linux-x64` | Linux x64 | Unit and integration tests, required |
| `win-x64` | Windows x64 | Unit tests, required |
| `linux-arm64` | Linux ARM64 | Integration tests on `ubuntu-24.04-arm`, advisory until its first green run |
| `osx-arm64` | macOS ARM64 | Unit and integration tests on `macos-latest`, advisory until its first green run |
| `osx-x64` | macOS x64 | Not run (no GitHub-hosted Intel macOS runner) |

Upstream publishes a `win-arm64` engine build but no native package for it yet; on that platform,
place `lbug_shared.dll` from the upstream release next to the application (the resolver probes
`runtimes/win-arm64/native/` and the application directory).

## Documentation

| Document | Contents |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | Complete API guide — every public member, with examples |
| [docs/2026-09-06-production-readiness.md](docs/2026-09-06-production-readiness.md) | Readiness review, benchmark analysis, and the LINQ direction |
| [benchmarks/](benchmarks/README.md) | Workload and micro-benchmark harnesses and their results |
| [docs/BUILDING.md](docs/BUILDING.md) | Building and testing from source |
| [docs/RELEASING.md](docs/RELEASING.md) | Release and publication process, versioning policy |
| [CHANGELOG.md](CHANGELOG.md) | What changed in each version |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guidelines |
| [SECURITY.md](SECURITY.md) | Vulnerability reporting |

## Relationship to upstream and to other .NET bindings

An independent client, not an official LadybugDB project. It targets the official C API
(`src/include/c_api/lbug.h`). Since mid-2026 upstream also ships its own .NET binding, so there are
now three options on nuget.org:

| Package | Owner | Shape |
|---|---|---|
| [`LadybugDB`](https://www.nuget.org/packages/LadybugDB) + `LadybugDB.Native.<rid>` | upstream ([LadybugDB/ladybug-dotnet](https://github.com/LadybugDB/ladybug-dotnet)) | Synchronous `Database`/`Connection`/`QueryResult` over the C API; rows as `object?[]`; net10.0 and netstandard2.0; five RIDs; version tracks the engine. No typed values, async, cancellation, projection, or transaction guard. |
| [`Ladybug`](https://www.nuget.org/packages/Ladybug) | Denis Knaack | An abstraction surface; ships no native binaries. |
| `LadybugDb.Client` (this repository) | independent | Typed `LadybugValue` for every engine type, `IAsyncEnumerable` rows, `Select<T>`, parameter objects, a managed transaction with a nested-`BEGIN` guard, refcounted native lifetimes. Async-shaped, completes synchronously. Uses upstream's `LadybugDB.Native` packages for the engine. |

If you need the upstream-maintained binding and a synchronous API, use `LadybugDB`. This client
exists for the typed, async-shaped surface above, and it is the one SharpMUSH is built against.

## License

MIT — see [LICENSE](LICENSE). This repository redistributes no LadybugDB binaries; the engine comes
from upstream's own MIT-licensed `LadybugDB.Native` packages.
