namespace LadybugDb.Client.Schema;

/// <summary>
/// Marks a type as the shape of one node table. Every public readable instance property is a
/// column (named by <see cref="ColumnAttribute"/>, or by the property name with its first letter
/// lowered), and exactly one property carries <see cref="KeyAttribute"/>.
/// </summary>
/// <remarks>
/// <code>
/// [Node("Object")]
/// public sealed record Obj([property: Key] long Dbref, string Name, long? Loc);
/// </code>
/// A positional record is the intended shape: its properties are also its constructor parameters,
/// which is what <see cref="LadybugConnection.Select{T}"/> and the LINQ layer materialize through.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class NodeAttribute : Attribute
{
    /// <summary>Marks the type as the shape of node table <paramref name="table"/>.</summary>
    /// <param name="table">The node table name.</param>
    public NodeAttribute(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        Table = table;
    }

    /// <summary>The node table name.</summary>
    public string Table { get; }
}

/// <summary>
/// Marks a type as the shape of one relationship table between two <see cref="NodeAttribute"/>
/// types. Its public readable instance properties are the relationship's own columns; a
/// relationship with none is an empty record.
/// </summary>
/// <remarks>
/// <code>
/// [Rel("Has", From = typeof(Obj), To = typeof(Attr))]
/// public sealed record Has(long Since);
///
/// [Rel("Located", typeof(Obj), typeof(Obj))]
/// public sealed record Located;
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class RelAttribute : Attribute
{
    /// <summary>Marks the type as the shape of relationship table <paramref name="table"/>; set <see cref="From"/> and <see cref="To"/> as named arguments.</summary>
    /// <param name="table">The relationship table name.</param>
    public RelAttribute(string table)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        Table = table;
    }

    /// <summary>Marks the type as the shape of relationship table <paramref name="table"/> from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="table">The relationship table name.</param>
    /// <param name="from">The <see cref="NodeAttribute"/> type at the source end.</param>
    /// <param name="to">The <see cref="NodeAttribute"/> type at the destination end.</param>
    public RelAttribute(string table, Type from, Type to) : this(table)
    {
        From = from;
        To = to;
    }

    /// <summary>The relationship table name.</summary>
    public string Table { get; }

    /// <summary>The <see cref="NodeAttribute"/> type at the source end.</summary>
    public Type? From { get; set; }

    /// <summary>The <see cref="NodeAttribute"/> type at the destination end.</summary>
    public Type? To { get; set; }
}

/// <summary>
/// Marks the primary-key property of a <see cref="NodeAttribute"/> type. On a positional record
/// it must be written <c>[property: Key]</c>; without <c>property:</c> the attribute would land on
/// the constructor parameter, which is why it is not allowed there.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class KeyAttribute : Attribute;

/// <summary>
/// Names the column a property maps to when the default - the property name with its first letter
/// lowered - is not the column's name. Names are matched case-insensitively by the engine
/// (measured: <c>o.DBREF</c> reads column <c>dbref</c>), so this is for names that differ by more
/// than case. On a positional record write <c>[property: Column("...")]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class ColumnAttribute : Attribute
{
    /// <summary>Maps the property to column <paramref name="name"/>.</summary>
    /// <param name="name">The column name.</param>
    public ColumnAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The column name.</summary>
    public string Name { get; }
}
