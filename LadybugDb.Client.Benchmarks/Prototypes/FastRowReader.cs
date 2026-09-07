using System.Runtime.InteropServices;
using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Benchmarks.Prototypes;

/// <summary>
/// A PROTOTYPE of a cheaper row-read path, kept out of the library on purpose: it exists to measure
/// how much of the per-row cost is the client's own interop discipline versus the engine.
/// </summary>
/// <remarks>
/// <para>
/// What the shipping <c>LadybugQueryResult.ReadRow</c> does per cell today: allocate a
/// <c>NativeMemory</c> block plus a <c>SafeHandle</c> for the <c>lbug_value</c> wrapper, take a
/// lease on it, allocate ANOTHER block plus <c>SafeHandle</c> for the <c>lbug_logical_type</c> that
/// <c>lbug_value_get_data_type</c> fills (which itself does a C++ <c>new LogicalType</c> per call),
/// dispatch on the type id, box the scalar into <c>LadybugValue</c>'s <c>object</c> payload, then
/// destroy both handles. Plus one native block and <c>SafeHandle</c> per row for the flat tuple.
/// </para>
/// <para>
/// What this prototype does instead, all of it verified against upstream <c>src/c_api</c>:
/// <c>lbug_query_result_get_next</c> and <c>lbug_flat_tuple_get_value</c> both hand back
/// C++-owned borrows (<c>_is_owned_by_cpp = true</c>, so their destroy calls are no-ops), which
/// means the wrapper structs can live on the stack; and the column type ids are read ONCE from
/// <c>lbug_query_result_get_column_data_type</c> rather than once per cell, since a column's type
/// cannot change between rows. The result's own lease is held for the whole read, which is the
/// same guarantee the shipping path gets from leasing per call, obtained once.
/// </para>
/// <para>
/// Two variants: <see cref="ReadValues"/> still materializes <c>LadybugValue[]</c> rows (so the
/// difference from the shipping path is purely interop overhead), and <see cref="ReadObjRows"/>
/// materializes the typed record directly with no boxing (the ceiling a source-generated or
/// column-typed reader could reach).
/// </para>
/// </remarks>
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
