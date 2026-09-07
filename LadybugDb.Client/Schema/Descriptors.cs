namespace LadybugDb.Client.Schema;

/// <summary>One column of a node or relationship table, as a CLR property maps to it.</summary>
/// <param name="Column">The column name: the <see cref="ColumnAttribute"/> name, or the property name with its first letter lowered.</param>
/// <param name="ClrName">The property name.</param>
/// <param name="ClrType">The property type, <see cref="Nullable{T}"/> included.</param>
/// <param name="EngineType">The <see cref="LadybugType"/> that backs <paramref name="ClrType"/> exactly; the column may be any narrower type <see cref="LadybugConnection.Select{T}"/>'s widening rule reads into it.</param>
/// <param name="EngineTypeName">The DDL type <see cref="LadybugSchema.CreateTablesAsync"/> creates the column as (<c>INT64</c>, <c>DECIMAL(38, 10)</c>, ...).</param>
public sealed record PropertyDescriptor(string Column, string ClrName, Type ClrType, LadybugType EngineType, string EngineTypeName);

/// <summary>A node table and the CLR type that shapes it.</summary>
/// <param name="ClrType">The <see cref="NodeAttribute"/> type.</param>
/// <param name="Table">The node table name.</param>
/// <param name="Properties">Every column, in declaration order.</param>
/// <param name="Key">The primary-key column; one of <paramref name="Properties"/>.</param>
public sealed record NodeDescriptor(Type ClrType, string Table, IReadOnlyList<PropertyDescriptor> Properties, PropertyDescriptor Key)
{
    /// <summary>The property named <paramref name="clrName"/> (exact match), or <see langword="null"/>.</summary>
    /// <param name="clrName">The CLR property name.</param>
    public PropertyDescriptor? FindProperty(string clrName) => Properties.FirstOrDefault(p => p.ClrName == clrName);

    /// <summary>Insertion order within the schema, so <see cref="LadybugSchema.Nodes"/> lists in the order added.</summary>
    internal int Order { get; init; }
}

/// <summary>A relationship table, its endpoints, and the CLR type that shapes it.</summary>
/// <param name="ClrType">The <see cref="RelAttribute"/> type.</param>
/// <param name="Table">The relationship table name.</param>
/// <param name="From">The node table at the source end.</param>
/// <param name="To">The node table at the destination end.</param>
/// <param name="Properties">The relationship's own columns, in declaration order; possibly none.</param>
public sealed record RelDescriptor(Type ClrType, string Table, NodeDescriptor From, NodeDescriptor To, IReadOnlyList<PropertyDescriptor> Properties)
{
    /// <summary>The property named <paramref name="clrName"/> (exact match), or <see langword="null"/>.</summary>
    /// <param name="clrName">The CLR property name.</param>
    public PropertyDescriptor? FindProperty(string clrName) => Properties.FirstOrDefault(p => p.ClrName == clrName);

    /// <summary>Insertion order within the schema, so <see cref="LadybugSchema.Rels"/> lists in the order added.</summary>
    internal int Order { get; init; }
}

/// <summary>
/// Thrown by <see cref="LadybugSchema.ValidateAsync"/> when the database's catalog does not match
/// the schema's descriptors. Every mismatch is collected before throwing, so one run reports them
/// all.
/// </summary>
public sealed class SchemaMismatchException : LadybugException
{
    /// <summary>Creates the exception from the mismatches found.</summary>
    /// <param name="mismatches">One line per mismatch, each naming the table and, where there is one, the column.</param>
    public SchemaMismatchException(IReadOnlyList<string> mismatches)
        : base(Compose(mismatches))
    {
        Mismatches = mismatches;
    }

    /// <summary>Every mismatch found, one line each.</summary>
    public IReadOnlyList<string> Mismatches { get; }

    private static string Compose(IReadOnlyList<string> mismatches)
    {
        ArgumentNullException.ThrowIfNull(mismatches);
        return $"The database does not match the schema ({mismatches.Count} mismatch{(mismatches.Count == 1 ? "" : "es")}):" +
            string.Concat(mismatches.Select(m => Environment.NewLine + "  - " + m));
    }
}
