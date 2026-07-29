namespace LadybugDb.Client;

/// <summary>The LadybugDB type of a <see cref="LadybugValue"/>.</summary>
public enum LadybugType
{
    /// <summary>
    /// A type this client does not yet marshal (currently <c>UUID</c>, <c>RECURSIVE_REL</c>,
    /// <c>INT128</c>, <c>UNION</c>, and <c>POINTER</c> - see docs/USAGE.md's "Type coverage"
    /// section). The payload is always <see langword="null"/>, so every <c>As*</c> accessor on a
    /// <see cref="LadybugValue"/> of this type - including <see cref="LadybugValue.AsString"/> -
    /// throws <see cref="InvalidOperationException"/>; there is no way to read the value out of
    /// this client today, only to detect that it occurred.
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
    /// <summary>INTERNAL_ID.</summary>
    InternalId,
}
