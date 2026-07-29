# Usage guide

This is the full guide to `LadybugDb.Client`. It assumes you've read the
[README](../README.md)'s quick start. Every code sample below was compiled and run against the
real engine while this guide was written.

- [Opening and configuring a database](#opening-and-configuring-a-database)
- [Connections](#connections)
- [Executing Cypher](#executing-cypher)
- [Prepared statements](#prepared-statements)
- [Reading results](#reading-results)
  - [Type coverage](#type-coverage)
- [Transactions](#transactions)
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
only thing standing between you and a Cypher injection bug; see
[Prepared statements](#prepared-statements) below. Every `QueryAsync` call returns a
result, even for statements like `CREATE TABLE` that don't produce rows; disposing it discards
that result and frees the underlying native resources. The `await using (var _ = ...) { }` shape
above is the idiom for "run this statement, I don't need to read anything back."

A statement that fails throws `LadybugException` or `LadybugWriteConflictException` — see
[Error handling](#error-handling).

## Prepared statements

```csharp
await using var stmt = await conn.PrepareAsync("CREATE (o:Object {dbref: $dbref, name: $name})");
stmt.Bind("dbref", 42L);
stmt.Bind("name", "Limbo");
await using (var _ = await stmt.ExecuteAsync()) { }

stmt.Bind("dbref", 43L);
stmt.Bind("name", "The Void");
await using (var _ = await stmt.ExecuteAsync()) { }
```

`conn.PrepareAsync(cypher)` compiles a Cypher string once and returns a `LadybugPreparedStatement`
that can be executed repeatedly with different bound values — the engine plans the query once, not
on every call, which matters for any statement run in a loop. It also sidesteps building Cypher by
string interpolation, which is both slower (no plan reuse) and a Cypher-injection risk if any bound
value comes from outside your program.

`LadybugPreparedStatement` has seventeen typed `Bind(name, value)` overloads covering the engine's
scalar and temporal parameter types — every integer width (signed and unsigned), `bool`, `float`,
`double`, `string`, `DateOnly`, `TimeSpan` (INTERVAL), `DateTime` (TIMESTAMP), `DateTimeOffset`
(TIMESTAMP_TZ), `BigDecimal` (DECIMAL, see [DECIMAL](#decimal-asdecimal-vs-asbigdecimal) below) —
plus `BindTimestampSeconds`/`BindTimestampMilliseconds`/`BindTimestampNanoseconds` for the other
three timestamp precisions, and `BindNull(name)` for a typed NULL. Call `ExecuteAsync()`
to run the statement with whatever values the most recent `Bind` calls set, and get back an ordinary
`LadybugQueryResult` — read it exactly as described in [Reading results](#reading-results) below.
Re-binding a subset of parameters and calling `ExecuteAsync()` again reuses every parameter not
re-bound since the last call.

**Binding a parameter name the Cypher doesn't reference doesn't throw.** `Bind`/`BindNull` only
validate the value being converted (e.g. an out-of-range `DateTime`); they do not check the name
against the prepared statement's actual parameter list, because the C API has no entry point to ask
it. A typo in a bound name, or a parameter your Cypher string doesn't actually use, silently
"succeeds" at bind time — confirmed empirically, not assumed from the header — and only surfaces if
the *Cypher* references a name that was never bound, which fails at `ExecuteAsync()` with
`LadybugException` ("Parameter name not found."), not at `Bind`. Double-check your parameter names
against the Cypher string itself; nothing else will catch a mismatch for you.

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

Every scalar, temporal, container, and graph type the engine commonly returns marshals to a typed
`LadybugValue` — `AsInt64()`, `AsString()`, `AsBoolean()`, `AsDateTime()`, `AsList()`, `AsNode()`,
`AsInternalId()`, and so on. Call the accessor matching `row.GetValue(i).Type`; calling the wrong
one throws `InvalidOperationException`. See [Type coverage](#type-coverage) below for the precise
list, including the handful of types this client does not yet marshal.

### Type coverage

Covered, each reading as its own `LadybugType`/accessor pair:

- **Scalars:** `BOOL`, every signed/unsigned integer width (`INT8`…`INT64`, `UINT8`…`UINT64`),
  `FLOAT`, `DOUBLE`, `STRING`, `BLOB`, `DECIMAL` (see below).
- **Temporal:** `DATE`, `TIMESTAMP` (and its `_SEC`/`_MS`/`_NS` variants, all normalized to one
  `DateTime` representation), `TIMESTAMP_TZ`, `INTERVAL` (see the lossy-conversion note above).
- **Containers:** `LIST`/`ARRAY` (`AsList()`), `STRUCT` (`AsStruct()`), `MAP` (`AsMap()`).
- **Graph:** `NODE` (`AsNode()`), `REL` (`AsRel()`), and `INTERNAL_ID` (`AsInternalId()`) — the
  last of these is what a bare `RETURN id(n)` produces, distinct from the `Id` property already on
  `AsNode()`/`AsRel()`'s own result.

**Not yet covered** — a value of one of these types reads back as `LadybugType.Unsupported` with
no usable payload, rather than throwing at read time:

- `UUID`
- `RECURSIVE_REL`
- `INT128`
- `UNION`
- `POINTER`

None of these five are reachable through ordinary Cypher in the schemas this guide's own samples
use; if your schema needs one, `LadybugType.Unsupported` is your signal to check back on a later
release rather than a silent gap.

### DECIMAL: `AsDecimal()` vs `AsBigDecimal()`

The engine's `DECIMAL` supports up to **38 significant digits** — `DECIMAL(38,0)` and
`DECIMAL(38,10)` are both accepted; `DECIMAL(39,0)` is rejected at `CREATE TABLE` time with
"Precision of DECIMAL/NUMERIC must be a positive integer…". .NET's own `decimal` holds only
**28-29 significant digits**, so there's a real gap (`DECIMAL(29..38)`) that `decimal` cannot
represent at all.

Two read accessors exist because of that gap:

- **`AsDecimal()`** parses the engine's exact decimal string into a `decimal`. Convenient, and
  correct for anything up to `decimal`'s own range — but a value needing more digits than that
  throws `LadybugException` (pointing you at `AsBigDecimal()`) rather than silently truncating or
  rounding.
- **`AsBigDecimal()`** parses the same string into an
  [`ExtendedNumerics.BigDecimal`](https://www.nuget.org/packages/ExtendedNumerics.BigDecimal) —
  arbitrary-precision, backed by a `BigInteger` mantissa — and is **always lossless**, for all 38
  engine digits. Use this for a `DECIMAL(29..38)` value, or any time exactness matters more than
  interop with existing `decimal`-based code.

Binding is symmetric: `LadybugPreparedStatement.Bind(string, BigDecimal)` is the write-side
counterpart to `AsBigDecimal()` — there is no separate `decimal`-typed overload, since `BigDecimal`
already converts implicitly from `decimal`/`double`/`int`/`BigInteger`. Build one with
`BigDecimal.Parse("12345.6789")` (or an implicit conversion from an existing `decimal`) and bind it
directly:

```csharp
using ExtendedNumerics;

await using var stmt = await conn.PrepareAsync("CREATE (n:Ledger {id: $id, amount: $amount})");
stmt.Bind("id", 1L);
stmt.Bind("amount", BigDecimal.Parse("12345.6789"));
await using (var _ = await stmt.ExecuteAsync()) { }
```

**Precision and scale are derived from the value itself**, not from the target column's declared
`DECIMAL(p,s)` — the C API gives a prepared statement no way to ask what a parameter's declared
column type is. What that means in practice, established empirically against a real database
(not assumed from the header):

- **A bound value with lower precision/scale than the column is widened, not rejected.** Binding
  `BigDecimal.Parse("123")` (precision 3, scale 0) into a `DECIMAL(18,4)` column reads back as
  `123.0000` — the engine pads to the column's own scale.
- **A bound value with higher *scale* than the column is silently rounded, not rejected.** Binding
  `BigDecimal.Parse("123.456")` (scale 3) into a `DECIMAL(18,2)` column reads back as `123.46` —
  confirmed round-half-away-from-zero (`1.5` → `2`, `-1.5` → `-2`, `2.5` → `3`). This is the one
  genuine sharp edge in this bind path: narrowing the scale does not throw, it loses precision
  silently. If your application can't tolerate that, round or reject on the .NET side before
  binding.
- **A Cypher *literal* rounds differently than a bound parameter, for the same value.** Confirmed
  empirically: `CREATE (r:R {v: 1.005})` into a `DECIMAL(18,2)` column stores `1.00`, while
  `stmt.Bind("v", BigDecimal.Parse("1.005"))` into the identical column stores `1.01`. The literal
  path parses `1.005` as a binary `double` first — which is actually `1.00499999999999989…` — so
  rounding that to two places rounds *down*; the bind path carries the exact decimal string
  `"1.005"` through to the engine, which rounds the true half-way value *away from zero*. This
  reproduces for other exact-half values too (`1.015`, `1.025`, `-1.005`, `-1.015`), while a value
  that isn't an exact half in decimal, like `2.675`, happens to agree on both paths (its nearest
  `double` rounds up anyway). **Practical takeaway: don't rely on a Cypher decimal literal and a
  bound `BigDecimal` producing the same stored value at the scale boundary — prefer binding
  (`Bind(string, BigDecimal)`) for any decimal value where the exact rounding matters**, since it's
  the path this client controls and documents; the literal path's rounding is the engine's own
  double-parsing behavior, not something this client can promise.
- **A bound value whose integer part doesn't fit the column's precision is rejected.** Binding
  `BigDecimal.Parse("12345.67")` into a `DECIMAL(5,2)` column (room for only 3 integer digits)
  throws `LadybugException` ("Overflow exception: Decimal Cast Failed: input 12345.67 is not in
  range of DECIMAL(5, 2)").
- **A value needing more than 38 significant digits throws before the native call is ever made** —
  this client's own guard, not an engine round-trip, since forwarding an out-of-range precision
  into `lbug_value_create_decimal` has no useful behavior to fall back on.

`ExtendedNumerics.BigDecimal` also normalizes trailing zeros out of its own mantissa by default
(`BigDecimal.AlwaysNormalize`, a `true`-by-default static on the type) — unlike `decimal`, which
preserves them. So `BigDecimal.Parse("1.2300")` and `BigDecimal.Parse("1.23")` are the identical
value the instant they're parsed. That's the dependency's own canonical form, not something this
client does; it doesn't affect round-trip correctness (both the value you bind and the value you
read back normalize the same way, so equality still holds exactly), only the trailing zeros'
*visibility* if you inspect `.Mantissa`/`.Exponent` directly. `AsString()` still returns the
engine's own storage exactly as-is, trailing zeros included, if you need that.

See `LadybugDb.Client.IntegrationTests/DecimalBidirectionalTests.cs` for the full evidence behind
every claim above, including the 38-digit maximum, negative values, and zero.

**`AsTimeSpan()` on an INTERVAL is lossy.** A native interval carries a separate months component
that `TimeSpan` has no concept of, so the conversion — delegated to the engine's own
`lbug_interval_to_difftime`, not computed by this client — converts months at a fixed 30 days each
before adding the days/microseconds components. An interval built from `INTERVAL 1 MONTH` and one
built from `INTERVAL 30 DAYS` are indistinguishable once read back as a `TimeSpan`. If your
application does calendar-aware arithmetic where a month isn't uniformly 30 days, don't round-trip
through `AsTimeSpan()` for that.

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
    await using var result = current;
    await foreach (var row in result) { /* ... */ }
    current = await result.NextResultAsync();
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

## Transactions

If you need a single logical write to span more than one statement, use
`conn.BeginTransactionAsync()`:

```csharp
await using (var tx = await conn.BeginTransactionAsync())
{
    await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 1, name: 'Limbo'})")) { }
    await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 2, name: 'The Void'})")) { }
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

**The raw-Cypher escape hatch bypasses all of that safety — and it can crash the process, not just
skip a rollback.** Nothing stops you from issuing `await conn.QueryAsync("BEGIN TRANSACTION")`
directly instead of calling `BeginTransactionAsync`, but if you do, this client has no way to know
a transaction is open. `BeginTransactionAsync`'s bookkeeping (the connection's `_activeTransaction`
tracking and the database-side registration that drives the automatic rollback-on-dispose in
[Disposal and lifetime](#disposal-and-lifetime)) only runs *inside* `BeginTransactionAsync` itself;
a transaction opened by handing `BEGIN TRANSACTION` to `QueryAsync` as a plain string is invisible
to it, so `LadybugDatabase.Dispose` never rolls it back.

Reproduced directly, not assumed: opening a transaction via raw `QueryAsync("BEGIN TRANSACTION")`,
leaving it uncommitted, then disposing the database and letting the connection's own `DisposeAsync`
run afterward, **aborts the whole process** — `lbug_connection_destroy`'s own auto-rollback (see
[Disposal and lifetime](#disposal-and-lifetime)) fires against a transaction the now-destroyed
database can no longer service, and the engine throws a native `lbug::common::TransactionManagerException`
("Invalid transaction type to rollback") that crosses the P/Invoke boundary as an unhandled
exception — `terminate()`, `SIGABRT`, the process is gone, not a catchable managed exception. This
is a real tradeoff you can reach for deliberately (for example, Cypher your database driver already
emits verbatim), not a footnote: reaching for it means you've opted back into managing that
transaction's entire lifetime by hand — commit or roll it back yourself, before the connection or
database can be disposed — exactly as if this client provided no transaction API at all, except
that getting it wrong here doesn't throw, it takes the process down.

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
// Kept as a named variable and disposed explicitly below, not `await using`, specifically to
// demonstrate the manual multi-step ordering this section is about - the fire-and-forget
// `await using (var _ = ...) { }` idiom from Executing Cypher still applies whenever you don't
// need to walk back through the disposal order like this.
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
await using (var _ = await conn.QueryAsync("CREATE (n:Object {dbref: 1})")) { } // never committed

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

**And if `BeginTransactionAsync` itself races a concurrent `Dispose()`.** Calling
`conn.BeginTransactionAsync()` on one thread while another thread calls `db.Dispose()` on the
owning database is safe, even though the engine considers the transaction open the instant its
`BEGIN TRANSACTION` succeeds - before this library's own bookkeeping has a chance to run. The
long-lived hold mentioned above is taken *before* `BEGIN TRANSACTION` is issued, not after it
succeeds, specifically so that window cannot be raced into: either the hold is acquired first (and
the database cannot be destroyed until the attempt resolves one way or the other), or the database
is already gone and `BeginTransactionAsync` throws `ObjectDisposedException` instead of proceeding.

## Concurrency and the single-writer constraint

By default, LadybugDB permits exactly one write transaction at a time and **rejects** a second
rather than queuing it. Under contention this is expected, not exceptional, and it's surfaced as
the typed, retryable `LadybugWriteConflictException` rather than a raw engine error string.

This was benchmarked, not assumed: with the default configuration, throughput was flat from 1 to
8 concurrent writers (roughly 2,400-2,800 mutations/sec regardless of writer count) while conflict
retries climbed past 10,000 over the same run. The client does not serialize writes internally —
if you open multiple connections and write from more than one at a time with the default
configuration, you *will* see this exception, by design. A retry loop at the call site is the
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

If you need a single logical write to span more than one statement, see
[Transactions](#transactions).

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
await using (var _ = await conn.QueryAsync(
    "CREATE NODE TABLE Attr(dbref INT64, name STRING, PRIMARY KEY(dbref))")) { }

// Avoid: composite STRING primary key costs ~4.8x an INT64 key at equal row count.
await using (var _ = await conn.QueryAsync(
    "CREATE NODE TABLE AttrByString(key STRING, name STRING, PRIMARY KEY(key))")) { }
```

**Don't pack many values into one wide column.** A `MAP(STRING,STRING)` holding ten attributes
cost 3.619 ms to read, against 0.123 ms for the key alone — because a single-row read
materializes the *whole* column value, not just the part you asked for. LadybugDB's columnar
storage scans many rows of few columns well, and fetches few rows of many columns badly. Prefer
one row per attribute (or a narrow, fixed set of columns) over one row holding a bag of values.

**Bulk load with `COPY`, not per-row `CREATE`.** 1,000,000 rows in 0.5 s versus 296 s — roughly
600× — for the same data inserted one `CREATE` at a time:

```csharp
await using (var _ = await conn.QueryAsync($"COPY Object FROM '{csvPath}'")) { }
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
  [Transactions](#transactions) for the full contract, including the raw-Cypher escape hatch's
  tradeoff.
