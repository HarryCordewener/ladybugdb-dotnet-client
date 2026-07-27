# LadybugDb.Client — Design

**Date:** 2026-07-27
**Status:** Approved, pending implementation plan

## Context

[LadybugDB](https://github.com/LadybugDB/ladybug) is an embedded property-graph database with
Cypher support, serializable ACID transactions, and vector/full-text indices. It is MIT licensed
and is the maintained continuation of Kuzu — its README states "The database was formerly known
as Kuzu." Upstream `kuzudb/kuzu` was archived on 2025-10-10; `LadybugDB/ladybug` was created
2025-10-07 and is actively developed (v0.18.3 released 2026-07-21).

Ladybug ships an official C API (`src/include/c_api/lbug.h`, ~76 KB, 178 exported functions) and
official bindings for Python, NodeJS, Rust, Go, Swift, Java, and C/C++. **There is no official
.NET binding.** A small third-party binding exists (`Ladybug` / `Ladybug.Native` 0.3.1 by Denis
Knaack, roughly 500 total downloads); this project is a separate, independent client.

### Motivation

SharpMUSH (.NET 10, Apache-2.0) needs embedded, in-process graph storage to replace SurrealDB's
embedded engine, which is Business Source License 1.1. Every actively maintained embedded graph
engine surveyed was either source-available (SurrealDB, Memgraph — BSL; FalkorDB — SSPL),
copyleft (Neo4j — GPLv3), unmaintained (CozoDB, last push 2024-12-04), or server-only (TypeDB,
HelixDB, Apache AGE). Ladybug is the only candidate where MIT licensing, true in-process
embedding, Cypher, and ACID transactions all hold at once.

The missing piece is a .NET binding. This project is that binding. SharpMUSH is the first
consumer, not the only intended one.

## Goals

- A general-purpose, idiomatic .NET client for LadybugDB, published to NuGet under MIT.
- Full coverage of the Ladybug C API surface: database and connection lifecycle, prepared
  statements with typed parameter binding, query result iteration, and value/type marshalling
  including temporal types.
- Correct native-resource lifetime management. The C API is manual-free throughout; leaks and
  double-frees are the primary technical risk.
- Native binaries distributed so that they never leak into a consumer's own package.

## Non-goals (v1)

- Apache Arrow interop. `lbug_query_result_get_arrow_schema` and `get_next_arrow_chunk` exist,
  but `Apache.Arrow` is a heavy dependency for speculative benefit.
- Extension/registry management and CLI wrapping.
- ADO.NET `DbProviderFactory` conformance. A graph engine is a poor fit for the abstraction and
  conforming would promise semantics we do not intend to honour.
- Genuine async offloading. Signatures are reserved so it can be added without a breaking change
  (see Decision 2).

## Decisions

### 1. Scope: full general-purpose binding

Cover the whole C API rather than only the subset SharpMUSH needs.

**Rationale:** the public API surface is then driven by Ladybug's own shape rather than by one
application's needs, which avoids a later generalization pass that would be a breaking change.
The cost is a larger up-front effort.

### 2. Async model: async-shaped signatures, synchronous under the hood

Public methods are async-signature (`QueryAsync`, `ExecuteAsync`) but currently complete
synchronously.

**Rationale:** this is the documented Microsoft.Data.Sqlite approach for embedded engines — the
work is CPU and local-disk bound, so threadpool hopping adds cost without benefit. Shipping
async-shaped signatures from day one means genuine offloading can be added later **without a
breaking change**. Consumers needing offload today can `Task.Run` at their own boundary, where
they already control concurrency.

**Known risk:** Ladybug inherits Kuzu's analytics-oriented design (columnar storage, vectorized
and factorized query processing). A slow traversal will block the calling thread. For a game
server this is a real concern; see Risks.

### 3. Native distribution: managed package plus one `.Native` package

`LadybugDb.Client` ships zero binaries. `LadybugDb.Client.Native` carries
`runtimes/<rid>/native/` for all six supported platforms. `dotnet publish` trims to the target
RID.

**Rationale:** keeps the managed package small and auditable, and guarantees native binaries
cannot silently propagate into a consumer's own package. This is the failure mode being unwound
in the sibling `loradb-dotnet-client` project, where both published packages shipped native
binaries and one of them consequently carried a misdeclared licence.

Supported RIDs, matching upstream's published release assets:

| RID | Upstream asset |
|---|---|
| `linux-x64` | `liblbug-linux-x86_64.tar.gz` |
| `linux-arm64` | `liblbug-linux-aarch64.tar.gz` |
| `osx-x64` | `liblbug-osx-x86_64.tar.gz` |
| `osx-arm64` | `liblbug-osx-arm64.tar.gz` |
| `win-x64` | `liblbug-windows-x86_64.zip` |
| `win-arm64` | `liblbug-windows-arm64.zip` |

### 4. Naming

Repo `ladybugdb-dotnet-client`; packages `LadybugDb.Client` and `LadybugDb.Client.Native`.

**Rationale:** directly parallel to the author's `LoraDb.Client` family so both repos share
conventions and mental model; uses the product name rather than upstream's opaque `lbug`
short identifier; avoids collision with the existing third-party `Ladybug` package.

### 5. Interop generation: ClangSharp, generated and committed

Generate raw interop from `lbug.h` with `ClangSharpPInvokeGenerator`, commit the output, and
regenerate on each Ladybug version bump. CI fails if regeneration produces a diff.

**Rationale:** 178 functions plus structs, enums, and temporal types is too large to transcribe
by hand reliably. Committing the output keeps interop diffable and reviewable. The drift check
converts an upstream API change into a red build rather than a runtime crash.

## Architecture

### Repository layout

```
LadybugDb.Client.slnx
Directory.Build.props
global.json
cliff.toml
LadybugDb.Client/                    managed, MIT, zero binaries
LadybugDb.Client.Native/             six RIDs of liblbug
LadybugDb.Client.Tests/              unit tests, no engine required
LadybugDb.Client.IntegrationTests/   real engine required
scripts/                             fetch-liblbug.{sh,ps1}, regen-interop.{sh,ps1}
docs/
.github/workflows/                   ci.yml, release.yml, nuget-publish.yml
```

This mirrors the layout of `loradb-dotnet-client` so conventions transfer between the two.

### Layers

**1. `Native/` — generated raw interop.** ClangSharp output. `internal` throughout; never part
of the public surface. Regenerated from the pinned header.

**2. `Interop/` — SafeHandle wrappers.** One `SafeHandle` per native object type, each bound to
its corresponding `lbug_*_destroy`: `LbugDatabaseHandle`, `LbugConnectionHandle`,
`LbugQueryResultHandle`, `LbugPreparedStatementHandle`, `LbugValueHandle`.

This layer is where the project succeeds or fails. The header declares **ten** distinct
destroy/free entry points (`lbug_destroy_string`, `lbug_destroy_blob`,
`lbug_flat_tuple_destroy`, and each object type's own destructor); every one of them is a
potential leak or double-free. Concentrating lifetime management in this layer makes those
faults unrepresentable rather than merely unlikely, and gives finalizers as a backstop.

**3. Public API.** `LadybugDatabase`, `LadybugConnection`, `LadybugPreparedStatement`,
`LadybugQueryResult`, `LadybugValue`.

### Native library loading

A `[ModuleInitializer]` registers a `NativeLibrary.SetDllImportResolver` that probes
`runtimes/{rid}/native/` beside the executing assembly and under `AppContext.BaseDirectory` —
precisely where NuGet lays out the `.Native` package's RID assets. When the library is absent,
throw a `DllNotFoundException` whose message names `LadybugDb.Client.Native` as the missing
dependency.

This pattern is carried over from `loradb-dotnet-client`, where it has been verified end-to-end
against packed `.nupkg` files consumed from a local feed.

## Public API

```csharp
using var db = new LadybugDatabase("./mydb", new LadybugConfig { ReadOnly = false });
await using var conn = await db.ConnectAsync();

await using var result = await conn.QueryAsync(
    "MATCH (o:Object) WHERE o.dbref = $ref RETURN o",
    new { @ref = 42 });

await foreach (var row in result)
{
    // ...
}
```

Note the deliberate asymmetry in the sample: `LadybugDatabase` is constructed and disposed
**synchronously** (`using`), because opening a file-backed database is a local operation with no
plausible async form. Connections and query results are `IAsyncDisposable` (`await using`) so
they match the async-shaped method surface described in Decision 2.

- `LadybugQueryResult` implements `IAsyncEnumerable<LadybugRow>` over
  `lbug_query_result_has_next` / `get_next` plus `lbug_flat_tuple_get_value`.
- `PrepareAsync` returns a `LadybugPreparedStatement` exposing typed binds covering all **twenty**
  `lbug_prepared_statement_bind_*` variants, verified against the header:
  `bool`, `double`, `float`, `string`, `date`, `interval`, `value`; the signed and unsigned
  integer widths `int8` / `int16` / `int32` / `int64` and `uint8` / `uint16` / `uint32` /
  `uint64`; and `timestamp` with its `_ms` / `_ns` / `_sec` / `_tz` forms.
- Multi-statement scripts surface through `NextResultAsync()`, wrapping
  `lbug_query_result_get_next_query_result`.

### Transactions

The C API exposes **no** `lbug_transaction_*` functions; transactions are driven through Cypher,
as in Kuzu. The client exposes:

```csharp
await using var tx = await conn.BeginTransactionAsync();
// ...
await tx.CommitAsync();
```

which issues `BEGIN TRANSACTION`, `COMMIT`, and `ROLLBACK` as Cypher statements, rolling back on
dispose when not committed. This is documented explicitly as Cypher-driven rather than presented
as a native primitive, so the semantics are not misread.

## Marshalling

Values dispatch on `lbug_data_type_get_id` into a `LadybugValue` mapped to .NET as bool, long,
double, string, `DateOnly`, `TimeSpan`, `DateTime`, `DateTimeOffset`, `byte[]`, node, rel, list,
struct, and map.

Two specific hazards:

- **Strings.** Every `char*` the API returns requires `lbug_destroy_string`. This is the largest
  leak risk in the binding, so all string reads route through exactly one helper.
- **Temporal types.** Compute conversions directly from epoch units rather than routing through
  `struct tm` / `difftime`, whose layout and behaviour differ across the six target platforms.

## Error handling

`lbug_get_last_error` plus the per-object accessors (`lbug_prepared_statement_get_error_message`,
`lbug_query_result_get_error_message`). Every native call's returned state is checked and mapped
to a `LadybugException` carrying both the native message and the Cypher statement that produced
it. Handle-level safety comes from the SafeHandle layer. A missing native library produces the
actionable `DllNotFoundException` described above.

## Native acquisition

`scripts/fetch-liblbug.{sh,ps1}` downloads the six `liblbug-*` release archives for a pinned
Ladybug tag, verifies SHA256 against a committed lockfile, and extracts them into
`LadybugDb.Client.Native/runtimes/<rid>/native/`. The pinned version lives in a `liblbug.version`
file.

**Binaries are not committed to the repository** — six platforms at 5–9 MB each would bloat it
permanently. CI fetches at pack time. This deliberately differs from `loradb-dotnet-client`,
which commits its binaries because it must build them from source; Ladybug publishes prebuilt
shared libraries with every release, so there is nothing to build.

**Trade-off:** offline builds require a primed cache.

## Testing

**Unit tests** (no engine required): marshalling correctness, SafeHandle disposal semantics,
native-resolver probing logic, and error mapping.

**Integration tests** (real `liblbug`): database and connection lifecycle; every value type
round-tripped; all thirteen prepared-statement bind types; transaction commit and rollback;
multi-result scripts; concurrency behaviour. All twenty bind variants are covered, including the
integer widths, since a mis-sized marshal there corrupts data silently rather than throwing.
Plus a **leak test** that runs thousands of queries and asserts stable process memory — with ten
destroy entry points, manual-free is the standing hazard and deserves a test that fails when it
regresses.

**Interop drift check** in CI: regenerate from the pinned header and fail on any diff.

**Platform matrix:** linux-x64 and win-x64 at minimum. The remaining four RIDs are packaged and
smoke-tested only where runners permit; this limitation is stated rather than papered over.

## Risks and open questions

1. **Workload fit is the biggest unknown.** Ladybug is optimized for analytical workloads over
   large databases — columnar storage, vectorized and factorized query processing. SharpMUSH's
   pattern is the opposite: small, frequent, write-heavy mutations. Write throughput against a
   realistic mutation pattern should be measured early, before the binding's API is built out
   against assumptions that may not hold.
2. **Write concurrency.** The `Vela-Engineering/kuzu` fork advertises concurrent multi-writer
   support as a differentiator, which implies mainline Ladybug has single-writer constraints.
   The concurrency model needs to be characterized by test, not assumed.
3. **Ecosystem fragmentation.** Post-Kuzu there are multiple active forks (LadybugDB,
   Vela-Engineering). LadybugDB is clearly mainline by stars, release cadence, and binding
   ecosystem, but this is worth periodic re-evaluation.
4. **Blocking calls under load** — see Decision 2. If measurement shows slow traversals stalling
   threads in practice, the reserved async signatures allow adding a connection-affine offload
   without an API break.

## References

- LadybugDB: https://github.com/LadybugDB/ladybug (MIT, v0.18.3 on 2026-07-21)
- C API header: `src/include/c_api/lbug.h`
- Upstream release assets: `liblbug-{linux-x86_64,linux-aarch64,osx-arm64,osx-x86_64,windows-x86_64,windows-arm64}`
- Archived predecessor: https://github.com/kuzudb/kuzu (archived 2025-10-10)
- Sibling project and prior art: https://github.com/HarryCordewener/loradb-dotnet-client
