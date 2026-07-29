using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>Marshals a native <c>lbug_value</c> into a fully managed <see cref="LadybugValue"/>.</summary>
internal static class ValueReader
{
    /// <summary>
    /// Reads <paramref name="value"/> eagerly into a <see cref="LadybugValue"/> that holds only
    /// managed data. Callers must hold whatever lease keeps <paramref name="value"/> alive for the
    /// duration of this call; nothing here retains the pointer afterward.
    /// </summary>
    internal static unsafe LadybugValue Read(lbug_value* value)
    {
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
            lbug_data_type_id.LBUG_STRING => ReadString(value),
            lbug_data_type_id.LBUG_DATE => ReadDate(value),
            lbug_data_type_id.LBUG_TIMESTAMP => ReadTimestamp(value),
            lbug_data_type_id.LBUG_TIMESTAMP_SEC => ReadTimestampSec(value),
            lbug_data_type_id.LBUG_TIMESTAMP_MS => ReadTimestampMs(value),
            lbug_data_type_id.LBUG_TIMESTAMP_NS => ReadTimestampNs(value),
            lbug_data_type_id.LBUG_TIMESTAMP_TZ => ReadTimestampTz(value),
            lbug_data_type_id.LBUG_INTERVAL => ReadInterval(value),
            lbug_data_type_id.LBUG_BLOB => ReadBlob(value),
            _ => new LadybugValue(LadybugType.Unsupported, null),
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
                var bytes = new byte[length];
                if (length > 0)
                    new ReadOnlySpan<byte>(raw, checked((int)length)).CopyTo(bytes);
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
