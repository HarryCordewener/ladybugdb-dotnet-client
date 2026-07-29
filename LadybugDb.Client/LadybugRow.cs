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

    /// <summary>
    /// The number of columns in this row. <c>0</c> for <c>default(LadybugRow)</c> rather than
    /// throwing - see <see cref="GetValue"/>/<see cref="GetColumnName"/> for why the other
    /// accessors throw instead of also defaulting silently.
    /// </summary>
    public int ColumnCount => _values?.Length ?? 0;

    /// <summary>Gets the value at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or &gt;= <see cref="ColumnCount"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This is <c>default(LadybugRow)</c> - never produced by <see cref="LadybugQueryResult"/>'s
    /// own enumerator (its <c>Current</c> throws first rather than ever handing one out), but
    /// reachable if a caller declares <c>LadybugRow</c> directly (e.g. as a field default or via
    /// <see langword="default"/>) instead of obtaining one from a query result.
    /// </exception>
    public LadybugValue GetValue(int index) => _values is null ? throw Uninitialized() : _values[index];

    /// <summary>Gets the name of the column at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is negative or &gt;= <see cref="ColumnCount"/>.</exception>
    /// <exception cref="InvalidOperationException">This is <c>default(LadybugRow)</c> - see <see cref="GetValue"/>.</exception>
    public string GetColumnName(int index) => _columnNames is null ? throw Uninitialized() : _columnNames[index];

    /// <summary>
    /// Gets the value of the column named <paramref name="columnName"/>. If more than one column
    /// has this name - legal Cypher, e.g. <c>RETURN n.a AS x, n.b AS x</c> - resolves to the
    /// first match, the same "leftmost wins" behavior <c>System.Data</c>'s
    /// <c>DataRow[string]</c>/ADO.NET column lookups use for duplicate column names.
    /// </summary>
    /// <param name="columnName">The column name, as returned by the query (an alias, if the Cypher used <c>AS</c>).</param>
    /// <exception cref="ArgumentException">No column has this name.</exception>
    /// <exception cref="InvalidOperationException">This is <c>default(LadybugRow)</c> - see <see cref="GetValue"/>.</exception>
    public LadybugValue this[string columnName]
    {
        get
        {
            if (_columnNames is null) throw Uninitialized();

            var index = Array.IndexOf(_columnNames, columnName);
            if (index < 0)
                throw new ArgumentException($"No column named '{columnName}'.", nameof(columnName));
            return _values![index];
        }
    }

    private static InvalidOperationException Uninitialized() => new(
        "This LadybugRow is uninitialized (default(LadybugRow)) and carries no columns. Obtain " +
        "a LadybugRow from LadybugQueryResult's enumerator instead of declaring one directly.");

    /// <summary>
    /// A human-readable rendering of this row as <c>columnName: value, ...</c> pairs. Exists so
    /// debugger output and failed-assertion messages show the row's actual contents instead of the
    /// useless <c>LadybugDb.Client.LadybugRow</c> the inherited <see cref="ValueType.ToString"/>
    /// would otherwise print regardless of what the row holds - the same reasoning as
    /// <see cref="LadybugValue.ToString"/>. <c>default(LadybugRow)</c> renders as <c>"{}"</c>
    /// rather than throwing, matching <see cref="ColumnCount"/>'s own default-safety.
    /// </summary>
    public override string ToString()
    {
        var values = _values;
        var columnNames = _columnNames;
        if (values is null) return "{}";
        return "{" + string.Join(", ", values.Select((v, i) => $"{columnNames[i]}: {v}")) + "}";
    }
}
