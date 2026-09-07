using System.Text;
using LadybugDb.Client.Cypher;

namespace LadybugDb.Client.Schema;

/// <summary>One column of a table definition: its name and its engine type, as the engine spells it.</summary>
/// <param name="Name">The column name; backticked by the renderer when it is not a plain identifier.</param>
/// <param name="Type">
/// The engine type name: <c>INT64</c>, <c>STRING</c>, <c>DECIMAL(38, 10)</c>, <c>STRING[]</c>, ...
/// Rendered verbatim, so it is restricted to the shape of a type name - see <see cref="Ddl"/>.
/// </param>
public sealed record ColumnDefinition(string Name, string Type);

/// <summary>
/// Renders the engine's DDL statements: <c>CREATE NODE TABLE</c>, <c>CREATE REL TABLE</c>,
/// <c>DROP TABLE</c>. DDL takes no parameters, so everything here is interpolated - which is why
/// every piece is either an identifier (rendered through <see cref="Identifier"/>) or a type name
/// checked against the narrow grammar <see cref="TypeName.IsValid"/> accepts.
/// </summary>
public static class Ddl
{
    /// <summary><c>CREATE NODE TABLE name(column TYPE, ..., PRIMARY KEY(key))</c>.</summary>
    /// <param name="name">The table name.</param>
    /// <param name="columns">The columns, in declaration order. At least one, and one of them the key.</param>
    /// <param name="primaryKey">The name of the primary-key column, which must be one of <paramref name="columns"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty, names a column twice, carries a type that is not a type name, or does not contain <paramref name="primaryKey"/>.</exception>
    public static string CreateNodeTable(string name, IReadOnlyList<ColumnDefinition> columns, string primaryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKey);
        if (columns.Count == 0)
            throw new ArgumentException($"Node table '{name}' needs at least one column.", nameof(columns));
        if (!columns.Any(c => string.Equals(c.Name, primaryKey, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"Primary key '{primaryKey}' of node table '{name}' is not one of its columns " +
                $"({string.Join(", ", columns.Select(c => $"'{c.Name}'"))}).", nameof(primaryKey));
        }

        var text = new StringBuilder("CREATE NODE TABLE ").Append(Identifier.Render(name)).Append('(');
        AppendColumns(text, name, columns);
        return text.Append(", PRIMARY KEY(").Append(Identifier.Render(primaryKey)).Append("))").ToString();
    }

    /// <summary><c>CREATE REL TABLE name(FROM from TO to, column TYPE, ...)</c>.</summary>
    /// <param name="name">The relationship table name.</param>
    /// <param name="from">The source node table.</param>
    /// <param name="to">The destination node table.</param>
    /// <param name="columns">The relationship's own columns, possibly none.</param>
    /// <exception cref="ArgumentException">A column is named twice or carries a type that is not a type name.</exception>
    public static string CreateRelTable(string name, string from, string to, IReadOnlyList<ColumnDefinition> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(from);
        ArgumentException.ThrowIfNullOrWhiteSpace(to);
        ArgumentNullException.ThrowIfNull(columns);

        var text = new StringBuilder("CREATE REL TABLE ").Append(Identifier.Render(name))
            .Append("(FROM ").Append(Identifier.Render(from)).Append(" TO ").Append(Identifier.Render(to));
        if (columns.Count > 0)
        {
            text.Append(", ");
            AppendColumns(text, name, columns);
        }

        return text.Append(')').ToString();
    }

    /// <summary><c>DROP TABLE name</c>.</summary>
    /// <param name="name">The table name.</param>
    public static string DropTable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return "DROP TABLE " + Identifier.Render(name);
    }

    /// <summary>Whether <paramref name="type"/> has the shape of an engine type name - see <see cref="TypeName.IsValid"/>.</summary>
    /// <param name="type">The type name to test.</param>
    public static bool IsTypeName(string type) => TypeName.IsValid(type);

    private static void AppendColumns(StringBuilder text, string table, IReadOnlyList<ColumnDefinition> columns)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            ArgumentNullException.ThrowIfNull(column);
            if (!seen.Add(column.Name))
                throw new ArgumentException($"Column '{column.Name}' of table '{table}' is declared twice.", nameof(columns));
            if (!IsTypeName(column.Type))
            {
                throw new ArgumentException(
                    $"Column '{column.Name}' of table '{table}' has type '{column.Type}', which is not a type name. " +
                    "A type is a name such as INT64 or STRING, optionally with a parenthesized digit list " +
                    "(DECIMAL(38, 10)) or a list suffix (STRING[]).", nameof(columns));
            }

            if (i > 0) text.Append(", ");
            text.Append(Identifier.Render(column.Name)).Append(' ').Append(column.Type);
        }
    }
}
