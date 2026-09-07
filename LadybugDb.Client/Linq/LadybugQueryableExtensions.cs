using System.Linq.Expressions;
using System.Reflection;

namespace LadybugDb.Client.Linq;

/// <summary>
/// The async boundary of a LadybugDB query: each method here renders the chain once, runs it, and
/// hands back either a stream or a terminal's value. Beyond <see cref="AsAsyncEnumerable{T}"/>,
/// further <c>System.Linq.AsyncEnumerable</c> operators run on the client over the streamed rows;
/// the method name is the boundary.
/// </summary>
public static class LadybugQueryableExtensions
{
    /// <summary>Streams the query's elements. The underlying result is released when the enumerator is - on completion, <c>break</c>, a throw, or cancellation.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs and between rows.</param>
    /// <exception cref="InvalidOperationException"><paramref name="source"/> is not a LadybugDB query.</exception>
    public static IAsyncEnumerable<T> AsAsyncEnumerable<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Provider(source).ExecuteAsync<IAsyncEnumerable<T>>(source.Expression, cancellationToken);

    /// <summary>Runs the query and collects its elements.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs and between rows.</param>
    public static async Task<List<T>> ToListAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default)
    {
        var list = new List<T>();
        await foreach (var element in AsAsyncEnumerable(source, cancellationToken).ConfigureAwait(false)) list.Add(element);
        return list;
    }

    /// <summary>Runs the query and collects its elements into an array.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs and between rows.</param>
    public static async Task<T[]> ToArrayAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        [.. await ToListAsync(source, cancellationToken).ConfigureAwait(false)];

    /// <summary>The first element (<c>LIMIT 1</c>).</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    /// <exception cref="InvalidOperationException">The query returned no rows.</exception>
    public static Task<T> FirstAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, T>(source, nameof(Queryable.First), cancellationToken);

    /// <summary>The first element (<c>LIMIT 1</c>), or <see langword="default"/> when there is none.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    public static Task<T?> FirstOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, T?>(source, nameof(Queryable.FirstOrDefault), cancellationToken);

    /// <summary>The only element (<c>LIMIT 2</c>, then checked).</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    /// <exception cref="InvalidOperationException">The query returned no rows, or more than one.</exception>
    public static Task<T> SingleAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, T>(source, nameof(Queryable.Single), cancellationToken);

    /// <summary>The only element (<c>LIMIT 2</c>, then checked), or <see langword="default"/> when there is none.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    /// <exception cref="InvalidOperationException">The query returned more than one row.</exception>
    public static Task<T?> SingleOrDefaultAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, T?>(source, nameof(Queryable.SingleOrDefault), cancellationToken);

    /// <summary><c>RETURN count(*)</c> over the matched rows.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query, not yet projected.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    public static Task<long> CountAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, long>(source, nameof(Queryable.LongCount), cancellationToken);

    /// <summary>Whether the query matches any row.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">A LadybugDB query, not yet projected.</param>
    /// <param name="cancellationToken">Checked before the statement runs.</param>
    public static Task<bool> AnyAsync<T>(this IQueryable<T> source, CancellationToken cancellationToken = default) =>
        Terminal<T, bool>(source, nameof(Queryable.Any), cancellationToken);

    private static Task<TResult> Terminal<T, TResult>(IQueryable<T> source, string terminal, CancellationToken cancellationToken)
    {
        var provider = Provider(source);
        var method = typeof(Queryable).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == terminal && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(T));
        return provider.ExecuteAsync<Task<TResult>>(Expression.Call(method, source.Expression), cancellationToken);
    }

    private static ILadybugAsyncQueryProvider Provider<T>(IQueryable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Provider as ILadybugAsyncQueryProvider
            ?? throw new InvalidOperationException(
                $"This query's provider is {source.Provider.GetType().Name}, not a LadybugDB one, so it has no async execution here. " +
                "These methods are for queries from LadybugConnection.Nodes<T>() and Match<T>().");
    }
}
