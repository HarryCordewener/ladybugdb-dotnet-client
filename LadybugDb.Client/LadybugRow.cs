using ExtendedNumerics;
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

    // ---------------------------------------------------------------- typed column accessors
    //
    // GetValue(i).AsInt64() is two hops for the single most common thing a caller does with a row.
    // These collapse it to one, by index and by column name, one pair per type the engine returns.
    // They add no conversion behaviour of their own: each is exactly the corresponding LadybugValue
    // accessor, so the widening, precision and null rules - and the exceptions that report them -
    // stay defined in one place rather than being restated per row accessor.


    /// <summary>Reads column <paramref name="index"/> as bool, via <see cref="LadybugValue.AsBoolean"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as bool.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>BOOL</c> this accessor can read - see <see cref="LadybugValue.AsBoolean"/>.</exception>
    public bool GetBoolean(int index) => GetValue(index).AsBoolean();

    /// <summary>Reads column <paramref name="columnName"/> as bool, via <see cref="LadybugValue.AsBoolean"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as bool.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>BOOL</c> this accessor can read - see <see cref="LadybugValue.AsBoolean"/>.</exception>
    public bool GetBoolean(string columnName) => this[columnName].AsBoolean();

    /// <summary>Reads column <paramref name="index"/> as long, via <see cref="LadybugValue.AsInt64"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as long.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT64</c> this accessor can read - see <see cref="LadybugValue.AsInt64"/>.</exception>
    public long GetInt64(int index) => GetValue(index).AsInt64();

    /// <summary>Reads column <paramref name="columnName"/> as long, via <see cref="LadybugValue.AsInt64"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as long.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT64</c> this accessor can read - see <see cref="LadybugValue.AsInt64"/>.</exception>
    public long GetInt64(string columnName) => this[columnName].AsInt64();

    /// <summary>Reads column <paramref name="index"/> as int, via <see cref="LadybugValue.AsInt32"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as int.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT32</c> this accessor can read - see <see cref="LadybugValue.AsInt32"/>.</exception>
    public int GetInt32(int index) => GetValue(index).AsInt32();

    /// <summary>Reads column <paramref name="columnName"/> as int, via <see cref="LadybugValue.AsInt32"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as int.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT32</c> this accessor can read - see <see cref="LadybugValue.AsInt32"/>.</exception>
    public int GetInt32(string columnName) => this[columnName].AsInt32();

    /// <summary>Reads column <paramref name="index"/> as short, via <see cref="LadybugValue.AsInt16"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as short.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT16</c> this accessor can read - see <see cref="LadybugValue.AsInt16"/>.</exception>
    public short GetInt16(int index) => GetValue(index).AsInt16();

    /// <summary>Reads column <paramref name="columnName"/> as short, via <see cref="LadybugValue.AsInt16"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as short.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT16</c> this accessor can read - see <see cref="LadybugValue.AsInt16"/>.</exception>
    public short GetInt16(string columnName) => this[columnName].AsInt16();

    /// <summary>Reads column <paramref name="index"/> as sbyte, via <see cref="LadybugValue.AsSByte"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as sbyte.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT8</c> this accessor can read - see <see cref="LadybugValue.AsSByte"/>.</exception>
    public sbyte GetSByte(int index) => GetValue(index).AsSByte();

    /// <summary>Reads column <paramref name="columnName"/> as sbyte, via <see cref="LadybugValue.AsSByte"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as sbyte.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT8</c> this accessor can read - see <see cref="LadybugValue.AsSByte"/>.</exception>
    public sbyte GetSByte(string columnName) => this[columnName].AsSByte();

    /// <summary>Reads column <paramref name="index"/> as ulong, via <see cref="LadybugValue.AsUInt64"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as ulong.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT64</c> this accessor can read - see <see cref="LadybugValue.AsUInt64"/>.</exception>
    public ulong GetUInt64(int index) => GetValue(index).AsUInt64();

    /// <summary>Reads column <paramref name="columnName"/> as ulong, via <see cref="LadybugValue.AsUInt64"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as ulong.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT64</c> this accessor can read - see <see cref="LadybugValue.AsUInt64"/>.</exception>
    public ulong GetUInt64(string columnName) => this[columnName].AsUInt64();

    /// <summary>Reads column <paramref name="index"/> as uint, via <see cref="LadybugValue.AsUInt32"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as uint.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT32</c> this accessor can read - see <see cref="LadybugValue.AsUInt32"/>.</exception>
    public uint GetUInt32(int index) => GetValue(index).AsUInt32();

    /// <summary>Reads column <paramref name="columnName"/> as uint, via <see cref="LadybugValue.AsUInt32"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as uint.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT32</c> this accessor can read - see <see cref="LadybugValue.AsUInt32"/>.</exception>
    public uint GetUInt32(string columnName) => this[columnName].AsUInt32();

    /// <summary>Reads column <paramref name="index"/> as ushort, via <see cref="LadybugValue.AsUInt16"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as ushort.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT16</c> this accessor can read - see <see cref="LadybugValue.AsUInt16"/>.</exception>
    public ushort GetUInt16(int index) => GetValue(index).AsUInt16();

    /// <summary>Reads column <paramref name="columnName"/> as ushort, via <see cref="LadybugValue.AsUInt16"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as ushort.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT16</c> this accessor can read - see <see cref="LadybugValue.AsUInt16"/>.</exception>
    public ushort GetUInt16(string columnName) => this[columnName].AsUInt16();

    /// <summary>Reads column <paramref name="index"/> as byte, via <see cref="LadybugValue.AsByte"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as byte.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT8</c> this accessor can read - see <see cref="LadybugValue.AsByte"/>.</exception>
    public byte GetByte(int index) => GetValue(index).AsByte();

    /// <summary>Reads column <paramref name="columnName"/> as byte, via <see cref="LadybugValue.AsByte"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as byte.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UINT8</c> this accessor can read - see <see cref="LadybugValue.AsByte"/>.</exception>
    public byte GetByte(string columnName) => this[columnName].AsByte();

    /// <summary>Reads column <paramref name="index"/> as Int128, via <see cref="LadybugValue.AsInt128"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as Int128.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT128</c> this accessor can read - see <see cref="LadybugValue.AsInt128"/>.</exception>
    public Int128 GetInt128(int index) => GetValue(index).AsInt128();

    /// <summary>Reads column <paramref name="columnName"/> as Int128, via <see cref="LadybugValue.AsInt128"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as Int128.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INT128</c> this accessor can read - see <see cref="LadybugValue.AsInt128"/>.</exception>
    public Int128 GetInt128(string columnName) => this[columnName].AsInt128();

    /// <summary>Reads column <paramref name="index"/> as float, via <see cref="LadybugValue.AsSingle"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as float.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>FLOAT</c> this accessor can read - see <see cref="LadybugValue.AsSingle"/>.</exception>
    public float GetSingle(int index) => GetValue(index).AsSingle();

    /// <summary>Reads column <paramref name="columnName"/> as float, via <see cref="LadybugValue.AsSingle"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as float.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>FLOAT</c> this accessor can read - see <see cref="LadybugValue.AsSingle"/>.</exception>
    public float GetSingle(string columnName) => this[columnName].AsSingle();

    /// <summary>Reads column <paramref name="index"/> as double, via <see cref="LadybugValue.AsDouble"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as double.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DOUBLE</c> this accessor can read - see <see cref="LadybugValue.AsDouble"/>.</exception>
    public double GetDouble(int index) => GetValue(index).AsDouble();

    /// <summary>Reads column <paramref name="columnName"/> as double, via <see cref="LadybugValue.AsDouble"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as double.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DOUBLE</c> this accessor can read - see <see cref="LadybugValue.AsDouble"/>.</exception>
    public double GetDouble(string columnName) => this[columnName].AsDouble();

    /// <summary>Reads column <paramref name="index"/> as decimal, via <see cref="LadybugValue.AsDecimal"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as decimal.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DECIMAL</c> this accessor can read - see <see cref="LadybugValue.AsDecimal"/>.</exception>
    public decimal GetDecimal(int index) => GetValue(index).AsDecimal();

    /// <summary>Reads column <paramref name="columnName"/> as decimal, via <see cref="LadybugValue.AsDecimal"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as decimal.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DECIMAL</c> this accessor can read - see <see cref="LadybugValue.AsDecimal"/>.</exception>
    public decimal GetDecimal(string columnName) => this[columnName].AsDecimal();

    /// <summary>Reads column <paramref name="index"/> as BigDecimal, via <see cref="LadybugValue.AsBigDecimal"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as BigDecimal.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DECIMAL</c> this accessor can read - see <see cref="LadybugValue.AsBigDecimal"/>.</exception>
    public BigDecimal GetBigDecimal(int index) => GetValue(index).AsBigDecimal();

    /// <summary>Reads column <paramref name="columnName"/> as BigDecimal, via <see cref="LadybugValue.AsBigDecimal"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as BigDecimal.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DECIMAL</c> this accessor can read - see <see cref="LadybugValue.AsBigDecimal"/>.</exception>
    public BigDecimal GetBigDecimal(string columnName) => this[columnName].AsBigDecimal();

    /// <summary>Reads column <paramref name="index"/> as string, via <see cref="LadybugValue.AsString"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as string.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>STRING</c> this accessor can read - see <see cref="LadybugValue.AsString"/>.</exception>
    public string GetString(int index) => GetValue(index).AsString();

    /// <summary>Reads column <paramref name="columnName"/> as string, via <see cref="LadybugValue.AsString"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as string.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>STRING</c> this accessor can read - see <see cref="LadybugValue.AsString"/>.</exception>
    public string GetString(string columnName) => this[columnName].AsString();

    /// <summary>Reads column <paramref name="index"/> as DateOnly, via <see cref="LadybugValue.AsDateOnly"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as DateOnly.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DATE</c> this accessor can read - see <see cref="LadybugValue.AsDateOnly"/>.</exception>
    public DateOnly GetDateOnly(int index) => GetValue(index).AsDateOnly();

    /// <summary>Reads column <paramref name="columnName"/> as DateOnly, via <see cref="LadybugValue.AsDateOnly"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as DateOnly.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>DATE</c> this accessor can read - see <see cref="LadybugValue.AsDateOnly"/>.</exception>
    public DateOnly GetDateOnly(string columnName) => this[columnName].AsDateOnly();

    /// <summary>Reads column <paramref name="index"/> as DateTime, via <see cref="LadybugValue.AsDateTime"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as DateTime.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>TIMESTAMP</c> this accessor can read - see <see cref="LadybugValue.AsDateTime"/>.</exception>
    public DateTime GetDateTime(int index) => GetValue(index).AsDateTime();

    /// <summary>Reads column <paramref name="columnName"/> as DateTime, via <see cref="LadybugValue.AsDateTime"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as DateTime.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>TIMESTAMP</c> this accessor can read - see <see cref="LadybugValue.AsDateTime"/>.</exception>
    public DateTime GetDateTime(string columnName) => this[columnName].AsDateTime();

    /// <summary>Reads column <paramref name="index"/> as DateTimeOffset, via <see cref="LadybugValue.AsDateTimeOffset"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as DateTimeOffset.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>TIMESTAMP_TZ</c> this accessor can read - see <see cref="LadybugValue.AsDateTimeOffset"/>.</exception>
    public DateTimeOffset GetDateTimeOffset(int index) => GetValue(index).AsDateTimeOffset();

    /// <summary>Reads column <paramref name="columnName"/> as DateTimeOffset, via <see cref="LadybugValue.AsDateTimeOffset"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as DateTimeOffset.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>TIMESTAMP_TZ</c> this accessor can read - see <see cref="LadybugValue.AsDateTimeOffset"/>.</exception>
    public DateTimeOffset GetDateTimeOffset(string columnName) => this[columnName].AsDateTimeOffset();

    /// <summary>Reads column <paramref name="index"/> as TimeSpan, via <see cref="LadybugValue.AsTimeSpan"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as TimeSpan.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INTERVAL</c> this accessor can read - see <see cref="LadybugValue.AsTimeSpan"/>.</exception>
    public TimeSpan GetTimeSpan(int index) => GetValue(index).AsTimeSpan();

    /// <summary>Reads column <paramref name="columnName"/> as TimeSpan, via <see cref="LadybugValue.AsTimeSpan"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as TimeSpan.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>INTERVAL</c> this accessor can read - see <see cref="LadybugValue.AsTimeSpan"/>.</exception>
    public TimeSpan GetTimeSpan(string columnName) => this[columnName].AsTimeSpan();

    /// <summary>Reads column <paramref name="index"/> as byte[], via <see cref="LadybugValue.AsBlob"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as byte[].</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>BLOB</c> this accessor can read - see <see cref="LadybugValue.AsBlob"/>.</exception>
    public byte[] GetBlob(int index) => GetValue(index).AsBlob();

    /// <summary>Reads column <paramref name="columnName"/> as byte[], via <see cref="LadybugValue.AsBlob"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as byte[].</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not a <c>BLOB</c> this accessor can read - see <see cref="LadybugValue.AsBlob"/>.</exception>
    public byte[] GetBlob(string columnName) => this[columnName].AsBlob();

    /// <summary>Reads column <paramref name="index"/> as Guid, via <see cref="LadybugValue.AsGuid"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    /// <returns>The column's value as Guid.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside <see cref="ColumnCount"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UUID</c> this accessor can read - see <see cref="LadybugValue.AsGuid"/>.</exception>
    public Guid GetGuid(int index) => GetValue(index).AsGuid();

    /// <summary>Reads column <paramref name="columnName"/> as Guid, via <see cref="LadybugValue.AsGuid"/>.</summary>
    /// <param name="columnName">The column name, as returned by the query.</param>
    /// <returns>The column's value as Guid.</returns>
    /// <exception cref="KeyNotFoundException">No column is named <paramref name="columnName"/>.</exception>
    /// <exception cref="LadybugException">The column is not an <c>UUID</c> this accessor can read - see <see cref="LadybugValue.AsGuid"/>.</exception>
    public Guid GetGuid(string columnName) => this[columnName].AsGuid();
}
