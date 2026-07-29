# API Ergonomics: Parameter Objects and Typed Projection — Design

**Date:** 2026-07-29
**Status:** Approved, pending implementation plan

## Context

`LadybugDb.Client` is merged at `main` (162 tests, .NET 10). Two rough edges in the public surface
motivate this work.

**Binding is ceremonious.** A parameterized query today costs `PrepareAsync` → one `Bind` call per
parameter → `ExecuteAsync`, even when the statement runs once:

```csharp
await using var stmt = await conn.PrepareAsync(
    "MATCH (o:Object) WHERE o.dbref = $dbref AND o.name = $name RETURN o.name");
stmt.Bind("dbref", 42L);
stmt.Bind("name", "Limbo");
await using var result = await stmt.ExecuteAsync();
```

**Results are untyped.** Every caller writes positional or by-name extraction and converts each
column by hand, at every call site.

## Goals

- A parameterized query should be expressible in one call, reading like the Cypher beside it.
- A result should be projectable into a caller-defined shape without hand-written per-column code.
- Both should stream rather than materialize.
- Neither should compromise the existing typed surface, which stays as the precise, allocation-free path.

## Non-goals

- A query builder, LINQ provider, or expression translation. The Cypher string stays the query.
- Change tracking, identity map, or any ORM behaviour beyond projection.
- Replacing the 19 typed `Bind` overloads. They remain, and remain the recommended path where
  performance or explicit typing matters.

## Decisions

### 1. Parameters: anonymous object or dictionary, on both entry points

Two overloads per entry point:

```csharp
// LadybugConnection
ValueTask<LadybugQueryResult> QueryAsync(string cypher, object parameters, CancellationToken ct = default);
ValueTask<LadybugQueryResult> QueryAsync(string cypher, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default);

// LadybugPreparedStatement
ValueTask<LadybugQueryResult> ExecuteAsync(object parameters, CancellationToken ct = default);
ValueTask<LadybugQueryResult> ExecuteAsync(IReadOnlyDictionary<string, object?> parameters, CancellationToken ct = default);
```

**Rationale.** The anonymous-object form is the ergonomic win at the call site; the dictionary form
covers parameter names computed at runtime, which the anonymous form cannot express. Supplying both
means a caller building a query dynamically is never pushed back to the chained API.

The dictionary overload reads keys directly and uses **no reflection**. Only the `object` overload
reflects, and only over the parameter object's public properties.

C# overload resolution prefers the `IReadOnlyDictionary` overload for a `Dictionary` argument, so the
two coexist for every concrete argument.

**One ambiguity must be closed explicitly:** a bare `QueryAsync(cypher, null)` matches both overloads
and fails to compile. The `object` overload therefore takes a non-nullable `object parameters`, and a
caller wanting no parameters uses the existing single-argument `QueryAsync(cypher)`. Passing `null`
is a caller error, not a supported "no parameters" spelling — and the compiler says so at the call
site rather than the client throwing at runtime. This must be covered by a compile-level check or a
documented test, since it is the kind of thing that silently regresses when an overload is added.

The one-shot `QueryAsync(cypher, parameters)` overload prepares, binds, executes, and disposes the
statement internally. It exists because a parameterized query run **once** should not require the
caller to manage a statement's lifetime.

### 2. Value dispatch

Each parameter value dispatches on its runtime type to the existing typed `Bind`:

| Value | Bind used |
|---|---|
| `null` | `BindNull` |
| `bool`, `sbyte`, `short`, `int`, `long`, `byte`, `ushort`, `uint`, `ulong`, `float`, `double`, `string` | matching typed overload |
| `DateOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan` | matching typed overload |
| `Guid`, `Int128`, `BigDecimal` | matching typed overload |
| anything else | `ArgumentException` naming the parameter **and** its runtime type |

**Open question to settle by measurement during implementation, not by assumption:** a C# `int`
dispatches to `Bind(int)`, binding INT32. Whether the engine coerces INT32 into an INT64 column, or
rejects it, decides whether integer values auto-widen to `long`. Measure it; if the engine rejects,
widen `int`/`short`/`sbyte` to `Bind(long)` and document the behaviour. Silent numeric coercion has
already produced one defect in this project (literal-vs-bound DECIMAL rounding), so this must be
established empirically.

### 3. `Select<T>` — streaming typed projection

```csharp
IAsyncEnumerable<T> Select<T>(string cypher, object? parameters = null, CancellationToken ct = default);
```

Streams; does not materialize. Implemented as an `async IAsyncEnumerable<T>` iterator that holds the
underlying `LadybugQueryResult` in an `await using` **inside** the iterator body.

**This lifetime detail is load-bearing.** The caller never receives the `LadybugQueryResult`, so the
method owns its disposal. The compiler-generated enumerator's `DisposeAsync` runs when enumeration
completes *and* when the caller `break`s early, so the result is released on both paths. A leaked
`LadybugQueryResult` per query would be precisely the class of defect this codebase has repeatedly
found; it gets an explicit test.

### 4. Mapping: constructor only, with scalar unwrap

**Constructor matching.** Select the constructor whose parameter names all match returned column
names, case-insensitively. This makes the idiomatic .NET result shape work with no settable
properties:

```csharp
record Person(long Dbref, string Name);

await foreach (var p in conn.Select<Person>(
    "MATCH (o:Object) WHERE o.dbref > $min RETURN o.dbref AS Dbref, o.name AS Name",
    new { min = 40L }))
{
    Console.WriteLine($"{p.Dbref}: {p.Name}");
}
```

Rules:
- Exactly one fully-matching constructor → use it.
- No matching constructor → `InvalidOperationException` listing the returned columns and each
  rejected candidate constructor with its parameter names. The error must make the mismatch obvious
  without the caller reaching for a debugger.
- More than one fully-matching constructor → throw as ambiguous. Do not guess.
- Columns not matching any parameter are ignored; a parameter with no matching column is an error.

**Scalar unwrap.** When `T` is a scalar type and the result has exactly one column, the column is
converted directly to `T` — no constructor involved:

```csharp
var total = await conn.Select<long>("MATCH (o:Object) RETURN count(*)").FirstAsync();
```

Scalar types are those with a direct `LadybugValue` accessor: `bool`, the integer widths, `Int128`,
`float`, `double`, `decimal`, `BigDecimal`, `string`, `byte[]`, `Guid`, `DateOnly`, `DateTime`,
`DateTimeOffset`, `TimeSpan`, plus their nullable forms. `T` scalar with more than one column is an
error naming the column count.

**Conversion.** Each column converts through the matching `LadybugValue` accessor for the target
type. A column whose `LadybugType` has no conversion to the target throws, naming the column, its
`LadybugType`, and the target type. `null` columns map to `null` for reference and `Nullable<T>`
targets, and are an error for non-nullable value types — naming the column.

**Caching.** The resolved constructor and per-column conversion plan are cached per `T` in a static
`ConcurrentDictionary<Type, …>`, keyed additionally by the result's column-name set so that two
different queries projecting the same `T` do not collide. Reflection therefore runs once per
`(T, column shape)`, not once per row — necessary because the method streams.

### 5. AOT and trimming

`Select<T>` and the `object`-parameters overloads use reflection and are annotated
`[RequiresUnreferencedCode]`. The annotation adds no API surface and no runtime cost, and gives
AOT/trim consumers a **build-time** warning identifying the limitation rather than a runtime failure.

The dictionary-parameter overloads use no reflection and carry no annotation.

This preserves the option of a source generator later replacing the reflection internals without an
API change, and of the library declaring `IsAotCompatible` for the non-annotated surface. No AOT or
trim posture is declared in any csproj today; this design does not change that, but does not
foreclose it.

## Architecture

```
LadybugDb.Client/
  LadybugConnection.cs            MODIFY: QueryAsync(+params) x2, Select<T>
  LadybugPreparedStatement.cs     MODIFY: ExecuteAsync(+params) x2
  Mapping/ParameterBinder.cs      NEW internal: object/dictionary -> typed Bind dispatch
  Mapping/RowMapper.cs            NEW internal: constructor resolution, scalar unwrap, conversion
  Mapping/MappingCache.cs         NEW internal: per-(T, column shape) plan cache
```

Mapping lives in its own folder because it is the only reflective code in the client, and isolating
it keeps the boundary that a future source generator would replace clearly drawn.

## Error handling

Every new failure is typed and names what failed:

| Condition | Exception | Must name |
|---|---|---|
| Unsupported parameter value type | `ArgumentException` | parameter name, runtime type |
| No matching constructor | `InvalidOperationException` | columns, rejected candidates |
| Ambiguous constructors | `InvalidOperationException` | the competing constructors |
| Column not convertible to target | `LadybugException` | column, `LadybugType`, target type |
| `null` into non-nullable value type | `LadybugException` | column, target type |
| Scalar `T` with multiple columns | `InvalidOperationException` | column count |

## Testing

Integration tests against the real engine:

- Both parameter forms on both entry points; prepared-statement reuse across executions.
- Positional `record` projection; nullable columns; extra columns ignored; missing column errors.
- Scalar unwrap, including `count(*)` and a nullable scalar.
- Wrong-type conversion produces the typed error, not a wrong value.
- **Early-`break` disposal**: enumerate partially, break, and assert the underlying result is
  released — the leak class this codebase has repeatedly found.
- Integer-width dispatch, per the measurement in Decision 2.
- Mapping cache correctness: two different column shapes projecting the same `T` must not collide.

## What this does not change

The 19 typed `Bind` overloads, `QueryAsync(cypher)`, `ExecuteAsync()`, and
`IAsyncEnumerable<LadybugRow>` enumeration all keep their current behaviour and remain the precise,
reflection-free path.
