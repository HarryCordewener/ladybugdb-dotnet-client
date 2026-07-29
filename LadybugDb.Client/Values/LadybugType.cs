namespace LadybugDb.Client;

/// <summary>The LadybugDB type of a <see cref="LadybugValue"/>.</summary>
public enum LadybugType
{
    /// <summary>
    /// A type this client does not model with its own typed accessor (currently <c>UNION</c> and
    /// <c>POINTER</c> - see docs/USAGE.md's "Type coverage" section). Unlike a truly opaque value,
    /// the payload is the engine's own generic string rendering of the value (via
    /// <c>lbug_value_to_string</c>), so <see cref="LadybugValue.AsString"/> reads it - there is
    /// just no dedicated <c>LadybugType</c>/accessor pair for it. Every other typed <c>As*</c>
    /// accessor still throws <see cref="InvalidOperationException"/> for a value of this type.
    /// </summary>
    Unsupported = 0,
    /// <summary>SQL NULL.</summary>
    Null,
    /// <summary>BOOL.</summary>
    Boolean,
    /// <summary>INT8.</summary>
    Int8,
    /// <summary>INT16.</summary>
    Int16,
    /// <summary>INT32.</summary>
    Int32,
    /// <summary>INT64, and SERIAL.</summary>
    Int64,
    /// <summary>UINT8.</summary>
    UInt8,
    /// <summary>UINT16.</summary>
    UInt16,
    /// <summary>UINT32.</summary>
    UInt32,
    /// <summary>UINT64.</summary>
    UInt64,
    /// <summary>
    /// INT128. Backed by <see cref="System.Int128"/>, constructed purely in managed code from the
    /// engine's <c>lbug_int128_t{low,high}</c> pair - never by marshalling <see cref="System.Int128"/>
    /// itself across the native boundary, which has open ABI defects on some of this client's
    /// supported RIDs (see <see cref="LadybugValue.AsInt128"/>).
    /// </summary>
    Int128,
    /// <summary>FLOAT.</summary>
    Single,
    /// <summary>DOUBLE.</summary>
    Double,
    /// <summary>
    /// DECIMAL. Backed by the engine's exact decimal string, not a parsed <see cref="decimal"/> -
    /// see <see cref="LadybugValue.AsDecimal"/> (bounded, 28-29 significant digits) and
    /// <see cref="LadybugValue.AsBigDecimal"/> (always lossless, all 38 digits) for why parsing is
    /// deferred to those accessors.
    /// </summary>
    Decimal,
    /// <summary>STRING.</summary>
    String,
    /// <summary>BLOB.</summary>
    Blob,
    /// <summary>
    /// UUID. Backed by <see cref="Guid"/>, parsed from the engine's own string rendering of the
    /// value (see <see cref="LadybugValue.AsGuid"/>) - not a 16-byte array, which would silently
    /// produce the wrong <see cref="Guid"/> due to <see cref="Guid"/>'s mixed-endian byte layout.
    /// </summary>
    Uuid,
    /// <summary>DATE.</summary>
    Date,
    /// <summary>TIMESTAMP and its SEC/MS/NS variants.</summary>
    Timestamp,
    /// <summary>TIMESTAMP_TZ.</summary>
    TimestampTz,
    /// <summary>INTERVAL.</summary>
    Interval,
    /// <summary>LIST and ARRAY.</summary>
    List,
    /// <summary>STRUCT.</summary>
    Struct,
    /// <summary>MAP.</summary>
    Map,
    /// <summary>NODE.</summary>
    Node,
    /// <summary>REL.</summary>
    Rel,
    /// <summary>
    /// RECURSIVE_REL - the result of a variable-length path match (e.g. <c>(a)-[:R*1..3]-&gt;(b)</c>).
    /// Backed by a <see cref="LadybugPath"/> exposing the matched nodes and relationships, each
    /// already marshalled the same way a plain <see cref="Node"/>/<see cref="Rel"/> value is.
    /// </summary>
    Path,
    /// <summary>INTERNAL_ID.</summary>
    InternalId,
}
