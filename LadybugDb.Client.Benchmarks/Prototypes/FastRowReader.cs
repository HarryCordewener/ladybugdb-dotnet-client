using System.Runtime.InteropServices;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Benchmarks.Prototypes;

/// <summary>
/// The prototype that measured the headroom in the row read path; the library adopted its shape
/// (stack-allocated borrows, column types read once) in <c>LadybugQueryResult.ReadRow</c>. Kept as
/// the reference point the benchmarks compare against: <see cref="ReadValues"/> produces the same
/// boxed <see cref="LadybugValue"/> rows as the library, <see cref="ReadObjRows"/> materializes a
/// typed record with no boxing - the ceiling a source-generated reader could reach.
/// </summary>
internal static unsafe class FastRowReader
{
    internal static lbug_data_type_id[] ReadColumnTypes(LadybugQueryResult result)
    {
        using var lease = result.Handle.Acquire();
        var qr = (lbug_query_result*)lease.Pointer;
        var count = LbugNative.lbug_query_result_get_num_columns(qr);
        var types = new lbug_data_type_id[count];
        for (ulong i = 0; i < count; i++)
        {
            lbug_logical_type type;
            var state = LbugNative.lbug_query_result_get_column_data_type(qr, i, &type);
            if (state != lbug_state.LbugSuccess) throw new LadybugException($"column {i} type");
            try
            {
                types[i] = LbugNative.lbug_data_type_get_id(&type);
            }
            finally
            {
                LbugNative.lbug_data_type_destroy(&type);
            }
        }
        return types;
    }

    /// <summary>Shipping-equivalent output (boxed <see cref="LadybugValue"/> rows) via the cheaper interop path.</summary>
    internal static List<LadybugValue[]> ReadValues(LadybugQueryResult result)
    {
        var types = ReadColumnTypes(result);
        var rows = new List<LadybugValue[]>();
        using var lease = result.Handle.Acquire();
        var qr = (lbug_query_result*)lease.Pointer;

        lbug_flat_tuple tuple;
        lbug_value value;
        while (LbugNative.lbug_query_result_has_next(qr) != 0)
        {
            if (LbugNative.lbug_query_result_get_next(qr, &tuple) != lbug_state.LbugSuccess)
                throw new LadybugException("get_next");

            var cells = new LadybugValue[types.Length];
            for (var i = 0; i < types.Length; i++)
            {
                if (LbugNative.lbug_flat_tuple_get_value(&tuple, (ulong)i, &value) != lbug_state.LbugSuccess)
                    throw new LadybugException("get_value");
                cells[i] = ReadCell(&value, types[i]);
            }
            rows.Add(cells);
        }
        return rows;
    }

    /// <summary>Typed, unboxed materialization of the <c>dbref, name, loc</c> shape.</summary>
    internal static List<ObjRow> ReadObjRows(LadybugQueryResult result)
    {
        var rows = new List<ObjRow>();
        using var lease = result.Handle.Acquire();
        var qr = (lbug_query_result*)lease.Pointer;

        lbug_flat_tuple tuple;
        lbug_value value;
        while (LbugNative.lbug_query_result_has_next(qr) != 0)
        {
            if (LbugNative.lbug_query_result_get_next(qr, &tuple) != lbug_state.LbugSuccess)
                throw new LadybugException("get_next");

            LbugNative.lbug_flat_tuple_get_value(&tuple, 0, &value);
            long dbref;
            LbugNative.lbug_value_get_int64(&value, &dbref);

            LbugNative.lbug_flat_tuple_get_value(&tuple, 1, &value);
            var name = ReadString(&value);

            LbugNative.lbug_flat_tuple_get_value(&tuple, 2, &value);
            long loc;
            LbugNative.lbug_value_get_int64(&value, &loc);

            rows.Add(new ObjRow(dbref, name, loc));
        }
        return rows;
    }

    private static LadybugValue ReadCell(lbug_value* value, lbug_data_type_id type)
    {
        if (LbugNative.lbug_value_is_null(value) != 0) return new LadybugValue(LadybugType.Null, null);
        switch (type)
        {
            case lbug_data_type_id.LBUG_INT64:
            case lbug_data_type_id.LBUG_SERIAL:
            {
                long v;
                LbugNative.lbug_value_get_int64(value, &v);
                return new LadybugValue(LadybugType.Int64, v);
            }
            case lbug_data_type_id.LBUG_STRING:
                return new LadybugValue(LadybugType.String, ReadString(value));
            default:
                // Anything else falls back to the shipping reader, which handles every type.
                return ValueReader.Read(value);
        }
    }

    private static string ReadString(lbug_value* value)
    {
        sbyte* raw;
        if (LbugNative.lbug_value_get_string(value, &raw) != lbug_state.LbugSuccess)
            throw new LadybugException("get_string");
        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)raw) ?? string.Empty;
        }
        finally
        {
            LbugNative.lbug_destroy_string(raw);
        }
    }
}
