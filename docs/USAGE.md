# Usage guide

This is the full guide to `LadybugDb.Client`. It assumes you've read the
[README](../README.md)'s quick start. Every code sample below was compiled and run against the
real engine while this guide was written.

- [Opening and configuring a database](#opening-and-configuring-a-database)
- [Connections](#connections)
- [Executing Cypher](#executing-cypher)
- [Reading results](#reading-results)
- [Error handling](#error-handling)
- [Disposal and lifetime](#disposal-and-lifetime)
- [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint)
- [Schema guidance](#schema-guidance)
- [What's deferred](#whats-deferred)

## Opening and configuring a database

```csharp
using LadybugDb.Client;

using var db = new LadybugDatabase("./mydb");
```

`LadybugDatabase` opens (creating if necessary) the database directory at the given path.
Opening is a synchronous, local file operation — the constructor itself does the work, so the
type implements `IDisposable`, not `IAsyncDisposable`. A failed open throws `LadybugException`.

Pass a `LadybugConfig` to change engine defaults:

```csharp
var config = new LadybugConfig
{
    BufferPoolSize = 512UL * 1024 * 1024, // 512 MiB; 0 selects the engine default
    MaxThreads = 4,                       // 0 selects the engine default
    EnableCompression = true,
    ReadOnly = false,
    MaxDbSize = 0,                        // 0 selects the engine default
};

using var db = new LadybugDatabase("./mydb", config);
```

`LadybugConfig` is a record with these properties:

| Property | Type | Default | Meaning |
|---|---|---|---|
| `BufferPoolSize` | `ulong` | `0` (engine default) | Max buffer pool size in bytes. Raise this if you're working a dataset large enough that the engine default causes excess disk I/O; the benchmark data in [Schema guidance](#schema-guidance) shows buffer pool size is *not* the fix for slow point lookups caused by a STRING primary key, so don't reach for it first. |
| `MaxThreads` | `ulong` | `0` (engine default) | Max threads used during query execution. Lower this to cap the CPU a single embedded instance can claim, e.g. in a host process running many other things on the same core budget. |
| `EnableCompression` | `bool` | `true` | Compress supported types on disk. Leave this on unless you have a specific reason to trade disk space for CPU. |
| `ReadOnly` | `bool` | `false` | Opens the database read-only. No write transaction is permitted; use this for a process that only ever queries a database another process (or an earlier run) writes to. |
| `MaxDbSize` | `ulong` | `0` (engine default) | Max database size in bytes. |

## Connections

```csharp
await using var conn = await db.ConnectAsync();
```

A `LadybugConnection` is how you actually run Cypher. Multiple connections may share one
database — this is how you get read/write concurrency, and also how you can trigger the
single-writer conflict described below. `ConnectAsync` (like every method on `LadybugConnection`
and `LadybugQueryResult`) is async-shaped but currently completes synchronously: the engine is
embedded and the work is CPU- and local-disk-bound, so thread-pool offloading would add cost
without benefit. This is the same approach `Microsoft.Data.Sqlite` takes for embedded engines.
Signatures are async so real offloading can be added later without a breaking change; if you need
to get work off your caller's thread today, wrap the call in `Task.Run` at your own boundary,
where you already control concurrency.

Because the underlying call is synchronous, a `CancellationToken` passed to `ConnectAsync` or
`QueryAsync` can only cancel *before* the call starts (via `ThrowIfCancellationRequested`) — it
cannot interrupt a query already in flight, since there is no `await` point inside the native
call for cancellation to preempt.

## Executing Cypher

```csharp
await using (var _ = await conn.QueryAsync(
    "CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }
await using (var _ = await conn.QueryAsync(
    "CREATE (o:Object {dbref: 42, name: 'Limbo'})")) { }
```

`QueryAsync` takes a plain Cypher string and returns a `LadybugQueryResult`. There is no
parameterized-query API yet (see [What's deferred](#whats-deferred)) — build the statement
yourself, and be deliberate about what you interpolate into it. Every `QueryAsync` call returns a
result, even for statements like `CREATE TABLE` that don't produce rows; disposing it discards
that result and frees the underlying native resources. The `await using (var _ = ...) { }` shape
above is the idiom for "run this statement, I don't need to read anything back."

A statement that fails throws `LadybugException` or `LadybugWriteConflictException` — see
[Error handling](#error-handling).

## Reading results

```csharp
await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.name");
while (result.HasNext)
{
    var name = await result.ReadStringAsync(0);
    Console.WriteLine(name);
}
```

`HasNext` reports whether another row is available. `ReadStringAsync(columnIndex)` advances to
that row *and* reads the given column as a string in one call — there's no separate "advance"
step. This is a deliberate, temporary shape: it exists to prove the tuple/value ownership chain
end to end for the foundation milestone, and only string columns are readable today. It returns
`null` once there are no more rows. Milestone 2 replaces this with `IAsyncEnumerable<LadybugRow>`
and typed column access (see [What's deferred](#whats-deferred)); don't build on the "advance and
read are the same call" behavior lasting.

## Error handling

Two exception types, both under `LadybugDb.Client`:

- **`LadybugException`** — a general engine error: bad Cypher, a missing table, a type mismatch,
  a failed database open. Carries the failing statement (`ex.Statement`, when known) and folds it
  into `ex.Message`. Not retryable by itself — the statement or schema is wrong and retrying
  unchanged will fail the same way.
- **`LadybugWriteConflictException`** — a `LadybugException` subtype thrown when the engine
  refuses a write because another write transaction is already active. This *is* retryable: see
  [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint).

```csharp
try
{
    await conn.QueryAsync("MATCH (o:NoSuchTable) RETURN o.nope");
}
catch (LadybugWriteConflictException ex)
{
    // Retryable: another connection holds the single write transaction.
}
catch (LadybugException ex)
{
    // Not retryable by itself: a genuine query/engine error.
    Console.WriteLine($"query failed on: {ex.Statement} - {ex.Message}");
}
```

Catch `LadybugWriteConflictException` before the base `LadybugException` — it's the more specific
type, and the two mean different things for whether a caller should retry.

Beyond these two, `ArgumentException` (or a subtype) can come from local argument validation —
for example, `QueryAsync` with a null or whitespace-only Cypher string, or the `LadybugDatabase`
constructor with a null or whitespace path — before any call reaches the engine. And a database,
connection, or result used after its own disposal, or after an ancestor's disposal, throws
`ObjectDisposedException` (see [Disposal and lifetime](#disposal-and-lifetime)).

## Disposal and lifetime

`LadybugDatabase` is `IDisposable`; `LadybugConnection` and `LadybugQueryResult` are
`IAsyncDisposable`. Disposal is safe in any order — it never corrupts state or crashes the
process — but children should normally be disposed before the database they came from:

```csharp
var db = new LadybugDatabase(path);
var conn = await db.ConnectAsync();
// ... use conn ...
var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.name");

// Correct order: children first, then the database.
await result.DisposeAsync();
await conn.DisposeAsync();
db.Dispose();
```

Disposing a database out from under a still-open connection or result doesn't crash — it throws a
managed `ObjectDisposedException` on the next call against that connection or result, instead:

```csharp
var db = new LadybugDatabase(path);
var conn = await db.ConnectAsync();

db.Dispose(); // disposed out from under the still-open connection

try
{
    await conn.QueryAsync("MATCH (n) RETURN n");
}
catch (ObjectDisposedException)
{
    // Expected: the parent database is gone.
}

await conn.DisposeAsync(); // still safe, even now
```

One subtlety: post-dispose behavior is memory-safe but not strictly *deterministic* in timing.
Each handle only actually closes once every outstanding lease on it has drained — a call already
in flight on another thread when `Dispose()` runs may still complete normally instead of throwing,
and a burst of concurrent calls can keep succeeding for a short time afterward while those leases
finish draining. A call that starts after the handle has fully closed always throws
`ObjectDisposedException`. It never falls through to unmanaged memory either way.

## Concurrency and the single-writer constraint

LadybugDB permits exactly one write transaction at a time and **rejects** a second rather than
queuing it. Under contention this is expected, not exceptional, and it's surfaced as the typed,
retryable `LadybugWriteConflictException` rather than a raw engine error string.

This was benchmarked, not assumed: throughput is flat from 1 to 8 concurrent writers
(~450 mutations/sec) while conflict retries climb past 10,000 over the same run. The client does
not serialize writes internally today — if you open multiple connections and write from more than
one at a time, you *will* see this exception, by design. A retry loop at the call site is the
expected pattern:

```csharp
async Task<LadybugQueryResult> ExecuteWithRetryAsync(
    LadybugConnection conn, string cypher, int maxAttempts = 5)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            return await conn.QueryAsync(cypher);
        }
        catch (LadybugWriteConflictException) when (attempt < maxAttempts)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt));
        }
    }
}
```

If you need a single logical write to span more than one statement, wrap it with `BEGIN
TRANSACTION` / `COMMIT` (or `ROLLBACK`) as plain Cypher — there's no dedicated transaction API yet
(see [What's deferred](#whats-deferred)), but the engine's own transaction statements work today
and are what holds the write lock for the duration.

## Schema guidance

This is measured against the real engine (see the design spec's Risks section for the full
benchmark), not general graph-database folklore. It applies regardless of which milestone of this
client you're using, because it's about how LadybugDB itself performs, not about this binding.

**Use `INT64` primary keys, not `STRING`.** At an identical row count, a `STRING` key costs
**4.8×** what an `INT64` key costs. Switching a 1,000,000-row table from a composite string key
(e.g. `"42/DESC"`) to an `INT64` key moved point-lookup p50 latency from 1.785 ms to
**0.087 ms** — a 20× improvement that took p99 from over budget to roughly **8.7× under** the
2 ms budget it was measured against.

```csharp
// Good: INT64 primary key.
await conn.QueryAsync("CREATE NODE TABLE Attr(dbref INT64, name STRING, PRIMARY KEY(dbref))");

// Avoid: composite STRING primary key costs ~4.8x an INT64 key at equal row count.
await conn.QueryAsync("CREATE NODE TABLE AttrByString(key STRING, name STRING, PRIMARY KEY(key))");
```

**Don't pack many values into one wide column.** A `MAP(STRING,STRING)` holding ten attributes
cost 3.619 ms to read, against 0.123 ms for the key alone — because a single-row read
materializes the *whole* column value, not just the part you asked for. LadybugDB's columnar
storage scans many rows of few columns well, and fetches few rows of many columns badly. Prefer
one row per attribute (or a narrow, fixed set of columns) over one row holding a bag of values.

**Bulk load with `COPY`, not per-row `CREATE`.** 1,000,000 rows in 0.5 s versus 296 s — roughly
600× — for the same data inserted one `CREATE` at a time:

```csharp
await conn.QueryAsync($"COPY Object FROM '{csvPath}'");
```

Row-count scaling itself is healthy once the schema is right: 10× the rows costs only 1.8–2.6×,
not 10×.

## What's deferred

These are explicitly out of scope for this milestone — reviewed and triaged, not overlooked. See
[docs/MILESTONE-2-CARRYOVER.md](MILESTONE-2-CARRYOVER.md) for the complete, reviewed list with
rationale for each item. In summary:

- **Parameterized queries.** No `$param`-style placeholders bound from an object or dictionary —
  every statement is a plain string.
- **Typed value reading.** `ReadStringAsync` is the only column reader; no int/bool/date/etc.
  marshalling yet, and it advances a row *and* reads a column in one call rather than separating
  those concerns.
- **`await foreach` iteration.** No `IAsyncEnumerable<T>` over a result; use `HasNext` and
  `ReadStringAsync` in a loop.
- **A dedicated transaction API.** `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` work as plain
  Cypher statements today; there's no typed `Transaction` object wrapping them.
- **Internal write serialization.** The client does not queue concurrent writers for you; it
  surfaces the engine's own rejection as `LadybugWriteConflictException` (see
  [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint)).
