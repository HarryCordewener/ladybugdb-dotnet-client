using System.Collections.Frozen;
using ExtendedNumerics;
using LadybugDb.Client.Mapping;

namespace LadybugDb.Client.Schema;

/// <summary>
/// The CLR-to-engine type map the schema layer runs on: for each CLR type the DDL type it creates,
/// and the engine column types it can be read from. The read direction is <see cref="RowMapper"/>'s
/// lossless-widening rule restated as data; the unit suite pins the two together for every pair,
/// so the rule is decided once, in <see cref="RowMapper"/>, and only mirrored here.
/// </summary>
internal static class EngineTypeMap
{
    /// <summary>One CLR type's mapping.</summary>
    /// <param name="Ddl">The engine type a column created for this CLR type gets.</param>
    /// <param name="Exact">The <see cref="LadybugType"/> that backs the CLR type exactly.</param>
    /// <param name="Readable">Every column type the CLR type reads, <paramref name="Exact"/> included.</param>
    internal sealed record Entry(string Ddl, LadybugType Exact, LadybugType[] Readable);

    private static readonly FrozenDictionary<Type, Entry> Entries = new Dictionary<Type, Entry>
    {
        [typeof(bool)] = new("BOOL", LadybugType.Boolean, [LadybugType.Boolean]),
        [typeof(sbyte)] = new("INT8", LadybugType.Int8, [LadybugType.Int8]),
        [typeof(byte)] = new("UINT8", LadybugType.UInt8, [LadybugType.UInt8]),
        [typeof(float)] = new("FLOAT", LadybugType.Single, [LadybugType.Single]),
        [typeof(short)] = new("INT16", LadybugType.Int16, [LadybugType.Int16, LadybugType.Int8, LadybugType.UInt8]),
        [typeof(int)] = new("INT32", LadybugType.Int32,
            [LadybugType.Int32, LadybugType.Int16, LadybugType.Int8, LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(long)] = new("INT64", LadybugType.Int64,
            [LadybugType.Int64, LadybugType.Int32, LadybugType.Int16, LadybugType.Int8, LadybugType.UInt32, LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(Int128)] = new("INT128", LadybugType.Int128,
            [LadybugType.Int128, LadybugType.Int64, LadybugType.Int32, LadybugType.Int16, LadybugType.Int8,
             LadybugType.UInt64, LadybugType.UInt32, LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(ushort)] = new("UINT16", LadybugType.UInt16, [LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(uint)] = new("UINT32", LadybugType.UInt32, [LadybugType.UInt32, LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(ulong)] = new("UINT64", LadybugType.UInt64, [LadybugType.UInt64, LadybugType.UInt32, LadybugType.UInt16, LadybugType.UInt8]),
        [typeof(double)] = new("DOUBLE", LadybugType.Double, [LadybugType.Double, LadybugType.Single]),

        // The engine's widest DECIMAL. A `decimal` property reads only the values that fit its 28-29
        // digits (RowMapper reports the rest, never rounds); BigDecimal reads all 38.
        [typeof(decimal)] = new("DECIMAL(38, 10)", LadybugType.Decimal, [LadybugType.Decimal]),
        [typeof(BigDecimal)] = new("DECIMAL(38, 10)", LadybugType.Decimal, [LadybugType.Decimal]),

        // AsString reads any value whose payload is already a string, which for a column means DECIMAL too.
        [typeof(string)] = new("STRING", LadybugType.String, [LadybugType.String, LadybugType.Decimal]),
        [typeof(byte[])] = new("BLOB", LadybugType.Blob, [LadybugType.Blob]),
        [typeof(Guid)] = new("UUID", LadybugType.Uuid, [LadybugType.Uuid]),
        [typeof(DateOnly)] = new("DATE", LadybugType.Date, [LadybugType.Date]),
        [typeof(DateTime)] = new("TIMESTAMP", LadybugType.Timestamp, [LadybugType.Timestamp]),
        [typeof(DateTimeOffset)] = new("TIMESTAMP_TZ", LadybugType.TimestampTz, [LadybugType.TimestampTz]),
        [typeof(TimeSpan)] = new("INTERVAL", LadybugType.Interval, [LadybugType.Interval]),
    }.ToFrozenDictionary();

    /// <summary>Engine type-name prefixes (the text before any <c>(</c> or <c>[</c>) and the <see cref="LadybugType"/> each names.</summary>
    private static readonly FrozenDictionary<string, LadybugType> Names = new Dictionary<string, LadybugType>(StringComparer.OrdinalIgnoreCase)
    {
        ["BOOL"] = LadybugType.Boolean, ["BOOLEAN"] = LadybugType.Boolean,
        ["INT8"] = LadybugType.Int8, ["INT16"] = LadybugType.Int16, ["INT32"] = LadybugType.Int32, ["INT64"] = LadybugType.Int64,
        ["UINT8"] = LadybugType.UInt8, ["UINT16"] = LadybugType.UInt16, ["UINT32"] = LadybugType.UInt32, ["UINT64"] = LadybugType.UInt64,
        ["INT128"] = LadybugType.Int128, ["SERIAL"] = LadybugType.Int64,
        ["FLOAT"] = LadybugType.Single, ["REAL"] = LadybugType.Single, ["DOUBLE"] = LadybugType.Double,
        ["DECIMAL"] = LadybugType.Decimal, ["STRING"] = LadybugType.String, ["BLOB"] = LadybugType.Blob, ["UUID"] = LadybugType.Uuid,
        ["DATE"] = LadybugType.Date,
        ["TIMESTAMP"] = LadybugType.Timestamp, ["TIMESTAMP_SEC"] = LadybugType.Timestamp, ["TIMESTAMP_MS"] = LadybugType.Timestamp, ["TIMESTAMP_NS"] = LadybugType.Timestamp,
        ["TIMESTAMP_TZ"] = LadybugType.TimestampTz, ["INTERVAL"] = LadybugType.Interval,
        ["STRUCT"] = LadybugType.Struct, ["MAP"] = LadybugType.Map, ["NODE"] = LadybugType.Node, ["REL"] = LadybugType.Rel,
        ["INTERNAL_ID"] = LadybugType.InternalId,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every CLR type in the map, for the test that pins it to <see cref="RowMapper"/>.</summary>
    internal static IEnumerable<Type> ClrTypes => Entries.Keys;

    /// <summary>The mapping for <paramref name="clrType"/> (a <see cref="Nullable{T}"/> maps as its <c>T</c>), or <see langword="null"/> when it has none.</summary>
    internal static Entry? Get(Type clrType) =>
        Entries.TryGetValue(Nullable.GetUnderlyingType(clrType) ?? clrType, out var entry) ? entry : null;

    /// <summary>The DDL type name a column for <paramref name="clrType"/> gets, or <see langword="null"/> when the type is not mapped.</summary>
    internal static string? DdlTypeName(Type clrType) => Get(clrType)?.Ddl;

    /// <summary>Whether a property of <paramref name="clrType"/> can be read from a column of <paramref name="column"/> under <see cref="RowMapper"/>'s widening rule.</summary>
    internal static bool CanRead(Type clrType, LadybugType column) =>
        Get(clrType) is { } entry && Array.IndexOf(entry.Readable, column) >= 0;

    /// <summary>
    /// The <see cref="LadybugType"/> an engine type name from the catalog denotes: <c>INT64</c>,
    /// <c>DECIMAL(38, 10)</c>, <c>STRING[]</c> (a list, whatever the element), <c>TIMESTAMP_MS</c>
    /// (a timestamp, whatever the precision); <see langword="null"/> for a name this client does not know.
    /// </summary>
    /// <param name="engineType">The type text as <c>CALL table_info</c> reports it.</param>
    internal static LadybugType? Parse(string engineType)
    {
        ArgumentNullException.ThrowIfNull(engineType);
        var text = engineType.Trim();
        var end = text.IndexOfAny(['(', '[']);
        var name = end < 0 ? text : text[..end];
        if (end >= 0 && text[end] == '[') return LadybugType.List;
        return Names.TryGetValue(name, out var type) ? type : null;
    }

    /// <summary>Renders a CLR type the way it is written in C#, for messages.</summary>
    internal static string Describe(Type clrType) => RowMapper.Describe(clrType);
}
