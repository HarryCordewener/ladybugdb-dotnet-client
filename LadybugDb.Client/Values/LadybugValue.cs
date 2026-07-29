namespace LadybugDb.Client;

/// <summary>
/// A single value read from a query result column, already fully marshalled into managed memory.
/// </summary>
/// <remarks>
/// Deliberately holds no native pointer. The <c>lbug_value</c> this was read from is destroyed
/// when its owning handle's scope ends (typically at the end of the row it came from), so a
/// <see cref="LadybugValue"/> that kept a pointer into it would be a use-after-free the moment it
/// outlived that scope. <see cref="ValueReader.Read"/> copies everything needed into the managed
/// payload eagerly, before the native value goes away.
/// </remarks>
public readonly struct LadybugValue
{
    private readonly object? _payload;

    internal LadybugValue(LadybugType type, object? payload)
    {
        Type = type;
        _payload = payload;
    }

    /// <summary>The LadybugDB type this value was read as.</summary>
    public LadybugType Type { get; }

    /// <summary><see langword="true"/> if this value is SQL NULL.</summary>
    public bool IsNull => Type == LadybugType.Null;

    /// <summary>Reads this value as a <see cref="bool"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Boolean"/>.</exception>
    public bool AsBoolean() => As<bool>(LadybugType.Boolean);

    /// <summary>Reads this value as an <see cref="long"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Int64"/>.</exception>
    public long AsInt64() => As<long>(LadybugType.Int64);

    /// <summary>Reads this value as an <see cref="int"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Int32"/>.</exception>
    public int AsInt32() => As<int>(LadybugType.Int32);

    /// <summary>Reads this value as a <see cref="short"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Int16"/>.</exception>
    public short AsInt16() => As<short>(LadybugType.Int16);

    /// <summary>Reads this value as an <see cref="sbyte"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Int8"/>.</exception>
    public sbyte AsSByte() => As<sbyte>(LadybugType.Int8);

    /// <summary>Reads this value as a <see cref="ulong"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.UInt64"/>.</exception>
    public ulong AsUInt64() => As<ulong>(LadybugType.UInt64);

    /// <summary>Reads this value as a <see cref="uint"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.UInt32"/>.</exception>
    public uint AsUInt32() => As<uint>(LadybugType.UInt32);

    /// <summary>Reads this value as a <see cref="ushort"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.UInt16"/>.</exception>
    public ushort AsUInt16() => As<ushort>(LadybugType.UInt16);

    /// <summary>Reads this value as a <see cref="byte"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.UInt8"/>.</exception>
    public byte AsByte() => As<byte>(LadybugType.UInt8);

    /// <summary>Reads this value as a <see cref="float"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Single"/>.</exception>
    public float AsSingle() => As<float>(LadybugType.Single);

    /// <summary>Reads this value as a <see cref="double"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Double"/>.</exception>
    public double AsDouble() => As<double>(LadybugType.Double);

    /// <summary>
    /// Reads this value as a <see cref="string"/>. Unlike the other accessors, this accepts any
    /// <see cref="Type"/> whose payload is already a managed string, not only
    /// <see cref="LadybugType.String"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's payload is not a string.</exception>
    public string AsString()
    {
        if (_payload is string s) return s;
        throw new InvalidOperationException($"Value is {Type}, not a type backed by a string.");
    }

    /// <summary>Reads this value as a <see cref="DateOnly"/>.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Date"/>.</exception>
    public DateOnly AsDateOnly() => As<DateOnly>(LadybugType.Date);

    /// <summary>
    /// Reads this value as a UTC <see cref="DateTime"/>. Applies to <see cref="LadybugType.Timestamp"/>,
    /// which covers TIMESTAMP and its SEC/MS/NS variants; all of them are normalized to a single
    /// UTC <see cref="DateTime"/> representation regardless of the native storage unit.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Timestamp"/>.</exception>
    public DateTime AsDateTime() => As<DateTime>(LadybugType.Timestamp);

    /// <summary>
    /// Reads this value as a <see cref="DateTimeOffset"/>. Applies to <see cref="LadybugType.TimestampTz"/>.
    /// LadybugDB stores TIMESTAMP_TZ as UTC microseconds and does not retain a distinct source offset,
    /// so the offset component is always <see cref="TimeSpan.Zero"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.TimestampTz"/>.</exception>
    public DateTimeOffset AsDateTimeOffset() => As<DateTimeOffset>(LadybugType.TimestampTz);

    /// <summary>Reads this value as a <see cref="TimeSpan"/>. Applies to <see cref="LadybugType.Interval"/>.</summary>
    /// <remarks>
    /// Lossy: the native interval carries a separate months component that <see cref="TimeSpan"/> has no
    /// concept of. The conversion is delegated to the engine's own <c>lbug_interval_to_difftime</c>
    /// (seconds), not computed by this client — empirically, that function converts months at a fixed
    /// 30 days each before adding the days/microseconds components. A caller doing calendar-aware
    /// arithmetic — where a month is not uniformly 30 days — should not rely on this conversion.
    /// An interval whose true magnitude does not fit in a <see cref="TimeSpan"/> is caught while the
    /// row is materialized (<c>ReadRowAsync</c>) and surfaces there as a <see cref="LadybugException"/>
    /// rather than reaching this accessor as a silently wrapped value; a <see cref="LadybugValue"/> of
    /// type <see cref="LadybugType.Interval"/> already holds a converted <see cref="TimeSpan"/> by the
    /// time this is callable.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Interval"/>.</exception>
    public TimeSpan AsTimeSpan() => As<TimeSpan>(LadybugType.Interval);

    /// <summary>
    /// Reads this value as a <see cref="byte"/> array holding a copy of the underlying BLOB bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Blob"/>.</exception>
    public byte[] AsBlob()
    {
        if (_payload is byte[] blob) return blob;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Blob}.");
    }

    private T As<T>(LadybugType expected) where T : struct
    {
        if (Type != expected)
            throw new InvalidOperationException($"Value is {Type}, not {expected}.");
        return (T)_payload!;
    }
}
