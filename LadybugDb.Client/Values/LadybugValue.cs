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

    private T As<T>(LadybugType expected) where T : struct
    {
        if (Type != expected)
            throw new InvalidOperationException($"Value is {Type}, not {expected}.");
        return (T)_payload!;
    }
}
