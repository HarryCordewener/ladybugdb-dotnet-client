using System.Diagnostics.CodeAnalysis;
using LadybugDb.Client.Cypher;
using LadybugDb.Client.Linq;
using LadybugDb.Client.Mapping;
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

    /// <summary>
    /// The escape hatch: a <c>MATCH</c> whose pattern you write, with <typeparamref name="T"/> bound
    /// to <paramref name="variable"/>, so the rest of the chain - <c>Where</c>, <c>Select</c>, the
    /// graph steps, the terminals - translates exactly as it does after <see cref="Nodes{T}"/>. For
    /// every shape the whitelist refuses: several patterns, inline property constraints, an
    /// undirected or untyped relationship, a variable the steps cannot name.
    /// </summary>
    /// <typeparam name="T">The <see cref="NodeAttribute"/> type <paramref name="variable"/> is bound to.</typeparam>
    /// <param name="pattern">
    /// The text after <c>MATCH</c>, verbatim: <c>(r:Object {dbref: $room})-[:Exit]-&gt;(n:Object)</c>.
    /// Its <c>$name</c> placeholders bind from <paramref name="parameters"/>. A parse error is the
    /// engine's, reported when the query runs.
    /// </param>
    /// <param name="parameters">
    /// The placeholders' values as a parameter object or dictionary, the shape
    /// <see cref="QueryAsync(string, object, CancellationToken)"/> takes; <see langword="null"/> when
    /// the pattern has none. Names of the form <c>p&lt;digits&gt;</c> are reserved for the values the
    /// translator binds itself.
    /// </param>
    /// <param name="variable">The pattern variable <typeparamref name="T"/> stands for; it must appear in <paramref name="pattern"/>.</param>
    /// <param name="schema">As for <see cref="Nodes{T}"/>.</param>
    /// <returns>A query that runs when enumerated, as <see cref="Nodes{T}"/> returns.</returns>
    /// <exception cref="ArgumentException"><paramref name="pattern"/> or <paramref name="variable"/> is blank, <paramref name="variable"/> is not a plain identifier or does not appear in the pattern, or <paramref name="parameters"/> is not a parameter bag.</exception>
    [RequiresUnreferencedCode("Resolves [Node]/[Rel] descriptors, projected constructors and row conversions by reflection, and reads the parameters object's public properties the same way.")]
    public IQueryable<T> Match<T>(string pattern, object? parameters = null, string variable = "n", LadybugSchema? schema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(variable);
        if (!Identifier.IsPlain(variable))
        {
            throw new ArgumentException($"'{variable}' is not a plain identifier: letters, digits and underscores only, not starting with a digit.", nameof(variable));
        }

        if (!QueryTranslator.MentionsVariable(pattern, variable))
        {
            throw new ArgumentException($"The pattern '{pattern}' does not bind a variable named '{variable}', so {typeof(T).Name} has nothing to stand for. Name the variable the pattern uses.", nameof(variable));
        }

        var bound = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var (name, value) in ParameterBinder.Enumerate(parameters)) bound.Add(name, value);
        }

        return new LadybugQueryable<T>(new LadybugQueryProvider(this, schema ?? LadybugSchema.Default), new MatchRoot(typeof(T), pattern, bound, variable));
    }
}
