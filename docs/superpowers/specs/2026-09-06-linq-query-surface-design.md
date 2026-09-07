# LINQ query surface for LadybugDb.Client — design

Date: 2026-09-06. Status: Phases A and B implemented (2026-09-07); Phase C (source generator) not started. Research behind every claim here is in
[`docs/research/2026-09-06-linq-over-graph-databases.md`](../../research/2026-09-06-linq-over-graph-databases.md).

## Why

Today every read is a Cypher string plus either `LadybugRow` accessors or `Select<T>`. That is the
right floor and stays. What SharpMUSH will write thousands of times is a small set of shapes:

- one object by dbref, a handful of its properties;
- one attribute of one object by name;
- everything located in a room, or every exit from it;
- an object with all of its attributes;
- one or two hops along `Located`/`Exit`/`Owner`.

Those shapes are worth expressing as typed C# so the compiler catches a renamed property, a wrong
parameter type, or a projection that no longer matches a record constructor. The engine's strict
schema (one table per node type, one per relationship type, columns declared up front) makes this
tractable in a way it never was for Neo4j: a C# type maps to exactly one table.

## What the survey says

Thirteen years of .NET graph clients converge on the same lessons:

1. **Do not build a general expression translator.** Every provider that tried (re-linq-based,
   Gremlinq, Neo4jClient) ended up with a whitelist and an issue tracker full of "expression not
   supported". Ship the whitelist on day one, throw on everything else with the offending
   sub-expression in the message, and never fall back to client evaluation.
2. **Lambdas belong in WHERE, RETURN and ORDER BY only.** The MATCH pattern is a graph concept with no
   C# analogue; Neo4jClient hands it to a string, Blueprint41 to generated alias objects, Gremlinq to
   typed traversal steps. Typed traversal steps over a typed schema win for this engine.
3. **An explicit Cypher AST underneath** (neo4j-cypher-dsl, MongoDB LINQ3's AST) is what keeps the
   translator small: the LINQ layer produces AST nodes, one renderer produces text. The AST is
   reusable without LINQ, so a fluent DSL and the LINQ front end share one code path and one test
   suite.
4. **Async: plain `IQueryable<T>` plus own async terminals.** `IAsyncQueryable<T>` is not in the
   BCL and the runtime team has said it will not be. Every shipping provider (EF Core, MongoDB,
   Marten, RavenDB) implements `IQueryProvider` and adds `ExecuteAsync<TResult>`; none implements
   `IAsyncEnumerable<T>` on the queryable root, because on .NET 10 that makes `Where`/`Select`
   ambiguous with the in-box `System.Linq.AsyncEnumerable` operators (EF Core issue #24041).
5. **Source generators materialize rows and emit meta-models; they do not translate queries.**
   Interceptor-based query precompilation (EF Core, Dapper.AOT) churns with every SDK.

## Architecture

Three layers, each usable without the one above it.

```
LadybugDb.Client.Cypher      immutable AST + renderer + parameter collector      (Phase A)
LadybugDb.Client.Schema      [Node]/[Rel] descriptors, catalog validation         (Phase A)
LadybugDb.Client.Linq        IQueryable<T> front end, whitelisted translator,     (Phase B)
                             async terminals, graph steps
LadybugDb.Client.Generators  source-generated row readers and static meta-model   (Phase C)
```

All of it ships inside `LadybugDb.Client` (namespaces, not packages) until a second consumer asks
for the split; the generator is a separate analyzer package by necessity.

### Layer A: Cypher AST and renderer

An immutable object model for the subset of Cypher this engine accepts, rendered by one visitor.
Nothing in it knows about C# types or reflection.

```csharp
var o = Cypher.Node("Object", "o");
var r = Cypher.Node("Object", "r");
var q = Cypher.Match(o.RelTo("Located", r))
             .Where(r.Prop("dbref").Eq(Cypher.Param("room", 42L)))
             .Return(o.Prop("dbref").As("dbref"), o.Prop("name").As("name"))
             .OrderBy(o.Prop("name"))
             .Limit(50);
CypherText text = q.Render();
// text.Cypher     == "MATCH (o:Object)-[:Located]->(r:Object) WHERE r.dbref = $room RETURN o.dbref AS dbref, o.name AS name ORDER BY o.name LIMIT 50"
// text.Parameters == { room = 42L }
```

Node and relationship patterns, `WHERE` expressions (comparison, boolean, null tests, `IN`,
`STARTS WITH`/`CONTAINS`/`ENDS WITH`, `label()`, function calls), `WITH`, `RETURN` with aliases and
aggregates, `ORDER BY`/`SKIP`/`LIMIT`, `EXISTS {}` and `COUNT {}` subqueries, variable-length
relationships with the mandatory upper bound, `UNWIND`, and the write clauses `CREATE`, `SET`,
`DELETE`, `MERGE`. Every literal that is not a schema name becomes a `$p<n>` parameter; the renderer
never interpolates values. Identifiers are validated against the engine's identifier grammar or
backtick-quoted.

Renders the LadybugDB dialect: `label(n)` not `labels(n)`, `id(n)` not `elementId(n)`, `list_*`
functions, `cast(x, 'TYPE')`, no `SET n += {}` (rendered as one `SET` per key), no `REMOVE` (`SET x =
NULL`).

Tests: golden-text tests for every clause, and every rendered statement executed once against the
real engine so the dialect table cannot rot.

### Layer A: schema descriptors

```csharp
[Node("Object")]
public sealed record Obj([property: Key] long Dbref, string Name, long Loc);

[Node("Attr")]
public sealed record Attr([property: Key] string Akey, string Aname, string Aval);

[Rel("Has", From = typeof(Obj), To = typeof(Attr))]
public sealed record Has;

[Rel("Located", From = typeof(Obj), To = typeof(Obj))]
public sealed record Located;
```

`LadybugSchema.FromAssembly(...)` or `LadybugSchema.For<Obj, Attr, Has, Located>()` builds the
descriptor set once: table name, property names and CLR types, key, relationship endpoints.
`schema.ValidateAsync(connection)` runs `CALL show_tables()`, `CALL table_info(...)` and `CALL
show_connection(...)` and reports every mismatch (missing table, missing column, CLR type that the
engine column cannot widen into using the same rule `RowMapper` already applies) in one exception.
`schema.CreateTablesAsync(connection)` renders the DDL for a fresh database. Both are opt-in; the
LINQ layer works from the descriptors alone.

Property-to-column mapping is the constructor-parameter rule `RowMapper` already implements
(case-insensitive name match), so a `[Node]` record is also a valid `Select<T>` target with no
second mapping system.

### Layer B: LINQ front end

```csharp
public sealed class LadybugQueryable<T> : IOrderedQueryable<T>   // never IAsyncEnumerable<T>
public sealed class LadybugQueryProvider : IQueryProvider, ILadybugAsyncQueryProvider
public interface ILadybugAsyncQueryProvider { TResult ExecuteAsync<TResult>(Expression e, CancellationToken ct); }
```

Entry points on `LadybugConnection` and `LadybugTransaction`:

```csharp
IQueryable<T> Nodes<T>()                                  // MATCH (n:T)
IQueryable<T> Match<T>(string pattern, object? parameters) // string escape hatch, T bound to a named variable
```

Graph steps as extension methods over `IQueryable<T>`, each appending a pattern segment:

```csharp
q.Out<TRel, TTarget>()                     // (n)-[:TRel]->(m:TTarget), current variable becomes m
q.In<TRel, TSource>()                      // (m:TSource)-[:TRel]->(n)
q.OutWithRel<TRel, TTarget>()              // yields (rel, target) pairs when TRel has properties
q.Out<TRel, TTarget>(min, max)             // variable-length, upper bound required by the engine
q.WhereExists<TRel, TTarget>(pred)         // EXISTS { MATCH (n)-[:TRel]->(x:TTarget) WHERE ... }
```

Standard operators with their Cypher targets:

| LINQ | Cypher |
|---|---|
| `Where(pred)` | `WHERE` (whitelist below) |
| `Select(proj)` | `RETURN` with one alias per projected member; anonymous types, records, tuples, scalars |
| `OrderBy/ThenBy/Descending` | `ORDER BY` |
| `Skip(n)` / `Take(n)` | `SKIP $p` / `LIMIT $p` |
| `Distinct()` | `RETURN DISTINCT` |
| `Count()`, `Any()`, `First()`, `FirstOrDefault()`, `Single()`, `SingleOrDefault()` | `RETURN count(*)`, `LIMIT 1`, `LIMIT 2` |
| `GroupBy(key).Select(g => new { g.Key, n = g.Count() })` | implicit grouping in `RETURN` (Phase B.2) |
| everything else | `NotSupportedException` naming the operator |

Predicate whitelist (the entire list; anything else throws at translation with the sub-expression
text):

| C# | Cypher |
|---|---|
| `==`, `!=`, `<`, `<=`, `>`, `>=` between a member and a constant, closure, or another member | the same operator |
| `&&`, `||`, `!` on translatable sub-expressions | `AND`, `OR`, `NOT` |
| `x.Prop == null`, `!= null` | `IS NULL`, `IS NOT NULL` |
| `x.S.StartsWith(c)`, `.EndsWith(c)`, `.Contains(c)` (ordinal only) | `STARTS WITH`, `ENDS WITH`, `CONTAINS` |
| `collection.Contains(x.Prop)` where collection is a closure | `x.prop IN $p` |
| `x.List.Contains(c)` where the column is a LIST | `list_contains(x.list, $p)` |
| `x.S.Length`, `x.S.ToUpper()`, `.ToLower()` | `size()`, `upper()`, `lower()` |
| a bare boolean member `x.Flag` | **rejected**; write `x.Flag == true` (Cypher's three-valued NULL makes the bare form ambiguous, the same rule Neo4jClient adopted) |
| closure variables, constants | `$p<n>` parameters, bound with the typed `Bind` overloads (never interpolated) |

Projection rules: a member access renders `alias.prop AS name`; `new { ... }`, a record
constructor, or a tuple render one alias per argument; the whole node (`Select(o => o)` or no
`Select`) renders `RETURN o` and materializes through `LadybugNode` into the `[Node]` record;
aggregates (`Count()`, `Sum(...)`, `Max(...)`) inside a projection render the Cypher aggregate.
Materialization reuses `RowMapper.ResolvePlan<T>(columnNames)` with the projection's alias names,
so there is one mapping engine, one widening rule and one set of error messages.

Execution:

- `GetEnumerator()` runs the query synchronously: the engine is in-process and every existing async
  method completes synchronously, so a sync enumerator is honest and cheap.
- `AsAsyncEnumerable()` returns `IAsyncEnumerable<T>` by rendering once and streaming through the
  existing `Select<T>` iterator (which already owns and disposes the result on every exit path).
  Once a caller is on `IAsyncEnumerable<T>`, further `System.Linq.AsyncEnumerable` operators run
  client-side; the boundary is the method name, and the docs say so.
- `ToListAsync`, `ToArrayAsync`, `FirstOrDefaultAsync`, `SingleAsync`, `CountAsync`, `AnyAsync` as
  extensions on `IQueryable<T>` that dispatch to `ILadybugAsyncQueryProvider` and throw a clear
  error for a foreign provider.
- `CancellationToken` is checked before the statement runs and between rows, exactly as today;
  wiring it to `lbug_connection_interrupt` is a separate change on `LadybugConnection`.
- Rendered text is cached per expression-tree shape (parameters extracted first, then the shape
  hashed), so a repeated query prepares once via a per-connection prepared-statement cache. That
  cache is worth adding to `LadybugConnection` regardless of LINQ; the benchmark report shows why.

### Layer C: source generator

Two generated artefacts, both optional:

1. **Row readers.** For every `[Node]` record and every record used as a `Select` target, a
   `static T Read(LadybugRow row, int[] columnMap)` that calls the typed accessors directly. This
   removes the `object?[]` and the per-column box the reflective `RowMapper` path pays, and makes
   the projection trim- and AOT-safe (today `Select<T>` carries `RequiresUnreferencedCode`).
2. **Static meta-model.** `Obj_.Dbref`, `Obj_.Name`, `Has_`, `Located_` as typed property and
   relationship references usable in the fluent DSL without lambdas (the neo4j-cypher-dsl codegen
   shape), so a query can be built in non-expression contexts (attribute-driven softcode compilers,
   for instance) with the same compile-time checking.

Not in scope for the generator: translating any lambda.

## What SharpMUSH code looks like

```csharp
// one object by dbref, three properties, prepared once and reused
var name = await conn.Nodes<Obj>()
    .Where(o => o.Dbref == dbref)
    .Select(o => new { o.Name, o.Loc })
    .FirstOrDefaultAsync(ct);

// contents of a room
await foreach (var thing in conn.Nodes<Obj>()
    .In<Located, Obj>()                          // (thing)-[:Located]->(room)
    .Where(room => room.Dbref == roomRef)        // predicate on the current variable
    .Select(room => room)                        // return the *source* variable? see note
    .AsAsyncEnumerable())
```

That second example exposes the one real design question in the front end: after a traversal step
the "current" variable moves to the target, and a projection over the source needs a way to name
it. Two options, to be decided in Phase B's spike:

- **Pair-carrying steps.** `Out<TRel,TTarget>()` yields `(TSource Source, TTarget Target)` and the
  caller projects from either; `OutWithRel` yields the triple. Simple, explicit, verbose.
- **Named variables.** `conn.Match<Obj>("(thing)-[:Located]->(room:Object)", ...)` binds `T` to a
  named variable and later steps refer to names. This is Neo4jClient's convention-coupled shape.

The spike should prototype both against the four SharpMUSH shapes above and pick by how the
resulting code reads, not by translator convenience.

```csharp
// one attribute of one object, through the graph
var value = await conn.Nodes<Obj>()
    .Where(o => o.Dbref == dbref)
    .Out<Has, Attr>()
    .Where(a => a.Aname == attrName)
    .Select(a => a.Aval)
    .FirstOrDefaultAsync(ct);

// the escape hatch: any Cypher, typed result
var exits = conn.Match<Exit>("(r:Object {dbref: $room})-[:Exit]->(e:Object)", new { room })
                .Select(e => new { e.Dbref, e.Name });
```

## Testing

- Translation tests: expression in, `(cypher, parameters)` out, exact text. One per whitelist row,
  one per refusal.
- Engine round-trips: every translated shape executed against a real database with the schema
  above and the rows asserted. The compile-and-run rule the repository already enforces for
  documentation samples applies to every example in this document.
- Refusal tests: bare boolean member, unsupported method, client-evaluable-but-forbidden expressions,
  each asserting the message names the sub-expression.
- `AsAsyncEnumerable` disposal: the same `LiveCount` assertion `SelectDisposalTests` uses, on early
  break and on a throwing loop body.
- Cache tests: a repeated shape prepares once (`lbug_query_summary_get_compiling_time` or a
  prepare counter).

## Phasing and estimates

| Phase | Deliverable | Size |
|---|---|---|
| A | AST + renderer + parameter collection + dialect tests; `[Node]`/`[Rel]` descriptors; catalog validation; a fluent `Cypher.Match(...)` DSL usable immediately | medium; no LINQ risk |
| B.1 | `IQueryable<T>` root, provider, `Where`/`Select`/`OrderBy`/`Skip`/`Take`/terminals, graph steps, async terminals, prepared-statement cache | medium-large; the translator whitelist is the risk |
| B.2 | `GroupBy` aggregates, `EXISTS`/`COUNT` subqueries, variable-length steps | small increments |
| C | source-generated readers and meta-model | medium; independent of B |

Phase A alone already gives SharpMUSH parameterized, never-interpolated Cypher with typed results.
If Phase B stalls, A is not wasted.

## Decisions recorded

- **Not re-linq.** Frozen since 2018; its from/where/select model does not fit MATCH-first queries;
  EF Core and Marten both removed it.
- **Not Ix `IAsyncQueryable`.** Not BCL, not planned, and async lambdas inside expression trees
  cannot be translated to Cypher anyway.
- **Not an EF Core provider.** EF's model (change tracking, identity map, relationship fixup)
  duplicates what SharpMUSH's own object cache does, and the provider contract is enormous.
- **Not `IAsyncEnumerable<T>` on the root.** EF Core #24041; the .NET 10 in-box operators make the
  ambiguity worse.
- **Not client evaluation, ever.** MongoDB v3 and EF Core 3.0 both went there after years of
  silent full-table scans.
- **Not source-generated Cypher from lambdas.** No prior art; interceptor churn.

## Risks

- The whitelist becomes a feature-request queue. Mitigation: the string `Match` escape hatch is
  first-class, documented next to every refusal message.
- Cypher NULL semantics versus C# booleans. Mitigation: the bare-boolean refusal and `IS NULL`
  rendering are decided here, not case by case.
- Maintenance cost. Marten's note is the honest benchmark; Phase A's AST is the part that pays for
  itself regardless.
- Engine dialect drift between releases. Mitigation: every rendered clause is executed in the
  integration suite against the pinned engine, so a dialect change fails loudly on the version bump.
