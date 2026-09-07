# Research: expressing graph queries as LINQ in .NET (2026)

Researched 2026-09-06 to inform
[`superpowers/specs/2026-09-06-linq-query-surface-design.md`](../superpowers/specs/2026-09-06-linq-query-surface-design.md).
"Verified" means read from the page, API or source that day.

## 1. Existing .NET LINQ-to-graph providers

| Project | Approach | Latest release | Activity | Notes |
|---|---|---|---|---|
| Neo4jClient | Fluent Cypher DSL: `Match(string)` + `Where<T>(Expression<Func<T,bool>>)` + `Return(lambda)`. Not an `IQueryProvider`. | 5.1.20, 2024-12 | 2024-12 | 6.3M downloads; the most-used .NET Cypher client for 13 years. |
| Neo4j.Driver | String Cypher, records. No LINQ. | 5.28.4, 2025-12 | active | Memgraph recommends the same driver. |
| Blueprint41 | Schema as C# code, T4 code generation, typed alias DSL (`Node.Person.Alias(out var p).In.DIRECTED.Out.Movie`). | 1.1.8, 2025-08 | 2025-09 | 56K downloads, 40 stars. The build step is the adoption cost. |
| CypherNet | Fluent typed DSL over the HTTP endpoint. | – | archived 2018 | Dead. |
| `Ladybug` (Knaackee) | String Cypher; "LINQ" is LINQ over materialized rows. | 0.3.1, 2026-03 | none since | Not expression translation. |
| `LadybugDB` (official) | String Cypher, sync, no mapping. | 0.19.1, 2026-08 | active | No LINQ. |
| ExRam.Gremlinq | Fluent typed traversal with a whitelisted `Where` expression recognizer. Not `IQueryable`. | Core 14.1.1, 2026-08 | 2026-09 | MIT; 1.2M downloads; its issue tracker is mostly "expression not supported". |
| Gremlin.NET | Official untyped fluent traversal. | 3.8.1, 2026-04 | active | |
| EF Core Cosmos | Only ever targeted the NoSQL API, never Gremlin. | 10.0.x | active | "Dropped Gremlin" is a misremembering. |
| linq-to-gremlin | EF Core provider for Gremlin. | – | 2017 | Abandoned. |
| Stardust.Paradox | Attributed interfaces plus codegen; fluent Gremlin; no LINQ. | 2.3.x | 2026-06 | Cosmos-centric. |
| GraphEngine LIKQ | Lambda-in-traversal on Trinity. | 2017 | dormant | |
| Neo4j.Berries.OGM | Eloquent-style `Match(x => x.Where(...))`; no LINQ. | 1.2.0-preview | 2025-07 | |
| Apache AGE .NET | String Cypher through Npgsql. | 1.0 / 2.0 | | |
| openCypherTranspiler (Microsoft) | Cypher to T-SQL: ANTLR parser, logical plan, renderer, schema via provider interface. | – | archived 2024-07 | Prior art for a schema-aware Cypher AST in .NET. |

### Lessons from source and issue trackers

- Neo4jClient's `CypherWhereExpressionVisitor` supports exactly: `And/AndAlso/Or/OrElse/Not`, six
  comparison operators, `StartsWith/Contains/EndsWith`, member access on the lambda parameter, and
  closure capture as parameters. Everything else throws `NotSupportedException`. It refuses bare
  boolean member access and unary `!` because Cypher's null semantics make them ambiguous. Lambda
  parameter names flow directly into Cypher identifiers, so renaming a lambda parameter silently
  breaks the query. Tatham Oddie's 2013 rationale: mapping Cypher onto `IQueryProvider` "doesn't make
  a lot of sense" because clause ordering and RETURN semantics do not map onto the LINQ operator
  chain; offer lambdas only for predicates and projections.
- ExRam.Gremlinq's docs enumerate the recognized `Where` patterns and state that anything else throws
  at runtime. Its history is largely spent growing that whitelist. A whitelist with a good error and
  a documented list beats a general translator.
- Blueprint41 gets compile-time checked patterns from a generated meta-model at the cost of a build
  step and a bespoke schema DSL.
- EF Core 3.0 removed re-linq because it blocked fixing client evaluation; the pipeline became pure
  `ExpressionVisitor` passes. Marten 7 (2024) also removed re-linq, with mixed results. Marten's own
  notes call a LINQ provider "an unholy nightmare" to support.

## 2. Query-provider infrastructure in 2026

| Library | Status |
|---|---|
| Remotion.Linq (re-linq) | 2.2.0 (2018-02). netstandard1.0, loads on .NET 10. Last substantive commit January 2018. Still consumed by NHibernate 5.x and Realm. EF Core dropped it in 3.0, Marten in 7.0. Its `QueryModel` (from/where/select) fits SQL and fits Cypher's MATCH-first shape poorly. Nobody consulted recommends it for a new provider. |
| IQToolkit | No 2.0 package; repository pushed 2024-09. A readable reference (`QueryProvider` base, partial evaluator, language/mapping/policy layering), not a dependency. |
| LinqKit 1.3.11, NeinLinq 7.4.0 | Consumer-side rewriting; useful to users of a provider. NeinLinq's `RewriteQueryProvider` is a clean small example of wrapping a provider with async support. |
| EF Core query pipeline | Not reusable; the visitor-pass architecture (preprocess, normalize, translate, shape) is what everyone copies. |
| Marten | Own parser to a `Statement` model; `IMethodCallParser` extension point; the honest write-up of maintenance cost. |
| MongoDB.Driver LINQ3 | Expression pre-processing, per-method translators, an explicit target AST (`AstStage/AstExpression/AstFilter`), AST optimizers, then rendering. v3.0 dropped `IMongoQueryable` for plain `IQueryable` and throws `ExpressionNotSupportedException` instead of client-evaluating. The cleanest modern reference. |
| RavenDB | `IRavenQueryable<T> : IQueryable<T>` plus Raven-specific async terminals. |

Ranking as references: MongoDB LINQ3 (explicit target AST, optimizer, renderer), EF Core's front-end
passes (partial evaluation, parameter extraction), Marten (pluggability and cost), IQToolkit (small
provider base). re-linq is legacy.

## 3. Async LINQ in .NET 10

- .NET 10 ships `System.Linq.AsyncEnumerable` in-box (namespace `System.Linq`, static class
  `AsyncEnumerable`) with the full `Enumerable` operator set plus `*Async` terminals. The community
  `System.Linq.Async` is now legacy; referencing both on .NET 10 causes `CS0121` ambiguities.
- `IAsyncQueryable<T>` is not in the BCL. dotnet/runtime #77698 is open in the Future milestone;
  #117232 was closed as not planned (July 2025). Ix's `System.Linq.Async.Queryable` 7.0.1 still
  exists and depends on `System.Linq.AsyncEnumerable`; everyone shipping a provider (EF, Mongo,
  Marten, Raven) chose plain `IQueryable<T>` plus their own async terminals.
- EF Core's pattern: `IAsyncQueryProvider : IQueryProvider { TResult ExecuteAsync<TResult>(Expression, CancellationToken); }`
  where `TResult` is the raw type (`Task<int>` or `IAsyncEnumerable<T>`); `EntityQueryable<T>`
  implements `IOrderedQueryable<T>` and `IAsyncEnumerable<T>`; `ToListAsync` does `await foreach`
  over `AsAsyncEnumerable()`. EF removed `IAsyncEnumerable<T>` from `DbSet<T>` in 6.0 (issue #24041)
  because implementing both on one type makes `Where/Select` ambiguous when async LINQ operators are
  in scope, which on .NET 10 they always are.

Recommended pattern for a third-party provider: root type `IOrderedQueryable<T>` that does not also
implement `IAsyncEnumerable<T>`; a provider implementing `IQueryProvider` plus an own
`ExecuteAsync<TResult>(Expression, CancellationToken)`; terminal extensions `AsAsyncEnumerable()`,
`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`, `AnyAsync` on `IQueryable<T>` that dispatch to
the provider; sync `GetEnumerator()` runs the query synchronously (cheap here, the engine is
in-process). Skip Ix `IAsyncQueryable`.

## 4. Expressing MATCH patterns in C#

| Design | Exemplar | Power | Cost |
|---|---|---|---|
| Fluent step traversal | Gremlin.NET, Gremlinq | High for traversals | Unnatural for Cypher's declarative multi-variable patterns |
| Typed node/rel classes plus generated meta-model | Blueprint41, neo4j-cypher-dsl codegen | Compile-time checked patterns | A generation step |
| Lambda-typed patterns `.Match<A,R,B>((a,r,b) => ...)` | No shipping .NET exemplar | Compact | Must invent a pattern language with no prior art |
| String pattern plus typed WHERE/RETURN lambdas | Neo4jClient | Full Cypher power where it matters | Lambda parameter names coupled to pattern variables by convention |
| Immutable Cypher AST builder | neo4j-cypher-dsl (Java, 2026-09), `@neo4j/cypher-builder` (JS), Cymple (Python) | Only valid Cypher; composable; parameterized | Verbose; no LINQ; the proven substrate under Spring Data Neo4j |

Best trade: a small immutable Cypher AST plus renderer as the substrate, typed node/relationship
descriptors on top producing pattern objects, and lambdas restricted to WHERE predicates, RETURN
projections and ORDER BY keys.

## 5. Kuzu/LadybugDB specifics relevant to a LINQ layer

- Schema is mandatory (`CREATE NODE TABLE`, `CREATE REL TABLE`); each node and relationship has
  exactly one table. A LINQ layer can map `T` to a table one-to-one, which Neo4j providers never could.
- Patterns are standard: `(a:Person)-[:Knows]->(b)`; `{prop: value}` maps inside patterns; no `WHERE`
  inside a pattern; label predicates use `label(n)`. Variable-length relationships need an upper
  bound (default 30); `SHORTEST`, `ALL SHORTEST`, `WSHORTEST`.
- Subqueries: only `EXISTS { MATCH ... }` and `COUNT { MATCH ... }`. `CASE`, list comprehension,
  `WITH`, `UNWIND`, `UNION`, `ORDER BY/SKIP/LIMIT`, implicit grouping in `RETURN` are present.
  Missing versus Neo4j: `FOREACH`, `REMOVE` (use `SET x = NULL`), `SET n += {}`, `labels()` (use
  `label()`), `elementId()` (use `id()`); list functions are `list_*`; casts are `cast(x, 'TYPE')`.
- Parameters are `$name`; the C API binds by name.
- The catalog is Cypher-only: `CALL show_tables()`, `CALL table_info('T')`, `CALL show_connection('R')`,
  `CALL show_indexes()`. The C API has no catalog functions. Runtime introspection is three `CALL`s.
- No community ORM or object-graph mapper exists for Kuzu or Ladybug in Python, Rust or Java.

## 6. Source generators

Prior art: neo4j-cypher-dsl-codegen (annotation processor emitting a static meta-model `Person_.NAME`),
Blueprint41's T4, EF Core precompiled queries (interceptor-based, restricted to static query shapes,
still emitting trimming warnings), Dapper.AOT (interceptor API churn across SDK versions), Marten #4405
(a source-generator proof of concept for compiled queries, no outcome yet). Nobody has shipped
source-generated Cypher from expression trees. A generator is the right tool for reflection-free row
materialization and a static meta-model; it is the wrong tool for translating arbitrary `Where`
lambdas.

## Recommendation

1. Layered: Cypher AST plus renderer; typed schema descriptors validated at runtime against the
   catalog; an `IQueryable<T>` front end with a deliberately narrow, whitelisted translator; async via
   an own `ExecuteAsync<TResult>` plus `AsAsyncEnumerable()`/`ToListAsync()` extensions; a source
   generator reserved for row materialization and the meta-model. Refuse untranslatable expressions
   at translation time naming the sub-expression; never client-evaluate.
2. A Neo4jClient-style fluent DSL over the same AST is a cheaper v1 that can grow into (1).
3. Blueprint41-style full code generation gives the strongest compile-time guarantees and the
   weakest adoption story.

Not recommended: re-linq; Ix `IAsyncQueryable`; source-generated Cypher from expression trees;
implementing `IAsyncEnumerable<T>` on the queryable root.

## Sources

Neo4jClient repository, wiki and `CypherWhereExpressionVisitor.cs`, issues #104 and #174, the 2013
"Linq To Cypher" thread; Blueprint41 repository and wiki; upstream discussion #544; `Knaackee/ladybug.net`;
`LadybugDB/ladybug-dotnet`; ExRam.Gremlinq docs ("Recognized Where Expressions") and issues; Gremlin.NET;
EF Core Cosmos provider docs; linq-to-gremlin; Stardust.Paradox; GraphEngine LIKQ; CypherNet;
Neo4j.Berries.OGM; ApacheAGE and Konnektr.Npgsql.Age; Memgraph C# docs; openCypherTranspiler; re-linq
repository and commit log; NHibernate 5.6.1 dependencies; IQToolkit; LinqKit; NeinLinq; CODE Magazine on
EF Core 3.0; Jeremy Miller on Marten 7 LINQ; Marten `Linq/notes.md` and extension docs; Marten #4405;
MongoDB LINQ3 upgrade guide; RavenDB #14788; Microsoft Learn on the .NET 10 `AsyncEnumerable` change;
endjin on Ix v7; dotnet/reactive discussion #2294 and the `System.Linq.Async.Queryable` sources;
dotnet/runtime #77698 and #117232; dotnet/efcore #24041, `IAsyncQueryProvider.cs`, `EntityQueryable.cs`,
`EntityFrameworkQueryableExtensions.cs`; the AsyncQueryableAdapterPrototype; neo4j/cypher-dsl and
neo4j/cypher-builder; Cymple; cypher-query-builder; docs.ladybugdb.com (Cypher differences, MATCH,
subqueries, CALL, expressions); kuzudb discussion #4300; Kuzu prepared-statement docs; Dapper.AOT
#148; dotnet/efcore #25009 and #34446; EF Core Native AOT docs; npgsql #2940; Gedaq; sqlite-sg.
