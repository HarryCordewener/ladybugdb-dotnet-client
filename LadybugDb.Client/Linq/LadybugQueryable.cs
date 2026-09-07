using System.Diagnostics.CodeAnalysis;
using System.Collections;
using System.Linq.Expressions;

namespace LadybugDb.Client.Linq;

/// <summary>Where a query chain starts: which type, and how its first <c>MATCH</c> is written.</summary>
/// <param name="ElementType">The <c>[Node]</c> type the root yields.</param>
internal abstract record QueryRoot(Type ElementType);

/// <summary><c>MATCH (n:Table)</c> for <paramref name="ElementType"/>'s table - <c>LadybugConnection.Nodes&lt;T&gt;()</c>.</summary>
/// <param name="ElementType">The <c>[Node]</c> type.</param>
internal sealed record NodesRoot(Type ElementType) : QueryRoot(ElementType);

/// <summary><c>MATCH pattern</c> written by the caller, with <paramref name="ElementType"/> bound to <paramref name="Variable"/> - <c>LadybugConnection.Match&lt;T&gt;()</c>.</summary>
/// <param name="ElementType">The <c>[Node]</c> type.</param>
/// <param name="Pattern">The text after <c>MATCH</c>, verbatim.</param>
/// <param name="Parameters">The values of the pattern's <c>$name</c> placeholders.</param>
/// <param name="Variable">The pattern variable <paramref name="ElementType"/> stands for.</param>
internal sealed record MatchRoot(Type ElementType, string Pattern, IReadOnlyDictionary<string, object?> Parameters, string Variable) : QueryRoot(ElementType);

/// <summary>Non-generic access to a <see cref="LadybugQueryable{T}"/>'s root, for the translator.</summary>
internal interface IQueryRoot
{
    /// <summary>The root this queryable starts from.</summary>
    QueryRoot Root { get; }
}

/// <summary>
/// An <see cref="IQueryable{T}"/> over a LadybugDB node table, translated to one Cypher statement
/// when enumerated. Obtained from <c>LadybugConnection.Nodes&lt;T&gt;()</c>; every standard
/// operator returns another instance of this type.
/// </summary>
/// <typeparam name="T">The element type: a <c>[Node]</c> record at the root, or whatever a <c>Select</c> projects.</typeparam>
/// <remarks>
/// <para>
/// Implements <see cref="IOrderedQueryable{T}"/> and deliberately <b>not</b>
/// <see cref="IAsyncEnumerable{T}"/>: on .NET 10 the in-box <c>System.Linq.AsyncEnumerable</c>
/// operators would make <c>Where</c>/<c>Select</c> ambiguous on a type that is both (EF Core issue
/// #24041). Call <see cref="LadybugQueryableExtensions.AsAsyncEnumerable{T}"/> or one of the
/// <c>...Async</c> terminals to cross into async.
/// </para>
/// <para>
/// Enumerating synchronously (<c>foreach</c>, <c>ToList()</c>) is honest: the engine is in-process
/// and every operation this client wraps completes synchronously, so the sync enumerator neither
/// blocks a thread on a pending task nor spins one up.
/// </para>
/// </remarks>
public sealed class LadybugQueryable<T> : IOrderedQueryable<T>, IQueryRoot
{
    private readonly LadybugQueryProvider _provider;

    /// <summary>A root queryable: <c>MATCH (n:Table)</c> for <typeparamref name="T"/>.</summary>
    internal LadybugQueryable(LadybugQueryProvider provider, QueryRoot root)
    {
        _provider = provider;
        Root = root;
        Expression = Expression.Constant(this);
    }

    /// <summary>A queryable further along a chain, wrapping the operator call <paramref name="expression"/>.</summary>
    internal LadybugQueryable(LadybugQueryProvider provider, Expression expression, QueryRoot root)
    {
        _provider = provider;
        Root = root;
        Expression = expression;
    }

    QueryRoot IQueryRoot.Root => Root;

    internal QueryRoot Root { get; }

    /// <inheritdoc/>
    public Type ElementType => typeof(T);

    /// <inheritdoc/>
    public Expression Expression { get; }

    /// <inheritdoc/>
    public IQueryProvider Provider => _provider;

    /// <summary>Translates the chain to Cypher, runs it, and streams the rows projected into <typeparamref name="T"/>.</summary>
    /// <exception cref="NotSupportedException">Some part of the chain has no Cypher translation; the message names it.</exception>
    /// <exception cref="InvalidOperationException">The queryable was built for translation only, with no connection.</exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Every LadybugQueryable is created through LadybugConnection.Nodes<T>/Match<T>, which carry the trimming and AOT requirements.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "See IL2026 on this member.")]
    public IEnumerator<T> GetEnumerator() => _provider.ExecuteSequence<T>(Expression).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The Cypher this chain translates to, for logging and tests. Translation reads closures now, as execution would.</summary>
    /// <exception cref="NotSupportedException">Some part of the chain has no Cypher translation; the message names it.</exception>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Every LadybugQueryable is created through LadybugConnection.Nodes<T>/Match<T>, which carry the trimming and AOT requirements.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "See IL2026 on this member.")]
    public override string ToString() => _provider.Translate(Expression).Text.Cypher;
}
