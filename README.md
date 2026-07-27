# LadybugDb.Client

A .NET client for [LadybugDB](https://github.com/LadybugDB/ladybug) — an MIT-licensed embedded
property-graph database with Cypher, serializable ACID transactions, and vector/full-text
indices. LadybugDB is the maintained continuation of Kuzu.

Embedded means in-process: no server, no daemon, no separate install.

> **Status: design phase.** No packages are published yet and there is no usable code in this
> repository. The design is settled and recorded in
> [`docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md`](docs/superpowers/specs/2026-07-27-ladybugdb-dotnet-client-design.md).

## Planned packages

| Package | Contents |
|---|---|
| `LadybugDb.Client` | Managed client. No native binaries. |
| `LadybugDb.Client.Native` | `liblbug` for six RIDs: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`. |

The managed package deliberately ships zero native binaries, so they cannot propagate silently
into a consumer's own package. Install `LadybugDb.Client.Native` alongside it to run the embedded
engine.

## Intended usage

```csharp
using var db = new LadybugDatabase("./mydb");
await using var conn = await db.ConnectAsync();

await using var result = await conn.QueryAsync(
    "MATCH (o:Object) WHERE o.dbref = $ref RETURN o",
    new { @ref = 42 });

await foreach (var row in result)
{
    // ...
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
