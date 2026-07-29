using System.Collections.ObjectModel;
using System.Globalization;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>Marshals a native <c>lbug_value</c> into a fully managed <see cref="LadybugValue"/>.</summary>
internal static class ValueReader
{
    /// <summary>
    /// The maximum number of nested container levels (LIST/STRUCT/MAP/NODE) this reader will
    /// recurse through. Container values are user data with unbounded nesting, so naive recursion
    /// on them is a stack-overflow vector; past this depth <see cref="Read(lbug_value*, int)"/>
    /// throws a <see cref="LadybugException"/> instead of continuing to recurse.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// Reads <paramref name="value"/> eagerly into a <see cref="LadybugValue"/> that holds only
    /// managed data. Callers must hold whatever lease keeps <paramref name="value"/> alive for the
    /// duration of this call; nothing here retains the pointer afterward.
    /// </summary>
    internal static unsafe LadybugValue Read(lbug_value* value) => Read(value, 0);

    /// <summary>
    /// Recursive worker behind <see cref="Read(lbug_value*)"/>. <paramref name="depth"/> counts
    /// how many container levels have already been entered to read <paramref name="value"/>;
    /// each container branch (list/struct/map/node) passes <c>depth + 1</c> when it recurses into
    /// a child element it owns.
    /// </summary>
    private static unsafe LadybugValue Read(lbug_value* value, int depth)
    {
        if (depth > MaxDepth)
            throw new LadybugException(
                $"A value's container nesting exceeded the supported maximum depth of {MaxDepth}.");

        if (LbugNative.lbug_value_is_null(value) != 0)
            return new LadybugValue(LadybugType.Null, null);

        using var typeHandle = LbugLogicalTypeHandle.GetDataType(value);
        lbug_data_type_id typeId;
        using (var lease = typeHandle.Acquire())
        {
            typeId = LbugNative.lbug_data_type_get_id((lbug_logical_type*)lease.Pointer);
        }

        return typeId switch
        {
            lbug_data_type_id.LBUG_BOOL => ReadBool(value),
            lbug_data_type_id.LBUG_INT64 or lbug_data_type_id.LBUG_SERIAL => ReadInt64(value),
            lbug_data_type_id.LBUG_INT32 => ReadInt32(value),
            lbug_data_type_id.LBUG_INT16 => ReadInt16(value),
            lbug_data_type_id.LBUG_INT8 => ReadInt8(value),
            lbug_data_type_id.LBUG_UINT64 => ReadUInt64(value),
            lbug_data_type_id.LBUG_UINT32 => ReadUInt32(value),
            lbug_data_type_id.LBUG_UINT16 => ReadUInt16(value),
            lbug_data_type_id.LBUG_UINT8 => ReadUInt8(value),
            lbug_data_type_id.LBUG_DOUBLE => ReadDouble(value),
            lbug_data_type_id.LBUG_FLOAT => ReadSingle(value),
            lbug_data_type_id.LBUG_DECIMAL => ReadDecimal(value),
            lbug_data_type_id.LBUG_STRING => ReadString(value),
            lbug_data_type_id.LBUG_DATE => ReadDate(value),
            lbug_data_type_id.LBUG_TIMESTAMP => ReadTimestamp(value),
            lbug_data_type_id.LBUG_TIMESTAMP_SEC => ReadTimestampSec(value),
            lbug_data_type_id.LBUG_TIMESTAMP_MS => ReadTimestampMs(value),
            lbug_data_type_id.LBUG_TIMESTAMP_NS => ReadTimestampNs(value),
            lbug_data_type_id.LBUG_TIMESTAMP_TZ => ReadTimestampTz(value),
            lbug_data_type_id.LBUG_INTERVAL => ReadInterval(value),
            lbug_data_type_id.LBUG_BLOB => ReadBlob(value),
            lbug_data_type_id.LBUG_INTERNAL_ID => ReadInternalIdValue(value),
            lbug_data_type_id.LBUG_LIST or lbug_data_type_id.LBUG_ARRAY => ReadList(value, depth),
            lbug_data_type_id.LBUG_STRUCT => ReadStruct(value, depth),
            lbug_data_type_id.LBUG_MAP => ReadMap(value, depth),
            lbug_data_type_id.LBUG_NODE => ReadNode(value, depth),
            lbug_data_type_id.LBUG_REL => ReadRel(value, depth),
            lbug_data_type_id.LBUG_RECURSIVE_REL => ReadRecursiveRel(value, depth),
            lbug_data_type_id.LBUG_UUID => ReadUuid(value),
            lbug_data_type_id.LBUG_INT128 => ReadInt128(value),
            lbug_data_type_id.LBUG_UNION => ReadUnion(value, depth),
            _ => ReadUnsupported(value),
        };
    }

    private static unsafe LadybugValue ReadBool(lbug_value* value)
    {
        bool result;
        var state = LbugNative.lbug_value_get_bool(value, &result);
        ThrowIfFailed(state, "bool");
        return new LadybugValue(LadybugType.Boolean, result);
    }

    private static unsafe LadybugValue ReadInt64(lbug_value* value)
    {
        long result;
        var state = LbugNative.lbug_value_get_int64(value, &result);
        ThrowIfFailed(state, "int64");
        return new LadybugValue(LadybugType.Int64, result);
    }

    private static unsafe LadybugValue ReadInt32(lbug_value* value)
    {
        int result;
        var state = LbugNative.lbug_value_get_int32(value, &result);
        ThrowIfFailed(state, "int32");
        return new LadybugValue(LadybugType.Int32, result);
    }

    private static unsafe LadybugValue ReadInt16(lbug_value* value)
    {
        short result;
        var state = LbugNative.lbug_value_get_int16(value, &result);
        ThrowIfFailed(state, "int16");
        return new LadybugValue(LadybugType.Int16, result);
    }

    private static unsafe LadybugValue ReadInt8(lbug_value* value)
    {
        sbyte result;
        var state = LbugNative.lbug_value_get_int8(value, &result);
        ThrowIfFailed(state, "int8");
        return new LadybugValue(LadybugType.Int8, result);
    }

    private static unsafe LadybugValue ReadUInt64(lbug_value* value)
    {
        ulong result;
        var state = LbugNative.lbug_value_get_uint64(value, &result);
        ThrowIfFailed(state, "uint64");
        return new LadybugValue(LadybugType.UInt64, result);
    }

    private static unsafe LadybugValue ReadUInt32(lbug_value* value)
    {
        uint result;
        var state = LbugNative.lbug_value_get_uint32(value, &result);
        ThrowIfFailed(state, "uint32");
        return new LadybugValue(LadybugType.UInt32, result);
    }

    private static unsafe LadybugValue ReadUInt16(lbug_value* value)
    {
        ushort result;
        var state = LbugNative.lbug_value_get_uint16(value, &result);
        ThrowIfFailed(state, "uint16");
        return new LadybugValue(LadybugType.UInt16, result);
    }

    private static unsafe LadybugValue ReadUInt8(lbug_value* value)
    {
        byte result;
        var state = LbugNative.lbug_value_get_uint8(value, &result);
        ThrowIfFailed(state, "uint8");
        return new LadybugValue(LadybugType.UInt8, result);
    }

    /// <summary>
    /// Reads an INT128 value. <c>lbug_int128_t{low,high}</c> is a blittable struct passed and read
    /// by pointer here, never <see cref="Int128"/> itself - <see cref="Int128"/> has open ABI
    /// defects marshalling across a P/Invoke boundary (wrong layout on big-endian, incorrect
    /// by-value parameter passing, and disabled support for "Int128 wrapped in a struct" on X64
    /// SysV/ARM64 - both RIDs this client ships - per .NET's own ABI support docs). The
    /// <see cref="Int128"/> value itself is constructed purely in managed code, from the two
    /// 64-bit halves the native call already handed back.
    /// </summary>
    private static unsafe LadybugValue ReadInt128(lbug_value* value)
    {
        lbug_int128_t native;
        var state = LbugNative.lbug_value_get_int128(value, &native);
        ThrowIfFailed(state, "int128");
        var result = new Int128((ulong)native.high, native.low);
        return new LadybugValue(LadybugType.Int128, result);
    }

    private static unsafe LadybugValue ReadDouble(lbug_value* value)
    {
        double result;
        var state = LbugNative.lbug_value_get_double(value, &result);
        ThrowIfFailed(state, "double");
        return new LadybugValue(LadybugType.Double, result);
    }

    private static unsafe LadybugValue ReadSingle(lbug_value* value)
    {
        float result;
        var state = LbugNative.lbug_value_get_float(value, &result);
        ThrowIfFailed(state, "float");
        return new LadybugValue(LadybugType.Single, result);
    }

    private static unsafe LadybugValue ReadString(lbug_value* value)
    {
        sbyte* raw;
        var state = LbugNative.lbug_value_get_string(value, &raw);
        ThrowIfFailed(state, "string");
        return new LadybugValue(LadybugType.String, NativeString.TakeOwnership(raw));
    }

    /// <summary>
    /// Reads a UUID value. <c>lbug_value_get_uuid</c> hands this back as a caller-owned
    /// <c>char*</c> - the same string-ownership contract as <see cref="ReadString"/> - so it goes
    /// through the same <see cref="NativeString.TakeOwnership"/> path before being parsed as a
    /// <see cref="Guid"/> under <see cref="CultureInfo.InvariantCulture"/>. There is no byte array
    /// anywhere in this path: a 16-byte marshalling would hit <see cref="Guid"/>'s mixed-endian
    /// layout and silently produce the wrong value, which the string round trip avoids entirely.
    /// </summary>
    private static unsafe LadybugValue ReadUuid(lbug_value* value)
    {
        sbyte* raw;
        var state = LbugNative.lbug_value_get_uuid(value, &raw);
        ThrowIfFailed(state, "uuid");
        var text = NativeString.TakeOwnership(raw);
        try
        {
            return new LadybugValue(LadybugType.Uuid, Guid.Parse(text, CultureInfo.InvariantCulture));
        }
        catch (FormatException ex)
        {
            throw ConversionFailed("uuid", ex);
        }
    }

    /// <summary>
    /// Reads a DECIMAL value. The engine hands this back as a caller-owned <c>char*</c> - the same
    /// string-ownership contract as <see cref="ReadString"/> and every other native string in this
    /// client - via <c>lbug_value_get_decimal_as_string</c>, so it goes through the same
    /// <see cref="NativeString.TakeOwnership"/> path. The exact engine string is stored as the
    /// payload rather than a parsed <see cref="decimal"/>: the engine supports DECIMAL up to 38
    /// significant digits while <see cref="decimal"/> holds only 28-29, so parsing eagerly here
    /// would throw on values the caller never asked to convert. Parsing is deferred to
    /// <see cref="LadybugValue.AsDecimal"/>, which the caller reaches only if it wants a
    /// <see cref="decimal"/>; <see cref="LadybugValue.AsString"/> reads this losslessly regardless.
    /// </summary>
    private static unsafe LadybugValue ReadDecimal(lbug_value* value)
    {
        sbyte* raw;
        var state = LbugNative.lbug_value_get_decimal_as_string(value, &raw);
        ThrowIfFailed(state, "decimal");
        return new LadybugValue(LadybugType.Decimal, NativeString.TakeOwnership(raw));
    }

    private static unsafe LadybugValue ReadDate(lbug_value* value)
    {
        lbug_date_t native;
        var state = LbugNative.lbug_value_get_date(value, &native);
        ThrowIfFailed(state, "date");
        try
        {
            // native.days is days since 1970-01-01. checked() so an out-of-range day count throws
            // instead of silently wrapping before DateOnly.FromDayNumber ever sees it.
            var date = DateOnly.FromDayNumber(checked(new DateOnly(1970, 1, 1).DayNumber + native.days));
            return new LadybugValue(LadybugType.Date, date);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("date", ex);
        }
    }

    private static unsafe LadybugValue ReadTimestamp(lbug_value* value)
    {
        lbug_timestamp_t native;
        var state = LbugNative.lbug_value_get_timestamp(value, &native);
        ThrowIfFailed(state, "timestamp");
        try
        {
            // native.value is microseconds since 1970-01-01T00:00:00Z.
            return new LadybugValue(LadybugType.Timestamp, FromMicros(native.value));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("timestamp", ex);
        }
    }

    private static unsafe LadybugValue ReadTimestampSec(lbug_value* value)
    {
        lbug_timestamp_sec_t native;
        var state = LbugNative.lbug_value_get_timestamp_sec(value, &native);
        ThrowIfFailed(state, "timestamp_sec");
        try
        {
            // native.value is seconds since 1970-01-01T00:00:00Z.
            return new LadybugValue(LadybugType.Timestamp, DateTime.UnixEpoch.AddSeconds(native.value));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("timestamp_sec", ex);
        }
    }

    private static unsafe LadybugValue ReadTimestampMs(lbug_value* value)
    {
        lbug_timestamp_ms_t native;
        var state = LbugNative.lbug_value_get_timestamp_ms(value, &native);
        ThrowIfFailed(state, "timestamp_ms");
        try
        {
            // native.value is milliseconds since 1970-01-01T00:00:00Z.
            return new LadybugValue(LadybugType.Timestamp, DateTime.UnixEpoch.AddMilliseconds(native.value));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("timestamp_ms", ex);
        }
    }

    private static unsafe LadybugValue ReadTimestampNs(lbug_value* value)
    {
        lbug_timestamp_ns_t native;
        var state = LbugNative.lbug_value_get_timestamp_ns(value, &native);
        ThrowIfFailed(state, "timestamp_ns");
        try
        {
            // native.value is nanoseconds since 1970-01-01T00:00:00Z; a DateTime tick is 100ns, so
            // this truncates sub-100ns precision that DateTime cannot represent.
            return new LadybugValue(LadybugType.Timestamp, DateTime.UnixEpoch.AddTicks(native.value / 100));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("timestamp_ns", ex);
        }
    }

    private static unsafe LadybugValue ReadTimestampTz(lbug_value* value)
    {
        lbug_timestamp_tz_t native;
        var state = LbugNative.lbug_value_get_timestamp_tz(value, &native);
        ThrowIfFailed(state, "timestamp_tz");
        try
        {
            // native.value is microseconds since 1970-01-01T00:00:00Z; LadybugDB does not retain a
            // distinct source offset, so this is always reported at a zero UTC offset.
            return new LadybugValue(LadybugType.TimestampTz, new DateTimeOffset(FromMicros(native.value), TimeSpan.Zero));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            throw ConversionFailed("timestamp_tz", ex);
        }
    }

    private static unsafe LadybugValue ReadInterval(lbug_value* value)
    {
        lbug_interval_t native;
        var state = LbugNative.lbug_value_get_interval(value, &native);
        ThrowIfFailed(state, "interval");

        // Convert via the engine's own lbug_interval_to_difftime rather than hand-rolled C#
        // arithmetic: this is not one of the 12 excluded *_to_tm/*_from_tm functions (those are
        // excluded because struct tm has no portable ABI; this one takes/returns a plain double of
        // seconds), and it avoids re-deriving the months-to-days convention ourselves. Empirically
        // confirmed (native probe against liblbug) that it applies exactly 30 days/month - e.g.
        // months=1 -> 2,592,000s (= 30 days) - matching what this client previously assumed by
        // hand, but this is now the engine's authoritative answer, not a guess.
        double seconds;
        LbugNative.lbug_interval_to_difftime(native, &seconds);
        try
        {
            return new LadybugValue(LadybugType.Interval, TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            throw ConversionFailed("interval", ex);
        }
    }

    private static unsafe LadybugValue ReadBlob(lbug_value* value)
    {
        byte* raw;
        ulong length;
        var state = LbugNative.lbug_value_get_blob(value, &raw, &length);
        ThrowIfFailed(state, "blob");
        try
        {
            try
            {
                // The checked cast runs BEFORE the allocation, not after: an over-large blob (length
                // > int.MaxValue, which byte[] cannot index anyway) must throw the cheap, immediate
                // OverflowException here, not attempt a multi-gigabyte `new byte[length]` first and
                // let that fail with OutOfMemoryException instead - a much less specific error for
                // the same fundamentally unrepresentable input.
                var count = checked((int)length);
                var bytes = new byte[count];
                if (count > 0)
                    new ReadOnlySpan<byte>(raw, count).CopyTo(bytes);
                return new LadybugValue(LadybugType.Blob, bytes);
            }
            catch (OverflowException ex)
            {
                throw ConversionFailed("blob", ex);
            }
        }
        finally
        {
            // The blob buffer from lbug_value_get_blob is caller-owned per third-party/lbug.h and
            // must be released exactly once, on every path (including the throw above), mirroring
            // NativeString.TakeOwnership.
            LbugNative.lbug_destroy_blob(raw);
        }
    }

    /// <summary>
    /// Reads a LIST or ARRAY value. Every element from <c>lbug_value_get_list_element</c> is a new
    /// <c>lbug_value</c> the caller owns; each is wrapped in an <see cref="LbugValueHandle"/> inside
    /// a <c>using</c> so it is destroyed exactly once, right after being read recursively.
    /// </summary>
    private static unsafe LadybugValue ReadList(lbug_value* value, int depth)
    {
        ulong size;
        var state = LbugNative.lbug_value_get_list_size(value, &size);
        ThrowIfFailed(state, "list");

        var items = new LadybugValue[size];
        for (ulong i = 0; i < size; i++)
        {
            using var elementHandle = LbugValueHandle.GetListElement(value, i, out var elementState);
            if (elementState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read list element {i}."));

            using var lease = elementHandle.Acquire();
            items[i] = Read((lbug_value*)lease.Pointer, depth + 1);
        }

        // Wrapped read-only: LadybugValue is a struct copied freely between callers, and AsList()
        // exposes this array as IReadOnlyList<T>. Without this wrapper a caller could cast back to
        // LadybugValue[] and mutate the single backing store shared by every copy of this value.
        return new LadybugValue(LadybugType.List, Array.AsReadOnly(items));
    }

    /// <summary>
    /// Reads a STRUCT value. Every field value from <c>lbug_value_get_struct_field_value</c> is a
    /// new <c>lbug_value</c> the caller owns; same wrap-in-<c>using</c>-then-recurse discipline as
    /// <see cref="ReadList"/>. Field names come back as caller-owned <c>char*</c>, so each goes
    /// through <see cref="NativeString.TakeOwnership"/> like every other native string in this client.
    /// </summary>
    private static unsafe LadybugValue ReadStruct(lbug_value* value, int depth)
    {
        ulong count;
        var state = LbugNative.lbug_value_get_struct_num_fields(value, &count);
        ThrowIfFailed(state, "struct");

        var fields = new Dictionary<string, LadybugValue>();
        for (ulong i = 0; i < count; i++)
        {
            sbyte* namePtr;
            var nameState = LbugNative.lbug_value_get_struct_field_name(value, i, &namePtr);
            ThrowIfFailed(nameState, "struct field name");
            var name = NativeString.TakeOwnership(namePtr);

            using var fieldHandle = LbugValueHandle.GetStructFieldValue(value, i, out var fieldState);
            if (fieldState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read struct field '{name}'."));

            using var lease = fieldHandle.Acquire();
            fields[name] = Read((lbug_value*)lease.Pointer, depth + 1);
        }

        // Wrapped read-only for the same reason as ReadList: AsStruct() exposes this dictionary as
        // IReadOnlyDictionary<T>, and without the wrapper a cast back to Dictionary<,> would give
        // mutable access to the store shared by every copy of this LadybugValue.
        return new LadybugValue(LadybugType.Struct, new ReadOnlyDictionary<string, LadybugValue>(fields));
    }

    /// <summary>
    /// Reads a MAP value as an ordered list of key/value pairs. Both the key from
    /// <c>lbug_value_get_map_key</c> and the value from <c>lbug_value_get_map_value</c> are new
    /// <c>lbug_value</c>s the caller owns - two owned values per entry, not one - so each is
    /// wrapped in its own <see cref="LbugValueHandle"/> and destroyed before moving to the next index.
    /// </summary>
    private static unsafe LadybugValue ReadMap(lbug_value* value, int depth)
    {
        ulong count;
        var state = LbugNative.lbug_value_get_map_size(value, &count);
        ThrowIfFailed(state, "map");

        var entries = new KeyValuePair<LadybugValue, LadybugValue>[count];
        for (ulong i = 0; i < count; i++)
        {
            LadybugValue key;
            using (var keyHandle = LbugValueHandle.GetMapKey(value, i, out var keyState))
            {
                if (keyState != lbug_state.LbugSuccess)
                    throw new LadybugException(NativeString.WithErrorDetail($"Failed to read map key {i}."));

                using var lease = keyHandle.Acquire();
                key = Read((lbug_value*)lease.Pointer, depth + 1);
            }

            LadybugValue mapValue;
            using (var valueHandle = LbugValueHandle.GetMapValue(value, i, out var valueState))
            {
                if (valueState != lbug_state.LbugSuccess)
                    throw new LadybugException(NativeString.WithErrorDetail($"Failed to read map value {i}."));

                using var lease = valueHandle.Acquire();
                mapValue = Read((lbug_value*)lease.Pointer, depth + 1);
            }

            entries[i] = new KeyValuePair<LadybugValue, LadybugValue>(key, mapValue);
        }

        // Wrapped read-only for the same reason as ReadList.
        return new LadybugValue(LadybugType.Map, Array.AsReadOnly(entries));
    }

    /// <summary>
    /// Reads a NODE value's id, label, and properties.
    /// </summary>
    /// <remarks>
    /// Per <c>third-party/lbug.h</c>, <c>lbug_node_val_get_id_val</c>, <c>lbug_node_val_get_label_val</c>,
    /// and <c>lbug_node_val_get_property_value_at</c> all "return" through a caller-supplied
    /// <c>out_value</c> they fill in place - the identical shape documented for
    /// <c>lbug_value_get_list_element</c> et al., not a borrowed pointer into the node. Each is
    /// therefore wrapped in its own owned <see cref="LbugValueHandle"/> (see
    /// <see cref="LbugValueHandle.GetNodeIdValue"/>/<see cref="LbugValueHandle.GetNodeLabelValue"/>/
    /// <see cref="LbugValueHandle.GetNodePropertyValueAt"/>) and destroyed via <c>using</c>, exactly
    /// like the container element getters. Property names come back as caller-owned <c>char*</c>,
    /// same as struct field names.
    /// </remarks>
    private static unsafe LadybugValue ReadNode(lbug_value* value, int depth)
    {
        LadybugInternalId id;
        using (var idHandle = LbugValueHandle.GetNodeIdValue(value, out var idState))
        {
            if (idState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a node's internal id."));

            using var lease = idHandle.Acquire();
            id = ReadInternalId((lbug_value*)lease.Pointer);
        }

        string label;
        using (var labelHandle = LbugValueHandle.GetNodeLabelValue(value, out var labelState))
        {
            if (labelState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a node's label."));

            using var lease = labelHandle.Acquire();
            label = ReadString((lbug_value*)lease.Pointer).AsString();
        }

        ulong propertyCount;
        var sizeState = LbugNative.lbug_node_val_get_property_size(value, &propertyCount);
        ThrowIfFailed(sizeState, "node property count");

        var properties = new Dictionary<string, LadybugValue>();
        for (ulong i = 0; i < propertyCount; i++)
        {
            sbyte* namePtr;
            var nameState = LbugNative.lbug_node_val_get_property_name_at(value, i, &namePtr);
            ThrowIfFailed(nameState, "node property name");
            var name = NativeString.TakeOwnership(namePtr);

            using var propertyHandle = LbugValueHandle.GetNodePropertyValueAt(value, i, out var propertyState);
            if (propertyState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read node property '{name}'."));

            using var lease = propertyHandle.Acquire();
            properties[name] = Read((lbug_value*)lease.Pointer, depth + 1);
        }

        // Wrapped read-only for the same reason as ReadStruct: LadybugNode.Properties is public and
        // must not hand out a mutable Dictionary<,> that every copy of the containing LadybugValue
        // (and every holder of this LadybugNode) shares.
        return new LadybugValue(LadybugType.Node,
            new LadybugNode(id, label, new ReadOnlyDictionary<string, LadybugValue>(properties)));
    }

    /// <summary>
    /// Reads a REL value's id, source/destination node ids, label, and properties. Mirrors
    /// <see cref="ReadNode"/> exactly: every <c>lbug_rel_val_get_*</c> entry point used here shares
    /// the identical caller-supplied-<c>out_value</c> ownership shape as the <c>lbug_node_val_get_*</c>
    /// family (re-confirmed against <c>third-party/lbug.h</c> lines 1463-1515 and the generated
    /// signatures in <c>LbugNative.g.cs</c> for this fix round, not assumed from the Task 3 reading),
    /// so each result is wrapped in its own owned <see cref="LbugValueHandle"/> and destroyed via
    /// <c>using</c>. Rel properties are user data exactly like node properties, so they consume the
    /// same recursion <paramref name="depth"/> budget.
    /// </summary>
    private static unsafe LadybugValue ReadRel(lbug_value* value, int depth)
    {
        LadybugInternalId id;
        using (var idHandle = LbugValueHandle.GetRelIdValue(value, out var idState))
        {
            if (idState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a relationship's internal id."));

            using var lease = idHandle.Acquire();
            id = ReadInternalId((lbug_value*)lease.Pointer);
        }

        LadybugInternalId sourceId;
        using (var srcHandle = LbugValueHandle.GetRelSrcIdValue(value, out var srcState))
        {
            if (srcState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a relationship's source node id."));

            using var lease = srcHandle.Acquire();
            sourceId = ReadInternalId((lbug_value*)lease.Pointer);
        }

        LadybugInternalId destinationId;
        using (var dstHandle = LbugValueHandle.GetRelDstIdValue(value, out var dstState))
        {
            if (dstState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a relationship's destination node id."));

            using var lease = dstHandle.Acquire();
            destinationId = ReadInternalId((lbug_value*)lease.Pointer);
        }

        string label;
        using (var labelHandle = LbugValueHandle.GetRelLabelValue(value, out var labelState))
        {
            if (labelState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a relationship's label."));

            using var lease = labelHandle.Acquire();
            label = ReadString((lbug_value*)lease.Pointer).AsString();
        }

        ulong propertyCount;
        var sizeState = LbugNative.lbug_rel_val_get_property_size(value, &propertyCount);
        ThrowIfFailed(sizeState, "rel property count");

        var properties = new Dictionary<string, LadybugValue>();
        for (ulong i = 0; i < propertyCount; i++)
        {
            sbyte* namePtr;
            var nameState = LbugNative.lbug_rel_val_get_property_name_at(value, i, &namePtr);
            ThrowIfFailed(nameState, "rel property name");
            var name = NativeString.TakeOwnership(namePtr);

            using var propertyHandle = LbugValueHandle.GetRelPropertyValueAt(value, i, out var propertyState);
            if (propertyState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail($"Failed to read relationship property '{name}'."));

            using var lease = propertyHandle.Acquire();
            properties[name] = Read((lbug_value*)lease.Pointer, depth + 1);
        }

        return new LadybugValue(LadybugType.Rel,
            new LadybugRel(id, sourceId, destinationId, label, new ReadOnlyDictionary<string, LadybugValue>(properties)));
    }

    /// <summary>
    /// Reads a RECURSIVE_REL value - the result of a variable-length path match. Per
    /// <c>third-party/lbug.h</c>, <c>lbug_value_get_recursive_rel_node_list</c> and
    /// <c>lbug_value_get_recursive_rel_rel_list</c> each fill a caller-supplied <c>out_value</c> -
    /// the identical shape as <c>lbug_value_get_list_element</c> - so each result is wrapped in its
    /// own owned <see cref="LbugValueHandle"/> and destroyed via <c>using</c>, exactly like every
    /// other container getter in this reader. Both payloads are themselves a LIST (of NODE, and of
    /// REL respectively), so this dispatches back into the top-level <see cref="Read(lbug_value*, int)"/>
    /// rather than re-implementing list marshalling - it detects LIST via
    /// <c>lbug_data_type_get_id</c> and calls <see cref="ReadList"/> itself, reusing the already
    /// tested element-by-element list/node/rel machinery. Path contents are user data with
    /// unbounded nesting exactly like node/rel properties, so this consumes the same <paramref name="depth"/>
    /// recursion budget: the node/rel lists are read at <c>depth + 1</c>, and each node/rel's own
    /// properties are read a further level in from there by the normal <see cref="ReadNode"/>/<see cref="ReadRel"/> path.
    /// </summary>
    private static unsafe LadybugValue ReadRecursiveRel(lbug_value* value, int depth)
    {
        List<LadybugNode> nodes;
        using (var nodeListHandle = LbugValueHandle.GetRecursiveRelNodeList(value, out var nodeListState))
        {
            if (nodeListState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a path's node list."));

            using var lease = nodeListHandle.Acquire();
            var nodeList = Read((lbug_value*)lease.Pointer, depth + 1);
            nodes = nodeList.AsList().Select(n => n.AsNode()).ToList();
        }

        List<LadybugRel> rels;
        using (var relListHandle = LbugValueHandle.GetRecursiveRelRelList(value, out var relListState))
        {
            if (relListState != lbug_state.LbugSuccess)
                throw new LadybugException(NativeString.WithErrorDetail("Failed to read a path's relationship list."));

            using var lease = relListHandle.Acquire();
            var relList = Read((lbug_value*)lease.Pointer, depth + 1);
            rels = relList.AsList().Select(r => r.AsRel()).ToList();
        }

        return new LadybugValue(LadybugType.Path, new LadybugPath(nodes.AsReadOnly(), rels.AsReadOnly()));
    }

    private static unsafe LadybugInternalId ReadInternalId(lbug_value* value)
    {
        lbug_internal_id_t native;
        var state = LbugNative.lbug_value_get_internal_id(value, &native);
        ThrowIfFailed(state, "internal_id");
        return new LadybugInternalId(native.table_id, native.offset);
    }

    /// <summary>
    /// Reads a top-level INTERNAL_ID value - e.g. the result of <c>RETURN id(n)</c>, as opposed to
    /// the id embedded in a NODE/REL value (<see cref="ReadNode"/>/<see cref="ReadRel"/> call
    /// <see cref="ReadInternalId"/> directly for that). Shares the same underlying
    /// <see cref="LadybugInternalId"/> payload and native accessor; only the resulting
    /// <see cref="LadybugValue.Type"/> differs, since a bare <c>id(n)</c> result is its own column
    /// value rather than a field of a node or relationship.
    /// </summary>
    private static unsafe LadybugValue ReadInternalIdValue(lbug_value* value) =>
        new(LadybugType.InternalId, ReadInternalId(value));

    /// <summary>
    /// Reads a value of a type this client does not model with its own typed accessor (currently
    /// only <c>POINTER</c>, which is engine-internal and unreachable through any public Cypher path
    /// - see docs/USAGE.md's "UNION and POINTER" section) via the engine's generic
    /// <c>lbug_value_to_string</c>, so it is still readable through <see cref="LadybugValue.AsString"/>
    /// instead of throwing on every accessor.
    /// </summary>
    /// <remarks>
    /// <c>lbug_value_to_string</c> returns a caller-owned <c>char*</c>: its own doc comment in
    /// <c>third-party/lbug.h</c> does not repeat the "caller is responsible for freeing" language
    /// most other <c>char*</c>-returning functions there carry, but <c>lbug_destroy_string</c>'s own
    /// doc comment is unconditional - "Destroys any string created by the Lbug C API, including
    /// both the error message and the values returned by the API functions" - and there is no
    /// separate "borrowed string" free function anywhere in the header, so this routes through
    /// <see cref="NativeString.TakeOwnershipOrNull"/> like every other native string in this client.
    /// </remarks>
    private static unsafe LadybugValue ReadUnsupported(lbug_value* value)
    {
        var raw = LbugNative.lbug_value_to_string(value);
        return new LadybugValue(LadybugType.Unsupported, NativeString.TakeOwnershipOrNull(raw));
    }

    /// <summary>
    /// Reads a UNION value by returning its currently active member, marshalled with that member's
    /// own real type - not wrapped as <see cref="LadybugType.Unsupported"/> the way it used to be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A UNION value always holds exactly one concretely-typed value, never an ambiguous choice
    /// between members: the C header documents <c>lbug_value_get_struct_field_value</c> as
    /// operating on "physical type STRUCT (STRUCT, NODE, REL, RECURSIVE_REL, UNION)", and
    /// empirically (see docs/USAGE.md's "UNION and POINTER" section) field index 0 - the physical
    /// "tag" slot every UNION value carries - always holds that one resolved value, already
    /// correctly typed by the engine, regardless of how the value reached storage:
    /// </para>
    /// <list type="bullet">
    /// <item>A bare Cypher literal assigned to a UNION column (no explicit constructor) is coerced
    /// at WRITE time: the engine tries the declared member types in order and stores the value as
    /// the first one that accepts it - e.g. for <c>UNION(num INT64, txt STRING)</c>, the literal
    /// <c>'42'</c> is stored as the INT64 member, not the STRING one, because INT64 is declared
    /// first and the literal parses as one. <c>'hello'</c> falls through to the STRING member since
    /// it does not parse as INT64.</item>
    /// <item>The explicit <c>union_value(member := value)</c> constructor bypasses that coercion and
    /// stores the named member's own declared type directly - <c>union_value(txt := '42')</c>
    /// genuinely stores a STRING <c>"42"</c>, not an INT64 <c>42</c>, even though both would look
    /// identical through <see cref="LadybugValue.AsString"/> alone.</item>
    /// </list>
    /// <para>
    /// Either way, by the time a UNION value is read back, there is exactly one concrete value with
    /// one concrete type sitting in field 0 - never two live candidates sharing a rendering. The
    /// other struct fields (the union's named members themselves, at index 1 and up) are never
    /// independently readable through this API - confirmed empirically: reading any of them always
    /// fails, regardless of which member is actually active - so this reads only field 0, not every
    /// field the way <see cref="ReadStruct"/> does for an ordinary STRUCT value.
    /// </para>
    /// <para>
    /// This is why no new <see cref="LadybugType"/> member or dedicated accessor exists for UNION:
    /// the resolved value's own type already IS one of the existing <see cref="LadybugType"/>
    /// members, and reusing this reader's normal dispatch (<see cref="Read(lbug_value*, int)"/>)
    /// lets every existing typed accessor (<see cref="LadybugValue.AsInt64"/>,
    /// <see cref="LadybugValue.AsString"/>, etc.) simply work on it.
    /// </para>
    /// </remarks>
    private static unsafe LadybugValue ReadUnion(lbug_value* value, int depth)
    {
        using var fieldHandle = LbugValueHandle.GetStructFieldValue(value, 0, out var fieldState);
        if (fieldState != lbug_state.LbugSuccess)
            throw new LadybugException(NativeString.WithErrorDetail("Failed to read a UNION value's active member."));

        using var lease = fieldHandle.Acquire();
        return Read((lbug_value*)lease.Pointer, depth + 1);
    }

    private static DateTime FromMicros(long micros) =>
        // checked() so an out-of-range microsecond count throws instead of silently wrapping
        // before AddTicks ever sees it.
        DateTime.UnixEpoch.AddTicks(checked(micros * (TimeSpan.TicksPerMillisecond / 1000)));

    private static LadybugException ConversionFailed(string kind, Exception inner) =>
        new($"A {kind} value read from the engine could not be represented as .NET data.", inner);

    private static void ThrowIfFailed(lbug_state state, string kind)
    {
        if (state != lbug_state.LbugSuccess)
            throw new LadybugException(NativeString.WithErrorDetail(
                $"Failed to read a {kind} value from a column the engine reported as that type."));
    }
}
