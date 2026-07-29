namespace LadybugDb.Client;

/// <summary>
/// A LadybugDB internal id: uniquely identifies a node or relationship within a database as the
/// table it belongs to plus its offset within that table.
/// </summary>
/// <param name="TableId">The id of the table this row belongs to.</param>
/// <param name="Offset">The row's offset within <paramref name="TableId"/>.</param>
public readonly record struct LadybugInternalId(ulong TableId, ulong Offset);

/// <summary>A NODE value read from a query result, already fully marshalled into managed memory.</summary>
public sealed class LadybugNode : IEquatable<LadybugNode>
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

    /// <summary>
    /// Structural equality: same <see cref="Id"/>, same <see cref="Label"/>, and the same property
    /// names/values (regardless of enumeration order - see <see cref="ValueEqualityHelpers"/>).
    /// </summary>
    public bool Equals(LadybugNode? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id.Equals(other.Id)
            && Label == other.Label
            && ValueEqualityHelpers.DictionaryEquals(Properties, other.Properties);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as LadybugNode);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Id, Label, ValueEqualityHelpers.DictionaryHashCode(Properties));

    /// <inheritdoc/>
    public override string ToString() =>
        $"(:{Label} {ValueEqualityHelpers.DescribeDictionary(Properties)})";

    /// <summary>
    /// Structural equality, matching <see cref="Equals(LadybugNode?)"/> - without this operator,
    /// <c>==</c> on two <see cref="LadybugNode"/> references would silently fall back to
    /// reference equality (the default for a <see langword="class"/>), exactly the asymmetry the
    /// fix-round-4 <see cref="LadybugValue"/> equality finding was about, just relocated to a
    /// different type.
    /// </summary>
    public static bool operator ==(LadybugNode? left, LadybugNode? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>See the <c>==</c> operator above.</summary>
    public static bool operator !=(LadybugNode? left, LadybugNode? right) => !(left == right);
}

/// <summary>A REL value read from a query result, already fully marshalled into managed memory.</summary>
public sealed class LadybugRel : IEquatable<LadybugRel>
{
    internal LadybugRel(
        LadybugInternalId id,
        LadybugInternalId sourceId,
        LadybugInternalId destinationId,
        string label,
        IReadOnlyDictionary<string, LadybugValue> properties)
    {
        Id = id;
        SourceId = sourceId;
        DestinationId = destinationId;
        Label = label;
        Properties = properties;
    }

    /// <summary>This relationship's internal id.</summary>
    public LadybugInternalId Id { get; }

    /// <summary>The internal id of the source node this relationship points from.</summary>
    public LadybugInternalId SourceId { get; }

    /// <summary>The internal id of the destination node this relationship points to.</summary>
    public LadybugInternalId DestinationId { get; }

    /// <summary>The relationship table label this relationship belongs to.</summary>
    public string Label { get; }

    /// <summary>This relationship's properties, keyed by property name.</summary>
    public IReadOnlyDictionary<string, LadybugValue> Properties { get; }

    /// <summary>
    /// Structural equality: same <see cref="Id"/>, <see cref="SourceId"/>, <see cref="DestinationId"/>,
    /// <see cref="Label"/>, and the same property names/values (regardless of enumeration order -
    /// see <see cref="ValueEqualityHelpers"/>).
    /// </summary>
    public bool Equals(LadybugRel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id.Equals(other.Id)
            && SourceId.Equals(other.SourceId)
            && DestinationId.Equals(other.DestinationId)
            && Label == other.Label
            && ValueEqualityHelpers.DictionaryEquals(Properties, other.Properties);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as LadybugRel);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(Id, SourceId, DestinationId, Label, ValueEqualityHelpers.DictionaryHashCode(Properties));

    /// <inheritdoc/>
    public override string ToString() =>
        $"[:{Label} {ValueEqualityHelpers.DescribeDictionary(Properties)}]";

    /// <summary>
    /// Structural equality, matching <see cref="Equals(LadybugRel?)"/> - see
    /// <see cref="LadybugNode"/>'s identical operator for why this exists rather than falling back
    /// to the default reference equality a <see langword="class"/> would otherwise get.
    /// </summary>
    public static bool operator ==(LadybugRel? left, LadybugRel? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>See the <c>==</c> operator above.</summary>
    public static bool operator !=(LadybugRel? left, LadybugRel? right) => !(left == right);
}

/// <summary>
/// A RECURSIVE_REL value read from a query result - the result of a variable-length path match
/// (e.g. <c>MATCH p = (a)-[:R*1..3]-&gt;(b) RETURN p</c>) - already fully marshalled into managed
/// memory.
/// </summary>
public sealed class LadybugPath : IEquatable<LadybugPath>
{
    internal LadybugPath(IReadOnlyList<LadybugNode> nodes, IReadOnlyList<LadybugRel> relationships)
    {
        Nodes = nodes;
        Relationships = relationships;
    }

    /// <summary>The nodes visited along this path, in path order.</summary>
    public IReadOnlyList<LadybugNode> Nodes { get; }

    /// <summary>The relationships traversed along this path, in path order.</summary>
    public IReadOnlyList<LadybugRel> Relationships { get; }

    /// <summary>
    /// Structural equality: the same nodes and the same relationships, in the same order.
    /// </summary>
    public bool Equals(LadybugPath? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Nodes.SequenceEqual(other.Nodes) && Relationships.SequenceEqual(other.Relationships);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as LadybugPath);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var node in Nodes) hash.Add(node);
        foreach (var rel in Relationships) hash.Add(rel);
        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        "<" + string.Join(", ", Nodes.Select(n => n.ToString()).Concat(Relationships.Select(r => r.ToString()))) + ">";

    /// <summary>
    /// Structural equality, matching <see cref="Equals(LadybugPath?)"/> - see
    /// <see cref="LadybugNode"/>'s identical operator for why this exists rather than falling back
    /// to the default reference equality a <see langword="class"/> would otherwise get.
    /// </summary>
    public static bool operator ==(LadybugPath? left, LadybugPath? right) =>
        left is null ? right is null : left.Equals(right);

    /// <summary>See the <c>==</c> operator above.</summary>
    public static bool operator !=(LadybugPath? left, LadybugPath? right) => !(left == right);
}
