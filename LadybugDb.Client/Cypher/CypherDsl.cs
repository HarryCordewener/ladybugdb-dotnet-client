namespace LadybugDb.Client.Cypher;

/// <summary>
/// Entry points for building a <see cref="Query"/> without writing Cypher text: pattern and
/// expression factories, and the clause-by-clause <see cref="QueryBuilder"/>.
/// </summary>
/// <remarks>
/// <code>
/// var o = CypherDsl.Node("Object", "o");
/// var r = CypherDsl.Node("Object", "r");
/// var text = CypherDsl.Match(o.RelTo("Located", r))
///     .Where(r.Prop("dbref").Eq(CypherDsl.Param("room", 42L)))
///     .Return(o.Prop("dbref").As("dbref"), o.Prop("name").As("name"))
///     .OrderBy(o.Prop("name").Asc())
///     .Limit(50)
///     .Render();
/// // text.Cypher: MATCH (o:Object)-[:Located]->(r:Object) WHERE r.dbref = $room
/// //              RETURN o.dbref AS dbref, o.name AS name ORDER BY o.name LIMIT $p0
/// // text.Parameters: { room = 42L, p0 = 50L }
/// </code>
/// The AST records under this namespace can also be constructed directly; these factories only
/// save keystrokes. Named <c>CypherDsl</c> rather than <c>Cypher</c> because a type named like its
/// own namespace is unreachable by simple name from any namespace under <c>LadybugDb.Client</c>
/// (name lookup finds the <c>LadybugDb.Client.Cypher</c> namespace before any <c>using</c>) - which
/// includes this client's own LINQ translator and its tests.
/// </remarks>
public static class CypherDsl
{
    // ------------------------------------------------------------------------------- patterns

    /// <summary>A node pattern <c>(alias:label)</c>.</summary>
    /// <param name="label">The node table.</param>
    /// <param name="alias">The variable to bind the node to, or <see langword="null"/> for an anonymous node.</param>
    public static NodePattern Node(string label, string? alias = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return new NodePattern(alias, label);
    }

    /// <summary>A reference to an already-bound node variable, <c>(alias)</c> - for the inside of an <c>EXISTS</c> or a later <c>MATCH</c>.</summary>
    /// <param name="alias">The variable.</param>
    public static NodePattern NodeRef(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        return new NodePattern(alias, label: null);
    }

    // ---------------------------------------------------------------------------- expressions

    /// <summary><c>alias.name</c>.</summary>
    /// <param name="alias">The pattern variable.</param>
    /// <param name="name">The property name.</param>
    public static PropertyExpr Prop(string alias, string name) => new(alias, name);

    /// <summary>A bare variable: a pattern alias, or a name projected by <c>WITH</c>.</summary>
    /// <param name="alias">The variable.</param>
    public static VariableExpr Variable(string alias) => new(alias);

    /// <summary>A caller-named parameter <c>$name</c> bound to <paramref name="value"/>.</summary>
    /// <param name="name">The name, without <c>$</c>. Not of the reserved form <c>p&lt;digits&gt;</c>.</param>
    /// <param name="value">The value to bind.</param>
    public static ParameterExpr Param(string name, object? value) => new(name, value);

    /// <summary>A value, rendered as a generated <c>$p&lt;n&gt;</c> parameter (or as <c>NULL</c> for <see langword="null"/>).</summary>
    /// <param name="value">The value.</param>
    public static LiteralExpr Literal(object? value) => new(value);

    /// <summary>A list literal <c>[item, ...]</c>.</summary>
    /// <param name="items">The elements.</param>
    public static ListExpr List(params Expr[] items) => new(items);

    /// <summary>A function call <c>name(args)</c> in the engine's own spelling: <c>label</c>, <c>size</c>, <c>upper</c>, <c>list_contains</c>, <c>cast</c>, ...</summary>
    /// <param name="name">The function name.</param>
    /// <param name="arguments">The arguments.</param>
    public static FunctionExpr Func(string name, params Expr[] arguments) => new(name, arguments);

    /// <summary><c>count(*)</c>.</summary>
    public static CountExpr CountAll() => new(Distinct: false, Operand: null);

    /// <summary><c>count(operand)</c> or <c>count(DISTINCT operand)</c>.</summary>
    /// <param name="operand">The counted expression.</param>
    /// <param name="distinct">Whether to count distinct values only.</param>
    public static CountExpr Count(Expr operand, bool distinct = false) => new(distinct, operand);

    /// <summary><c>EXISTS { subquery }</c>.</summary>
    /// <param name="subquery">The subquery.</param>
    public static ExistsExpr Exists(Query subquery) => new(subquery);

    /// <summary><c>EXISTS { subquery }</c>.</summary>
    /// <param name="subquery">The subquery, built so far.</param>
    public static ExistsExpr Exists(QueryBuilder subquery)
    {
        ArgumentNullException.ThrowIfNull(subquery);
        return new ExistsExpr(subquery.Build());
    }

    /// <summary><c>COUNT { subquery }</c>.</summary>
    /// <param name="subquery">The subquery.</param>
    public static CountSubqueryExpr CountSubquery(Query subquery) => new(subquery);

    /// <summary><c>COUNT { subquery }</c>.</summary>
    /// <param name="subquery">The subquery, built so far.</param>
    public static CountSubqueryExpr CountSubquery(QueryBuilder subquery)
    {
        ArgumentNullException.ThrowIfNull(subquery);
        return new CountSubqueryExpr(subquery.Build());
    }

    /// <summary>One <c>WHEN when THEN then</c> arm for <see cref="Case"/>.</summary>
    /// <param name="when">The condition.</param>
    /// <param name="then">The result when it holds.</param>
    public static CaseBranch When(Expr when, Expr then) => new(when, then);

    /// <summary><c>CASE WHEN ... THEN ... [ELSE else] END</c>.</summary>
    /// <param name="branches">The arms, in order.</param>
    /// <param name="else">The <c>ELSE</c> result, or <see langword="null"/> to omit it.</param>
    public static CaseExpr Case(IEnumerable<CaseBranch> branches, Expr? @else = null)
    {
        ArgumentNullException.ThrowIfNull(branches);
        return new CaseExpr([.. branches], @else);
    }

    // -------------------------------------------------------------------------------- clauses

    /// <summary>Starts a query with <c>MATCH path, ...</c>.</summary>
    /// <param name="paths">The patterns. A <see cref="NodePattern"/> converts implicitly.</param>
    public static QueryBuilder Match(params PatternPath[] paths) => QueryBuilder.Empty.Match(paths);

    /// <summary>Starts a query with <c>OPTIONAL MATCH path, ...</c>.</summary>
    /// <param name="paths">The patterns.</param>
    public static QueryBuilder OptionalMatch(params PatternPath[] paths) => QueryBuilder.Empty.OptionalMatch(paths);

    /// <summary>Starts a query with <c>UNWIND list AS alias</c>.</summary>
    /// <param name="list">The list expression.</param>
    /// <param name="alias">The element variable.</param>
    public static QueryBuilder Unwind(Expr list, string alias) => QueryBuilder.Empty.Unwind(list, alias);

    /// <summary>Starts a statement with <c>CREATE path, ...</c>.</summary>
    /// <param name="paths">The patterns to create.</param>
    public static QueryBuilder Create(params PatternPath[] paths) => QueryBuilder.Empty.Create(paths);

    /// <summary>Starts a statement with <c>MERGE path</c>.</summary>
    /// <param name="path">The pattern to match or create.</param>
    public static QueryBuilder Merge(PatternPath path) => QueryBuilder.Empty.Merge(path);
}

/// <summary>
/// Accumulates the clauses of a <see cref="Query"/> in order. Immutable: every method returns a new
/// builder, so a prefix can be shared between queries.
/// </summary>
public sealed class QueryBuilder
{
    private readonly Clause[] _clauses;

    private QueryBuilder(Clause[] clauses) => _clauses = clauses;

    /// <summary>A builder with no clauses. <see cref="CypherDsl.Match"/> and friends start from here.</summary>
    public static QueryBuilder Empty { get; } = new([]);

    private QueryBuilder Append(Clause clause) => new([.. _clauses, clause]);

    /// <summary>Appends <c>MATCH path, ...</c>.</summary>
    /// <param name="paths">The patterns.</param>
    public QueryBuilder Match(params PatternPath[] paths) => Append(new MatchClause(paths, Optional: false));

    /// <summary>Appends <c>OPTIONAL MATCH path, ...</c>.</summary>
    /// <param name="paths">The patterns.</param>
    public QueryBuilder OptionalMatch(params PatternPath[] paths) => Append(new MatchClause(paths, Optional: true));

    /// <summary>
    /// Appends <c>WHERE predicate</c> - or, when the previous clause is already a <c>WHERE</c>,
    /// <c>AND</c>s the predicate into it, since two adjacent <c>WHERE</c>s are not valid Cypher and
    /// a caller adding conditions one at a time means their conjunction.
    /// </summary>
    /// <param name="predicate">The predicate.</param>
    public QueryBuilder Where(Expr predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        if (_clauses.Length > 0 && _clauses[^1] is WhereClause previous)
        {
            return new QueryBuilder([.. _clauses[..^1], new WhereClause(previous.Predicate.And(predicate))]);
        }

        return Append(new WhereClause(predicate));
    }

    /// <summary>Appends <c>WITH item, ...</c>.</summary>
    /// <param name="items">The projected items.</param>
    public QueryBuilder With(params Expr[] items) => Append(new WithClause(items, Distinct: false));

    /// <summary>Appends <c>WITH DISTINCT item, ...</c>.</summary>
    /// <param name="items">The projected items.</param>
    public QueryBuilder WithDistinct(params Expr[] items) => Append(new WithClause(items, Distinct: true));

    /// <summary>Appends <c>RETURN item, ...</c>.</summary>
    /// <param name="items">The projected items.</param>
    public QueryBuilder Return(params Expr[] items) => Append(new ReturnClause(items, Distinct: false));

    /// <summary>Appends <c>RETURN DISTINCT item, ...</c>.</summary>
    /// <param name="items">The projected items.</param>
    public QueryBuilder ReturnDistinct(params Expr[] items) => Append(new ReturnClause(items, Distinct: true));

    /// <summary>Appends <c>ORDER BY key, ...</c>.</summary>
    /// <param name="keys">The sort keys, from <see cref="Expr.Asc"/> and <see cref="Expr.Desc"/>.</param>
    public QueryBuilder OrderBy(params SortKey[] keys) => Append(new OrderByClause(keys));

    /// <summary>Appends <c>SKIP $p</c>, binding <paramref name="count"/> as a parameter.</summary>
    /// <param name="count">The rows to skip.</param>
    public QueryBuilder Skip(long count) => Append(new SkipClause(new LiteralExpr(count)));

    /// <summary>Appends <c>SKIP expression</c>.</summary>
    /// <param name="count">The row-count expression.</param>
    public QueryBuilder Skip(Expr count) => Append(new SkipClause(count));

    /// <summary>Appends <c>LIMIT $p</c>, binding <paramref name="count"/> as a parameter.</summary>
    /// <param name="count">The maximum rows.</param>
    public QueryBuilder Limit(long count) => Append(new LimitClause(new LiteralExpr(count)));

    /// <summary>Appends <c>LIMIT expression</c>.</summary>
    /// <param name="count">The row-count expression.</param>
    public QueryBuilder Limit(Expr count) => Append(new LimitClause(count));

    /// <summary>Appends <c>UNWIND list AS alias</c>.</summary>
    /// <param name="list">The list expression.</param>
    /// <param name="alias">The element variable.</param>
    public QueryBuilder Unwind(Expr list, string alias) => Append(new UnwindClause(list, alias));

    /// <summary>Appends <c>CREATE path, ...</c>. Property values in the patterns render as parameters.</summary>
    /// <param name="paths">The patterns to create. A <see cref="NodePattern"/> converts implicitly.</param>
    public QueryBuilder Create(params PatternPath[] paths) => Append(new CreateClause(paths));

    /// <summary>Appends <c>MERGE path</c>.</summary>
    /// <param name="path">The pattern to match or create.</param>
    public QueryBuilder Merge(PatternPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Append(new MergeClause(path));
    }

    /// <summary>Appends <c>SET alias.name = value</c>. See <see cref="Set(PropertyExpr, Expr)"/>.</summary>
    /// <param name="alias">The pattern variable.</param>
    /// <param name="name">The property to assign.</param>
    /// <param name="value">The value; <c>CypherDsl.Literal(null)</c> renders <c>= NULL</c>, this engine's spelling of <c>REMOVE</c>.</param>
    public QueryBuilder Set(string alias, string name, Expr value) => Set(new PropertyExpr(alias, name), value);

    /// <summary>
    /// Appends <c>SET target = value</c> - or, when the previous clause is already a <c>SET</c>, adds
    /// the assignment to it, so a caller assigning properties one at a time produces one
    /// comma-separated <c>SET</c> rather than a run of them. The engine has no <c>SET n += {map}</c>,
    /// so this is the shape a multi-property update takes.
    /// </summary>
    /// <param name="target">The property to assign.</param>
    /// <param name="value">The value; <c>CypherDsl.Literal(null)</c> renders <c>= NULL</c>, this engine's spelling of <c>REMOVE</c>.</param>
    public QueryBuilder Set(PropertyExpr target, Expr value)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(value);
        var item = new SetItem(target, value);
        if (_clauses.Length > 0 && _clauses[^1] is SetClause previous)
        {
            return new QueryBuilder([.. _clauses[..^1], new SetClause([.. previous.Assignments, item])]);
        }

        return Append(new SetClause([item]));
    }

    /// <summary>Appends <c>DELETE alias, ...</c>.</summary>
    /// <param name="aliases">The node or relationship variables to delete.</param>
    public QueryBuilder Delete(params string[] aliases) => Append(new DeleteClause(aliases, Detach: false));

    /// <summary>Appends <c>DETACH DELETE alias, ...</c>, deleting each node's relationships with it.</summary>
    /// <param name="aliases">The node variables to delete.</param>
    public QueryBuilder DetachDelete(params string[] aliases) => Append(new DeleteClause(aliases, Detach: true));

    /// <summary>The clauses so far, as an immutable <see cref="Query"/>.</summary>
    public Query Build() => new(_clauses);

    /// <summary><see cref="Build"/> then <see cref="Query.Render"/>.</summary>
    public CypherText Render() => Build().Render();
}
