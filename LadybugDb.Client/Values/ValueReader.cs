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

    private static void ThrowIfFailed(lbug_state state, string kind)
    {
        if (state != lbug_state.LbugSuccess)
            throw new LadybugException($"Failed to read a {kind} value from a column the engine reported as that type.");
    }
}
