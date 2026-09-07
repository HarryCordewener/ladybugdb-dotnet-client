using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace LadybugDb.Client.Linq;

/// <summary>
/// Typed traversal steps over the <c>[Rel]</c> schema, each appending one segment to the query's
/// <c>MATCH</c> pattern. A step yields pairs (or triples), so a later <c>Where</c> or <c>Select</c>
/// can name either end: <c>p.Source</c> is everything before the step, <c>p.Target</c> the node it
/// reached, <c>p.Rel</c> the relationship when the step asked for it.
/// </summary>
/// <remarks>
/// <para>
/// Every step validates at translation that <c>TRel</c>'s <c>[Rel]</c> connects
/// the current node's table to <c>TTarget</c>'s in the direction asked for, and
/// throws <see cref="InvalidOperationException"/> naming both tables otherwise - a wrong direction
/// is the mistake this catches, since the engine would silently match nothing.
/// </para>
/// <para>
/// These are marker methods: calling one only records the step in the expression tree. The
/// translator recognizes them by identity, so they work on LadybugDB queries alone and throw for
/// any other provider.
/// </para>
/// </remarks>
public static class GraphSteps
{
    /// <summary>Follows <typeparamref name="TRel"/> outward: <c>(source)-[:TRel]-&gt;(target:TTarget)</c>.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type whose <c>From</c> is the current node type and whose <c>To</c> is <typeparamref name="TTarget"/>.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TTarget Target)> Out<TSource, TRel, TTarget>(this IQueryable<TSource> source) =>
        Step<TSource, (TSource, TTarget)>(source, nameof(Out), 1, [typeof(TSource), typeof(TRel), typeof(TTarget)]);

    /// <summary>Follows <typeparamref name="TRel"/> outward over one to many hops: <c>(source)-[:TRel*min..max]-&gt;(target:TTarget)</c>. The engine requires the upper bound.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type, which must connect <typeparamref name="TTarget"/>'s table to itself for more than one hop.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    /// <param name="minHops">The fewest relationships to traverse.</param>
    /// <param name="maxHops">The most relationships to traverse.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TTarget Target)> Out<TSource, TRel, TTarget>(this IQueryable<TSource> source, int minHops, int maxHops) =>
        Step<TSource, (TSource, TTarget)>(source, nameof(Out), 3, [typeof(TSource), typeof(TRel), typeof(TTarget)], Expression.Constant(minHops), Expression.Constant(maxHops));

    /// <summary>Follows <typeparamref name="TRel"/> outward and keeps the relationship: <c>(source)-[r:TRel]-&gt;(target:TTarget)</c>, yielding <c>(Source, Rel, Target)</c>.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type whose <c>From</c> is the current node type and whose <c>To</c> is <typeparamref name="TTarget"/>.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TRel Rel, TTarget Target)> OutWithRel<TSource, TRel, TTarget>(this IQueryable<TSource> source) =>
        Step<TSource, (TSource, TRel, TTarget)>(source, nameof(OutWithRel), 1, [typeof(TSource), typeof(TRel), typeof(TTarget)]);

    /// <summary>Follows <typeparamref name="TRel"/> inward: <c>(source)&lt;-[:TRel]-(target:TTarget)</c> - the nodes that point at the current one.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type whose <c>From</c> is <typeparamref name="TTarget"/> and whose <c>To</c> is the current node type.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TTarget Target)> In<TSource, TRel, TTarget>(this IQueryable<TSource> source) =>
        Step<TSource, (TSource, TTarget)>(source, nameof(In), 1, [typeof(TSource), typeof(TRel), typeof(TTarget)]);

    /// <summary>Follows <typeparamref name="TRel"/> inward over one to many hops: <c>(source)&lt;-[:TRel*min..max]-(target:TTarget)</c>.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    /// <param name="minHops">The fewest relationships to traverse.</param>
    /// <param name="maxHops">The most relationships to traverse.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TTarget Target)> In<TSource, TRel, TTarget>(this IQueryable<TSource> source, int minHops, int maxHops) =>
        Step<TSource, (TSource, TTarget)>(source, nameof(In), 3, [typeof(TSource), typeof(TRel), typeof(TTarget)], Expression.Constant(minHops), Expression.Constant(maxHops));

    /// <summary>Follows <typeparamref name="TRel"/> inward and keeps the relationship: <c>(source)&lt;-[r:TRel]-(target:TTarget)</c>, yielding <c>(Source, Rel, Target)</c>.</summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type whose <c>From</c> is <typeparamref name="TTarget"/> and whose <c>To</c> is the current node type.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type reached.</typeparam>
    /// <param name="source">The query so far.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<(TSource Source, TRel Rel, TTarget Target)> InWithRel<TSource, TRel, TTarget>(this IQueryable<TSource> source) =>
        Step<TSource, (TSource, TRel, TTarget)>(source, nameof(InWithRel), 1, [typeof(TSource), typeof(TRel), typeof(TTarget)]);

    /// <summary>
    /// Keeps the current nodes that have at least one outgoing <typeparamref name="TRel"/> to a
    /// <typeparamref name="TTarget"/> satisfying <paramref name="predicate"/>:
    /// <c>WHERE EXISTS { MATCH (n)-[:TRel]-&gt;(x:TTarget) WHERE ... }</c>. The current element does
    /// not change.
    /// </summary>
    /// <typeparam name="TSource">The current element type.</typeparam>
    /// <typeparam name="TRel">The <c>[Rel]</c> type whose <c>From</c> is the current node type and whose <c>To</c> is <typeparamref name="TTarget"/>.</typeparam>
    /// <typeparam name="TTarget">The <c>[Node]</c> type at the far end.</typeparam>
    /// <param name="source">The query so far.</param>
    /// <param name="predicate">The condition on the far node, in the same whitelist as <c>Where</c>.</param>
    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    public static IQueryable<TSource> WhereExists<TSource, TRel, TTarget>(this IQueryable<TSource> source, Expression<Func<TTarget, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Step<TSource, TSource>(source, nameof(WhereExists), 2, [typeof(TSource), typeof(TRel), typeof(TTarget)], Expression.Quote(predicate));
    }

    [RequiresUnreferencedCode("The LINQ provider resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection. Use QueryAsync with LadybugRow, or Select<T>, when trimming.")]
    [RequiresDynamicCode("The LINQ provider builds generic method instantiations and expression trees at run time. Use QueryAsync with LadybugRow, or Select<T>, when publishing AOT.")]
    private static IQueryable<TResult> Step<TSource, TResult>(IQueryable<TSource> source, string name, int parameterCount, Type[] typeArguments, params Expression[] arguments)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Provider is not LadybugQueryProvider provider)
        {
            throw new InvalidOperationException(
                $"{name} is a LadybugDB graph step and this query's provider is {source.Provider.GetType().Name}. " +
                "Graph steps apply to queries from LadybugConnection.Nodes<T>() and Match<T>().");
        }

        var method = typeof(GraphSteps).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == name && m.GetParameters().Length == parameterCount)
            .MakeGenericMethod(typeArguments);
        return provider.CreateQuery<TResult>(Expression.Call(method, [source.Expression, .. arguments]));
    }
}
