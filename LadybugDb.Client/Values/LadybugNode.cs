namespace LadybugDb.Client;

/// <summary>
/// A LadybugDB internal id: uniquely identifies a node or relationship within a database as the
/// table it belongs to plus its offset within that table.
/// </summary>
/// <param name="TableId">The id of the table this row belongs to.</param>
/// <param name="Offset">The row's offset within <paramref name="TableId"/>.</param>
public readonly record struct LadybugInternalId(ulong TableId, ulong Offset);

/// <summary>A NODE value read from a query result, already fully marshalled into managed memory.</summary>
public sealed class LadybugNode
{
    internal LadybugNode(LadybugInternalId id, string label, IReadOnlyDictionary<string, LadybugValue> properties)
    {
        Id = id;
        Label = label;
        Properties = properties;
    }

    /// <summary>This node's internal id.</summary>
    public LadybugInternalId Id { get; }

    /// <summary>The node table label this node belongs to.</summary>
    public string Label { get; }

    /// <summary>This node's properties, keyed by property name.</summary>
    public IReadOnlyDictionary<string, LadybugValue> Properties { get; }
}
