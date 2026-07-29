namespace LadybugDb.Client;

/// <summary>
/// A single row of query results, already fully marshalled into managed memory.
/// </summary>
/// <remarks>
/// Temporary shape for Milestone 2 Task 1: it wraps a pre-materialized array of every column in
/// the row. Task 4 replaces this with full result-set enumeration.
/// </remarks>
public readonly struct LadybugRow
{
    private readonly LadybugValue[] _values;

    internal LadybugRow(LadybugValue[] values) => _values = values;

    /// <summary>The number of columns in this row.</summary>
    public int ColumnCount => _values.Length;

    /// <summary>Gets the value at <paramref name="index"/>.</summary>
    /// <param name="index">The zero-based column index.</param>
    public LadybugValue GetValue(int index) => _values[index];
}
