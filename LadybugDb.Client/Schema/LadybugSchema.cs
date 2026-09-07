using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace LadybugDb.Client.Schema;

/// <summary>
/// The set of <see cref="NodeAttribute"/> and <see cref="RelAttribute"/> types a program maps to
/// tables, described once by reflection: table names, columns, keys, and relationship endpoints.
/// The LINQ layer translates from these descriptors alone; <see cref="ValidateAsync"/> and
/// <see cref="CreateTablesAsync"/> are the optional ways to compare them with, or turn them into, a
/// real catalog.
/// </summary>
/// <remarks>
/// <para>
/// A schema built by <see cref="For"/> or <see cref="FromAssembly"/> is closed: it describes
/// exactly the types given (plus the endpoint types of every relationship, which are pulled in so a
/// <c>[Rel]</c> is never dangling), and asking it for any other type throws.
/// <see cref="Default"/> is the one open schema: it describes any annotated type on first request
/// and remembers it, which is what <c>LadybugConnection.Nodes&lt;T&gt;()</c> uses when no schema is
/// passed.
/// </para>
/// <para>
/// Property-to-column mapping is the constructor-parameter rule <see cref="LadybugConnection.Select{T}"/>
/// already applies (a case-insensitive name match), so a <c>[Node]</c> record is also a valid
/// <c>Select&lt;T&gt;</c> target with no second mapping system. The default column name lowers the
/// property's first letter (<c>Dbref</c> becomes <c>dbref</c>) for the rendered Cypher's sake only:
/// the engine resolves property and table names case-insensitively (measured: <c>o.DBREF</c> reads
/// column <c>dbref</c>, <c>MATCH (o:object)</c> finds table <c>Object</c>).
/// </para>
/// </remarks>
public sealed class LadybugSchema
{
    private readonly ConcurrentDictionary<Type, NodeDescriptor> _nodes = new();
    private readonly ConcurrentDictionary<Type, RelDescriptor> _rels = new();
    private readonly bool _open;

    private LadybugSchema(bool open) => _open = open;

    /// <summary>
    /// The open schema: describes any <c>[Node]</c> or <c>[Rel]</c> type the first time it is asked
    /// for and keeps the descriptor. <see cref="Nodes"/> and <see cref="Rels"/> list what has been
    /// asked for so far, so <see cref="ValidateAsync"/> on it checks only those.
    /// </summary>
    public static LadybugSchema Default { get; } = new(open: true);

    /// <summary>A closed schema of exactly <paramref name="types"/> and the endpoint types their relationships name.</summary>
    /// <param name="types">The <c>[Node]</c> and <c>[Rel]</c> types.</param>
    /// <exception cref="InvalidOperationException">A type carries neither attribute, a node has no <c>[Key]</c> or more than one, a property's type has no engine type, or a relationship endpoint is not a <c>[Node]</c> type.</exception>
    [RequiresUnreferencedCode("Reads the types' attributes and public properties by reflection.")]
    public static LadybugSchema For(params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var schema = new LadybugSchema(open: false);
        foreach (var type in types) schema.Add(type);
        return schema;
    }

    /// <summary>A closed schema of every <c>[Node]</c> and <c>[Rel]</c> type in <paramref name="assembly"/> that <paramref name="filter"/> admits.</summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="filter">Which annotated types to include; <see langword="null"/> for all of them.</param>
    /// <exception cref="InvalidOperationException">See <see cref="For"/>.</exception>
    [RequiresUnreferencedCode("Enumerates the assembly's types and reads their attributes and public properties by reflection.")]
    public static LadybugSchema FromAssembly(Assembly assembly, Func<Type, bool>? filter = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var schema = new LadybugSchema(open: false);
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsDefined(typeof(NodeAttribute), inherit: false) && !type.IsDefined(typeof(RelAttribute), inherit: false)) continue;
            if (filter is not null && !filter(type)) continue;
            schema.Add(type);
        }

        return schema;
    }

    /// <summary>The node descriptors, in the order they were added.</summary>
    public IReadOnlyList<NodeDescriptor> Nodes => [.. _nodes.Values.OrderBy(n => n.Order)];

    /// <summary>The relationship descriptors, in the order they were added.</summary>
    public IReadOnlyList<RelDescriptor> Rels => [.. _rels.Values.OrderBy(r => r.Order)];

    /// <summary>The descriptor of node type <paramref name="type"/>.</summary>
    /// <param name="type">A <c>[Node]</c> type.</param>
    /// <exception cref="InvalidOperationException">The type is not in this schema (or, for <see cref="Default"/>, is not a well-formed <c>[Node]</c> type).</exception>
    [RequiresUnreferencedCode("On the open schema, reads the type's attributes and public properties by reflection.")]
    public NodeDescriptor Node(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_nodes.TryGetValue(type, out var node)) return node;
        if (_open) return AddNode(type);
        throw new InvalidOperationException(
            $"{type.Name} is not a node type of this schema, which describes " +
            $"{(_nodes.IsEmpty ? "no node types" : string.Join(", ", _nodes.Values.OrderBy(n => n.Order).Select(n => n.ClrType.Name)))}. " +
            "Build the schema with every type the query uses, or use LadybugSchema.Default.");
    }

    /// <summary>The descriptor of node type <typeparamref name="T"/>. See <see cref="Node(Type)"/>.</summary>
    /// <typeparam name="T">A <c>[Node]</c> type.</typeparam>
    [RequiresUnreferencedCode("On the open schema, reads the type's attributes and public properties by reflection.")]
    public NodeDescriptor Node<T>() => Node(typeof(T));

    /// <summary>The descriptor of relationship type <paramref name="type"/>.</summary>
    /// <param name="type">A <c>[Rel]</c> type.</param>
    /// <exception cref="InvalidOperationException">The type is not in this schema (or, for <see cref="Default"/>, is not a well-formed <c>[Rel]</c> type).</exception>
    [RequiresUnreferencedCode("On the open schema, reads the type's attributes and public properties by reflection.")]
    public RelDescriptor Rel(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (_rels.TryGetValue(type, out var rel)) return rel;
        if (_open) return AddRel(type);
        throw new InvalidOperationException(
            $"{type.Name} is not a relationship type of this schema, which describes " +
            $"{(_rels.IsEmpty ? "no relationship types" : string.Join(", ", _rels.Values.OrderBy(r => r.Order).Select(r => r.ClrType.Name)))}. " +
            "Build the schema with every type the query uses, or use LadybugSchema.Default.");
    }

    /// <summary>The descriptor of relationship type <typeparamref name="T"/>. See <see cref="Rel(Type)"/>.</summary>
    /// <typeparam name="T">A <c>[Rel]</c> type.</typeparam>
    [RequiresUnreferencedCode("On the open schema, reads the type's attributes and public properties by reflection.")]
    public RelDescriptor Rel<T>() => Rel(typeof(T));

    /// <summary>The descriptor of node type <paramref name="type"/>, if this schema already describes it. Never discovers, even on <see cref="Default"/>.</summary>
    /// <param name="type">A <c>[Node]</c> type.</param>
    /// <param name="node">The descriptor, when present.</param>
    public bool TryGetNode(Type type, [NotNullWhen(true)] out NodeDescriptor? node) => _nodes.TryGetValue(type, out node);

    /// <summary>The descriptor of relationship type <paramref name="type"/>, if this schema already describes it. Never discovers, even on <see cref="Default"/>.</summary>
    /// <param name="type">A <c>[Rel]</c> type.</param>
    /// <param name="rel">The descriptor, when present.</param>
    public bool TryGetRel(Type type, [NotNullWhen(true)] out RelDescriptor? rel) => _rels.TryGetValue(type, out rel);

    /// <summary>
    /// The DDL for a fresh database: one <c>CREATE NODE TABLE</c> per node, then one
    /// <c>CREATE REL TABLE</c> per relationship (which the engine requires after both endpoint
    /// tables exist). <see cref="CreateTablesAsync"/> runs exactly these.
    /// </summary>
    public IReadOnlyList<string> CreateTableStatements()
    {
        var statements = new List<string>();
        foreach (var node in Nodes)
        {
            statements.Add(Ddl.CreateNodeTable(node.Table, [.. node.Properties.Select(Column)], node.Key.Column));
        }

        foreach (var rel in Rels)
        {
            statements.Add(Ddl.CreateRelTable(rel.Table, rel.From.Table, rel.To.Table, [.. rel.Properties.Select(Column)]));
        }

        return statements;

        static ColumnDefinition Column(PropertyDescriptor p) => new(p.Column, p.EngineTypeName);
    }

    /// <summary>Runs <see cref="CreateTableStatements"/> against <paramref name="connection"/>.</summary>
    /// <param name="connection">A connection to a database without these tables.</param>
    /// <param name="cancellationToken">Checked before each statement.</param>
    /// <exception cref="LadybugException">The engine rejected a statement - typically because a table already exists.</exception>
    public async ValueTask CreateTablesAsync(LadybugConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        foreach (var statement in CreateTableStatements())
        {
            await connection.ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Compares every descriptor with the database's catalog and throws once, listing every
    /// mismatch: a missing table or one of the wrong kind, a missing column, a column whose engine
    /// type the property cannot read (under <see cref="LadybugConnection.Select{T}"/>'s widening
    /// rule, so an <c>INT32</c> column into a <see langword="long"/> is fine and the reverse is
    /// not), a key on the wrong column, a relationship between the wrong tables. Extra columns and
    /// tables in the database are not mismatches. Names compare case-insensitively, as the engine
    /// resolves them.
    /// </summary>
    /// <param name="connection">A connection to the database to check.</param>
    /// <param name="cancellationToken">Checked before each catalog call.</param>
    /// <exception cref="SchemaMismatchException">At least one mismatch was found; <see cref="SchemaMismatchException.Mismatches"/> lists them all.</exception>
    public async ValueTask ValidateAsync(LadybugConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var mismatches = new List<string>();
        var tables = (await CatalogReader.ShowTablesAsync(connection, cancellationToken).ConfigureAwait(false))
            .ToDictionary(t => t.Name, t => t.Kind, StringComparer.OrdinalIgnoreCase);

        foreach (var node in Nodes)
        {
            if (!TableExists(tables, node.Table, "NODE", node.ClrType, mismatches)) continue;
            var columns = await CatalogReader.TableInfoAsync(connection, node.Table, cancellationToken).ConfigureAwait(false);
            CheckColumns(node.Table, node.ClrType, node.Properties, columns, mismatches);

            var key = columns.FirstOrDefault(c => c.IsPrimaryKey);
            if (key is not null && !Same(key.Name, node.Key.Column))
            {
                mismatches.Add(
                    $"Table '{node.Table}': the primary key is column '{key.Name}', but {node.ClrType.Name} marks " +
                    $"'{node.Key.ClrName}' (column '{node.Key.Column}') with [Key].");
            }
        }

        foreach (var rel in Rels)
        {
            if (!TableExists(tables, rel.Table, "REL", rel.ClrType, mismatches)) continue;
            var columns = await CatalogReader.TableInfoAsync(connection, rel.Table, cancellationToken).ConfigureAwait(false);
            CheckColumns(rel.Table, rel.ClrType, rel.Properties, columns, mismatches);

            var connections = await CatalogReader.ShowConnectionAsync(connection, rel.Table, cancellationToken).ConfigureAwait(false);
            if (!connections.Any(c => Same(c.From, rel.From.Table) && Same(c.To, rel.To.Table)))
            {
                mismatches.Add(
                    $"Table '{rel.Table}': connects {string.Join(" and ", connections.Select(c => $"'{c.From}' to '{c.To}'"))}, " +
                    $"but {rel.ClrType.Name} declares From = {rel.From.ClrType.Name} ('{rel.From.Table}') and To = {rel.To.ClrType.Name} ('{rel.To.Table}').");
            }
        }

        if (mismatches.Count > 0) throw new SchemaMismatchException(mismatches);
    }

    private static bool TableExists(Dictionary<string, string> tables, string table, string kind, Type clrType, List<string> mismatches)
    {
        if (!tables.TryGetValue(table, out var actualKind))
        {
            mismatches.Add($"Table '{table}' for {clrType.Name} does not exist.");
            return false;
        }

        if (!Same(actualKind, kind))
        {
            mismatches.Add($"Table '{table}' for {clrType.Name} is a {actualKind} table, not a {kind} table.");
            return false;
        }

        return true;
    }

    private static void CheckColumns(string table, Type clrType, IReadOnlyList<PropertyDescriptor> properties, List<CatalogColumn> columns, List<string> mismatches)
    {
        foreach (var property in properties)
        {
            var column = columns.FirstOrDefault(c => Same(c.Name, property.Column));
            if (column is null)
            {
                mismatches.Add($"Table '{table}': column '{property.Column}' for {clrType.Name}.{property.ClrName} does not exist.");
                continue;
            }

            var engineType = EngineTypeMap.Parse(column.Type);
            if (engineType is null || !EngineTypeMap.CanRead(property.ClrType, engineType.Value))
            {
                mismatches.Add(
                    $"Table '{table}': column '{column.Name}' is {column.Type}, which {clrType.Name}.{property.ClrName} " +
                    $"({EngineTypeMap.Describe(property.ClrType)}) cannot read - only lossless widening is accepted, " +
                    $"never narrowing.");
            }
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ----------------------------------------------------------------------------- building

    private int _nextOrder;

    [RequiresUnreferencedCode("Reads the type's attributes and public properties by reflection.")]
    private void Add(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type.IsDefined(typeof(NodeAttribute), inherit: false)) AddNode(type);
        else if (type.IsDefined(typeof(RelAttribute), inherit: false)) AddRel(type);
        else
        {
            throw new InvalidOperationException(
                $"{type.Name} carries neither [Node] nor [Rel], so it does not describe a table.");
        }
    }

    [RequiresUnreferencedCode("Reads the type's attributes and public properties by reflection.")]
    private NodeDescriptor AddNode(Type type)
    {
        if (_nodes.TryGetValue(type, out var existing)) return existing;
        var attribute = type.GetCustomAttribute<NodeAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{type.Name} carries no [Node] attribute, so it does not describe a node table.");

        var properties = DescribeProperties(type);
        var keys = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.IsDefined(typeof(KeyAttribute), inherit: false))
            .Select(p => p.Name)
            .ToArray();
        if (keys.Length != 1)
        {
            throw new InvalidOperationException(keys.Length == 0
                ? $"{type.Name} has no [Key] property. A node table needs a primary key; mark one property [Key] " +
                  "(on a positional record, [property: Key])."
                : $"{type.Name} has {keys.Length} [Key] properties ({string.Join(", ", keys.Select(k => $"'{k}'"))}); a node table has exactly one primary key.");
        }

        var key = properties.First(p => p.ClrName == keys[0]);
        var node = new NodeDescriptor(type, attribute.Table, properties, key) { Order = Interlocked.Increment(ref _nextOrder) };
        return _nodes.GetOrAdd(type, node);
    }

    [RequiresUnreferencedCode("Reads the type's attributes and public properties by reflection.")]
    private RelDescriptor AddRel(Type type)
    {
        if (_rels.TryGetValue(type, out var existing)) return existing;
        var attribute = type.GetCustomAttribute<RelAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{type.Name} carries no [Rel] attribute, so it does not describe a relationship table.");

        var from = Endpoint(type, attribute.From, nameof(RelAttribute.From));
        var to = Endpoint(type, attribute.To, nameof(RelAttribute.To));
        var rel = new RelDescriptor(type, attribute.Table, from, to, DescribeProperties(type)) { Order = Interlocked.Increment(ref _nextOrder) };
        return _rels.GetOrAdd(type, rel);
    }

    [RequiresUnreferencedCode("Reads the type's attributes and public properties by reflection.")]
    private NodeDescriptor Endpoint(Type rel, Type? endpoint, string role)
    {
        if (endpoint is null)
            throw new InvalidOperationException($"{rel.Name}'s [Rel] attribute does not set {role}; a relationship needs both endpoint types.");
        if (!endpoint.IsDefined(typeof(NodeAttribute), inherit: false))
        {
            throw new InvalidOperationException(
                $"{rel.Name}'s [Rel] attribute names {endpoint.Name} as {role}, but {endpoint.Name} carries no [Node] attribute. " +
                "A relationship's endpoints must be [Node] types.");
        }

        return AddNode(endpoint);
    }

    [RequiresUnreferencedCode("Reads the type's public properties by reflection.")]
    private static PropertyDescriptor[] DescribeProperties(Type type)
    {
        // MetadataToken order is declaration order, which GetProperties does not promise; DDL and
        // messages read better when columns come out in the order the record declares them.
        var members = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.MetadataToken);

        var descriptors = new List<PropertyDescriptor>();
        foreach (var member in members)
        {
            var entry = EngineTypeMap.Get(member.PropertyType)
                ?? throw new InvalidOperationException(
                    $"{type.Name}.{member.Name}: property '{member.Name}' is of type {EngineTypeMap.Describe(member.PropertyType)}, " +
                    "which maps to no engine column type. Supported: " +
                    $"{string.Join(", ", EngineTypeMap.ClrTypes.Select(EngineTypeMap.Describe).Order(StringComparer.Ordinal))} (each also as a nullable value type).");

            var column = member.GetCustomAttribute<ColumnAttribute>(inherit: false)?.Name ?? DefaultColumnName(member.Name);
            descriptors.Add(new PropertyDescriptor(column, member.Name, member.PropertyType, entry.Exact, entry.Ddl));
        }

        return [.. descriptors];
    }

    private static string DefaultColumnName(string clrName) =>
        clrName.Length > 0 && char.IsUpper(clrName[0]) ? char.ToLowerInvariant(clrName[0]) + clrName[1..] : clrName;
}
