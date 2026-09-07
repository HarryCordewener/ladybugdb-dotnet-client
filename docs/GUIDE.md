# Getting started with LadybugDb.Client

A walkthrough from an empty project to a working embedded graph database, in the order you will
meet the pieces. It builds one small example throughout: game objects with attributes and
locations, the shape SharpMUSH uses. Every code block below is compiled and run against the real
engine by `LadybugDb.Client.IntegrationTests/GuideSamplesTests.cs` (and the DI block by
`LadybugDb.Client.Extensions.Tests/GuideSamples.cs`), so what you read is what runs.

[docs/USAGE.md](USAGE.md) is the reference that documents every member; this is the tour.

- [1. Install](#1-install)
- [2. Open a database](#2-open-a-database)
- [3. Define the schema](#3-define-the-schema)
- [4. Write](#4-write)
- [5. Read](#5-read)
- [5b. LINQ](#5b-linq)
- [6. Hot paths: prepared statements and the cache](#6-hot-paths-prepared-statements-and-the-cache)
- [7. Transactions and conflicts](#7-transactions-and-conflicts)
- [8. Cancellation](#8-cancellation)
- [9. Connections, threads, and disposal](#9-connections-threads-and-disposal)
- [10. Observability](#10-observability)
- [11. Dependency injection and ASP.NET Core](#11-dependency-injection-and-aspnet-core)
- [12. When something goes wrong](#12-when-something-goes-wrong)

## 1. Install

Two packages: the client, and the engine binaries from upstream.

```console
dotnet add package LadybugDb.Client
dotnet add package LadybugDB.Native              # every platform, or LadybugDB.Native.linux-x64 etc.
```

`LadybugDb.Client` is managed code only and declares no native dependency, so which platforms you
ship and which engine version you run stay your choice. The native package's version is the
engine version. This build was generated against engine **v0.19.1** and accepts that or newer;
opening a database against an older engine throws a `LadybugException` that names the version to
install.

## 2. Open a database

A database is a file on disk (plus `.wal` and a few sibling files the engine manages), opened in
process. There is no server.

```csharp
using LadybugDb.Client;

var config = new LadybugConfig
{
    MaxThreads = 1,           // small queries: the default fan-out (one worker per core) only adds latency
    EnableMultiWrites = true, // several writers at once; conflicts are per row, not per database
};
using var db = new LadybugDatabase("./game.db", config);
await using var conn = await db.ConnectAsync();
```

`MaxThreads = 1` is the right default for point lookups and short traversals: measured, the
engine's default of one worker per core costs 17 to 50 percent extra latency on those. Raise it
for analytical scans. `EnableMultiWrites` is what lets more than one connection write at a time;
section 7 shows what happens without it.

## 3. Define the schema

The engine is strictly typed: every node and relationship has a table with declared columns. DDL
is Cypher run through `ExecuteAsync`, which discards the confirmation row DDL returns.

```csharp
await conn.ExecuteAsync("CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))");
await conn.ExecuteAsync("CREATE NODE TABLE Attr(key STRING, name STRING, value STRING, PRIMARY KEY(key))");
await conn.ExecuteAsync("CREATE REL TABLE Has(FROM Object TO Attr)");
await conn.ExecuteAsync("CREATE REL TABLE Located(FROM Object TO Object)");
```

Attributes are their own node table keyed by `"dbref/NAME"`, with a `Has` edge back to the object.
That layout won every benchmark over a `MAP` column on the object: a set touches one row, a read is
one key lookup, and it scales under concurrent writers.

## 4. Write

Pass values as a parameters object. Values are bound, never interpolated, and the engine
range-checks every coercion (`int` reaches an `INT64` column; an out-of-range value is an error,
not a truncation).

```csharp
await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 1L, name = "Limbo" });
await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 2L, name = "Wizard" });
await conn.ExecuteAsync(
    "MATCH (o:Object {dbref: $dbref}) CREATE (o)-[:Has]->(:Attr {key: $key, name: $name, value: $value})",
    new { dbref = 2L, key = "2/DESC", name = "DESC", value = "A tall figure." });
await conn.ExecuteAsync(
    "MATCH (a:Object {dbref: $a}), (b:Object {dbref: $b}) CREATE (a)-[:Located]->(b)",
    new { a = 2L, b = 1L });
```

`ExecuteAsync` returns nothing because the engine reports no affected-row count; use `QueryAsync`
if you want to read what a statement returns.

For bulk loading, `COPY FROM` a CSV file is two orders of magnitude faster than per-row `CREATE`
(100,000 objects with a million attributes and edges in about a second). The path is a literal, so
escape it with backslashes:

```csharp
var csv = Path.Combine(Path.GetTempPath(), "objects.csv");
File.WriteAllText(csv, "dbref,name\n10,Kitchen\n11,Garden\n");
var escaped = csv.Replace("\\", "\\\\").Replace("'", "\\'");
await conn.ExecuteAsync($"COPY Object FROM '{escaped}' (HEADER=true)");
```

## 5. Read

`QueryAsync` streams rows. Read columns by position or name with the typed accessors.

```csharp
await using var result = await conn.QueryAsync(
    "MATCH (o:Object) WHERE o.dbref >= $min RETURN o.dbref, o.name ORDER BY o.dbref", new { min = 1L });
await foreach (var row in result)
{
    Console.WriteLine($"#{row.GetInt64(0)} {row.GetString("o.name")}");
}
```

Two things to know: a result is single-pass (enumerate it once; materialize with `ToListAsync()`
if you need it twice), and an unaliased column is named after its expression (`o.name`).

`Select<T>` projects rows into a record whose constructor parameters name the columns. The match
is case-insensitive and an unaliased `o.name` matches a parameter called `Name`, so the usual
query needs no `AS` at all:

```csharp
record GameObject(long Dbref, string Name);

await foreach (var o in conn.Select<GameObject>("MATCH (o:Object) RETURN o.dbref, o.name ORDER BY o.dbref"))
{
    Console.WriteLine(o);
}

var count = await conn.Select<long>("MATCH (o:Object) RETURN count(o)").FirstAsync();
```

A whole node comes back as a `LadybugNode` with its label and a property dictionary; it costs
about 2.5 times a projected row, so project the columns you need when it matters:

```csharp
await using var nodes = await conn.QueryAsync("MATCH (o:Object {dbref: $d}) RETURN o", new { d = 2L });
await foreach (var row in nodes)
{
    var node = row.GetValue(0).AsNode();
    Console.WriteLine($"{node.Label} {node.Properties["name"].AsString()}");
}
```

Every engine type marshals to a typed `LadybugValue` (`AsInt64()`, `AsString()`, `AsList()`,
`AsNode()`, `AsBigDecimal()`, ...). [USAGE.md's type table](USAGE.md#type-coverage) has the full
list.

## 5b. LINQ

The same reads as typed C#: annotate the records with `[Node]`, `[Rel]` and `[Key]`, and
`conn.Nodes<T>()` is an `IQueryable<T>` that translates `Where`, `Select`, `OrderBy`, `Skip`/`Take`,
the graph steps and the `...Async` terminals into one parameterized Cypher statement. Nothing runs
on the client; an expression outside the whitelist throws at translation, naming it.

```csharp
using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;

[Node("Object")] record Obj([property: Key] long Dbref, string Name);
[Node("Attr")]   record AttrNode([property: Key] string Key, string Name, string Value);
[Rel("Has", From = typeof(Obj), To = typeof(AttrNode))] record Has;

var wizard = await conn.Nodes<Obj>().Where(o => o.Dbref == 2).Select(o => o.Name).SingleAsync();
// MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name LIMIT $p1

var desc = await conn.Nodes<Obj>()
    .Where(o => o.Dbref == 2)
    .Out<Obj, Has, AttrNode>()
    .Where(p => p.Target.Name == "DESC")
    .Select(p => p.Target.Value)
    .FirstOrDefaultAsync();
// MATCH (n0:Object)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.name = $p1 RETURN n1.value AS Value LIMIT $p2

var exits = await conn.Match<Obj>("(r:Object {dbref: $room})<-[:Located]-(n:Object)", new { room = 1L })
    .Select(n => n.Name).ToListAsync();            // the escape hatch: your MATCH, the same typed chain after it
```

`AsAsyncEnumerable()` is the boundary: before it, Cypher; after it, the in-box
`System.Linq.AsyncEnumerable` over the streamed rows. [USAGE.md's LINQ chapter](USAGE.md#linq) has
the whole whitelist, the graph steps, `GroupBy` aggregates and every refusal message.

## 6. Hot paths: prepared statements and the cache

A statement you run often should be planned once. `PrepareAsync` gives you a statement you bind
and execute repeatedly:

```csharp
await using var byDbref = await conn.PrepareAsync("MATCH (o:Object {dbref: $d}) RETURN o.name");
foreach (var d in new[] { 1L, 2L })
{
    byDbref.Bind("d", d);
    await using var r = await byDbref.ExecuteAsync();
    await foreach (var row in r) Console.WriteLine(row.GetString(0));
}
```

You do not have to manage that yourself for the common case: `QueryAsync(cypher, parameters)`,
`ExecuteAsync(cypher, parameters)` and `Select<T>` keep a per-connection cache of prepared
statements keyed by statement text (128 entries, least recently used; `LadybugConfig.StatementCacheSize`
tunes it). A key lookup through those overloads costs the same as through a statement you hold,
about 65 µs on the benchmark host. One rule follows from how the engine treats a prepared
statement: bind the same set of parameter names every time you run a given statement text. A call
with a different set is refused rather than run with stale values.

## 7. Transactions and conflicts

`BeginTransactionAsync` wraps `BEGIN`/`COMMIT`/`ROLLBACK`; disposing without committing rolls back.
It also refuses a nested `BEGIN`, which the engine would otherwise use to silently discard the
transaction in flight.

```csharp
await using (var tx = await conn.BeginTransactionAsync())
{
    await conn.ExecuteAsync("MATCH (a:Attr {key: $key}) SET a.value = $value", new { key = "2/DESC", value = "A short figure." });
    await conn.ExecuteAsync("CREATE (:Object {dbref: $dbref, name: $name})", new { dbref = 3L, name = "Lamp" });
    await tx.CommitAsync();
}
```

With the engine default (`EnableMultiWrites = false`) only one write transaction exists at a time
and a second writer is **refused**, not queued, with a `LadybugWriteConflictException`. With the
flag on, writers proceed and only a genuine collision on the same row raises that exception. Either
way it is retryable, and preparing a write statement can raise it too, so a retry loop is the
pattern:

```csharp
static async Task WithRetryAsync(Func<Task> work, int attempts = 5)
{
    for (var attempt = 1; ; attempt++)
    {
        try { await work(); return; }
        catch (LadybugWriteConflictException) when (attempt < attempts)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5 * attempt));
        }
    }
}

await WithRetryAsync(() => conn.ExecuteAsync(
    "MATCH (a:Attr {key: $key}) SET a.value = $value", new { key = "2/DESC", value = "A figure." }).AsTask());
```

Measured on 10,000 objects: eight writers reach 2,600 mutations per second with the default and
14,000 with the flag.

## 8. Cancellation

Every query method takes a `CancellationToken`. Cancelling it interrupts the running query in the
engine; the call throws `OperationCanceledException` and the connection stays usable.

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
try
{
    await conn.QueryAsync(
        "UNWIND range(1, 20000) AS a UNWIND range(1, 20000) AS b WITH a * b AS x WHERE x % 7 = 0 RETURN count(x)",
        cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("stopped");
}
```

A query that had already completed when the token fired returns its result; a `CREATE` that
happened is never reported as cancelled.

## 9. Connections, threads, and disposal

- One `LadybugDatabase` per process per database file. It is `IDisposable`.
- A `LadybugConnection` is safe to use from several threads, but the engine runs one statement
  per connection at a time. For parallelism, open one connection per worker; connections are cheap.
- `LadybugConnection`, `LadybugQueryResult`, `LadybugPreparedStatement` and `LadybugTransaction`
  implement both `IDisposable` and `IAsyncDisposable`, and the two are equivalent. Dispose results
  and connections before the database; if you get the order wrong nothing crashes, the child is
  closed for new work and the engine is released when the last one goes.
- Async methods complete synchronously: the engine is in process and the work is CPU and local
  disk. The signatures are async so that can change without breaking you.

## 10. Observability

The client emits an `ActivitySource` and a `Meter`, both named `LadybugDb.Client`, following the
OpenTelemetry database conventions (`db.system.name = "ladybugdb"`, `db.query.text`,
`db.operation.name`, `error.type`; one `db.client.operation.duration` histogram). With no listener
nothing is allocated.

```csharp
using LadybugDb.Client.Diagnostics;

services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(LadybugDiagnostics.ActivitySourceName))
    .WithMetrics(m => m.AddMeter(LadybugDiagnostics.MeterName));
```

## 11. Dependency injection and ASP.NET Core

`LadybugDb.Client.Extensions` registers the database as a singleton, a connection per scope (per
request in ASP.NET Core), options, and a health check named `ladybugdb`.

```csharp
using LadybugDb.Client.Extensions;

services.AddLadybugDb("./game.db", o => o.Config = o.Config with { MaxThreads = 1, EnableMultiWrites = true });
services.AddHealthChecks();
```

Or bind from configuration (`LadybugDb:DatabasePath`, `LadybugDb:Config:MaxThreads`, ...):

```csharp
services.AddLadybugDb(configuration.GetSection("LadybugDb"));
```

Then take a `LadybugConnection` in a constructor or a minimal-API handler; its lifetime is the
request's.

## 12. When something goes wrong

| Symptom | Cause | Fix |
|---|---|---|
| `DllNotFoundException` naming `LadybugDB.Native` | No native package installed | `dotnet add package LadybugDB.Native` (or one `LadybugDB.Native.<rid>`) |
| `LadybugException: The loaded LadybugDB engine is version 0.18.x, but this build ... needs 0.19 or newer` | Native package older than the client's pin | Update `LadybugDB.Native` to the named version |
| `InvalidOperationException: This LadybugQueryResult has already been enumerated once` | A result enumerated twice (`CountAsync()` then `ToListAsync()`, for instance) | Materialize once with `ToListAsync()` and reuse the list |
| `InvalidOperationException: ... already has an active transaction` | A nested `BEGIN` | Commit or dispose the open transaction first; the guard exists because the engine would discard it |
| `LadybugWriteConflictException` | Another writer, or a row collision under `EnableMultiWrites` | Retry (section 7) |
| `ArgumentException: This statement was first run with parameters [...]` | Same statement text, different parameter names | Bind the same names every time, or change the text |
| `Cannot project into T: no public constructor's parameters all match the returned columns` | Column names and constructor parameters differ | Alias with `AS`, or rename the parameter; the message lists both sides |
| `LadybugException: ... already exists in catalog` on a rerun against a "deleted" database | Only the directory was deleted; the database is a file plus `.wal`/`.shadow`/`.lock`/`.tmp` siblings | Delete the file and its siblings |
