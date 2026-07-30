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

### 1. Parameters: one `object` overload per entry point

A single parameter-taking overload per entry point, accepting either an anonymous object or a
dictionary and discriminating at runtime:

```csharp
// LadybugConnection
ValueTask<LadybugQueryResult> QueryAsync(string cypher, object parameters, CancellationToken ct = default);

// LadybugPreparedStatement
ValueTask<LadybugQueryResult> ExecuteAsync(object parameters, CancellationToken ct = default);
```

**Rationale.** The anonymous-object form is the ergonomic win at the call site; the dictionary form
covers parameter names computed at runtime, which the anonymous form cannot express. One `object`
parameter accepts both, so a caller building a query dynamically is never pushed back to the chained
API. Dictionaries are still read directly with **no reflection** — the discrimination is a type test,
not reflection.

**Why not two overloads, and why not a discriminated union.** An earlier draft of this design used
separate `object` and `IReadOnlyDictionary` overloads, which made `QueryAsync(cypher, null)`
ambiguous, which in turn motivated a two-case `Parameter` union to force explicitness. Both were
solving a problem the design created. With a single parameter overload there is no ambiguity to
resolve: measured against the existing `(string, CancellationToken)` overload, every intended call
resolves as expected — bare, with a token, with an anonymous object, with a dictionary, with an
object plus a token, and `(cypher, null)` compiles unambiguously and throws `ArgumentNullException`.
The union added a wrapper at every call site and bought only a compile-time rejection of nonsense
arguments, which the runtime error below already reports clearly.

The one-shot `QueryAsync(cypher, parameters)` overload prepares, binds, executes, and disposes the
statement internally. It exists because a parameterized query run **once** should not require the
caller to manage a statement's lifetime.

**Discrimination order is load-bearing — it was measured, not assumed.** A type test against
`IReadOnlyDictionary<string, object?>` alone is a silent-corruption bug: `Dictionary<string, long>`
does not match it and falls through to the reflection path, where the "parameters" become that
dictionary's own public properties — `Comparer`, `Count`, `Capacity`, `Keys`, `Values`, `Item` — with
no error raised. The binder therefore tests in this order:

1. `IReadOnlyDictionary<string, object?>` — the common case, enumerated without boxing. Note that
   nullable reference annotations are erased at runtime, so `Dictionary<string, object>` matches this
   too.
2. non-generic `System.Collections.IDictionary` — catches every other generic dictionary regardless
   of value type (`Dictionary<string, long>`, `SortedDictionary<string, string>`, …), boxing values.
   A non-string key is an `ArgumentException` naming the key's type.
3. otherwise, reflect over public properties.

Anonymous types are not `IDictionary`, so step 3 still receives them. A test must cover
`Dictionary<string, long>` specifically: it is the case that silently produced wrong parameter names,
and it regresses invisibly if the `IDictionary` step is ever dropped.

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

**Settled during implementation:**

- **The plan is resolved from the result's column shape, before the first row** — not from the first
  row, which is all `RowMapper` could do while `LadybugQueryResult`'s column names were private. A
  plan resolved from a row is never resolved at all for a result that has none, so a mismatched `T`
  raised *nothing* and a zero-row query silently "succeeded" against a `T` that could never have
  mapped its columns. `LadybugQueryResult` now exposes its column shape internally for this.
- **Disposal is asserted against a counter, not process memory.** `LadybugQueryResult` carries an
  internal `LiveCount` (constructed-minus-disposed, process-wide, no finalizer involved).
  `Environment.WorkingSet` is the wrong instrument for this specific leak twice over: it is a
  whole-process metric this repository already had to quarantine on hosted CI, and the handle under a
  leaked result *has* a finalizer, so a `GC.Collect()` before the measurement can release the very
  leak the test is looking for and report success. Each disposal test also asserts the count went
  **up** while the stream was open, so it cannot pass against an implementation whose result the
  counter never observed.
- **Argument validation splits.** `cypher` is validated eagerly by a non-iterator wrapper; everything
  needing the engine or reflection necessarily surfaces from the first `MoveNextAsync`.
- **`parameters: null` means "no parameters"** and routes to `QueryAsync(cypher, ct)`, rather than
  the `ArgumentNullException` the parameter-taking overload raises for an explicit null bag.

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

**Amended during implementation: conversion accepts lossless widening, not exact matches only.**
As first implemented, conversion was exact in both directions — an `INT32` column read into a `long`
target was an error — on the grounds that silent numeric coercion has already produced one defect in
this project. That is the right instinct pointed in one wrong direction, for two reasons found while
building on it:

- It makes a projected record's parameter types track the schema's *declared widths*, which is the
  opposite of what an ergonomics feature is for. A `long` field against an `INT32` column is not a
  coercion a caller can be surprised by; there is no value for which it is wrong.
- It is asymmetric with the bind side, where the engine was **measured** to coerce a bound `INT32`
  into an `INT64` column and to range-*check* rather than truncate (Decision 2's open question,
  settled). Refusing on read what the engine accepts on write is a rule with no principle behind it.

A target therefore reads the `LadybugType` that backs it exactly, plus every narrower type provably
contained in it: `INT8`/`INT16`/`INT32` into wider signed targets and `Int128`, `FLOAT` into
`double`, unsigned into wider unsigned, and unsigned into a signed target whose range contains the
whole unsigned range (`UINT32` into `long`, `UINT64` into `Int128`).

Nothing that can lose a bit, a digit, or a sign is accepted, and each refusal keeps the error naming
the column, its `LadybugType`, and the target type:

- **No narrowing** (`INT64` into `int`).
- **No signed into unsigned** (`INT8` into `byte`), however small the value — the alternative is
  reinterpreting the sign bit.
- **No unsigned into a same-width signed target** (`UINT32` into `int`, `UINT64` into `long`). The
  rule is about what the column's type can hold, not what one row happens to hold.
- **No integer into floating-point**, *including the cases that would be lossless.* `INT32` into
  `double` loses nothing, but `INT64` into `double` loses integers above 2^53 and `INT32` into
  `float` loses them above 2^24. A rule whose boundary is a mantissa width is not one callers can
  hold in their heads, and admitting its safe half would make the unsafe half look like an
  oversight. `CAST` in the Cypher expresses the conversion explicitly where it is wanted.

`decimal` keeps its own behaviour unchanged: a `DECIMAL` too precise for it throws rather than
rounding, and points at `BigDecimal`.

One existing unit assertion encoded the replaced rule (`INT32` into `long` throwing) and is
re-pointed at `INT64` into `int` — the direction that genuinely loses data — rather than dropped.

**Caching.** The resolved constructor and per-column conversion plan are cached per `T` in a static
`ConcurrentDictionary<Type, …>`, keyed additionally by the result's column-name set so that two
different queries projecting the same `T` do not collide. Reflection therefore runs once per
`(T, column shape)`, not once per row — necessary because the method streams.

### 5. AOT and trimming

`Select<T>` and the parameter-taking overloads are annotated `[RequiresUnreferencedCode]`. The
annotation adds no API surface and no runtime cost, and gives AOT/trim consumers a **build-time**
warning identifying the limitation rather than a runtime failure.

**Collapsing to one `object` overload costs something here, and the cost is accepted knowingly.**
A single overload may reflect, so it must be annotated, so a caller passing a *dictionary* — a path
that never reflects — still gets a trim warning. If that proves annoying in practice, an unannotated
`IReadOnlyDictionary<string, object?>` overload can be added later: adding an overload is not a
breaking change, and the only call it makes ambiguous is `QueryAsync(cypher, null)`, which is a
compile error on a call that has no meaning. Deferred rather than pre-built, because the annotation
matters only to a consumer who wants runtime-computed parameter names *and* trimming, and no such
consumer exists yet.

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
| `null` parameters object | `ArgumentNullException` | the parameter name |
| Dictionary with non-string keys | `ArgumentException` | the key's runtime type |
| No matching constructor | `InvalidOperationException` | columns, rejected candidates |
| Ambiguous constructors | `InvalidOperationException` | the competing constructors |
| Column not convertible to target | `LadybugException` | column, `LadybugType`, target type |
| `null` into non-nullable value type | `LadybugException` | column, target type |
| Scalar `T` with multiple columns | `InvalidOperationException` | column count |

## Testing

Integration tests against the real engine:

- Both parameter forms on both entry points; prepared-statement reuse across executions.
- **`Dictionary<string, long>` binds by its keys, not by `Count`/`Keys`/`Values`.** This is the case
  that silently bound a dictionary's own property names as parameters, and it regresses invisibly if
  the non-generic `IDictionary` step in Decision 1 is ever removed. A non-string-keyed dictionary
  raises `ArgumentException`.
- `QueryAsync(cypher, null)` throws `ArgumentNullException`, naming the parameter.
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
