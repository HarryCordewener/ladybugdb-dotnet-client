using System.Diagnostics.CodeAnalysis;
using LadybugDb.Client.Linq;
using LadybugDb.Client.Schema;

namespace LadybugDb.Client;

public sealed partial class LadybugConnection
{
    /// <summary>
    /// The nodes of <typeparamref name="T"/>'s table as an <see cref="IQueryable{T}"/>:
    /// <c>MATCH (n:Table)</c>, with <c>Where</c>, <c>Select</c>, <c>OrderBy</c>, <c>Skip</c>,
    /// <c>Take</c>, <c>Distinct</c>, the graph steps, and the terminals translated to one Cypher
    /// statement when the query runs. See <c>docs/USAGE.md</c>, LINQ, for the whitelist.
    /// </summary>
    /// <typeparam name="T">A <see cref="NodeAttribute"/> type.</typeparam>
    /// <param name="schema">
    /// The schema to translate against, or <see langword="null"/> for <see cref="LadybugSchema.Default"/>,
    /// which describes any annotated type on first use.
    /// </param>
    /// <returns>A query that runs when enumerated - synchronously through <c>foreach</c>, or through the <c>...Async</c> terminals in <see cref="LadybugQueryableExtensions"/>.</returns>
    /// <remarks>
    /// Translation happens at enumeration, not here: closures are read then, and an expression the
    /// whitelist does not cover throws <see cref="NotSupportedException"/> then, naming the
    /// sub-expression. Nothing is ever evaluated on the client.
    /// </remarks>
    [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection.")]
    public IQueryable<T> Nodes<T>(LadybugSchema? schema = null) =>
        new LadybugQueryable<T>(new LadybugQueryProvider(this, schema ?? LadybugSchema.Default), new NodesRoot(typeof(T)));
}
