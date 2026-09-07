namespace LadybugDb.Client.Schema;

/// <summary>A row of <c>CALL show_tables()</c>.</summary>
/// <param name="Name">The table name.</param>
/// <param name="Kind"><c>NODE</c> or <c>REL</c>.</param>
internal sealed record CatalogTable(string Name, string Kind);

/// <summary>A row of <c>CALL table_info('T')</c>.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Type">The engine type text, e.g. <c>INT64</c> or <c>DECIMAL(38, 10)</c>.</param>
/// <param name="IsPrimaryKey">Whether the column is the primary key. Always <see langword="false"/> for a relationship table, whose <c>table_info</c> has no such column.</param>
internal sealed record CatalogColumn(string Name, string Type, bool IsPrimaryKey);

/// <summary>A row of <c>CALL show_connection('R')</c>.</summary>
/// <param name="From">The source node table.</param>
/// <param name="To">The destination node table.</param>
internal sealed record CatalogConnection(string From, string To);

/// <summary>
/// Reads the catalog through the only interface the engine offers for it: three Cypher
/// <c>CALL</c>s. The C API has no catalog functions. Column names below are the engine's own,
/// verified against it in the integration suite's <c>SchemaValidationTests</c>:
/// <c>show_tables()</c> returns <c>id, name, type, database name, comment</c>;
/// <c>table_info('T')</c> returns <c>property id, name, type, default expression</c> and then
/// <c>primary key</c> for a node table or <c>storage_direction</c> for a relationship table;
/// <c>show_connection('R')</c> returns <c>source table name, destination table name, source table
/// primary key, destination table primary key</c>.
/// </summary>
internal static class CatalogReader
{
    /// <summary>
    /// The one place this client interpolates a value into Cypher. A <c>CALL</c> argument must be a
    /// literal - measured: <c>CALL table_info($t)</c> is refused with "Binder exception: $_0_ has
    /// type PARAMETER but LITERAL was expected" - so the table name is written as a single-quoted
    /// string with the engine's backslash escaping applied to the two characters that could end or
    /// escape it.
    /// </summary>
    private static string StringLiteral(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    internal static async ValueTask<List<CatalogTable>> ShowTablesAsync(LadybugConnection connection, CancellationToken cancellationToken)
    {
        await using var result = await connection.QueryAsync("CALL show_tables() RETURN *", cancellationToken).ConfigureAwait(false);
        var tables = new List<CatalogTable>();
        await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            tables.Add(new CatalogTable(row.GetString("name"), row.GetString("type")));
        }

        return tables;
    }

    internal static async ValueTask<List<CatalogColumn>> TableInfoAsync(LadybugConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var result = await connection.QueryAsync(
            "CALL table_info(" + StringLiteral(table) + ") RETURN *", cancellationToken).ConfigureAwait(false);
        var hasKeyColumn = result.ColumnNames.Contains("primary key", StringComparer.Ordinal);
        var columns = new List<CatalogColumn>();
        await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(new CatalogColumn(row.GetString("name"), row.GetString("type"), hasKeyColumn && row.GetBoolean("primary key")));
        }

        return columns;
    }

    internal static async ValueTask<List<CatalogConnection>> ShowConnectionAsync(LadybugConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var result = await connection.QueryAsync(
            "CALL show_connection(" + StringLiteral(table) + ") RETURN *", cancellationToken).ConfigureAwait(false);
        var connections = new List<CatalogConnection>();
        await foreach (var row in result.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            connections.Add(new CatalogConnection(row.GetString("source table name"), row.GetString("destination table name")));
        }

        return connections;
    }
}
