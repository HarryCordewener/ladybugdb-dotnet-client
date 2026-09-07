# LINQ query surface — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement Phases A and B of the LINQ design: a Cypher AST with a renderer, `[Node]`/`[Rel]` schema descriptors validated against the live catalog, and an `IQueryable<T>` front end with a whitelisted translator and async terminals, all on top of the existing `LadybugConnection` API.

**Architecture:** Three namespaces inside `LadybugDb.Client`: `LadybugDb.Client.Cypher` (immutable AST + renderer + parameter collector; knows nothing about CLR types), `LadybugDb.Client.Schema` (attributes, descriptors, catalog validation, DDL), `LadybugDb.Client.Linq` (queryable, provider, translator, graph steps, terminals). Materialization reuses `RowMapper` through `LadybugConnection.Select<T>`. Nothing is evaluated on the client; every untranslatable expression throws at translation with the offending sub-expression.

**Tech Stack:** .NET 10, `System.Linq.Expressions`, `System.Linq.AsyncEnumerable` (in-box), TUnit.

**Spec:** `docs/superpowers/specs/2026-09-06-linq-query-surface-design.md`. Research: `docs/research/2026-09-06-linq-over-graph-databases.md` (§5 for the engine's Cypher dialect).

## Global Constraints

- The queryable root implements `IOrderedQueryable<T>` and never `IAsyncEnumerable<T>` (EF Core #24041; the .NET 10 in-box `AsyncEnumerable` operators make the ambiguity worse).
- No client-side evaluation, ever. Unsupported expressions throw `NotSupportedException` whose message contains `expression.ToString()` of the offending node and names the string `Match` escape hatch.
- Every literal that is not a schema identifier renders as a `$p<n>` parameter bound through the typed `Bind` overloads. The renderer never interpolates values.
- Dialect: `label(n)` not `labels(n)`, `id(n)` not `elementId(n)`, `list_contains`, `cast(x, 'TYPE')`, no `SET n += {}`, no `REMOVE`, variable-length relationships need an upper bound (default 30 in the engine; the DSL requires an explicit one).
- Bare boolean members in predicates are rejected (Cypher three-valued logic); `x.Prop == null` renders `IS NULL`.
- Case-insensitive column-to-parameter matching and the dotted-suffix rule are `RowMapper`'s; the translator aliases every projected column to the target parameter name, so mapping never depends on the suffix rule.
- `TreatWarningsAsErrors` with XML docs on every public member; the reflective parts carry `[RequiresUnreferencedCode]` like `Select<T>` does.
- Every rendered clause is executed once against the real engine in the integration suite (the dialect table must not rot).
- Commit after every task.

## File structure

```
LadybugDb.Client/Cypher/
  CypherAst.cs            sealed records: NodePattern, RelPattern, PatternPath, Expr (+ subclasses), Clause (+ subclasses), Query
  CypherRenderer.cs       visitor producing CypherText { string Cypher; IReadOnlyDictionary<string, object?> Parameters }
  Cypher.cs               static entry points and the fluent builder (Cypher.Node(...), Match(...).Where(...).Return(...))
  Identifier.cs           validation / backtick quoting
LadybugDb.Client/Schema/
  NodeAttribute.cs, RelAttribute.cs, KeyAttribute.cs, ColumnAttribute.cs
  LadybugSchema.cs        descriptors built by reflection; ValidateAsync; CreateTablesAsync; DDL renderer
  CatalogReader.cs        CALL show_tables()/table_info()/show_connection() into records
LadybugDb.Client/Linq/
  LadybugQueryable.cs     LadybugQueryable<T> : IOrderedQueryable<T>
  LadybugQueryProvider.cs IQueryProvider + ILadybugAsyncQueryProvider
  QueryTranslator.cs      expression tree -> Cypher.Query (whitelist)
  GraphSteps.cs           Out/In/OutWithRel/In... extension methods (marker methods the translator recognizes)
  LadybugQueryableExtensions.cs  AsAsyncEnumerable, ToListAsync, FirstOrDefaultAsync, SingleAsync, CountAsync, AnyAsync
  Projection.cs           projection shape analysis (member, anonymous type, record ctor, tuple, whole node)
LadybugDb.Client/LadybugConnection.Linq.cs   partial: Nodes<T>(), Match<T>(pattern, parameters)
LadybugDb.Client.Tests/Cypher/*.cs, Schema/*.cs, Linq/*.cs            renderer golden tests, translator tests (no engine)
LadybugDb.Client.IntegrationTests/Linq/*.cs                          every shape against the real engine
```

---

### Task A1: Cypher AST and renderer for read queries

**Files:** create `Cypher/CypherAst.cs`, `Cypher/CypherRenderer.cs`, `Cypher/Identifier.cs`, `Cypher/Cypher.cs`; test `LadybugDb.Client.Tests/Cypher/RendererTests.cs`.

**Produces:**
```csharp
namespace LadybugDb.Client.Cypher;
public sealed record CypherText(string Cypher, IReadOnlyDictionary<string, object?> Parameters);
public abstract record Expr;                       // Prop(alias, name), Param(name), Literal(object?) [rendered as a parameter], Binary(op, l, r), Unary(op, e), IsNull(e, negated), In(e, Param), Func(name, args), Alias(expr, name), Count(distinct?, expr?), Exists(subquery), Case(...)
public sealed record NodePattern(string? Alias, string? Label, IReadOnlyList<(string Prop, Expr Value)> Props);
public sealed record RelPattern(string? Alias, string? Type, Direction Direction, int? MinHops, int? MaxHops);
public sealed record PatternPath(NodePattern Start, IReadOnlyList<(RelPattern Rel, NodePattern Node)> Steps);
public abstract record Clause;                     // Match(paths, optional), Where(expr), With(items, distinct), Return(items, distinct), OrderBy(keys), Skip(Expr), Limit(Expr), Unwind(expr, alias)
public sealed record Query(IReadOnlyList<Clause> Clauses) { public CypherText Render(); }
public static class Cypher { public static NodePattern Node(string label, string? alias = null); public static Expr Param(string name, object? value); public static Expr Prop(string alias, string name); ... }
```
The renderer allocates parameter names `$p0, $p1, ...` for `Literal` and keeps caller-supplied `Param` names; it validates identifiers with `Identifier.Render` (letters, digits, `_`, not starting with a digit; otherwise backticked, backticks doubled).

- [ ] **Step 1: Golden tests** (one per clause; first three shown, write the rest the same way for WITH, ORDER BY DESC, SKIP/LIMIT, DISTINCT, IS NULL / IS NOT NULL, IN, STARTS WITH/CONTAINS/ENDS WITH, `label()`, variable-length rel `[:R*1..3]`, backticked identifier, `EXISTS { MATCH ... }`, `COUNT { ... }`, incoming direction, undirected):

```csharp
[Test]
public async Task MatchWhereReturn_RendersWithParameters()
{
    var o = Cypher.Node("Object", "o");
    var q = Cypher.Match(o).Where(Cypher.Prop("o", "dbref").Eq(Cypher.Literal(42L)))
        .Return(Cypher.Prop("o", "name").As("Name")).Build();
    var text = q.Render();
    await Assert.That(text.Cypher).IsEqualTo("MATCH (o:Object) WHERE o.dbref = $p0 RETURN o.name AS Name");
    await Assert.That(text.Parameters["p0"]).IsEqualTo(42L);
}

[Test]
public async Task Traversal_RendersDirectionAndHops()
{
    var q = Cypher.Match(Cypher.Node("Object", "a").RelTo("Located", Cypher.Node("Object", "b"), minHops: 1, maxHops: 3))
        .Return(Cypher.Prop("b", "dbref").As("Dbref")).Build();
    await Assert.That(q.Render().Cypher).IsEqualTo("MATCH (a:Object)-[:Located*1..3]->(b:Object) RETURN b.dbref AS Dbref");
}

[Test]
public async Task Identifier_WithSpace_IsBackticked()
{
    await Assert.That(Identifier.Render("my table")).IsEqualTo("`my table`");
    await Assert.That(Identifier.Render("a`b")).IsEqualTo("`a``b`");
}
```
- [ ] **Step 2: Run; expected: compile failure.**
- [ ] **Step 3: Implement** the records and the renderer (a single `StringBuilder` walk; one `Render(Expr)` switch; parameters collected in a `Dictionary<string, object?>` in encounter order).
- [ ] **Step 4: Run; expected: PASS.**
- [ ] **Step 5: Integration test `LadybugDb.Client.IntegrationTests/Linq/RendererEngineTests.cs`:** every golden string executed with `conn.QueryAsync(text.Cypher, text.Parameters)` against a database with tables `Object(dbref INT64, name STRING, PRIMARY KEY(dbref))` and `Located(FROM Object TO Object)`; assert no exception and the expected row counts for MATCH/WHERE/traversal.
- [ ] **Step 6: Commit** `feat(cypher): AST and renderer for read queries`.

### Task A2: Write clauses and DDL

**Files:** extend `CypherAst.cs`/`CypherRenderer.cs` with `Create(paths)`, `Set(alias, prop, expr)` (one `SET` per assignment), `Delete(aliases, detach)`, `Merge(path)`; `Schema/Ddl.cs` with `CreateNodeTable(name, columns, keys)`, `CreateRelTable(name, from, to, columns)`; tests in `RendererWriteTests.cs`.

- [ ] Golden tests: `CREATE (o:Object {dbref: $p0, name: $p1})`, `MATCH (o:Object) WHERE o.dbref = $p0 SET o.name = $p1`, `MATCH (o:Object) WHERE o.dbref = $p0 DETACH DELETE o`, `CREATE NODE TABLE Object(dbref INT64, name STRING, PRIMARY KEY(dbref))`, `CREATE REL TABLE Located(FROM Object TO Object)`.
- [ ] Implement; run; integration-execute each against the engine (the DDL ones on a fresh database); commit `feat(cypher): write clauses and DDL rendering`.

### Task A3: Schema descriptors and catalog validation

**Files:** create `Schema/NodeAttribute.cs` (`[Node(string Table)]`), `Schema/RelAttribute.cs` (`[Rel(string Table, Type From, Type To)]`), `Schema/KeyAttribute.cs`, `Schema/ColumnAttribute.cs` (`[Column(string Name)]` override), `Schema/LadybugSchema.cs`, `Schema/CatalogReader.cs`; tests `LadybugDb.Client.Tests/Schema/SchemaTests.cs`, `LadybugDb.Client.IntegrationTests/Linq/SchemaValidationTests.cs`.

**Produces:**
```csharp
public sealed class LadybugSchema
{
    public static LadybugSchema For(params Type[] types);
    public static LadybugSchema FromAssembly(Assembly assembly);
    public NodeDescriptor Node(Type t); public RelDescriptor Rel(Type t);
    public IReadOnlyList<NodeDescriptor> Nodes { get; } public IReadOnlyList<RelDescriptor> Rels { get; }
    public ValueTask ValidateAsync(LadybugConnection connection, CancellationToken ct = default);   // throws SchemaMismatchException listing every mismatch
    public ValueTask CreateTablesAsync(LadybugConnection connection, CancellationToken ct = default);
}
public sealed record NodeDescriptor(Type ClrType, string Table, IReadOnlyList<PropertyDescriptor> Properties, PropertyDescriptor Key);
public sealed record RelDescriptor(Type ClrType, string Table, NodeDescriptor From, NodeDescriptor To, IReadOnlyList<PropertyDescriptor> Properties);
public sealed record PropertyDescriptor(string Column, string ClrName, Type ClrType, LadybugType EngineType);
public sealed class SchemaMismatchException : LadybugException { public IReadOnlyList<string> Mismatches { get; } }
```
CLR-to-engine type map (for DDL and validation): `long→INT64, int→INT32, short→INT16, sbyte→INT8, ulong→UINT64, uint→UINT32, ushort→UINT16, byte→UINT8, Int128→INT128, double→DOUBLE, float→FLOAT, bool→BOOL, string→STRING, byte[]→BLOB, Guid→UUID, DateOnly→DATE, DateTime→TIMESTAMP, DateTimeOffset→TIMESTAMP_TZ, TimeSpan→INTERVAL, BigDecimal→DECIMAL(38,10)`, `Nullable<T>` → same as `T`. Validation accepts an engine column that `RowMapper`'s widening rule can read into the CLR type (INT32 column into a `long` property is fine; the reverse is a mismatch).

- [ ] Unit tests: descriptors from the MUSH records in the spec (`Obj`, `Attr`, `Has`, `Located`); a record without `[Key]` throws; a `[Rel]` whose `From` is not a `[Node]` type throws.
- [ ] Integration: `CreateTablesAsync` on a fresh database then `ValidateAsync` passes; drop a column (recreate the table without it) and `ValidateAsync` throws `SchemaMismatchException` whose `Mismatches` names the table and column; a CLR `int` against an INT64 column is reported as a mismatch, a CLR `long` against INT32 is not.
- [ ] `CatalogReader`: `CALL show_tables() RETURN *` (columns: id, name, type, database name, comment), `CALL table_info('T') RETURN *` (property id, name, type, default expression, primary key), `CALL show_connection('R') RETURN *` (source table name, destination table name, source table primary key, destination table primary key). Verify the exact column names against the engine in the integration test before relying on them.
- [ ] Commit `feat(schema): [Node]/[Rel] descriptors, catalog validation, DDL`.

### Task B1: Queryable root, provider, and `Nodes<T>()` with `Where`/`Select`/`OrderBy`/`Skip`/`Take`

**Files:** create `Linq/LadybugQueryable.cs`, `Linq/LadybugQueryProvider.cs`, `Linq/QueryTranslator.cs`, `Linq/Projection.cs`, `Linq/LadybugQueryableExtensions.cs`, `LadybugConnection.Linq.cs`; tests `LadybugDb.Client.Tests/Linq/TranslatorTests.cs`, `LadybugDb.Client.IntegrationTests/Linq/QueryableTests.cs`.

**Produces:**
```csharp
public partial class LadybugConnection { public IQueryable<T> Nodes<T>(LadybugSchema? schema = null); }   // schema defaults to LadybugSchema.For(typeof(T)) resolved lazily and cached per connection
public interface ILadybugAsyncQueryProvider : IQueryProvider { TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken); }
public static class LadybugQueryableExtensions
{
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> source, CancellationToken ct = default);
    public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken ct = default);
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken ct = default);
    public static Task<T> FirstAsync<T>(...); SingleAsync; SingleOrDefaultAsync; CountAsync (Task<long>); AnyAsync (Task<bool>);
}
internal static class QueryTranslator { internal static TranslatedQuery Translate(Expression expression, LadybugSchema schema); }   // TranslatedQuery(CypherText Text, Type ResultType, ProjectionShape Shape)
```
Translation rules (the whole whitelist): `Where` → `WHERE` with `==, !=, <, <=, >, >=, &&, ||, !`, `== null`/`!= null`, `string.StartsWith/EndsWith/Contains` (ordinal, non-static, constant or closure argument), `collection.Contains(x.Prop)` (closure collection → `IN $p`), `x.S.Length` → `size()`, `ToUpper/ToLower` → `upper/lower`; constants and closures → parameters (closures evaluated once at translation via `Expression.Lambda(...).Compile()` on the captured sub-tree; cache compiled accessors per expression shape is NOT required in this task). `Select` → projection: member access, `new { ... }`, record constructor, `ValueTuple`, identity (whole node → `RETURN o` and materialization via `LadybugNode` into the `[Node]` record by property name). `OrderBy/OrderByDescending/ThenBy/ThenByDescending`, `Skip`, `Take`, `Distinct`, `Count/LongCount`, `Any`, `First/FirstOrDefault/Single/SingleOrDefault` (as `LIMIT 1`/`LIMIT 2`). Everything else: `NotSupportedException`.

Execution: `GetEnumerator()` renders and runs synchronously through `connection.Select<TResult>(text.Cypher, text.Parameters)` materialized with `ToBlockingEnumerable()` is NOT acceptable (it spins a thread); instead add an internal synchronous `LadybugConnection.SelectSync<T>(cypher, parameters)` that enumerates the result synchronously (every operation completes synchronously today). `ExecuteAsync<IAsyncEnumerable<T>>` returns `connection.Select<T>(...)`; scalar terminals go through `ExecuteAsync<Task<long>>` etc. Whole-node projection uses a `RowMapper`-independent path: read `LadybugNode`, construct `T` through its constructor by property name (case-insensitive), NULL properties → default.

- [ ] **Step 1: Translator tests (no engine)**, one per rule, asserting exact text and parameters, e.g.

```csharp
[Test]
public async Task WhereEqualsClosure_RendersParameter()
{
    var dbref = 42L;
    var q = Translate(NodesOf<Obj>().Where(o => o.Dbref == dbref).Select(o => o.Name));
    await Assert.That(q.Text.Cypher).IsEqualTo("MATCH (o:Obj) WHERE o.dbref = $p0 RETURN o.name AS Name");
    await Assert.That(q.Text.Parameters["p0"]).IsEqualTo(42L);
}

[Test]
public async Task BareBooleanMember_IsRefused()
{
    var ex = Assert.Throws<NotSupportedException>(() => Translate(NodesOf<Flagged>().Where(f => f.IsRoom)));
    await Assert.That(ex!.Message).Contains("f.IsRoom");
    await Assert.That(ex.Message).Contains("== true");
}
```
(`NodesOf<T>()` is a test helper returning a `LadybugQueryable<T>` bound to a null connection; translation never touches the connection.) Alias naming: the root variable is the lambda parameter name of the first `Where`/`Select` encountered, else `n`; property names render as the descriptor's column names.

- [ ] **Step 2: Run; compile failure. Step 3: implement. Step 4: PASS.**
- [ ] **Step 5: Integration tests** on the MUSH schema (`Obj`, `Attr`, `Has`, `Located`, 20 objects, 10 attributes each, loaded with `LadybugSchema.CreateTablesAsync` + `CREATE`): every shape above executed both through `ToListAsync()` and through sync `foreach`, plus `AsAsyncEnumerable()` early-`break` disposal asserted with `LadybugQueryResult.LiveCount` (as `SelectDisposalTests` does).
- [ ] **Step 6: Commit** `feat(linq): Nodes<T>() with Where/Select/OrderBy/Skip/Take and async terminals`.

### Task B2: Graph steps

**Files:** create `Linq/GraphSteps.cs`; extend `QueryTranslator.cs`; tests in `TranslatorTests.cs` and `Linq/GraphStepTests.cs` (integration).

**Produces:** pair-carrying steps (the spike's answer to the spec's open question; the named-variable alternative is the string `Match<T>` escape hatch, Task B3):
```csharp
public static IQueryable<(TSource Source, TTarget Target)> Out<TSource, TRel, TTarget>(this IQueryable<TSource> source);
public static IQueryable<(TSource Source, TTarget Target)> In<TSource, TRel, TTarget>(this IQueryable<TSource> source);   // (target)-[:TRel]->(source)
public static IQueryable<(TSource Source, TRel Rel, TTarget Target)> OutWithRel<TSource, TRel, TTarget>(this IQueryable<TSource> source);
public static IQueryable<(TSource Source, TTarget Target)> Out<TSource, TRel, TTarget>(this IQueryable<TSource> source, int minHops, int maxHops);
public static IQueryable<TSource> WhereExists<TSource, TRel, TTarget>(this IQueryable<TSource> source, Expression<Func<TTarget, bool>> predicate);
```
`TRel`'s `[Rel]` descriptor must connect `TSource`'s table to `TTarget`'s (validated at translation; mismatch throws `InvalidOperationException` naming both tables). After a step the current variable is the tuple; `Where(p => p.Target.Name == "x")` and `Select(p => new { p.Source.Dbref, p.Target.Name })` translate member access through the tuple items to the right alias. Aliases: `n0`, `n1`, ... per pattern node, `r0`, ... per rel.

- [ ] Translator tests: `Nodes<Obj>().Where(o => o.Dbref == d).Out<Obj, Has, Attr>().Where(p => p.Target.Aname == n).Select(p => p.Target.Aval)` → `MATCH (n0:Obj)-[:Has]->(n1:Attr) WHERE n0.dbref = $p0 AND n1.aname = $p1 RETURN n1.aval AS Aval`; `In` renders the arrow reversed; hops render `*1..3`; `WhereExists` renders `WHERE EXISTS { MATCH (n0)-[:Located]->(x:Obj) WHERE x.dbref = $p0 }`; a mismatched rel throws.
- [ ] Integration: room contents, object-with-attributes, two hops, exists; each asserted against known data.
- [ ] Commit `feat(linq): typed graph steps`.

### Task B3: String pattern escape hatch and aggregates

- [ ] `LadybugConnection.Match<T>(string pattern, object? parameters = null, string variable = "n")`: `MATCH <pattern>` verbatim with `T` bound to `variable`; the rest of the LINQ chain applies (`Where` on `T`, `Select`, etc.). Translator test: `Match<Obj>("(r:Object {dbref: $room})-[:Exit]->(n:Object)", new { room = 5L }).Select(e => e.Name)` → `MATCH (r:Object {dbref: $room})-[:Exit]->(n:Object) RETURN n.name AS Name` with `room` bound.
- [ ] `GroupBy(key).Select(g => new { g.Key, N = g.Count() })` → `RETURN n.loc AS Key, count(*) AS N` (implicit grouping; only `Count()`, `Sum(sel)`, `Min(sel)`, `Max(sel)`, `Average(sel)` inside the projection; anything else throws).
- [ ] Integration tests for both; commit `feat(linq): Match<T> escape hatch and aggregate projections`.

### Task B4: Documentation and samples

- [ ] `docs/USAGE.md`: a "LINQ" chapter (entry points, the whitelist table verbatim from the spec, graph steps, terminals, the async boundary, refusal messages, the escape hatch) with every sample executed by an integration test (`LadybugDb.Client.IntegrationTests/Linq/UsageSamplesTests.cs`).
- [ ] README: a LINQ example in "Current features"; move "LINQ" out of "Future features" if listed.
- [ ] Commit `docs: LINQ chapter with executed samples`.
