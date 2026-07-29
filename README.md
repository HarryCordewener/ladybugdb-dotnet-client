# LadybugDb.Client

[![ci](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml/badge.svg)](https://github.com/HarryCordewener/ladybugdb-dotnet-client/actions/workflows/ci.yml)

A .NET client for [LadybugDB](https://github.com/LadybugDB/ladybug) — an MIT-licensed, embedded
property-graph database with Cypher, serializable ACID transactions, and vector/full-text
indices. LadybugDB is the maintained continuation of Kuzu. Embedded means in-process: no server,
no daemon, no separate install.

This is an independent client, not an official LadybugDB project. Upstream ships official
bindings for Python, NodeJS, Rust, Go, Swift, Java, and C/C++, but none for .NET.

> **Status: foundation milestone, unpublished.** No package is on NuGet yet. What exists is real
> and tested against the actual engine — open a database, open connections, run Cypher, read
> string columns back — but the public surface is intentionally small. See
> [Current status and limitations](#current-status-and-limitations) before you invest in this.

## Install

Two packages, both required:

| Package | Contents |
|---|---|
| `LadybugDb.Client` | The managed client. Zero native binaries. |
| `LadybugDb.Client.Native` | `liblbug` binaries for six RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`. |

```console
dotnet add package LadybugDb.Client
dotnet add package LadybugDb.Client.Native
```

They're split so the native binaries can never propagate silently into a consumer's own package —
`LadybugDb.Client` alone will build and even resolve types, but any call into the engine throws a
`DllNotFoundException` with instructions to add `.Native` until you do.

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
while (result.HasNext)
{
    var name = await result.ReadStringAsync(0);
    Console.WriteLine(name); // Limbo
}
```

This example is compiled and run against the real engine as part of writing this README — see
[docs/USAGE.md](docs/USAGE.md) for the full guide, including configuration, error handling, and
the single-writer concurrency model.

## Current status and limitations

Supported today:

- Opening and closing a database (`LadybugDatabase`), with configurable buffer pool size, thread
  count, compression, read-only mode, and max size.
- Opening one or more connections to a database (`LadybugConnection`).
- Running a Cypher statement as a plain string and getting back a `LadybugQueryResult`.
- Reading a result's columns one string at a time (`ReadStringAsync`), row by row.
- Typed exceptions: `LadybugException` for engine errors (carrying the failing statement), and
  `LadybugWriteConflictException` for the specific, retryable case of a concurrent write conflict.
- Safe disposal ordering: disposing a database out from under a still-open connection or result
  throws `ObjectDisposedException`, not a crash.

Not yet supported (see [docs/MILESTONE-2-CARRYOVER.md](docs/MILESTONE-2-CARRYOVER.md) for the
full, reviewed list):

- Parameterized queries. Every statement is a plain string you build yourself.
- Reading columns as anything other than a string — no typed value marshalling yet.
- Iterating a result with `await foreach` (`IAsyncEnumerable<T>`) — use `HasNext` and
  `ReadStringAsync` in a loop instead.
- Explicit transaction control beyond issuing `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` as plain
  Cypher statements.
- Internal write serialization. LadybugDB allows exactly one write transaction at a time and
  rejects a second rather than queuing it; this client surfaces that rejection as
  `LadybugWriteConflictException` rather than hiding it behind an internal queue. See
  [docs/USAGE.md](docs/USAGE.md#concurrency-and-the-single-writer-constraint) for the retry
  pattern this implies.

## Supported platforms

Requires **.NET 10**. `LadybugDb.Client.Native` ships prebuilt `liblbug` for:

| RID | OS | Verified in CI |
|---|---|---|
| `linux-x64` | Linux x64 | Yes |
| `win-x64` | Windows x64 | Build matrix runs it; currently red — see [docs/BUILDING.md](docs/BUILDING.md#known-ci-gap-windows) |
| `linux-arm64` | Linux ARM64 | No — packaged from upstream releases, not exercised in CI |
| `osx-x64` | macOS x64 | No — packaged from upstream releases, not exercised in CI |
| `osx-arm64` | macOS ARM64 | No — packaged from upstream releases, not exercised in CI |
| `win-arm64` | Windows ARM64 | No — packaged from upstream releases, not exercised in CI |

## Documentation

- [docs/USAGE.md](docs/USAGE.md) — the full guide: configuration, connections, running Cypher,
  reading results, error handling, disposal, concurrency, and schema guidance.
- [docs/BUILDING.md](docs/BUILDING.md) — building and testing this client from source.
- [CONTRIBUTING.md](CONTRIBUTING.md) — how to propose changes.
- [SECURITY.md](SECURITY.md) — how to report a vulnerability.
- [docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md](docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md)
  — the full design, including what later milestones add and the benchmark data behind the schema
  guidance in the usage guide.

## Relationship to upstream

This is an independent client, not an official LadybugDB project. This binding targets the
official C API (`src/include/c_api/lbug.h`). A separate third-party binding also exists:
[`Ladybug`](https://www.nuget.org/packages/Ladybug) by Denis Knaack.

## License

MIT — see [LICENSE](LICENSE). LadybugDB itself is also MIT licensed, so the native binaries
redistributed in `LadybugDb.Client.Native` carry no additional restrictions; attribution ships in
that package's `THIRD-PARTY-NOTICES.md`.
