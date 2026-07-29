# LadybugDb.Client

[![ci](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml/badge.svg)](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml)

A .NET client for [LadybugDB](https://github.com/LadybugDB/ladybug) — an MIT-licensed embedded
property-graph database with Cypher, serializable ACID transactions, and vector/full-text indices.
Embedded means in-process: no server, no daemon, no separate install.

**Status:** pre-1.0, not yet published to NuGet. The API is functional and tested against the real
engine, but may change before 1.0.

## Requirements

- .NET 10 SDK
- One of the six supported platforms below

## Installation

No package is on NuGet yet. Build from source:

```console
git clone https://github.com/HarryCordewener/ladybugdb-dotnet-client.git
cd ladybugdb-dotnet-client
bash scripts/fetch-liblbug.sh
dotnet pack -c Release
```

This produces two packages under each project's `bin/Release`. **Both are required.**

| Package | Contents |
|---|---|
| `LadybugDb.Client` | Managed client. No native binaries. |
| `LadybugDb.Client.Native` | `liblbug` for six runtime identifiers. |

They are split so native binaries cannot propagate silently into a consumer's own package.
`LadybugDb.Client` alone compiles and resolves types; the first call into the engine throws
`DllNotFoundException` naming the missing package.

Reference them from a local feed, or add a project reference to
`LadybugDb.Client/LadybugDb.Client.csproj`. See [docs/BUILDING.md](docs/BUILDING.md) for details.

## Quick start

```csharp
using LadybugDb.Client;

using var db = new LadybugDatabase("./mydb");
await using var conn = await db.ConnectAsync();

await using (var _ = await conn.QueryAsync(
    "CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
await using (var _ = await conn.QueryAsync(
    "CREATE (o:Object {dbref: 42, name: 'Limbo'})")) { }

await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.name");
await foreach (var row in result)
{
    Console.WriteLine(row.GetValue(0).AsString()); // Limbo
}
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
- **Raw-Cypher transactions bypass safety guarantees.** Issuing `BEGIN TRANSACTION` through
  `QueryAsync` instead of `BeginTransactionAsync` forfeits the disposal protections above and can
  abort the process. See [docs/USAGE.md](docs/USAGE.md#transactions).
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

| RID | OS | Verified in CI |
|---|---|---|
| `linux-x64` | Linux x64 | Yes |
| `win-x64` | Windows x64 | Yes |
| `linux-arm64` | Linux ARM64 | No |
| `osx-x64` | macOS x64 | No |
| `osx-arm64` | macOS ARM64 | No |
| `win-arm64` | Windows ARM64 | No |

Unverified platforms are packaged from upstream releases but not exercised in CI.

## Documentation

| Document | Contents |
|---|---|
| [docs/USAGE.md](docs/USAGE.md) | Complete API guide — every public member, with examples |
| [docs/BUILDING.md](docs/BUILDING.md) | Building and testing from source |
| [docs/RELEASING.md](docs/RELEASING.md) | Release and publication process |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Contribution guidelines |
| [SECURITY.md](SECURITY.md) | Vulnerability reporting |

## Relationship to upstream

An independent client, not an official LadybugDB project. It targets the official C API
(`src/include/c_api/lbug.h`). Upstream ships bindings for Python, NodeJS, Rust, Go, Swift, Java, and
C/C++, but none for .NET. A separate third-party binding also exists:
[`Ladybug`](https://www.nuget.org/packages/Ladybug) by Denis Knaack.

## License

MIT — see [LICENSE](LICENSE). LadybugDB is also MIT licensed, so the native binaries redistributed in
`LadybugDb.Client.Native` carry no additional restrictions. Attribution ships in that package's
`THIRD-PARTY-NOTICES.md`.
