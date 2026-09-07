using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using LadybugDb.Client.Cypher;
using LadybugDb.Client.Schema;

namespace LadybugDb.Client.Linq;

/// <summary>
/// Turns a LINQ expression tree into a <see cref="Cypher.Query"/>. A whitelist, not a general
/// translator: every operator and every sub-expression form it accepts is listed in
/// <c>docs/USAGE.md</c>, and anything else throws <see cref="NotSupportedException"/> naming the
/// offending sub-expression and the string <c>Match&lt;T&gt;</c> escape hatch. Nothing is ever
/// evaluated on the client except closures, which become parameters.
/// </summary>
/// <remarks>
/// The chain is read root-first. Each operator updates one piece of state - the pattern, the
/// <c>WHERE</c> predicate, the projection, the sort keys, paging, the terminal - and a
/// <see cref="Stage"/> records how far along the Cypher clause order the chain has moved, so an
/// operator that Cypher cannot express at that point (<c>Where</c> after <c>Take</c>) is refused
/// rather than silently reordered. Lambda parameters resolve through <see cref="Binding"/>s: a
/// node variable at the root, the projected expressions after a <c>Select</c>, a tuple after a
/// graph step.
/// </remarks>
internal static class QueryTranslator
{
    /// <summary>Translates <paramref name="expression"/>, a chain of <see cref="Queryable"/> (and graph step) calls over a <see cref="LadybugQueryable{T}"/> root.</summary>
    /// <exception cref="NotSupportedException">The chain contains an operator or sub-expression with no Cypher translation.</exception>
    [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors and projected types by reflection.")]
    internal static TranslatedQuery Translate(Expression expression, LadybugSchema schema)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(schema);

        var calls = new List<MethodCallExpression>();
        var current = expression;
        while (current is MethodCallExpression call && call.Arguments.Count > 0)
        {
            calls.Add(call);
            current = call.Arguments[0];
        }

        if (current is not ConstantExpression { Value: IQueryRoot holder })
        {
            throw Refuse(current, "a query must start from LadybugConnection.Nodes<T>() or Match<T>()");
        }

        calls.Reverse();
        return new Translation(schema, holder.Root, calls).Run();
    }

    /// <summary>The exception every refusal raises: names the sub-expression and the escape hatch.</summary>
    internal static NotSupportedException Refuse(Expression expression, string reason) => Refuse(expression.ToString(), reason);

    /// <summary>The exception every refusal raises, for a sub-expression rendered with its context (<c>Name = o.Name</c>).</summary>
    internal static NotSupportedException Refuse(string expression, string reason) =>
        new($"Expression '{expression}' cannot be translated to Cypher: {reason}. Only the whitelist in docs/USAGE.md " +
            "(LINQ) translates, and nothing is evaluated on the client. For anything else, write the Cypher yourself with " +
            "LadybugConnection.Match<T>(pattern, parameters), which keeps the typed result.");

    /// <summary>Which Cypher clauses the chain has committed to so far; later operators must fit after them.</summary>
    private enum Stage
    {
        /// <summary><c>MATCH</c>, optionally <c>WHERE</c>: filters and sort keys still accepted.</summary>
        Matching,
        /// <summary><c>RETURN</c> written: filters and sort keys still accepted through the projected bindings.</summary>
        Projected,
        /// <summary><c>DISTINCT</c> written: only paging and terminals may follow.</summary>
        Distinct,
        /// <summary><c>SKIP</c>/<c>LIMIT</c> written: only terminals may follow.</summary>
        Paged,
    }

    private sealed class Translation
    {
        private readonly LadybugSchema _schema;
        private readonly QueryRoot _root;
        private readonly IReadOnlyList<MethodCallExpression> _calls;
        private readonly Dictionary<ParameterExpression, Binding> _scope = new();

        private PatternPath _path = null!;
        private Binding _current = null!;
        private Expr? _where;
        private readonly List<SortKey> _orderBy = [];
        private long? _skip;
        private long? _limit;
        private bool _distinct;
        private IReadOnlyList<Expr>? _projection;
        private ResultShape _shape = null!;
        private Type _elementType = null!;
        private Stage _stage = Stage.Matching;
        private Terminal _terminal = Terminal.Sequence;
        private int _nodeCount;
        private int _relCount;

        internal Translation(LadybugSchema schema, QueryRoot root, IReadOnlyList<MethodCallExpression> calls)
        {
            _schema = schema;
            _root = root;
            _calls = calls;
        }

        [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors and projected types by reflection.")]
        internal TranslatedQuery Run()
        {
            var node = _schema.Node(_root.ElementType);
            var alias = RootAlias();
            _path = CypherDsl.Node(node.Table, alias).ToPatternPath();
            _current = new NodeBinding(alias, node);
            _shape = new NodeShape(node);
            _elementType = _root.ElementType;

            foreach (var call in _calls) Apply(call);

            return new TranslatedQuery(Build().Render(), _elementType, _shape, _terminal);
        }

        /// <summary>
        /// The root variable's name: the parameter name of the first lambda in the chain, so the
        /// rendered Cypher reads like the C# that produced it (<c>o => o.Name</c> gives
        /// <c>MATCH (o:Object) RETURN o.name</c>), or <c>n</c> when there is none.
        /// </summary>
        private string RootAlias()
        {
            // With graph steps the pattern has several nodes, named n0, n1, ... in pattern order so
            // the rendered Cypher reads left to right; a lambda parameter would name only one of them.
            if (_calls.Any(IsStep)) return "n0";

            // A WhereExists predicate names the far node, not the root.
            foreach (var call in _calls.Where(c => c.Method.DeclaringType != typeof(GraphSteps)))
            {
                foreach (var argument in call.Arguments.Skip(1))
                {
                    if (TryLambda(argument, out var lambda) && lambda.Parameters.Count == 1)
                    {
                        return lambda.Parameters[0].Name is { Length: > 0 } name && Identifier.IsPlain(name) ? name : "n";
                    }
                }
            }

            return "n";
        }

        private static bool IsStep(MethodCallExpression call) =>
            call.Method.DeclaringType == typeof(GraphSteps) && call.Method.Name != nameof(GraphSteps.WhereExists);

        private Query Build()
        {
            var builder = CypherDsl.Match(_path);
            if (_where is not null) builder = builder.Where(_where);

            switch (_terminal)
            {
                case Terminal.Count or Terminal.LongCount:
                    return builder.Return(CypherDsl.CountAll().As("Count")).Build();
                case Terminal.Any:
                    // Not "AS Any": ANY is reserved, so it would render backticked.
                    return builder.Return(CypherDsl.CountAll().As("Found")).Build();
            }

            var items = _projection ?? Variables(_current);
            builder = _distinct ? builder.ReturnDistinct([.. items]) : builder.Return([.. items]);
            if (_orderBy.Count > 0) builder = builder.OrderBy([.. _orderBy]);
            if (_skip is { } skip) builder = builder.Skip(skip);
            if (_limit is { } limit) builder = builder.Limit(limit);
            return builder.Build();
        }

        /// <summary>The <c>RETURN</c> items for an unprojected chain: the node, or every variable of a step's tuple in order.</summary>
        private static Expr[] Variables(Binding binding) => binding switch
        {
            NodeBinding n => [CypherDsl.Variable(n.Alias)],
            RelBinding r => [CypherDsl.Variable(r.Alias)],
            ScalarBinding s => [s.Value],
            TupleBinding t => [.. t.Items.SelectMany(Variables)],
            _ => throw new InvalidOperationException($"No projection for a {binding.GetType().Name}."),
        };

        // ----------------------------------------------------------------------------- operators

        [RequiresUnreferencedCode("Resolves projected types by reflection.")]
        private void Apply(MethodCallExpression call)
        {
            var method = call.Method;
            if (method.DeclaringType == typeof(GraphSteps))
            {
                ApplyStep(call);
                return;
            }

            if (method.DeclaringType != typeof(Queryable))
            {
                throw Refuse(call, $"'{method.Name}' is not an operator this provider translates");
            }

            switch (method.Name)
            {
                case nameof(Queryable.Where):
                    Require(call, Stage.Projected);
                    AddWhere(Lambda(call, 1));
                    break;
                case nameof(Queryable.Select):
                    // Allowed after Skip/Take: a row-for-row projection commutes with paging, so
                    // RETURN ... SKIP ... LIMIT ... means the same as the LINQ order. Not after
                    // Distinct, where distinct-then-project and project-then-distinct differ.
                    if (_stage == Stage.Projected) throw Refuse(call, "a second Select after the first has nothing left to project; put everything in one Select");
                    if (_stage == Stage.Distinct) throw Refuse(call, "Select after Distinct would project rows Distinct already collapsed; call Distinct after Select");
                    Project(Lambda(call, 1));
                    if (_stage == Stage.Matching) _stage = Stage.Projected;
                    break;
                case nameof(Queryable.OrderBy):
                case nameof(Queryable.OrderByDescending):
                    Require(call, Stage.Projected);
                    _orderBy.Clear();
                    _orderBy.Add(SortKey(Lambda(call, 1), method.Name == nameof(Queryable.OrderByDescending)));
                    break;
                case nameof(Queryable.ThenBy):
                case nameof(Queryable.ThenByDescending):
                    Require(call, Stage.Projected);
                    _orderBy.Add(SortKey(Lambda(call, 1), method.Name == nameof(Queryable.ThenByDescending)));
                    break;
                case nameof(Queryable.Skip):
                    if (_limit is not null) throw Refuse(call, "Skip after Take would skip inside the taken rows, which SKIP/LIMIT cannot express; put Skip first");
                    if (_skip is not null) throw Refuse(call, "Skip after Skip; add the two counts");
                    _skip = Count(call.Arguments[1]);
                    _stage = Stage.Paged;
                    break;
                case nameof(Queryable.Take):
                    if (_limit is not null) throw Refuse(call, "Take after Take; keep the smaller one");
                    _limit = Count(call.Arguments[1]);
                    _stage = Stage.Paged;
                    break;
                case nameof(Queryable.Distinct):
                    Require(call, Stage.Projected);
                    _distinct = true;
                    _stage = Stage.Distinct;
                    break;
                case nameof(Queryable.Count):
                case nameof(Queryable.LongCount):
                case nameof(Queryable.Any):
                    if (_stage != Stage.Matching)
                    {
                        throw Refuse(call, $"{method.Name} after {(_stage == Stage.Projected ? "Select" : _stage == Stage.Distinct ? "Distinct" : "Skip/Take")} " +
                            "would need a WITH stage; count before projecting, or write the Cypher");
                    }

                    if (call.Arguments.Count > 1) AddWhere(Lambda(call, 1));
                    _terminal = method.Name switch
                    {
                        nameof(Queryable.Count) => Terminal.Count,
                        nameof(Queryable.LongCount) => Terminal.LongCount,
                        _ => Terminal.Any,
                    };
                    _elementType = method.ReturnType;
                    _shape = new RowShape();
                    break;
                case nameof(Queryable.First):
                case nameof(Queryable.FirstOrDefault):
                case nameof(Queryable.Single):
                case nameof(Queryable.SingleOrDefault):
                    if (call.Arguments.Count > 1)
                    {
                        Require(call, Stage.Projected);
                        AddWhere(Lambda(call, 1));
                    }

                    _terminal = method.Name switch
                    {
                        nameof(Queryable.First) => Terminal.First,
                        nameof(Queryable.FirstOrDefault) => Terminal.FirstOrDefault,
                        nameof(Queryable.Single) => Terminal.Single,
                        _ => Terminal.SingleOrDefault,
                    };
                    var needed = _terminal is Terminal.Single or Terminal.SingleOrDefault ? 2L : 1L;
                    _limit = _limit is { } existing ? Math.Min(existing, needed) : needed;
                    break;
                default:
                    throw Refuse(call, $"the operator '{method.Name}' is not translated");
            }
        }

        // --------------------------------------------------------------------------- graph steps

        [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors by reflection.")]
        private void ApplyStep(MethodCallExpression call)
        {
            var method = call.Method;
            var typeArguments = method.GetGenericArguments();
            var rel = _schema.Rel(typeArguments[1]);
            var target = _schema.Node(typeArguments[2]);
            var name = method.Name;

            if (name == nameof(GraphSteps.WhereExists))
            {
                Require(call, Stage.Projected);
                AddExists(Lambda(call, 1), rel, target, call);
                return;
            }

            // A step extends the pattern, which is fixed once RETURN is written.
            if (_stage != Stage.Matching) throw Refuse(call, $"{name} after Select/Distinct/Skip/Take would extend a pattern that is already projected; traverse first");

            var outgoing = name is nameof(GraphSteps.Out) or nameof(GraphSteps.OutWithRel);
            var from = LastNode(call);
            CheckDirection(rel, from.Node, target, outgoing, call);

            var withRel = name is nameof(GraphSteps.OutWithRel) or nameof(GraphSteps.InWithRel);
            var relAlias = withRel ? "r" + _relCount++ : null;
            var targetAlias = "n" + ++_nodeCount;
            int? min = null;
            int? max = null;
            if (call.Arguments.Count == 3)
            {
                min = (int)Evaluate(call.Arguments[1])!;
                max = (int)Evaluate(call.Arguments[2])!;
            }

            _path = _path.Extend(
                new RelPattern(relAlias, rel.Table, outgoing ? Direction.Outgoing : Direction.Incoming, min, max),
                CypherDsl.Node(target.Table, targetAlias));

            var targetBinding = new NodeBinding(targetAlias, target);
            var tuple = withRel
                ? new TupleBinding([_current, new RelBinding(relAlias!, rel), targetBinding])
                : new TupleBinding([_current, targetBinding]);
            _current = tuple;
            _elementType = ElementTypeOf(method.ReturnType);
            _shape = new TupleShape(_elementType, tuple);
        }

        /// <summary>The node the next step continues from: the current node, or the last node reached by the previous step.</summary>
        private NodeBinding LastNode(MethodCallExpression call)
        {
            var binding = _current;
            while (binding is TupleBinding tuple) binding = tuple.Items[^1];
            return binding as NodeBinding
                ?? throw Refuse(call, "a graph step continues from a node, and the current element is not one");
        }

        /// <summary>
        /// A relationship table has one direction. A step that asks for the other one would match
        /// nothing, silently; naming both tables here is what turns that into a compile-and-run
        /// error a reader can act on.
        /// </summary>
        private static void CheckDirection(RelDescriptor rel, NodeDescriptor from, NodeDescriptor target, bool outgoing, MethodCallExpression call)
        {
            var (expectedFrom, expectedTo) = outgoing ? (from, target) : (target, from);
            if (rel.From.ClrType == expectedFrom.ClrType && rel.To.ClrType == expectedTo.ClrType) return;

            throw new InvalidOperationException(
                $"{rel.ClrType.Name} connects '{rel.From.Table}' to '{rel.To.Table}', but {call.Method.Name}<{from.ClrType.Name}, {rel.ClrType.Name}, {target.ClrType.Name}> " +
                $"needs a relationship from '{expectedFrom.Table}' to '{expectedTo.Table}'. " +
                (rel.From.ClrType == expectedTo.ClrType && rel.To.ClrType == expectedFrom.ClrType
                    ? $"The direction is reversed: use {(outgoing ? "In" : "Out")} instead."
                    : "Check the [Rel] attribute's From and To."));
        }

        private void AddExists(LambdaExpression predicate, RelDescriptor rel, NodeDescriptor target, MethodCallExpression call)
        {
            var from = LastNode(call);
            CheckDirection(rel, from.Node, target, outgoing: true, call);
            if (predicate.Parameters.Count != 1) throw Refuse(predicate, "WhereExists takes a single-parameter predicate");

            // The far node's variable is the predicate's parameter name, as the root's is, so
            // `x => x.Dbref == 3` reads back as `(x:Object) WHERE x.dbref = $p0`.
            var alias = predicate.Parameters[0].Name is { Length: > 0 } n && Identifier.IsPlain(n) && n != from.Alias ? n : "x" + _nodeCount;
            _scope[predicate.Parameters[0]] = new NodeBinding(alias, target);
            var condition = new ExpressionTranslator(_scope).Predicate(predicate.Body);

            var sub = CypherDsl.Match(CypherDsl.NodeRef(from.Alias).RelTo(rel.Table, CypherDsl.Node(target.Table, alias))).Where(condition);
            var exists = CypherDsl.Exists(sub);
            _where = _where is null ? exists : _where.And(exists);
        }

        private static Type ElementTypeOf(Type queryableType) => queryableType.GetGenericArguments()[0];

        /// <summary>Refuses <paramref name="call"/> when the chain is already past <paramref name="latest"/>.</summary>
        private void Require(MethodCallExpression call, Stage latest)
        {
            if (_stage <= latest) return;
            var after = _stage switch
            {
                Stage.Distinct => "Distinct",
                _ => _skip is not null && _limit is not null ? "Skip/Take" : _skip is not null ? "Skip" : "Take",
            };
            throw Refuse(call, $"{call.Method.Name} after {after} would apply to the rows {after} already cut, which Cypher's clause order cannot express; put {call.Method.Name} first");
        }

        private void AddWhere(LambdaExpression predicate)
        {
            var expr = Bind(predicate).Predicate(predicate.Body);
            _where = _where is null ? expr : _where.And(expr);
        }

        private SortKey SortKey(LambdaExpression selector, bool descending) =>
            new(Bind(selector).Value(selector.Body), descending);

        private static long Count(Expression argument)
        {
            if (ContainsParameter(argument)) throw Refuse(argument, "Skip/Take counts must be constants or closures");
            return Convert.ToInt64(Evaluate(argument), System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Binds the lambda's parameter to the current binding and returns the expression translator for its body.</summary>
        private ExpressionTranslator Bind(LambdaExpression lambda)
        {
            if (lambda.Parameters.Count != 1) throw Refuse(lambda, "only single-parameter lambdas are translated (no index-taking overloads)");
            _scope[lambda.Parameters[0]] = _current;
            return new ExpressionTranslator(_scope);
        }

        // ---------------------------------------------------------------------------- projection

        [RequiresUnreferencedCode("Reads the projected type's constructor by reflection.")]
        private void Project(LambdaExpression selector)
        {
            var translator = Bind(selector);
            var body = selector.Body;
            _elementType = selector.ReturnType;

            // Identity, or the whole node: RETURN o, materialized from the NODE value.
            if (translator.TryResolveBinding(body, out var binding))
            {
                switch (binding)
                {
                    case NodeBinding node:
                        _projection = [CypherDsl.Variable(node.Alias)];
                        _shape = new NodeShape(node.Node);
                        _current = node;
                        return;
                    case TupleBinding tuple:
                        _projection = Variables(tuple);
                        _shape = new TupleShape(_elementType, tuple);
                        _current = tuple;
                        return;
                    default:
                        throw Refuse(body, "only a node, or a step's tuple, can be returned whole");
                }
            }

            switch (body)
            {
                case NewExpression @new when @new.Members is { Count: > 0 } members:
                    ProjectMembers(translator, @new.Arguments, members.Select(m => m.Name).ToArray());
                    break;
                case NewExpression @new when @new.Constructor is { } constructor:
                    ProjectMembers(translator, @new.Arguments, constructor.GetParameters().Select(p => p.Name!).ToArray());
                    break;
                case MemberInitExpression:
                    throw Refuse(body, "object initializers are not translated because rows are materialized through a constructor; project into a record, an anonymous type, or a tuple");
                default:
                    var value = ProjectedValue(translator, body);
                    _projection = [value.As(ScalarAlias(body))];
                    _shape = new RowShape();
                    _current = new ScalarBinding(value);
                    break;
            }
        }

        private void ProjectMembers(ExpressionTranslator translator, IReadOnlyList<Expression> arguments, string[] names)
        {
            var items = new Expr[arguments.Count];
            var members = new Dictionary<string, Expr>(StringComparer.Ordinal);
            for (var i = 0; i < arguments.Count; i++)
            {
                var value = ProjectedValue(translator, arguments[i], names[i]);
                items[i] = value.As(names[i]);
                members[names[i]] = value;
            }

            _projection = items;
            _shape = new RowShape();
            _current = new ProjectedBinding(members);
        }

        /// <summary>One projected column: any translatable value, but never a whole node, which no row mapping can receive.</summary>
        private static Expr ProjectedValue(ExpressionTranslator translator, Expression argument, string? name = null)
        {
            var label = name is null ? argument.ToString() : $"{name} = {argument}";
            if (translator.TryResolveBinding(argument, out var binding) && binding is NodeBinding or TupleBinding or RelBinding)
            {
                throw Refuse(label, "a whole node inside a projection has no column to map to; project its properties, or return the node alone");
            }

            if (argument is NewExpression or MemberInitExpression)
            {
                throw Refuse(label, "nested projections are not translated; project a flat shape");
            }

            var value = translator.Value(argument);

            // string.Length is an int; size() is INT64, which the row mapping refuses to narrow.
            // The cast makes the column the type the C# says it is. Predicates need no cast - the
            // engine compares INT64 to a bound value at any width.
            return argument.Type == typeof(int) && value is FunctionExpr { Name: "size" } ? value.Cast("INT32") : value;
        }

        /// <summary>The column alias for a scalar projection: the member or method name where there is one, so the row reads like the C#.</summary>
        private static string ScalarAlias(Expression body) => body switch
        {
            MemberExpression m => m.Member.Name,
            MethodCallExpression c => c.Method.Name,
            UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u => ScalarAlias(u.Operand),
            _ => "Value",
        };

        // ------------------------------------------------------------------------------- helpers

        private static LambdaExpression Lambda(MethodCallExpression call, int index)
        {
            if (index < call.Arguments.Count && TryLambda(call.Arguments[index], out var lambda)) return lambda;
            throw Refuse(call, $"argument {index} of {call.Method.Name} is not a lambda");
        }

        private static bool TryLambda(Expression argument, [NotNullWhen(true)] out LambdaExpression? lambda)
        {
            lambda = argument switch
            {
                UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression l } => l,
                LambdaExpression l => l,
                _ => null,
            };
            return lambda is not null;
        }
    }

    // ------------------------------------------------------------------------ expressions

    /// <summary>Translates the body of one lambda: predicates for <c>WHERE</c>, values for projections and sort keys.</summary>
    private sealed class ExpressionTranslator(Dictionary<ParameterExpression, Binding> scope)
    {
        /// <summary>A boolean-valued expression for <c>WHERE</c>.</summary>
        internal Expr Predicate(Expression expression)
        {
            switch (expression)
            {
                case BinaryExpression { NodeType: ExpressionType.AndAlso } b:
                    return Predicate(b.Left).And(Predicate(b.Right));
                case BinaryExpression { NodeType: ExpressionType.OrElse } b:
                    return Predicate(b.Left).Or(Predicate(b.Right));
                case UnaryExpression { NodeType: ExpressionType.Not } u:
                    RefuseBareBoolean(u.Operand);
                    return Predicate(u.Operand).Not();
                case BinaryExpression { NodeType: ExpressionType.Equal or ExpressionType.NotEqual } b:
                    var negated = b.NodeType == ExpressionType.NotEqual;
                    if (IsNull(b.Right)) return negated ? Value(b.Left).IsNotNull() : Value(b.Left).IsNull();
                    if (IsNull(b.Left)) return negated ? Value(b.Right).IsNotNull() : Value(b.Right).IsNull();
                    return negated ? Value(b.Left).Ne(Value(b.Right)) : Value(b.Left).Eq(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.LessThan } b:
                    return Value(b.Left).Lt(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.LessThanOrEqual } b:
                    return Value(b.Left).Le(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.GreaterThan } b:
                    return Value(b.Left).Gt(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.GreaterThanOrEqual } b:
                    return Value(b.Left).Ge(Value(b.Right));
                case MemberExpression { Member.Name: nameof(Nullable<int>.HasValue) } m when IsNullable(m.Expression?.Type):
                    return Value(m.Expression!).IsNotNull();
                case MethodCallExpression call:
                    return PredicateCall(call);
                case MemberExpression or ConstantExpression when expression.Type == typeof(bool):
                    RefuseBareBoolean(expression);
                    break;
            }

            throw Refuse(expression, "not a translatable predicate");
        }

        private Expr PredicateCall(MethodCallExpression call)
        {
            var method = call.Method;

            // string.StartsWith(string) and friends: the single-argument, ordinal overloads only. A
            // StringComparison argument would either be ignored or silently change meaning.
            if (method.DeclaringType == typeof(string) && !method.IsStatic && call.Arguments.Count == 1 && call.Arguments[0].Type == typeof(string))
            {
                var op = method.Name switch
                {
                    nameof(string.StartsWith) => BinaryOperator.StartsWith,
                    nameof(string.EndsWith) => BinaryOperator.EndsWith,
                    nameof(string.Contains) => BinaryOperator.Contains,
                    _ => (BinaryOperator?)null,
                };
                if (op is { } o) return new BinaryExpr(o, Value(call.Object!), Value(call.Arguments[0]));
            }

            // collection.Contains(x.Prop): the collection is a closure (no lambda parameter inside),
            // the tested value is translatable. Static Enumerable.Contains(source, item) and any
            // instance Contains(item) - List<T>, HashSet<T> - alike. An array binds to
            // MemoryExtensions.Contains(ReadOnlySpan<T>, T) under C# 14's first-class spans, with
            // the array wrapped in a conversion to a ref struct that no closure can hold; the
            // conversion is peeled off and the array underneath is what gets read.
            if (method.Name == nameof(Enumerable.Contains))
            {
                Expression? collection = null;
                Expression? item = null;
                if (method.IsStatic && call.Arguments.Count == 2) (collection, item) = (call.Arguments[0], call.Arguments[1]);
                else if (!method.IsStatic && call.Arguments.Count == 1 && call.Object is not null && call.Object.Type != typeof(string)) (collection, item) = (call.Object, call.Arguments[0]);
                collection = collection switch
                {
                    UnaryExpression { NodeType: ExpressionType.Convert } span when span.Type.IsByRefLike => span.Operand,
                    MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } span when span.Type.IsByRefLike => span.Arguments[0],
                    _ => collection,
                };

                if (collection is not null && item is not null && !ContainsParameter(collection))
                {
                    if (Evaluate(collection) is not IEnumerable values) throw Refuse(call, "the collection evaluated to null");
                    var items = new List<Expr>();
                    foreach (var value in values) items.Add(CypherDsl.Literal(value));
                    return Value(item).In([.. items]);
                }
            }

            throw Refuse(call, $"'{method.Name}' is not a translatable predicate method");
        }

        /// <summary>
        /// A bare boolean (<c>o.Flag</c>, <c>!o.Flag</c>) is refused rather than rendered as
        /// <c>o.flag</c>: under Cypher's three-valued logic a NULL flag is neither true nor false,
        /// so the C# reading ("the flag is set") and the Cypher reading differ exactly where it
        /// matters. <c>o.Flag == true</c> says which one is meant. The same rule Neo4jClient adopted.
        /// </summary>
        private static void RefuseBareBoolean(Expression expression)
        {
            if (expression.Type == typeof(bool) && expression is MemberExpression or ConstantExpression)
            {
                throw Refuse(expression,
                    "a bare boolean is ambiguous under Cypher's three-valued NULL logic; write it as a comparison, " +
                    $"'{expression} == true' or '{expression} == false'");
            }
        }

        /// <summary>A value expression: a property, a closure, a constant, or a function over one.</summary>
        internal Expr Value(Expression expression)
        {
            if (TryResolveBinding(expression, out var binding))
            {
                return binding switch
                {
                    NodeBinding n => CypherDsl.Variable(n.Alias),
                    RelBinding r => CypherDsl.Variable(r.Alias),
                    ScalarBinding s => s.Value,
                    _ => throw Refuse(expression, "a tuple of variables has no value of its own; pick one of its items"),
                };
            }

            // Anything that does not mention a lambda parameter is a closure (or a constant): read
            // it once, now, and bind the value as a parameter. Checked before the structural cases
            // so `room.Label.ToUpper()` binds the upper-cased string rather than rendering upper($p).
            if (!ContainsParameter(expression)) return CypherDsl.Literal(Evaluate(expression));

            switch (expression)
            {
                case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                    // Numeric promotion and Nullable<T> lifting only: the engine coerces bound values
                    // to the column's width itself, so the conversion carries no information.
                    return Value(u.Operand);
                case UnaryExpression { NodeType: ExpressionType.Negate or ExpressionType.NegateChecked } u:
                    return Value(u.Operand).Negate();
                case MemberExpression m when m.Expression is not null && TryResolveBinding(m.Expression, out var owner):
                    return Property(m, owner);
                case MemberExpression { Member.Name: nameof(Nullable<int>.Value) } m when IsNullable(m.Expression?.Type):
                    return Value(m.Expression!);
                case MemberExpression { Member.Name: nameof(string.Length) } m when m.Expression?.Type == typeof(string):
                    return CypherDsl.Func("size", Value(m.Expression));
                case MethodCallExpression { Method.DeclaringType: var t, Arguments.Count: 0, Object: not null } call when t == typeof(string) && call.Method.Name is nameof(string.ToUpper) or nameof(string.ToLower):
                    return CypherDsl.Func(call.Method.Name == nameof(string.ToUpper) ? "upper" : "lower", Value(call.Object));
                case BinaryExpression { NodeType: ExpressionType.Add } b:
                    return Value(b.Left).Plus(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.Subtract } b:
                    return Value(b.Left).Minus(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.Multiply } b:
                    return Value(b.Left).Times(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.Divide } b:
                    return Value(b.Left).DividedBy(Value(b.Right));
                case BinaryExpression { NodeType: ExpressionType.Modulo } b:
                    return Value(b.Left).Modulo(Value(b.Right));
            }

            throw Refuse(expression, "not a translatable value");
        }

        private static Expr Property(MemberExpression member, Binding owner)
        {
            switch (owner)
            {
                case NodeBinding n:
                    return n.Node.FindProperty(member.Member.Name) is { } np
                        ? CypherDsl.Prop(n.Alias, np.Column)
                        : throw Refuse(member, $"'{member.Member.Name}' is not a mapped property of {n.Node.ClrType.Name}");
                case RelBinding r:
                    return r.Rel.FindProperty(member.Member.Name) is { } rp
                        ? CypherDsl.Prop(r.Alias, rp.Column)
                        : throw Refuse(member, $"'{member.Member.Name}' is not a mapped property of {r.Rel.ClrType.Name}");
                case ProjectedBinding pb:
                    return pb.Members.TryGetValue(member.Member.Name, out var projected)
                        ? projected
                        : throw Refuse(member, $"'{member.Member.Name}' was not projected by the preceding Select");
                case ScalarBinding s when member.Member.Name == nameof(string.Length) && member.Expression?.Type == typeof(string):
                    return CypherDsl.Func("size", s.Value);
                default:
                    throw Refuse(member, "the member's owner is not a variable with properties");
            }
        }

        /// <summary>Whether <paramref name="expression"/> names a lambda parameter, or an item of a tuple one, and what it stands for.</summary>
        internal bool TryResolveBinding(Expression expression, [NotNullWhen(true)] out Binding? binding)
        {
            switch (expression)
            {
                case ParameterExpression p when scope.TryGetValue(p, out binding):
                    return true;
                case MemberExpression { Expression: not null } m when TryResolveBinding(m.Expression, out var owner) && owner is TupleBinding tuple:
                    if (m.Member.Name.StartsWith("Item", StringComparison.Ordinal)
                        && int.TryParse(m.Member.Name.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                        && index >= 1 && index <= tuple.Items.Count)
                    {
                        binding = tuple.Items[index - 1];
                        return true;
                    }

                    throw Refuse(expression, $"'{m.Member.Name}' is not an item of the step's tuple");
                default:
                    binding = null;
                    return false;
            }
        }

        private static bool IsNull(Expression expression) => expression is ConstantExpression { Value: null };

        private static bool IsNullable(Type? type) => type is not null && Nullable.GetUnderlyingType(type) is not null;
    }

    // -------------------------------------------------------------------------- closures

    /// <summary>Whether any lambda parameter appears in <paramref name="expression"/> - if none does, it is a closure that can be read now.</summary>
    private static bool ContainsParameter(Expression expression)
    {
        var finder = new ParameterFinder();
        finder.Visit(expression);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        internal bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return node;
        }
    }

    /// <summary>
    /// Reads a closure. Constants and field/property chains on constants - which is what the C#
    /// compiler emits for captured locals - are read directly; anything else is compiled once and
    /// run, which is the general fallback and not worth caching for a query that is re-translated
    /// per execution anyway.
    /// </summary>
    private static object? Evaluate(Expression expression)
    {
        switch (expression)
        {
            case ConstantExpression c:
                return c.Value;
            case MemberExpression { Member: FieldInfo field } m:
                return field.GetValue(m.Expression is null ? null : Evaluate(m.Expression));
            case MemberExpression { Member: PropertyInfo property } m when property.GetIndexParameters().Length == 0:
                return property.GetValue(m.Expression is null ? null : Evaluate(m.Expression));
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } u:
                var operand = Evaluate(u.Operand);
                var target = Nullable.GetUnderlyingType(u.Type) ?? u.Type;
                if (operand is null || target.IsInstanceOfType(operand)) return operand;
                if (operand is IConvertible && target.IsPrimitive) return System.Convert.ChangeType(operand, target, System.Globalization.CultureInfo.InvariantCulture);
                goto default;
            default:
                // The interpreter rather than IL generation: this runs once per translation on
                // small trees, and needs no dynamic-method plumbing.
                return Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true).Invoke();
        }
    }
}
