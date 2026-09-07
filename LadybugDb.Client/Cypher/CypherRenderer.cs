using System.Globalization;
using System.Text;

namespace LadybugDb.Client.Cypher;

/// <summary>
/// Renders a <see cref="Query"/> to Cypher text in this engine's dialect, collecting parameters as
/// it goes. One <see cref="StringBuilder"/> walk; one <c>switch</c> per node kind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Values are never interpolated.</b> Every <see cref="LiteralExpr"/> becomes a
/// <c>$p&lt;n&gt;</c> parameter, numbered in encounter order, and its value goes into
/// <see cref="CypherText.Parameters"/>. A <see cref="ParameterExpr"/> keeps the caller's name. The
/// generated shape <c>p&lt;digits&gt;</c> is therefore reserved: a caller-named parameter of that
/// form is refused rather than risking a collision with a literal rendered later in the same walk.
/// A caller name bound twice is fine when both bindings carry an equal value, and refused when they
/// do not - silently keeping one would run the query with a value the caller did not write.
/// </para>
/// <para>
/// <b>Parentheses follow precedence, not the tree.</b> A binary operand is parenthesized only when
/// its operator binds looser than its parent's, or equally and non-associatively (<c>a - (b - c)</c>,
/// <c>(a = b) = c</c>). <c>a AND b AND c</c> stays flat; <c>(a OR b) AND c</c> gets the parentheses
/// it needs; <c>NOT</c> always parenthesizes a binary operand for legibility. The precedence table is
/// Cypher's: <c>OR</c> below <c>AND</c>, below <c>NOT</c>, below comparison, below additive, below
/// multiplicative, below unary minus.
/// </para>
/// <para>
/// <b>Dialect.</b> Verified against the engine rather than assumed from openCypher: <c>SKIP $p</c>
/// and <c>LIMIT $p</c> accept parameters; <c>x IN [$p0, $p1]</c> accepts a list of parameters (and
/// <c>IN []</c> matches nothing); <c>EXISTS { MATCH ... }</c> and <c>COUNT { MATCH ... }</c> are the
/// subquery forms; a variable-length relationship without an upper bound runs with the engine's
/// default of 30, which is why the renderer insists on an explicit one.
/// </para>
/// </remarks>
internal static class CypherRenderer
{
    /// <summary>Renders <paramref name="query"/>.</summary>
    /// <exception cref="InvalidOperationException">See <see cref="Query.Render"/>.</exception>
    internal static CypherText Render(Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Clauses.Count == 0)
            throw new InvalidOperationException("A query needs at least one clause to render.");

        var walker = new Walker();
        walker.AppendClauses(query);
        return new CypherText(walker.Text.ToString(), walker.Parameters);
    }

    /// <summary>The precedence of every comparison and string operator - see <see cref="Precedence"/>.</summary>
    private const int ComparisonPrecedence = 4;

    /// <summary>Binding tightness of a binary operator; higher binds tighter. (3 is <c>NOT</c>, 7 is unary minus.)</summary>
    private static int Precedence(BinaryOperator op) => op switch
    {
        BinaryOperator.Or => 1,
        BinaryOperator.And => 2,
        BinaryOperator.Add or BinaryOperator.Subtract => 5,
        BinaryOperator.Multiply or BinaryOperator.Divide or BinaryOperator.Modulo => 6,
        _ => ComparisonPrecedence,
    };

    /// <summary>Whether <c>a op b op c</c> may be rendered without parentheses on either side.</summary>
    private static bool IsAssociative(BinaryOperator op) => op is
        BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Add or BinaryOperator.Multiply;

    private static string Token(BinaryOperator op) => op switch
    {
        BinaryOperator.Equal => "=",
        BinaryOperator.NotEqual => "<>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.And => "AND",
        BinaryOperator.Or => "OR",
        BinaryOperator.StartsWith => "STARTS WITH",
        BinaryOperator.EndsWith => "ENDS WITH",
        BinaryOperator.Contains => "CONTAINS",
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        _ => throw new InvalidOperationException($"Unknown binary operator {op}."),
    };

    /// <summary>The mutable state of one rendering pass: the text so far and the parameters seen.</summary>
    private sealed class Walker
    {
        private int _nextLiteral;

        internal StringBuilder Text { get; } = new();

        internal Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

        // ------------------------------------------------------------------------------ clauses

        internal void AppendClauses(Query query)
        {
            for (var i = 0; i < query.Clauses.Count; i++)
            {
                if (i > 0) Text.Append(' ');
                AppendClause(query.Clauses[i]);
            }
        }

        private void AppendClause(Clause clause)
        {
            switch (clause)
            {
                case MatchClause m:
                    Text.Append(m.Optional ? "OPTIONAL MATCH " : "MATCH ");
                    AppendList(m.Paths, AppendPath);
                    break;
                case WhereClause w:
                    Text.Append("WHERE ");
                    AppendExpr(w.Predicate);
                    break;
                case WithClause w:
                    Text.Append(w.Distinct ? "WITH DISTINCT " : "WITH ");
                    AppendList(w.Items, AppendExpr);
                    break;
                case ReturnClause r:
                    Text.Append(r.Distinct ? "RETURN DISTINCT " : "RETURN ");
                    AppendList(r.Items, AppendExpr);
                    break;
                case OrderByClause o:
                    Text.Append("ORDER BY ");
                    AppendList(o.Keys, AppendSortKey);
                    break;
                case SkipClause s:
                    Text.Append("SKIP ");
                    AppendExpr(s.Count);
                    break;
                case LimitClause l:
                    Text.Append("LIMIT ");
                    AppendExpr(l.Count);
                    break;
                case UnwindClause u:
                    Text.Append("UNWIND ");
                    AppendExpr(u.List);
                    Text.Append(" AS ").Append(Identifier.Render(u.Alias));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown clause {clause.GetType().Name}.");
            }
        }

        private void AppendSortKey(SortKey key)
        {
            AppendExpr(key.Expression);
            if (key.Descending) Text.Append(" DESC");
        }

        private void AppendList<T>(IReadOnlyList<T> items, Action<T> append)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) Text.Append(", ");
                append(items[i]);
            }
        }

        // ----------------------------------------------------------------------------- patterns

        private void AppendPath(PatternPath path)
        {
            AppendNode(path.Start);
            foreach (var step in path.Steps)
            {
                AppendRel(step.Rel);
                AppendNode(step.Node);
            }
        }

        private void AppendNode(NodePattern node)
        {
            Text.Append('(');
            if (node.Alias is not null) Text.Append(Identifier.Render(node.Alias));
            if (node.Label is not null) Text.Append(':').Append(Identifier.Render(node.Label));
            if (node.Properties.Count > 0)
            {
                if (node.Alias is not null || node.Label is not null) Text.Append(' ');
                AppendProperties(node.Properties);
            }

            Text.Append(')');
        }

        private void AppendRel(RelPattern rel)
        {
            Text.Append(rel.Direction == Direction.Incoming ? "<-[" : "-[");
            if (rel.Alias is not null) Text.Append(Identifier.Render(rel.Alias));
            if (rel.Type is not null) Text.Append(':').Append(Identifier.Render(rel.Type));
            AppendHops(rel);
            if (rel.Properties.Count > 0)
            {
                Text.Append(' ');
                AppendProperties(rel.Properties);
            }

            Text.Append(rel.Direction == Direction.Outgoing ? "]->" : "]-");
        }

        private void AppendHops(RelPattern rel)
        {
            if (rel.MinHops is null && rel.MaxHops is null) return;

            // The engine runs `*1..` with a default upper bound of 30 (measured: `(a)-[:R*1..]->(b)`
            // is accepted). A bound the query does not state is one a reader cannot see, so the DSL
            // requires it to be written down.
            if (rel.MaxHops is not { } max)
            {
                throw new InvalidOperationException(
                    $"Variable-length relationship [:{rel.Type}] has a lower hop bound but no upper bound. " +
                    "The engine would silently apply its default (30); state the upper bound explicitly.");
            }

            var min = rel.MinHops ?? 1;
            if (min < 0 || max < min)
            {
                throw new InvalidOperationException(
                    $"Variable-length relationship [:{rel.Type}] has hop bounds *{min}..{max}, which are not " +
                    "a valid range: the lower bound must be at least 0 and no greater than the upper bound.");
            }

            Text.Append('*')
                .Append(min.ToString(CultureInfo.InvariantCulture))
                .Append("..")
                .Append(max.ToString(CultureInfo.InvariantCulture));
        }

        private void AppendProperties(IReadOnlyList<PropertyValue> properties)
        {
            Text.Append('{');
            for (var i = 0; i < properties.Count; i++)
            {
                if (i > 0) Text.Append(", ");
                Text.Append(Identifier.Render(properties[i].Name)).Append(": ");
                AppendExpr(properties[i].Value);
            }

            Text.Append('}');
        }

        // -------------------------------------------------------------------------- expressions

        private void AppendExpr(Expr expr)
        {
            switch (expr)
            {
                case PropertyExpr p:
                    Text.Append(Identifier.Render(p.Alias)).Append('.').Append(Identifier.Render(p.Name));
                    break;
                case VariableExpr v:
                    Text.Append(Identifier.Render(v.Alias));
                    break;
                case ParameterExpr p:
                    Text.Append('$').Append(BindNamed(p.Name, p.Value));
                    break;
                case LiteralExpr l:
                    if (l.Value is null) Text.Append("NULL");
                    else Text.Append('$').Append(BindLiteral(l.Value));
                    break;
                case ListExpr l:
                    Text.Append('[');
                    AppendList(l.Items, AppendExpr);
                    Text.Append(']');
                    break;
                case BinaryExpr b:
                    AppendOperand(b.Left, b.Operator, rightSide: false);
                    Text.Append(' ').Append(Token(b.Operator)).Append(' ');
                    AppendOperand(b.Right, b.Operator, rightSide: true);
                    break;
                case UnaryExpr u:
                    Text.Append(u.Operator == UnaryOperator.Not ? "NOT " : "-");
                    AppendTight(u.Operand);
                    break;
                case IsNullExpr n:
                    AppendTight(n.Operand);
                    Text.Append(n.Negated ? " IS NOT NULL" : " IS NULL");
                    break;
                case InExpr i:
                    AppendTight(i.Operand);
                    Text.Append(" IN ");
                    AppendExpr(i.List);
                    break;
                case FunctionExpr f:
                    if (!Identifier.IsPlain(f.Name))
                        throw new InvalidOperationException($"'{f.Name}' is not a valid function name.");
                    Text.Append(f.Name).Append('(');
                    AppendList(f.Arguments, AppendExpr);
                    Text.Append(')');
                    break;
                case AliasExpr a:
                    AppendExpr(a.Expression);
                    Text.Append(" AS ").Append(Identifier.Render(a.Alias));
                    break;
                case CountExpr c:
                    AppendCount(c);
                    break;
                case ExistsExpr e:
                    Text.Append("EXISTS { ");
                    AppendClauses(e.Subquery);
                    Text.Append(" }");
                    break;
                case CountSubqueryExpr c:
                    Text.Append("COUNT { ");
                    AppendClauses(c.Subquery);
                    Text.Append(" }");
                    break;
                case CaseExpr c:
                    AppendCase(c);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown expression {expr.GetType().Name}.");
            }
        }

        private void AppendCount(CountExpr count)
        {
            if (count.Operand is null)
            {
                if (count.Distinct)
                    throw new InvalidOperationException("count(DISTINCT *) is not valid Cypher; count(DISTINCT expr) needs an operand.");
                Text.Append("count(*)");
                return;
            }

            Text.Append(count.Distinct ? "count(DISTINCT " : "count(");
            AppendExpr(count.Operand);
            Text.Append(')');
        }

        private void AppendCase(CaseExpr c)
        {
            if (c.Branches.Count == 0)
                throw new InvalidOperationException("A CASE expression needs at least one WHEN branch.");

            Text.Append("CASE");
            foreach (var branch in c.Branches)
            {
                Text.Append(" WHEN ");
                AppendExpr(branch.When);
                Text.Append(" THEN ");
                AppendExpr(branch.Then);
            }

            if (c.Else is not null)
            {
                Text.Append(" ELSE ");
                AppendExpr(c.Else);
            }

            Text.Append(" END");
        }

        /// <summary>Renders an operand of <paramref name="parent"/>, parenthesized when precedence requires it.</summary>
        private void AppendOperand(Expr operand, BinaryOperator parent, bool rightSide)
        {
            var parenthesize = operand is BinaryExpr child && NeedsParentheses(child.Operator, parent, rightSide);
            AppendMaybeParenthesized(operand, parenthesize);
        }

        /// <summary>
        /// Whether a binary <paramref name="child"/> rendered as an operand of <paramref name="parent"/>
        /// needs parentheses. Looser-binding always does. At equal precedence the tree is left-leaning
        /// by construction (<c>a - b - c</c> is <c>(a - b) - c</c>), so a left operand needs them only
        /// under a comparison, where <c>a = b = c</c> would read as a chain; a right operand needs them
        /// unless the parent is the same associative operator (<c>a AND b AND c</c> stays flat,
        /// <c>a - (b - c)</c> and <c>a + (b - c)</c> keep theirs).
        /// </summary>
        private static bool NeedsParentheses(BinaryOperator child, BinaryOperator parent, bool rightSide)
        {
            var childPrecedence = Precedence(child);
            var parentPrecedence = Precedence(parent);
            if (childPrecedence < parentPrecedence) return true;
            if (childPrecedence > parentPrecedence) return false;
            return rightSide
                ? !(IsAssociative(parent) && child == parent)
                : parentPrecedence == ComparisonPrecedence;
        }

        /// <summary>Renders the operand of a prefix or postfix operator, which binds tighter than any binary operator.</summary>
        private void AppendTight(Expr operand) => AppendMaybeParenthesized(operand, operand is BinaryExpr);

        private void AppendMaybeParenthesized(Expr operand, bool parenthesize)
        {
            if (parenthesize) Text.Append('(');
            AppendExpr(operand);
            if (parenthesize) Text.Append(')');
        }

        // ---------------------------------------------------------------------------- parameters

        private string BindLiteral(object value)
        {
            var name = "p" + _nextLiteral.ToString(CultureInfo.InvariantCulture);
            _nextLiteral++;
            Parameters.Add(name, value);
            return name;
        }

        private string BindNamed(string name, object? value)
        {
            if (!Identifier.IsPlain(name))
            {
                throw new InvalidOperationException(
                    $"'{name}' is not a valid parameter name: letters, digits and underscores only, not starting with a digit.");
            }

            if (IsReserved(name))
            {
                throw new InvalidOperationException(
                    $"Parameter name '{name}' is reserved: names of the form p<digits> are allocated to literals " +
                    "by the renderer, so a caller-named parameter in that shape could collide with one. Choose " +
                    "another name.");
            }

            if (Parameters.TryGetValue(name, out var existing))
            {
                if (!Equals(existing, value))
                {
                    throw new InvalidOperationException(
                        $"Parameter '{name}' is bound twice with different values ({existing ?? "NULL"} and " +
                        $"{value ?? "NULL"}). A name can be reused only with an equal value.");
                }

                return name;
            }

            Parameters.Add(name, value);
            return name;
        }

        private static bool IsReserved(string name)
        {
            if (name.Length < 2 || name[0] != 'p') return false;
            for (var i = 1; i < name.Length; i++)
            {
                if (!char.IsAsciiDigit(name[i])) return false;
            }

            return true;
        }
    }
}
