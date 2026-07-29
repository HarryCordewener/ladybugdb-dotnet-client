using System.Globalization;
using ExtendedNumerics;

namespace LadybugDb.Client;

/// <summary>
/// A single value read from a query result column, already fully marshalled into managed memory.
/// </summary>
/// <remarks>
/// Deliberately holds no native pointer. The <c>lbug_value</c> this was read from is destroyed
/// when its owning handle's scope ends (typically at the end of the row it came from), so a
/// <see cref="LadybugValue"/> that kept a pointer into it would be a use-after-free the moment it
/// outlived that scope. <see cref="ValueReader.Read(LadybugDb.Client.Native.lbug_value*)"/> copies everything needed into the managed
/// payload eagerly, before the native value goes away.
/// </remarks>
public readonly struct LadybugValue : IEquatable<LadybugValue>
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
    /// Reads this value as a <see cref="decimal"/>, parsed from the engine's exact decimal string
    /// with <see cref="NumberStyles.Number"/> under <see cref="CultureInfo.InvariantCulture"/> -
    /// explicitly invariant, since a machine configured with a comma decimal separator would
    /// otherwise misparse the engine's always-dot-separated string. The engine's DECIMAL supports
    /// up to 38 significant digits, while .NET's <see cref="decimal"/> holds only 28-29; a value in
    /// that gap does not truncate, round, or silently lose precision here - it throws instead. Use
    /// <see cref="AsBigDecimal"/> to read such a value losslessly, for all 38 digits.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Decimal"/>.</exception>
    /// <exception cref="LadybugException">The value's precision exceeds what <see cref="decimal"/> can represent.</exception>
    public decimal AsDecimal()
    {
        if (Type != LadybugType.Decimal)
            throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Decimal}.");

        var s = (string)_payload!;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            return d;

        throw new LadybugException(
            $"DECIMAL value '{s}' exceeds the range decimal can represent (decimal holds 28-29 " +
            "significant digits; the engine supports up to 38). Use AsBigDecimal() to read it losslessly.");
    }

    /// <summary>
    /// Reads this value as a <see cref="BigDecimal"/>, parsed from the exact engine decimal string
    /// under <see cref="CultureInfo.InvariantCulture"/> - the same string <see cref="AsDecimal"/>
    /// parses, and the same one <see cref="AsString"/> returns verbatim. Unlike
    /// <see cref="AsDecimal"/>, this is always lossless: <see cref="BigDecimal"/> is backed by an
    /// arbitrary-precision <see cref="System.Numerics.BigInteger"/> mantissa, so it holds every one
    /// of the engine's up-to-38 significant digits without the 28-29 digit ceiling
    /// <see cref="decimal"/> has. This is the accessor to use for a DECIMAL(29..38) value, or any
    /// time bidirectional exactness matters more than interop with existing <see cref="decimal"/>-based
    /// code.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Decimal"/>.</exception>
    public BigDecimal AsBigDecimal()
    {
        if (Type != LadybugType.Decimal)
            throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Decimal}.");

        return BigDecimal.Parse((string)_payload!, CultureInfo.InvariantCulture);
    }

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
    /// row is materialized (advancing the enumerator - see <see cref="LadybugQueryResult.GetAsyncEnumerator"/>)
    /// and surfaces there as a <see cref="LadybugException"/>
    /// rather than reaching this accessor as a silently wrapped value; a <see cref="LadybugValue"/> of
    /// type <see cref="LadybugType.Interval"/> already holds a converted <see cref="TimeSpan"/> by the
    /// time this is callable.
    /// </remarks>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Interval"/>.</exception>
    public TimeSpan AsTimeSpan() => As<TimeSpan>(LadybugType.Interval);

    /// <summary>
    /// Reads this value as a <see cref="byte"/> array. Returns a fresh copy on every call: unlike
    /// the container accessors (<see cref="AsList"/>/<see cref="AsStruct"/>/<see cref="AsMap"/>),
    /// which return an immutable, read-only-wrapped view over shared internal storage,
    /// <see cref="byte"/><c>[]</c> itself has no immutable counterpart in this API, so the only way
    /// to keep this value's internal state (and every other copy of this <see cref="LadybugValue"/>,
    /// since it is a struct sharing the same underlying payload reference) safe from a caller
    /// mutating the returned array is to hand out a new array each time rather than the live one.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Blob"/>.</exception>
    public byte[] AsBlob()
    {
        if (_payload is byte[] blob) return (byte[])blob.Clone();
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Blob}.");
    }

    /// <summary>Reads this value as a list of elements. Applies to <see cref="LadybugType.List"/>, which covers LIST and ARRAY.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.List"/>.</exception>
    public IReadOnlyList<LadybugValue> AsList()
    {
        if (_payload is IReadOnlyList<LadybugValue> list) return list;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.List}.");
    }

    /// <summary>Reads this value as a set of named fields, keyed by field name.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Struct"/>.</exception>
    public IReadOnlyDictionary<string, LadybugValue> AsStruct()
    {
        if (_payload is IReadOnlyDictionary<string, LadybugValue> @struct) return @struct;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Struct}.");
    }

    /// <summary>
    /// Reads this value as an ordered sequence of key/value pairs. A list rather than a
    /// dictionary: a MAP key is itself a <see cref="LadybugValue"/>, which is not guaranteed
    /// hashable in the general case (for example a key that is itself a list or struct).
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Map"/>.</exception>
    public IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> AsMap()
    {
        if (_payload is IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> map) return map;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Map}.");
    }

    /// <summary>Reads this value as a graph node.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Node"/>.</exception>
    public LadybugNode AsNode()
    {
        if (_payload is LadybugNode node) return node;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Node}.");
    }

    /// <summary>Reads this value as a graph relationship.</summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.Rel"/>.</exception>
    public LadybugRel AsRel()
    {
        if (_payload is LadybugRel rel) return rel;
        throw new InvalidOperationException($"Value is {Type}, not {LadybugType.Rel}.");
    }

    /// <summary>
    /// Reads this value as a LadybugDB internal id - the result of a bare <c>RETURN id(n)</c> (or
    /// <c>id(r)</c>), as opposed to the id embedded in a <see cref="AsNode"/>/<see cref="AsRel"/>
    /// result's own <c>Id</c> property.
    /// </summary>
    /// <exception cref="InvalidOperationException">This value's <see cref="Type"/> is not <see cref="LadybugType.InternalId"/>.</exception>
    public LadybugInternalId AsInternalId() => As<LadybugInternalId>(LadybugType.InternalId);

    private T As<T>(LadybugType expected) where T : struct
    {
        if (Type != expected)
            throw new InvalidOperationException($"Value is {Type}, not {expected}.");
        return (T)_payload!;
    }

    /// <summary>
    /// Structural equality: same <see cref="Type"/> and an equal payload, recursing into
    /// containers (<see cref="LadybugType.List"/>/<see cref="LadybugType.Struct"/>/
    /// <see cref="LadybugType.Map"/>/<see cref="LadybugType.Node"/>/<see cref="LadybugType.Rel"/>)
    /// element-by-element rather than by reference. Explicitly implemented rather than left to the
    /// inherited <see cref="ValueType.Equals(object?)"/>, which boxes this struct's payload and
    /// compares the box - correct for scalar payloads, silently wrong for reference-typed
    /// container payloads (<c>byte[]</c>/list/struct/map), where it degenerates to reference
    /// equality: two independently-read values with identical content would compare unequal
    /// purely because they are backed by different array/collection instances. That asymmetry
    /// (works for <see cref="AsInt64"/>, silently breaks for <see cref="AsList"/>) is exactly the
    /// kind of inconsistency <see cref="Equals(object?)"/>/<see cref="GetHashCode"/>/
    /// <see cref="Dictionary{TKey,TValue}"/> keys/<c>Distinct()</c> would otherwise hit silently
    /// rather than loudly.
    /// </summary>
    public bool Equals(LadybugValue other)
    {
        if (Type != other.Type) return false;

        return (_payload, other._payload) switch
        {
            (null, null) => true,
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b),
            (IReadOnlyList<LadybugValue> a, IReadOnlyList<LadybugValue> b) => a.SequenceEqual(b),
            (IReadOnlyDictionary<string, LadybugValue> a, IReadOnlyDictionary<string, LadybugValue> b) =>
                ValueEqualityHelpers.DictionaryEquals(a, b),
            (IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> a, IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> b) =>
                MapEquals(a, b),
            (LadybugNode a, LadybugNode b) => a.Equals(b),
            (LadybugRel a, LadybugRel b) => a.Equals(b),
            _ => object.Equals(_payload, other._payload),
        };
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is LadybugValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var payloadHash = _payload switch
        {
            null => 0,
            byte[] blob => HashBlob(blob),
            IReadOnlyList<LadybugValue> list => HashList(list),
            IReadOnlyDictionary<string, LadybugValue> @struct => ValueEqualityHelpers.DictionaryHashCode(@struct),
            IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> map => HashMap(map),
            _ => _payload.GetHashCode(),
        };
        return HashCode.Combine(Type, payloadHash);
    }

    /// <summary>
    /// A human-readable rendering of this value - the type name for <see langword="null"/>/
    /// unsupported payloads, otherwise the payload's own natural representation (recursing into
    /// containers). Exists specifically so debugger output and failed-assertion messages (e.g.
    /// <c>Assert.That(row.GetValue(0)).IsEqualTo(...)</c> in a test) show the actual value instead
    /// of the useless <c>LadybugDb.Client.LadybugValue</c> the inherited <see cref="ValueType.ToString"/>
    /// would otherwise print for every value regardless of what it holds.
    /// </summary>
    public override string ToString()
    {
        if (IsNull) return "null";

        return _payload switch
        {
            null => Type.ToString(),
            byte[] blob => $"Blob[{blob.Length}]",
            IReadOnlyList<LadybugValue> list => "[" + string.Join(", ", list) + "]",
            IReadOnlyDictionary<string, LadybugValue> @struct => ValueEqualityHelpers.DescribeDictionary(@struct),
            IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> map =>
                "{" + string.Join(", ", map.Select(kv => $"{kv.Key}: {kv.Value}")) + "}",
            _ => _payload.ToString() ?? Type.ToString(),
        };
    }

    private static bool MapEquals(
        IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> a,
        IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> b)
    {
        // Order-sensitive, unlike DictionaryEquals: a MAP is an ordered sequence of entries (see
        // AsMap's remarks on why it isn't a Dictionary), so two maps with the same entries in a
        // different order are not the same value.
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Key.Equals(b[i].Key) || !a[i].Value.Equals(b[i].Value)) return false;
        }
        return true;
    }

    private static int HashList(IReadOnlyList<LadybugValue> list)
    {
        var hash = new HashCode();
        foreach (var item in list) hash.Add(item);
        return hash.ToHashCode();
    }

    private static int HashMap(IReadOnlyList<KeyValuePair<LadybugValue, LadybugValue>> map)
    {
        var hash = new HashCode();
        foreach (var (key, value) in map)
        {
            hash.Add(key);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    private static int HashBlob(byte[] blob)
    {
        var hash = new HashCode();
        hash.AddBytes(blob);
        return hash.ToHashCode();
    }
}
