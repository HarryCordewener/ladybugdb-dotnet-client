# LadybugDb.Client

A .NET client for [LadybugDB](https://github.com/LadybugDB/ladybug) — an MIT-licensed embedded
property-graph database with Cypher, serializable ACID transactions, and vector/full-text
indices. LadybugDB is the maintained continuation of Kuzu.

Embedded means in-process: no server, no daemon, no separate install.

> **Status: foundation milestone.** No packages are published to NuGet yet, but the client is real
> and tested against the actual engine: open a database, open connections, run Cypher, and read
> string column values back. The full design - including what later milestones add - is recorded
> in
> [`docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md`](docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md).

### What this milestone supports

- Opening and closing a database (`LadybugDatabase`), with configurable buffer pool size, thread
  count, compression, and read-only mode.
- Opening one or more connections to a database (`LadybugConnection`).
- Running a Cypher statement as a plain string and getting back a `LadybugQueryResult`.
- Reading a result's columns one string at a time (`ReadStringAsync`), row by row.
- Typed exceptions: `LadybugException` for engine errors (carrying the failing statement), and
  `LadybugWriteConflictException` for the specific, retryable case of a concurrent write conflict.
- Safe disposal ordering: disposing a database out from under a still-open connection or result
  throws `ObjectDisposedException`, not a crash.

### What this milestone does not support yet

These are deferred to Milestone 2:

- Parameterized queries (`$ref`-style placeholders bound from a plain object or dictionary).
- Reading columns as anything other than a string - no typed value marshalling yet.
- Iterating a result with `await foreach` (`IAsyncEnumerable<T>`) - use `HasNext` and
  `ReadStringAsync` in a loop instead.
- Explicit transaction control beyond issuing `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` as plain
  Cypher statements.

## Planned packages

| Package | Contents |
|---|---|
| `LadybugDb.Client` | Managed client. No native binaries. |
| `LadybugDb.Client.Native` | `liblbug` for six RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`. |

The managed package deliberately ships zero native binaries, so they cannot propagate silently
into a consumer's own package. Install `LadybugDb.Client.Native` alongside it to run the embedded
engine.

## Usage

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

## Relationship to upstream

This is an independent client, not an official LadybugDB project. Upstream ships official
bindings for Python, NodeJS, Rust, Go, Swift, Java, and C/C++, but none for .NET. This binding
targets the official C API (`src/include/c_api/lbug.h`).

A separate third-party binding also exists: [`Ladybug`](https://www.nuget.org/packages/Ladybug)
by Denis Knaack.

## License

MIT — see [LICENSE](LICENSE). LadybugDB itself is also MIT licensed, so the native binaries
redistributed in `LadybugDb.Client.Native` carry no additional restrictions. Attribution for
upstream and its vendored dependencies will ship in `THIRD-PARTY-NOTICES.md` alongside the
native package.
