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
| `EnableMultiWrites` | `bool` | `false` | Maps to the engine's `enable_multi_writes` setting. Measured to genuinely lift LadybugDB's one-write-transaction-at-a-time restriction — see [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint) for the numbers. |

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

`QueryAsync` takes a plain Cypher string and returns a `LadybugQueryResult`. For a statement you
run repeatedly with different values, prefer `conn.PrepareAsync(cypher)` and the resulting
`LadybugPreparedStatement`'s typed `Bind` overloads over interpolating values into the string
yourself — it avoids re-planning the same query on every call, and avoids interpolation being the
only thing standing between you and a Cypher injection bug. Every `QueryAsync` call returns a
result, even for statements like `CREATE TABLE` that don't produce rows; disposing it discards
that result and frees the underlying native resources. The `await using (var _ = ...) { }` shape
above is the idiom for "run this statement, I don't need to read anything back."

A statement that fails throws `LadybugException` or `LadybugWriteConflictException` — see
[Error handling](#error-handling).

## Reading results

```csharp
await using var result = await conn.QueryAsync("MATCH (o:Object) RETURN o.name");
await foreach (var row in result)
{
    var name = row.GetValue(0).AsString();
    Console.WriteLine(name);
}
```

`LadybugQueryResult` implements `IAsyncEnumerable<LadybugRow>`, so `await foreach` is the normal
way to read a result — each iteration advances one row. A `LadybugRow` is fully marshalled into
managed memory the moment it's yielded; nothing in it holds a native pointer, so it's safe to keep
around after the enumerator moves past it.

**A result is single-pass.** There is one native cursor behind a result, not one per enumerator,
so calling `GetAsyncEnumerator()` (what `await foreach` does under the hood) a second time throws
`InvalidOperationException` instead of silently handing back an enumerator that shares — and
therefore appears to have already consumed — the same underlying rows as the first. This matters
even if you never call `GetAsyncEnumerator()` yourself: .NET 10's in-box `System.Linq.AsyncEnumerable`
LINQ operators call it too, so `await result.CountAsync()` followed by `await result.ToListAsync()`
on the same result would otherwise silently return an empty list from the second call. If you need
the rows more than once, materialize them yourself once — `var rows = await result.ToListAsync();`
— and reuse the list.

Columns are addressable three ways:

```csharp
row.GetValue(0)                 // by position
row.GetColumnName(0)            // the column's name (an alias, if the Cypher used AS)
row["label"]                    // by name
```

Every LadybugDB type marshals to a typed `LadybugValue` — `AsInt64()`, `AsString()`, `AsBoolean()`,
`AsDateTime()`, `AsList()`, `AsNode()`, and so on for every scalar, temporal, container, and graph
type the engine has. Call the accessor matching `row.GetValue(i).Type`; calling the wrong one
throws `InvalidOperationException`.

`await foreach` honours a `CancellationToken` between rows via `result.WithCancellation(token)` —
it can't interrupt a single row already being read (there's no `await` point inside the native
call for that), but it's checked before every row starts.

A `HasNext` property is also available if you need to peek without an `await foreach` loop. For a
script that runs more than one Cypher statement in a single `QueryAsync` call, walk the chained
results with `NextResultAsync()`:

```csharp
var current = await conn.QueryAsync("MATCH (o:Object) RETURN o.name; MATCH (o:Object) RETURN count(*);");
while (current is not null)
{
    await foreach (var row in current) { /* ... */ }
    current = await current.NextResultAsync();
}
```

**`NextResultAsync` is also single-pass, and shares one cursor across the whole chain** — measured
against the real engine, not assumed from the (silent-on-this-point) C header. There is exactly
one native cursor behind a multi-statement script, not one per result: only ever call
`NextResultAsync()` on the most recently returned result, as the loop above does
(`current = await current.NextResultAsync();`). Calling it again on an *earlier* result after a
*later* one already advanced further throws `InvalidOperationException` — without that guard, it
would silently hand back a stale duplicate of a result already in hand (and can leave a later
statement in the script never read through any result at all), rather than failing loudly.

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
    // Retryable: another connection holds the single write transaction (with the default
    // LadybugConfig.EnableMultiWrites = false).
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

`LadybugDatabase` is `IDisposable`; `LadybugConnection`, `LadybugQueryResult`, and
`LadybugTransaction` are `IAsyncDisposable`. Disposal is safe in any order — it never corrupts
state or crashes the process — but children should normally be disposed before the database they
came from:

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

**This includes an open transaction.** If a connection still has a `LadybugTransaction` open on
it (`BeginTransactionAsync` called, neither `CommitAsync` nor `RollbackAsync` run yet) when its
database is disposed, the database rolls that transaction back itself, before releasing its own
handle, then the transaction and connection can be disposed afterward — in any order — with no
further effect:

```csharp
var db = new LadybugDatabase(path);
var conn = await db.ConnectAsync();
var tx = await conn.BeginTransactionAsync();
await conn.QueryAsync("CREATE (n:Object {dbref: 1})"); // never committed

db.Dispose(); // rolls the open transaction back first, then releases its own handle

await tx.DisposeAsync();   // already rolled back; a no-op
await conn.DisposeAsync(); // still safe
```

This is not optional cleanup — it is why disposal is safe in any order at all. LadybugDB's own
`lbug_connection_destroy` auto-rolls-back any transaction still open on the connection it is
destroying, and that auto-rollback needs the database to be alive; without the database-side
rollback above running first, destroying a connection with an open transaction *after* its
database was already disposed would ask the engine to roll back against a database that no longer
exists.

One subtlety: post-dispose behavior is memory-safe but not strictly *deterministic* in timing.
Each handle only actually closes once every outstanding lease on it has drained — a call already
in flight on another thread when `Dispose()` runs may still complete normally instead of throwing,
and a burst of concurrent calls can keep succeeding for a short time afterward while those leases
finish draining. A call that starts after the handle has fully closed always throws
`ObjectDisposedException`. It never falls through to unmanaged memory either way.

**This also holds if you never dispose anything at all.** Neither `LadybugDatabase` nor
`LadybugConnection` has a finalizer of its own — only their underlying native handles do — so
abandoning a database, connection, and open transaction without a `using`/`await using` anywhere
relies entirely on the GC's finalizer path, whose order between two independently-finalizable
objects the CLR does not guarantee. A connection is engineered to hold its owning database alive
(via a reference-counted lease, for as long as it has an open transaction) specifically so that,
whichever order the finalizers actually run in, the database is never gone by the time the
connection needs it to safely close out that transaction. Still write `using`/`await using` —
relying on finalization means your data changes reach disk on the GC's schedule, not yours — but
forgetting to is not a crash risk.

## Concurrency and the single-writer constraint

By default, LadybugDB permits exactly one write transaction at a time and **rejects** a second
rather than queuing it. Under contention this is expected, not exceptional, and it's surfaced as
the typed, retryable `LadybugWriteConflictException` rather than a raw engine error string.

This was benchmarked, not assumed: with the default configuration, throughput was flat from 1 to
8 concurrent writers (~450 mutations/sec) while conflict retries climbed past 10,000 over the same
run. The client does not serialize writes internally — if you open multiple connections and write
from more than one at a time with the default configuration, you *will* see this exception, by
design. A retry loop at the call site is the expected pattern:

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

### `EnableMultiWrites`: measured, not assumed

`LadybugConfig.EnableMultiWrites` maps to the engine's `enable_multi_writes` setting, and it was
an open question — since the very first benchmark in this project — whether it actually changes
anything. It does. Set on a database, the same 1/2/4/8-concurrent-writer workload above produced
**zero** `LadybugWriteConflictException`s at any writer count, across four separate 3-second runs,
and throughput rose with concurrency instead of staying flat (roughly 2,600-2,900 mutations/sec at
one writer, up to 3,500-3,900/sec at four to eight). With it off (the default), conflicts climbed
with writer count in every run of the same experiment (0 → ~2,700 → ~8,000 → ~18,000 over the same
3-second window) while throughput stayed essentially flat.

These specific mutations/sec figures are this machine's, not a portable number - an independent
spot-check on different hardware/load saw 602-1,248 mut/s instead of ~2,600-3,900, with the same
conflict counts scaling into the thousands with the flag off. What travels is the *shape* of the
result (conflicts present and climbing with the flag off, zero with it on, throughput flat vs.
scaling), not the absolute rate - re-measure on your own hardware if the exact numbers matter to
you.

```csharp
var config = new LadybugConfig { EnableMultiWrites = true };
using var db = new LadybugDatabase("./mydb", config);
```

Because the flag genuinely lifts the restriction at the engine level, this client does not add a
client-side write lock on top of it — concurrency is the engine's business once you've told it
`EnableMultiWrites = true`. If you leave it off (the default), the retry-loop pattern above is
still the expected approach; the client makes no attempt to serialize writers for you either way.

If you need a single logical write to span more than one statement, use
`conn.BeginTransactionAsync()`:

```csharp
await using (var tx = await conn.BeginTransactionAsync())
{
    await conn.QueryAsync("CREATE (n:Object {dbref: 1, name: 'Limbo'})");
    await conn.QueryAsync("CREATE (n:Object {dbref: 2, name: 'The Void'})");
    await tx.CommitAsync();
}
```

`LadybugTransaction` is a thin wrapper, not a native primitive — **the C API has no transaction
functions at all.** `BeginTransactionAsync` issues `BEGIN TRANSACTION`, `CommitAsync` issues
`COMMIT`, and `RollbackAsync` issues `ROLLBACK`, all as ordinary Cypher through the exact same
query path as `QueryAsync`. What it buys you over issuing those statements yourself: disposing a
transaction that was never committed or rolled back rolls it back automatically (including when
its database is disposed first — see [Disposal and lifetime](#disposal-and-lifetime)), a second
commit or rollback throws `InvalidOperationException` instead of silently doing nothing or hitting
the engine again, and beginning a second transaction on a connection that already has one open
throws `InvalidOperationException` client-side rather than sending a nested `BEGIN TRANSACTION`
that the engine would reject in a way that invalidates the *first* transaction, not just the
second call. Every statement run on the connection while a transaction is open — not just ones
issued through the `LadybugTransaction` object — participates in it, because the transaction lives
on the connection itself, matching what `BEGIN TRANSACTION` means to the engine.

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

No functional gaps remain in the API surface this guide documents. See
[docs/MILESTONE-2-CARRYOVER.md](MILESTONE-2-CARRYOVER.md) for smaller, reviewed
implementation-detail items (test coverage, interop breadth) that don't affect the public API.

Two items worth calling out explicitly, since earlier versions of this guide listed them as
missing entirely:

- **Internal write serialization** is still not something the client does for you — it surfaces
  the engine's own rejection as `LadybugWriteConflictException` when `EnableMultiWrites` is off
  (the default). That is a deliberate design choice, not a gap: see
  [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint)
  for the measurement that settled it - turning the flag on genuinely lifts the restriction at the
  engine level, so a second, client-side lock on top of it would just be redundant.
- **A dedicated transaction API** now exists (`BeginTransactionAsync` / `LadybugTransaction`), but
  it is a wrapper over `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` Cypher, not a native engine
  primitive - the C API has no transaction functions at all. See
  [Concurrency and the single-writer constraint](#concurrency-and-the-single-writer-constraint)
  for the full contract.
