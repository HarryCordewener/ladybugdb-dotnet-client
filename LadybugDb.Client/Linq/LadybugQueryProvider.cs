using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using LadybugDb.Client.Mapping;
using LadybugDb.Client.Schema;

namespace LadybugDb.Client.Linq;

/// <summary>
/// An <see cref="IQueryProvider"/> that can also run a query asynchronously. Every
/// <c>...Async</c> terminal in <see cref="LadybugQueryableExtensions"/> dispatches here, and
/// throws for a queryable whose provider is anything else.
/// </summary>
public interface ILadybugAsyncQueryProvider : IQueryProvider
{
    /// <summary>
    /// Runs <paramref name="expression"/> asynchronously. <typeparamref name="TResult"/> is
    /// <see cref="IAsyncEnumerable{T}"/> of the element type for a sequence, or
    /// <see cref="Task{TResult}"/> of the terminal's result for a chain ending in a
    /// <see cref="Queryable"/> terminal (<c>Count</c>, <c>First</c>, ...).
    /// </summary>
    /// <typeparam name="TResult">The shape of the result.</typeparam>
    /// <param name="expression">The query.</param>
    /// <param name="cancellationToken">Checked before the statement runs and between rows.</param>
    TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken);
}

/// <summary>
/// Translates a <see cref="LadybugQueryable{T}"/> chain and runs it on a
/// <see cref="LadybugConnection"/>. One instance per <c>Nodes&lt;T&gt;()</c> call; holds no state
/// beyond the connection and the schema to translate against.
/// </summary>
public sealed class LadybugQueryProvider : ILadybugAsyncQueryProvider
{
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo> GenericMethods = new();

    private readonly LadybugConnection? _connection;
    private readonly LadybugSchema _schema;

    /// <summary>A provider over <paramref name="connection"/>. A <see langword="null"/> connection makes a provider that can translate but not execute, for tests.</summary>
    [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection.")]
    internal LadybugQueryProvider(LadybugConnection? connection, LadybugSchema schema)
    {
        _connection = connection;
        _schema = schema;
    }

    /// <summary>The schema queries translate against.</summary>
    public LadybugSchema Schema => _schema;

    private LadybugConnection Connection => _connection
        ?? throw new InvalidOperationException("This query was built for translation only and has no connection to run on.");

    // ----------------------------------------------------------------------------- IQueryProvider

    /// <inheritdoc/>
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return new LadybugQueryable<TElement>(this, expression, RootOf(expression));
    }

    /// <inheritdoc/>
    public IQueryable CreateQuery(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var elementType = ElementTypeOf(expression.Type)
            ?? throw new ArgumentException($"'{expression.Type}' is not an IQueryable<T>.", nameof(expression));
        return (IQueryable)Generic(nameof(CreateQuery), elementType).Invoke(this, [expression])!;
    }

    /// <inheritdoc/>
    public object? Execute(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Generic(nameof(Execute), expression.Type).Invoke(this, [expression]);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Some part of the chain has no Cypher translation; the message names it.</exception>
    public TResult Execute<TResult>(Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var query = Translate(expression);
        return query.Terminal switch
        {
            Terminal.Sequence => (TResult)Generic(nameof(ExecuteSequence), query.ElementType).Invoke(this, [expression])!,
            Terminal.Count => (TResult)(object)checked((int)Scalar(query)),
            Terminal.LongCount => (TResult)(object)Scalar(query),
            Terminal.Any => (TResult)(object)(Scalar(query) > 0),
            _ => Pick<TResult>(query, Rows<TResult>(query)),
        };
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Some part of the chain has no Cypher translation; the message names it.</exception>
    /// <exception cref="ArgumentException"><typeparamref name="TResult"/> is neither an <see cref="IAsyncEnumerable{T}"/> nor a <see cref="Task{TResult}"/> matching the chain's terminal.</exception>
    public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var query = Translate(expression);
        var resultType = typeof(TResult);

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
        {
            if (query.Terminal != Terminal.Sequence) throw new ArgumentException("The chain ends in a scalar terminal, so its result is a Task, not a stream.", nameof(expression));
            return (TResult)Generic(nameof(ExecuteSequenceAsync), query.ElementType).Invoke(this, [expression, cancellationToken])!;
        }

        if (resultType.IsGenericType && resultType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = resultType.GetGenericArguments()[0];
            return query.Terminal switch
            {
                Terminal.Sequence => throw new ArgumentException("The chain has no terminal; ask for an IAsyncEnumerable<T>, or add Count/First/... to the chain.", nameof(expression)),
                Terminal.Count or Terminal.LongCount or Terminal.Any => (TResult)Generic(nameof(ScalarAsync), inner).Invoke(this, [query, cancellationToken])!,
                _ => (TResult)Generic(nameof(PickAsync), inner).Invoke(this, [query, cancellationToken])!,
            };
        }

        throw new ArgumentException($"'{resultType}' is neither IAsyncEnumerable<T> nor Task<T>.", nameof(expression));
    }

    // ------------------------------------------------------------------------------- execution

    /// <summary>Translates <paramref name="expression"/> against this provider's schema.</summary>
    internal TranslatedQuery Translate(Expression expression) => QueryTranslator.Translate(expression, _schema);

    /// <summary>Runs the chain synchronously and streams its elements. The result is released when the enumerator is.</summary>
    internal IEnumerable<T> ExecuteSequence<T>(Expression expression) => Rows<T>(Translate(expression));

    /// <summary>Runs the chain and streams its elements. The result is released when the enumerator is, on every exit path.</summary>
    internal IAsyncEnumerable<T> ExecuteSequenceAsync<T>(Expression expression, CancellationToken cancellationToken) =>
        RowsAsync<T>(Translate(expression), cancellationToken);

    private IEnumerable<T> Rows<T>(TranslatedQuery query)
    {
        var result = Open(query, default).AsTask().GetAwaiter().GetResult();
        try
        {
            var materialize = Materializer.For<T>(query, result.ColumnNamesArray);
            var enumerator = result.GetAsyncEnumerator();
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                yield return materialize(enumerator.Current);
            }
        }
        finally
        {
            // Every operation this client wraps completes synchronously (see LadybugConnection's
            // remarks), so waiting on these ValueTasks never blocks on anything that has not already
            // happened; they are awaited through AsTask so a future genuinely-asynchronous
            // implementation would still be correct, just no longer free.
            result.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async IAsyncEnumerable<T> RowsAsync<T>(TranslatedQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The await using is inside the iterator body, as in LadybugConnection.SelectCore, so the
        // generated enumerator's DisposeAsync releases the result on completion, early break, a
        // throw from the caller's loop body, and cancellation alike.
        await using var result = await Open(query, cancellationToken).ConfigureAwait(false);
        var materialize = Materializer.For<T>(query, result.ColumnNamesArray);
        await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return materialize(row);
        }
    }

    private ValueTask<LadybugQueryResult> Open(TranslatedQuery query, CancellationToken cancellationToken) =>
        query.Text.Parameters.Count == 0
            ? Connection.QueryAsync(query.Text.Cypher, cancellationToken)
            : Connection.QueryAsync(query.Text.Cypher, query.Text.Parameters, cancellationToken);

    private long Scalar(TranslatedQuery query) => Rows<long>(query).First();

    private async Task<T> ScalarAsync<T>(TranslatedQuery query, CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (var value in RowsAsync<long>(query, cancellationToken).ConfigureAwait(false))
        {
            count = value;
        }

        return query.Terminal switch
        {
            Terminal.Count => (T)(object)checked((int)count),
            Terminal.LongCount => (T)(object)count,
            _ => (T)(object)(count > 0),
        };
    }

    private async Task<T> PickAsync<T>(TranslatedQuery query, CancellationToken cancellationToken)
    {
        var picked = new List<T>(2);
        await foreach (var element in RowsAsync<T>(query, cancellationToken).ConfigureAwait(false))
        {
            picked.Add(element);
        }

        return Pick<T>(query, picked);
    }

    /// <summary>Applies a First/Single terminal to the (already LIMITed) rows, with the same errors LINQ to Objects raises.</summary>
    private static T Pick<T>(TranslatedQuery query, IEnumerable<T> rows)
    {
        using var enumerator = rows.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return query.Terminal is Terminal.FirstOrDefault or Terminal.SingleOrDefault
                ? default!
                : throw new InvalidOperationException("Sequence contains no elements");
        }

        var first = enumerator.Current;
        if (query.Terminal is Terminal.Single or Terminal.SingleOrDefault && enumerator.MoveNext())
        {
            throw new InvalidOperationException("Sequence contains more than one element");
        }

        return first;
    }

    // ---------------------------------------------------------------------------------- helpers

    private static MethodInfo Generic(string name, Type argument) =>
        GenericMethods.GetOrAdd((argument, name), static key =>
            typeof(LadybugQueryProvider)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(m => m.Name == key.Item2 && m.IsGenericMethodDefinition)
                .MakeGenericMethod(key.Item1));

    private static Type? ElementTypeOf(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>)
            ? type.GetGenericArguments()[0]
            : type.GetInterfaces().Select(ElementTypeOf).FirstOrDefault(t => t is not null);

    private static QueryRoot RootOf(Expression expression)
    {
        var current = expression;
        while (current is MethodCallExpression call && call.Arguments.Count > 0) current = call.Arguments[0];
        return current is ConstantExpression { Value: IQueryRoot holder }
            ? holder.Root
            : throw new ArgumentException("The expression does not start from a LadybugDB queryable.", nameof(expression));
    }
}

/// <summary>Builds the per-row function that turns a <see cref="LadybugRow"/> into a query's element.</summary>
internal static class Materializer
{
    /// <summary>
    /// For a <see cref="RowShape"/>, <see cref="RowMapper"/>'s plan over the result's columns - one
    /// mapping engine, one widening rule, one set of errors, and the translator aliases every
    /// column to the constructor parameter it feeds, so nothing here depends on the dotted-suffix
    /// rule. For a <see cref="NodeShape"/>, the NODE value's properties are laid out as a row in the
    /// descriptor's column order and mapped through the same plan; a NULL property into a
    /// non-nullable parameter is the same error <c>Select&lt;T&gt;</c> raises, never a silent default.
    /// </summary>
    [RequiresUnreferencedCode("Resolves T's constructor and its parameter types by reflection.")]
    internal static Func<LadybugRow, T> For<T>(TranslatedQuery query, string[] columnNames)
    {
        switch (query.Shape)
        {
            case NodeShape node:
                var names = node.Node.Properties.Select(p => p.Column).ToArray();
                var plan = RowMapper.ResolvePlan<T>(names);
                return row =>
                {
                    var value = row.GetValue(0);
                    if (value.IsNull)
                    {
                        throw new LadybugException($"Column '{row.GetColumnName(0)}' is NULL where a {node.Node.ClrType.Name} node was expected.");
                    }

                    var properties = value.AsNode().Properties;
                    var values = new LadybugValue[names.Length];
                    for (var i = 0; i < names.Length; i++) values[i] = Lookup(properties, names[i]);
                    return plan.Map(new LadybugRow(values, names));
                };
            default:
                return RowMapper.ResolvePlan<T>(columnNames).Map;
        }
    }

    /// <summary>A node's property by name, case-insensitively as the engine resolves names; NULL when the node has no such property.</summary>
    private static LadybugValue Lookup(IReadOnlyDictionary<string, LadybugValue> properties, string name)
    {
        if (properties.TryGetValue(name, out var exact)) return exact;
        foreach (var (key, value) in properties)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }

        return new LadybugValue(LadybugType.Null, null);
    }
}
