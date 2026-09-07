namespace LadybugDb.Client.Cypher;

/// <summary>
/// Rendered Cypher text plus the parameters it references, ready for
/// <see cref="LadybugConnection.QueryAsync(string, object, CancellationToken)"/> or
/// <see cref="LadybugConnection.Select{T}"/>.
/// </summary>
/// <param name="Cypher">The statement text. Every value in it is a <c>$name</c> placeholder.</param>
/// <param name="Parameters">
/// The values those placeholders name, keyed without the <c>$</c>. A
/// <c>Dictionary&lt;string, object?&gt;</c> underneath, so the parameter-object overloads read it
/// directly with no reflection.
/// </param>
public sealed record CypherText(string Cypher, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>The direction of a relationship pattern.</summary>
public enum Direction
{
    /// <summary><c>(a)-[:R]-&gt;(b)</c>.</summary>
    Outgoing,
    /// <summary><c>(a)&lt;-[:R]-(b)</c>.</summary>
    Incoming,
    /// <summary><c>(a)-[:R]-(b)</c>.</summary>
    Undirected,
}

/// <summary>A binary operator, rendered infix.</summary>
public enum BinaryOperator
{
    /// <summary><c>=</c>.</summary>
    Equal,
    /// <summary><c>&lt;&gt;</c>.</summary>
    NotEqual,
    /// <summary><c>&lt;</c>.</summary>
    LessThan,
    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,
    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,
    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,
    /// <summary><c>AND</c>.</summary>
    And,
    /// <summary><c>OR</c>.</summary>
    Or,
    /// <summary><c>STARTS WITH</c>.</summary>
    StartsWith,
    /// <summary><c>ENDS WITH</c>.</summary>
    EndsWith,
    /// <summary><c>CONTAINS</c>.</summary>
    Contains,
    /// <summary><c>+</c>.</summary>
    Add,
    /// <summary><c>-</c>.</summary>
    Subtract,
    /// <summary><c>*</c>.</summary>
    Multiply,
    /// <summary><c>/</c>.</summary>
    Divide,
    /// <summary><c>%</c>.</summary>
    Modulo,
}

/// <summary>A unary operator, rendered prefix.</summary>
public enum UnaryOperator
{
    /// <summary><c>NOT</c>.</summary>
    Not,
    /// <summary>Arithmetic negation, <c>-</c>.</summary>
    Negate,
}

/// <summary>
/// An expression in the Cypher subset this client renders. Immutable; the fluent members build a
/// larger expression from this one without changing it.
/// </summary>
/// <remarks>
/// Nothing here knows about CLR types or reflection: a <see cref="LiteralExpr"/> carries an opaque
/// value that the renderer turns into a <c>$p&lt;n&gt;</c> parameter, and the typed <c>Bind</c>
/// overloads decide at execution time how it crosses into the engine.
/// </remarks>
public abstract record Expr
{
    /// <summary><c>this = other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Eq(Expr other) => new BinaryExpr(BinaryOperator.Equal, this, other);

    /// <summary><c>this &lt;&gt; other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Ne(Expr other) => new BinaryExpr(BinaryOperator.NotEqual, this, other);

    /// <summary><c>this &lt; other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Lt(Expr other) => new BinaryExpr(BinaryOperator.LessThan, this, other);

    /// <summary><c>this &lt;= other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Le(Expr other) => new BinaryExpr(BinaryOperator.LessThanOrEqual, this, other);

    /// <summary><c>this &gt; other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Gt(Expr other) => new BinaryExpr(BinaryOperator.GreaterThan, this, other);

    /// <summary><c>this &gt;= other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Ge(Expr other) => new BinaryExpr(BinaryOperator.GreaterThanOrEqual, this, other);

    /// <summary><c>this AND other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr And(Expr other) => new BinaryExpr(BinaryOperator.And, this, other);

    /// <summary><c>this OR other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Or(Expr other) => new BinaryExpr(BinaryOperator.Or, this, other);

    /// <summary><c>this STARTS WITH other</c>.</summary>
    /// <param name="other">The prefix.</param>
    public Expr StartsWith(Expr other) => new BinaryExpr(BinaryOperator.StartsWith, this, other);

    /// <summary><c>this ENDS WITH other</c>.</summary>
    /// <param name="other">The suffix.</param>
    public Expr EndsWith(Expr other) => new BinaryExpr(BinaryOperator.EndsWith, this, other);

    /// <summary><c>this CONTAINS other</c>.</summary>
    /// <param name="other">The substring.</param>
    public Expr Contains(Expr other) => new BinaryExpr(BinaryOperator.Contains, this, other);

    /// <summary><c>this + other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Plus(Expr other) => new BinaryExpr(BinaryOperator.Add, this, other);

    /// <summary><c>this - other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Minus(Expr other) => new BinaryExpr(BinaryOperator.Subtract, this, other);

    /// <summary><c>this * other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Times(Expr other) => new BinaryExpr(BinaryOperator.Multiply, this, other);

    /// <summary><c>this / other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr DividedBy(Expr other) => new BinaryExpr(BinaryOperator.Divide, this, other);

    /// <summary><c>this % other</c>.</summary>
    /// <param name="other">The right operand.</param>
    public Expr Modulo(Expr other) => new BinaryExpr(BinaryOperator.Modulo, this, other);

    /// <summary><c>NOT this</c>.</summary>
    public Expr Not() => new UnaryExpr(UnaryOperator.Not, this);

    /// <summary><c>-this</c>.</summary>
    public Expr Negate() => new UnaryExpr(UnaryOperator.Negate, this);

    /// <summary><c>this IS NULL</c>.</summary>
    public Expr IsNull() => new IsNullExpr(this, Negated: false);

    /// <summary><c>this IS NOT NULL</c>.</summary>
    public Expr IsNotNull() => new IsNullExpr(this, Negated: true);

    /// <summary>
    /// <c>this IN [item, ...]</c>, one element per <paramref name="items"/> entry - so a list of
    /// literals renders as a list of parameters, since the client has no list-typed bind.
    /// </summary>
    /// <param name="items">The list elements; empty renders <c>IN []</c>, which matches nothing.</param>
    public Expr In(params Expr[] items) => new InExpr(this, new ListExpr(items));

    /// <summary><c>this IN list</c>, for a list-valued expression such as a caller-bound list parameter.</summary>
    /// <param name="list">The list expression.</param>
    public Expr InList(Expr list) => new InExpr(this, list);

    /// <summary><c>this AS alias</c>, for a <c>RETURN</c> or <c>WITH</c> item.</summary>
    /// <param name="alias">The column name.</param>
    public AliasExpr As(string alias) => new(this, alias);

    /// <summary>An ascending <c>ORDER BY</c> key on this expression.</summary>
    public SortKey Asc() => new(this, Descending: false);

    /// <summary>A descending <c>ORDER BY</c> key on this expression.</summary>
    public SortKey Desc() => new(this, Descending: true);
}

/// <summary>A property of a pattern variable: <c>alias.name</c>.</summary>
/// <param name="Alias">The pattern variable.</param>
/// <param name="Name">The property (column) name.</param>
public sealed record PropertyExpr(string Alias, string Name) : Expr;

/// <summary>A bare pattern variable or projected alias: <c>o</c>, or <c>N</c> after <c>WITH count(*) AS N</c>.</summary>
/// <param name="Alias">The variable name.</param>
public sealed record VariableExpr(string Alias) : Expr;

/// <summary>A parameter the caller names: <c>$name</c>, bound to <paramref name="Value"/>.</summary>
/// <param name="Name">The parameter name, without <c>$</c>. Must be a plain identifier and not of the reserved form <c>p&lt;digits&gt;</c>.</param>
/// <param name="Value">The value to bind.</param>
public sealed record ParameterExpr(string Name, object? Value) : Expr;

/// <summary>
/// A value. Never interpolated: the renderer allocates a <c>$p&lt;n&gt;</c> parameter for it, in
/// encounter order, and returns the value in <see cref="CypherText.Parameters"/>. A
/// <see langword="null"/> renders as the <c>NULL</c> keyword instead, since there is nothing to bind.
/// </summary>
/// <param name="Value">The value.</param>
public sealed record LiteralExpr(object? Value) : Expr;

/// <summary>A list literal: <c>[a, b, c]</c>.</summary>
/// <param name="Items">The elements.</param>
public sealed record ListExpr(IReadOnlyList<Expr> Items) : Expr;

/// <summary>An infix operator applied to two operands.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Left">The left operand.</param>
/// <param name="Right">The right operand.</param>
public sealed record BinaryExpr(BinaryOperator Operator, Expr Left, Expr Right) : Expr;

/// <summary>A prefix operator applied to one operand.</summary>
/// <param name="Operator">The operator.</param>
/// <param name="Operand">The operand.</param>
public sealed record UnaryExpr(UnaryOperator Operator, Expr Operand) : Expr;

/// <summary><c>operand IS NULL</c> or, negated, <c>operand IS NOT NULL</c>.</summary>
/// <param name="Operand">The tested expression.</param>
/// <param name="Negated"><see langword="true"/> for <c>IS NOT NULL</c>.</param>
public sealed record IsNullExpr(Expr Operand, bool Negated) : Expr;

/// <summary><c>operand IN list</c>.</summary>
/// <param name="Operand">The tested expression.</param>
/// <param name="List">The list, usually a <see cref="ListExpr"/>.</param>
public sealed record InExpr(Expr Operand, Expr List) : Expr;

/// <summary>A function call: <c>name(arg, ...)</c>. The name is rendered verbatim, so it must be a plain identifier.</summary>
/// <param name="Name">The function name, as the engine spells it (<c>label</c>, <c>size</c>, <c>list_contains</c>, ...).</param>
/// <param name="Arguments">The arguments.</param>
public sealed record FunctionExpr(string Name, IReadOnlyList<Expr> Arguments) : Expr;

/// <summary><c>expression AS alias</c>.</summary>
/// <param name="Expression">The projected expression.</param>
/// <param name="Alias">The column name.</param>
public sealed record AliasExpr(Expr Expression, string Alias) : Expr;

/// <summary><c>count(*)</c>, <c>count(operand)</c>, or <c>count(DISTINCT operand)</c>.</summary>
/// <param name="Distinct">Whether to count distinct values; requires an <paramref name="Operand"/>.</param>
/// <param name="Operand">The counted expression, or <see langword="null"/> for <c>count(*)</c>.</param>
public sealed record CountExpr(bool Distinct, Expr? Operand) : Expr;

/// <summary><c>EXISTS { subquery }</c>.</summary>
/// <param name="Subquery">The subquery, usually a <c>MATCH</c> with an optional <c>WHERE</c>.</param>
public sealed record ExistsExpr(Query Subquery) : Expr;

/// <summary><c>COUNT { subquery }</c>.</summary>
/// <param name="Subquery">The subquery, usually a <c>MATCH</c> with an optional <c>WHERE</c>.</param>
public sealed record CountSubqueryExpr(Query Subquery) : Expr;

/// <summary>One <c>WHEN condition THEN result</c> arm of a <see cref="CaseExpr"/>.</summary>
/// <param name="When">The condition.</param>
/// <param name="Then">The result when it holds.</param>
public sealed record CaseBranch(Expr When, Expr Then);

/// <summary><c>CASE WHEN ... THEN ... [ELSE ...] END</c>.</summary>
/// <param name="Branches">The arms, in order.</param>
/// <param name="Else">The result when no arm matches, or <see langword="null"/> to omit <c>ELSE</c> (yielding <c>NULL</c>).</param>
public sealed record CaseExpr(IReadOnlyList<CaseBranch> Branches, Expr? Else) : Expr;

/// <summary>One <c>ORDER BY</c> key.</summary>
/// <param name="Expression">The sorted expression.</param>
/// <param name="Descending"><see langword="true"/> for <c>DESC</c>.</param>
public sealed record SortKey(Expr Expression, bool Descending);

/// <summary>A property assignment inside a pattern (<c>{name: value}</c>) or a <c>SET</c>.</summary>
/// <param name="Name">The property name.</param>
/// <param name="Value">The value expression.</param>
public sealed record PropertyValue(string Name, Expr Value);

/// <summary>A node pattern: <c>(alias:Label {name: value, ...})</c>, every part optional.</summary>
/// <param name="Alias">The variable to bind the node to, or <see langword="null"/> for an anonymous node.</param>
/// <param name="Label">The node table, or <see langword="null"/> to refer to a variable already bound.</param>
/// <param name="Properties">Inline property constraints.</param>
public sealed record NodePattern(string? Alias, string? Label, IReadOnlyList<PropertyValue> Properties)
{
    /// <summary>A node pattern with no inline properties.</summary>
    /// <param name="alias">The variable to bind the node to, or <see langword="null"/>.</param>
    /// <param name="label">The node table, or <see langword="null"/>.</param>
    public NodePattern(string? alias, string? label) : this(alias, label, []) { }

    /// <summary><c>alias.name</c> for this node's variable.</summary>
    /// <param name="name">The property name.</param>
    /// <exception cref="InvalidOperationException">This pattern has no alias to refer to.</exception>
    public PropertyExpr Prop(string name) =>
        new(Alias ?? throw new InvalidOperationException(
            $"Node pattern (:{Label}) has no alias, so its property '{name}' cannot be referenced. " +
            "Give the pattern an alias."), name);

    /// <summary>This node's variable as an expression, for <c>RETURN o</c> or <c>label(o)</c>.</summary>
    /// <exception cref="InvalidOperationException">This pattern has no alias to refer to.</exception>
    public VariableExpr Variable() =>
        new(Alias ?? throw new InvalidOperationException(
            $"Node pattern (:{Label}) has no alias, so it cannot be referenced as a variable. " +
            "Give the pattern an alias."));

    /// <summary>This pattern with an inline property constraint added: <c>(o:Object {name: value})</c>.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value expression, typically a <see cref="LiteralExpr"/>.</param>
    public NodePattern With(string name, Expr value) =>
        this with { Properties = [.. Properties, new PropertyValue(name, value)] };

    /// <summary>This node as a single-node path.</summary>
    public PatternPath ToPatternPath() => new(this, []);

    /// <summary>This node followed by an outgoing relationship to <paramref name="target"/>: <c>(this)-[:type]-&gt;(target)</c>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="target">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">The lower hop bound of a variable-length relationship; defaults to 1 when only <paramref name="maxHops"/> is given.</param>
    /// <param name="maxHops">The upper hop bound. Required whenever the relationship is variable-length: the engine has a default, but this DSL refuses to rely on it.</param>
    public PatternPath RelTo(string? type, NodePattern target, string? alias = null, int? minHops = null, int? maxHops = null) =>
        ToPatternPath().RelTo(type, target, alias, minHops, maxHops);

    /// <summary>This node followed by an incoming relationship from <paramref name="source"/>: <c>(this)&lt;-[:type]-(source)</c>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="source">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">See <see cref="RelTo"/>.</param>
    /// <param name="maxHops">See <see cref="RelTo"/>.</param>
    public PatternPath RelFrom(string? type, NodePattern source, string? alias = null, int? minHops = null, int? maxHops = null) =>
        ToPatternPath().RelFrom(type, source, alias, minHops, maxHops);

    /// <summary>This node followed by an undirected relationship to <paramref name="other"/>: <c>(this)-[:type]-(other)</c>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="other">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">See <see cref="RelTo"/>.</param>
    /// <param name="maxHops">See <see cref="RelTo"/>.</param>
    public PatternPath Rel(string? type, NodePattern other, string? alias = null, int? minHops = null, int? maxHops = null) =>
        ToPatternPath().Rel(type, other, alias, minHops, maxHops);

    /// <summary>A node is a one-node path, so it can be passed wherever a path is expected.</summary>
    /// <param name="node">The node.</param>
    public static implicit operator PatternPath(NodePattern node) => node.ToPatternPath();
}

/// <summary>A relationship pattern between two nodes: <c>-[alias:Type*min..max {name: value}]-&gt;</c>.</summary>
/// <param name="Alias">The variable to bind the relationship to, or <see langword="null"/>.</param>
/// <param name="Type">The relationship table, or <see langword="null"/> for any.</param>
/// <param name="Direction">Which way the arrow points, read left to right.</param>
/// <param name="MinHops">The lower hop bound, or <see langword="null"/> for a single hop (or 1 if <paramref name="MaxHops"/> is set).</param>
/// <param name="MaxHops">The upper hop bound. The renderer refuses a variable-length pattern without one.</param>
/// <param name="Properties">Inline property constraints.</param>
public sealed record RelPattern(
    string? Alias,
    string? Type,
    Direction Direction,
    int? MinHops,
    int? MaxHops,
    IReadOnlyList<PropertyValue> Properties)
{
    /// <summary>A relationship pattern with no inline properties.</summary>
    /// <param name="alias">The variable to bind the relationship to, or <see langword="null"/>.</param>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="direction">Which way the arrow points.</param>
    /// <param name="minHops">The lower hop bound, or <see langword="null"/>.</param>
    /// <param name="maxHops">The upper hop bound, or <see langword="null"/> for a single hop.</param>
    public RelPattern(string? alias, string? type, Direction direction, int? minHops = null, int? maxHops = null)
        : this(alias, type, direction, minHops, maxHops, []) { }

    /// <summary>This pattern with an inline property constraint added.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value expression.</param>
    public RelPattern With(string name, Expr value) =>
        this with { Properties = [.. Properties, new PropertyValue(name, value)] };
}

/// <summary>One hop of a <see cref="PatternPath"/>: a relationship and the node it reaches.</summary>
/// <param name="Rel">The relationship.</param>
/// <param name="Node">The node at its far end.</param>
public sealed record PatternStep(RelPattern Rel, NodePattern Node);

/// <summary>A linear pattern: a start node followed by zero or more relationship-node steps.</summary>
/// <param name="Start">The first node.</param>
/// <param name="Steps">Each subsequent hop, in order.</param>
public sealed record PatternPath(NodePattern Start, IReadOnlyList<PatternStep> Steps)
{
    /// <summary>A one-node path.</summary>
    /// <param name="node">The node.</param>
    public static PatternPath FromNodePattern(NodePattern node) => new(node, []);

    /// <summary>This path extended by an outgoing relationship to <paramref name="target"/>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="target">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">See <see cref="NodePattern.RelTo"/>.</param>
    /// <param name="maxHops">See <see cref="NodePattern.RelTo"/>.</param>
    public PatternPath RelTo(string? type, NodePattern target, string? alias = null, int? minHops = null, int? maxHops = null) =>
        Extend(new RelPattern(alias, type, Direction.Outgoing, minHops, maxHops), target);

    /// <summary>This path extended by an incoming relationship from <paramref name="source"/>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="source">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">See <see cref="NodePattern.RelTo"/>.</param>
    /// <param name="maxHops">See <see cref="NodePattern.RelTo"/>.</param>
    public PatternPath RelFrom(string? type, NodePattern source, string? alias = null, int? minHops = null, int? maxHops = null) =>
        Extend(new RelPattern(alias, type, Direction.Incoming, minHops, maxHops), source);

    /// <summary>This path extended by an undirected relationship to <paramref name="other"/>.</summary>
    /// <param name="type">The relationship table, or <see langword="null"/> for any.</param>
    /// <param name="other">The node at the far end.</param>
    /// <param name="alias">A variable to bind the relationship to.</param>
    /// <param name="minHops">See <see cref="NodePattern.RelTo"/>.</param>
    /// <param name="maxHops">See <see cref="NodePattern.RelTo"/>.</param>
    public PatternPath Rel(string? type, NodePattern other, string? alias = null, int? minHops = null, int? maxHops = null) =>
        Extend(new RelPattern(alias, type, Direction.Undirected, minHops, maxHops), other);

    /// <summary>This path extended by one explicit step.</summary>
    /// <param name="rel">The relationship.</param>
    /// <param name="node">The node at its far end.</param>
    public PatternPath Extend(RelPattern rel, NodePattern node) =>
        this with { Steps = [.. Steps, new PatternStep(rel, node)] };
}

/// <summary>One clause of a <see cref="Query"/>.</summary>
public abstract record Clause;

/// <summary><c>MATCH path, ...</c> or <c>OPTIONAL MATCH path, ...</c>.</summary>
/// <param name="Paths">The patterns, rendered comma-separated.</param>
/// <param name="Optional"><see langword="true"/> for <c>OPTIONAL MATCH</c>.</param>
public sealed record MatchClause(IReadOnlyList<PatternPath> Paths, bool Optional) : Clause;

/// <summary><c>WHERE predicate</c>.</summary>
/// <param name="Predicate">The predicate.</param>
public sealed record WhereClause(Expr Predicate) : Clause;

/// <summary><c>WITH [DISTINCT] item, ...</c>.</summary>
/// <param name="Items">The projected items, usually <see cref="AliasExpr"/>s.</param>
/// <param name="Distinct"><see langword="true"/> for <c>WITH DISTINCT</c>.</param>
public sealed record WithClause(IReadOnlyList<Expr> Items, bool Distinct) : Clause;

/// <summary><c>RETURN [DISTINCT] item, ...</c>.</summary>
/// <param name="Items">The projected items, usually <see cref="AliasExpr"/>s.</param>
/// <param name="Distinct"><see langword="true"/> for <c>RETURN DISTINCT</c>.</param>
public sealed record ReturnClause(IReadOnlyList<Expr> Items, bool Distinct) : Clause;

/// <summary><c>ORDER BY key, ...</c>.</summary>
/// <param name="Keys">The sort keys, in precedence order.</param>
public sealed record OrderByClause(IReadOnlyList<SortKey> Keys) : Clause;

/// <summary><c>SKIP count</c>.</summary>
/// <param name="Count">The row count, usually a <see cref="LiteralExpr"/> so it binds as a parameter (the engine accepts <c>SKIP $p</c>).</param>
public sealed record SkipClause(Expr Count) : Clause;

/// <summary><c>LIMIT count</c>.</summary>
/// <param name="Count">The row count, usually a <see cref="LiteralExpr"/> so it binds as a parameter (the engine accepts <c>LIMIT $p</c>).</param>
public sealed record LimitClause(Expr Count) : Clause;

/// <summary><c>UNWIND list AS alias</c>.</summary>
/// <param name="List">The list expression.</param>
/// <param name="Alias">The variable each element is bound to.</param>
public sealed record UnwindClause(Expr List, string Alias) : Clause;

/// <summary>A whole statement: its clauses in order.</summary>
/// <param name="Clauses">The clauses, rendered in order separated by single spaces.</param>
public sealed record Query(IReadOnlyList<Clause> Clauses)
{
    /// <summary>Renders this query to text plus its parameters. See <see cref="CypherRenderer"/> for the rules.</summary>
    /// <exception cref="InvalidOperationException">The query is empty, or a clause is not renderable (a variable-length relationship without an upper bound, a parameter name that is reserved or bound twice to different values).</exception>
    public CypherText Render() => CypherRenderer.Render(this);
}
