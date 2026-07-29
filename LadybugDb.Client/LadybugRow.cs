namespace LadybugDb.Client;

/// <summary>
/// A single row of query results, already fully marshalled into managed memory.
/// </summary>
/// <remarks>
/// Wraps a pre-materialized array of every column in the row, plus a reference to the column-name
/// array <see cref="LadybugQueryResult"/> read once when the result was created (shared, not
/// copied, across every row of that result - see <see cref="LadybugQueryResult"/>'s remarks on
/// why column names are read exactly once rather than per row).
/// </remarks>
public readonly struct LadybugRow
{
    private readonly LadybugValue[] _values;
    private readonly string[] _columnNames;

    internal LadybugRow(LadybugValue[] values, string[] columnNames)
    {
        _values = values;
        _columnNames = columnNames;
    }

    /// <summary>The number of columns in this row.</summary>
    public int ColumnCount => _values.Length;

    /// <summary>Gets the value at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or &gt;= <see cref="ColumnCount"/>.</exception>
    public LadybugValue GetValue(int index) => _values[index];

    /// <summary>Gets the name of the column at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or &gt;= <see cref="ColumnCount"/>.</exception>
    public string GetColumnName(int index) => _columnNames[index];

    /// <summary>
    /// Gets the value of the column named <paramref name="columnName"/>. If more than one column
    /// has this name - legal Cypher, e.g. <c>RETURN n.a AS x, n.b AS x</c> - resolves to the
    /// first match, the same "leftmost wins" behavior <c>System.Data</c>'s
    /// <c>DataRow[string]</c>/ADO.NET column lookups use for duplicate column names.
    /// </summary>
    /// <param name="columnName">The column name, as returned by the query (an alias, if the Cypher used <c>AS</c>).</param>
    /// <exception cref="ArgumentException">No column has this name.</exception>
    public LadybugValue this[string columnName]
    {
        get
        {
            var index = Array.IndexOf(_columnNames, columnName);
            if (index < 0)
                throw new ArgumentException($"No column named '{columnName}'.", nameof(columnName));
            return _values[index];
        }
    }
}
