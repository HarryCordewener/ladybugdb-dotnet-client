using LadybugDb.Client.Cypher;
using LadybugDb.Client.Schema;

namespace LadybugDb.Client.Linq;

/// <summary>What a lambda parameter stands for at one point of a query chain.</summary>
internal abstract record Binding;

/// <summary>A node pattern variable: <c>p.Prop</c> renders <c>alias.column</c> through the descriptor.</summary>
internal sealed record NodeBinding(string Alias, NodeDescriptor Node) : Binding;

/// <summary>A relationship pattern variable.</summary>
internal sealed record RelBinding(string Alias, RelDescriptor Rel) : Binding;

/// <summary>A <see cref="ValueTuple"/> of bindings, as a graph step yields: <c>p.Item1</c> (<c>Source</c>), <c>p.Item2</c> (<c>Target</c>), ...</summary>
internal sealed record TupleBinding(IReadOnlyList<Binding> Items) : Binding;

/// <summary>
/// The groups of a <c>GroupBy</c>: <c>g.Key</c> renders <paramref name="Key"/>, and the aggregates
/// over <c>g</c> (<c>g.Count()</c>, <c>g.Sum(x =&gt; ...)</c>) render Cypher's, with the selector's
/// parameter bound to <paramref name="Element"/>. Cypher groups implicitly - <c>RETURN key,
/// count(*)</c> - so the binding lives only until the <c>Select</c> that projects it.
/// </summary>
/// <param name="Key">The grouping key expression.</param>
/// <param name="Element">What each element of a group was before grouping.</param>
internal sealed record GroupBinding(Expr Key, Binding Element) : Binding;

/// <summary>The result of a <c>Select</c> with named members: <c>x.Name</c> renders the expression projected as <c>Name</c>.</summary>
internal sealed record ProjectedBinding(IReadOnlyDictionary<string, Expr> Members) : Binding;

/// <summary>The result of a scalar <c>Select</c>: the parameter itself renders the projected expression.</summary>
internal sealed record ScalarBinding(Expr Value) : Binding;

/// <summary>How the rows a query returns become its element type.</summary>
internal abstract record ResultShape;

/// <summary>One column per projected member, materialized by <see cref="Mapping.RowMapper"/> through the element type's constructor (or the scalar unwrap).</summary>
internal sealed record RowShape : ResultShape;

/// <summary>A single NODE column, materialized into the <c>[Node]</c> record through its constructor by property name.</summary>
internal sealed record NodeShape(NodeDescriptor Node) : ResultShape;

/// <summary>
/// One NODE or REL column per variable of a graph step's tuple, in <see cref="Binding"/> order,
/// materialized into the <see cref="ValueTuple"/> the steps declared - nested where steps were
/// chained.
/// </summary>
/// <param name="TupleType">The <see cref="ValueTuple"/> type to build.</param>
/// <param name="Binding">The tuple's structure; each leaf is one column.</param>
internal sealed record TupleShape(Type TupleType, TupleBinding Binding) : ResultShape;

/// <summary>What the query returns beyond its rows.</summary>
internal enum Terminal
{
    /// <summary>The rows themselves.</summary>
    Sequence,
    /// <summary><c>count(*)</c> as an <see cref="int"/>.</summary>
    Count,
    /// <summary><c>count(*)</c> as a <see cref="long"/>.</summary>
    LongCount,
    /// <summary><c>count(*) &gt; 0</c>.</summary>
    Any,
    /// <summary>The first row (<c>LIMIT 1</c>); none is an error.</summary>
    First,
    /// <summary>The first row (<c>LIMIT 1</c>) or <see langword="default"/>.</summary>
    FirstOrDefault,
    /// <summary>The only row (<c>LIMIT 2</c>); none or two is an error.</summary>
    Single,
    /// <summary>The only row (<c>LIMIT 2</c>) or <see langword="default"/>; two is an error.</summary>
    SingleOrDefault,
}

/// <summary>A translated query: the Cypher and its parameters, what each row becomes, and how the result is reduced.</summary>
/// <param name="Text">The statement and its parameters.</param>
/// <param name="ElementType">The type each row materializes to (for a scalar terminal, the element the terminal picks from).</param>
/// <param name="Shape">How a row becomes an element.</param>
/// <param name="Terminal">How the rows are reduced, if at all.</param>
internal sealed record TranslatedQuery(CypherText Text, Type ElementType, ResultShape Shape, Terminal Terminal);
