using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugValueHandle : LbugStructHandle
{
    /// <summary>
    /// <see langword="true"/> for every factory below except <see cref="CreateNull"/>: the caller
    /// (this library) allocated the <c>lbug_value</c> struct storage itself via
    /// <see cref="LbugStructHandle.AllocateUnowned"/>, so releasing it must free that allocation
    /// with <see cref="LbugStructHandle.FreeStorage"/> in addition to calling <c>lbug_value_destroy</c>.
    /// <see langword="false"/> only for a value obtained from <c>lbug_value_create_null</c>, which
    /// returns a pointer the ENGINE allocated and owns end to end - per <c>third-party/lbug.h</c>,
    /// "Caller is responsible for destroying the returned value" is the entire contract, with no
    /// separate free step implied. Calling <c>FreeStorage</c>'s <c>NativeMemory.Free</c> on that
    /// pointer afterward would apply the wrong allocator to memory this process's native allocator
    /// never carved out, and - because <c>lbug_value_destroy</c> already frees what it owns - risks
    /// a double free of the same address, not merely a leak.
    /// </summary>
    private readonly bool _ownsManagedStorage;

    private LbugValueHandle() : this(ownsManagedStorage: true) { }

    private LbugValueHandle(bool ownsManagedStorage) => _ownsManagedStorage = ownsManagedStorage;

    /// <summary>
    /// Runs <c>lbug_value_create_null</c> and wraps the resulting <c>lbug_value*</c> - backs
    /// <see cref="LadybugDb.Client.LadybugPreparedStatement.BindNull"/> via
    /// <c>lbug_prepared_statement_bind_value</c>.
    /// </summary>
    /// <remarks>
    /// Unlike every other factory here, this native call takes no <c>out_value</c> parameter: it
    /// returns a freshly engine-allocated <c>lbug_value*</c> directly, so there is nothing for this
    /// library to allocate via <see cref="LbugStructHandle.AllocateUnowned"/> first - the returned
    /// pointer is adopted as-is. See <see cref="_ownsManagedStorage"/> for why that also changes how
    /// this handle must be released.
    /// </remarks>
    internal static unsafe LbugValueHandle CreateNull()
    {
        var native = LbugNative.lbug_value_create_null();
        var handle = new LbugValueHandle(ownsManagedStorage: false);
        handle.Adopt(native);
        return handle;
    }

    /// <summary>
    /// Runs <c>lbug_flat_tuple_get_value</c> and takes ownership of the resulting
    /// <c>lbug_value</c> storage.
    /// </summary>
    /// <remarks>
    /// Same reasoning as <see cref="LbugFlatTupleHandle.GetNext"/>: only adopts on
    /// <see cref="lbug_state.LbugSuccess"/>, since nothing documents <c>lbug_value_destroy</c> as
    /// safe on storage <c>lbug_flat_tuple_get_value</c> never populated.
    /// </remarks>
    internal static unsafe LbugValueHandle GetValue(
        LbugFlatTupleHandle tuple, ulong columnIndex, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var value = (lbug_value*)storage;
            using (var lease = tuple.Acquire())
            {
                state = LbugNative.lbug_flat_tuple_get_value((lbug_flat_tuple*)lease.Pointer, columnIndex, value);
            }

            var handle = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                // See LbugDatabaseHandle.Open: set before Adopt so a failure here biases toward a
                // leak (storage never freed) rather than a double free.
                adopted = true;
                handle.Adopt(storage);
            }
            return handle;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_value_get_list_element</c> and takes ownership of the resulting element.
    /// </summary>
    /// <remarks>
    /// <paramref name="list"/> is a raw pointer, not a handle: like <see cref="LbugLogicalTypeHandle.GetDataType"/>,
    /// this is always called from inside a scope that already holds a lease covering it (see
    /// <see cref="LadybugDb.Client.ValueReader.Read(lbug_value*)"/>), so a second lease here would be redundant.
    /// Only adopts on <see cref="lbug_state.LbugSuccess"/>, same reasoning as <see cref="GetValue"/>.
    /// </remarks>
    internal static unsafe LbugValueHandle GetListElement(lbug_value* list, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_value_get_list_element(list, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_value_get_struct_field_value</c> and takes ownership of the resulting field
    /// value. Same raw-pointer and success-only-adopt reasoning as <see cref="GetListElement"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetStructFieldValue(lbug_value* @struct, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_value_get_struct_field_value(@struct, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_value_get_map_key</c> and takes ownership of the resulting key value. Same
    /// raw-pointer and success-only-adopt reasoning as <see cref="GetListElement"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetMapKey(lbug_value* map, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_value_get_map_key(map, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_value_get_map_value</c> and takes ownership of the resulting value. Same
    /// raw-pointer and success-only-adopt reasoning as <see cref="GetListElement"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetMapValue(lbug_value* map, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_value_get_map_value(map, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_node_val_get_id_val</c> and takes ownership of the resulting INTERNAL_ID
    /// value. Per <c>third-party/lbug.h</c>: "Returns the internal id value of the given node
    /// value as a lbug value" via an <c>out_value</c> the caller supplies storage for - the same
    /// shape as <c>lbug_value_get_list_element</c>, so this follows the same owned-storage,
    /// success-only-adopt pattern as <see cref="GetListElement"/> rather than treating it as a
    /// borrowed pointer.
    /// </summary>
    internal static unsafe LbugValueHandle GetNodeIdValue(lbug_value* node, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_node_val_get_id_val(node, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_node_val_get_label_val</c> and takes ownership of the resulting STRING value.
    /// Same owned <c>out_value</c> reasoning as <see cref="GetNodeIdValue"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetNodeLabelValue(lbug_value* node, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_node_val_get_label_val(node, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_node_val_get_property_value_at</c> and takes ownership of the resulting
    /// property value. Same owned <c>out_value</c> reasoning as <see cref="GetNodeIdValue"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetNodePropertyValueAt(lbug_value* node, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_node_val_get_property_value_at(node, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_rel_val_get_id_val</c> and takes ownership of the resulting INTERNAL_ID value.
    /// Same owned <c>out_value</c> reasoning as <see cref="GetNodeIdValue"/> - re-confirmed against
    /// <c>third-party/lbug.h</c> (lines 1463-1467) and <c>LbugNative.g.cs</c> for this fix round.
    /// </summary>
    internal static unsafe LbugValueHandle GetRelIdValue(lbug_value* rel, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_rel_val_get_id_val(rel, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_rel_val_get_src_id_val</c> and takes ownership of the resulting INTERNAL_ID
    /// value for the relationship's source node. Same owned <c>out_value</c> reasoning as
    /// <see cref="GetNodeIdValue"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetRelSrcIdValue(lbug_value* rel, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_rel_val_get_src_id_val(rel, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_rel_val_get_dst_id_val</c> and takes ownership of the resulting INTERNAL_ID
    /// value for the relationship's destination node. Same owned <c>out_value</c> reasoning as
    /// <see cref="GetNodeIdValue"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetRelDstIdValue(lbug_value* rel, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_rel_val_get_dst_id_val(rel, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_rel_val_get_label_val</c> and takes ownership of the resulting STRING value.
    /// Same owned <c>out_value</c> reasoning as <see cref="GetNodeLabelValue"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetRelLabelValue(lbug_value* rel, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_rel_val_get_label_val(rel, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    /// <summary>
    /// Runs <c>lbug_rel_val_get_property_value_at</c> and takes ownership of the resulting
    /// property value. Same owned <c>out_value</c> reasoning as <see cref="GetNodePropertyValueAt"/>.
    /// </summary>
    internal static unsafe LbugValueHandle GetRelPropertyValueAt(lbug_value* rel, ulong index, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_value));
        var adopted = false;
        try
        {
            var outValue = (lbug_value*)storage;
            state = LbugNative.lbug_rel_val_get_property_value_at(rel, index, outValue);

            var result = new LbugValueHandle();
            if (state == lbug_state.LbugSuccess)
            {
                adopted = true;
                result.Adopt(storage);
            }
            return result;
        }
        finally
        {
            if (!adopted) FreeUnowned(storage);
        }
    }

    protected override unsafe bool ReleaseHandle()
    {
        try
        {
            LbugNative.lbug_value_destroy((lbug_value*)handle);
        }
        catch
        {
            // ReleaseHandle runs on the finalizer thread and must never throw; see
            // LbugDatabaseHandle.ReleaseHandle for the full rationale.
            return false;
        }
        finally
        {
            // See _ownsManagedStorage: only free the struct memory this library itself allocated.
            // A value from CreateNull() was allocated by the engine and already fully released by
            // lbug_value_destroy above; NativeMemory.Free-ing it too would double-free it.
            if (_ownsManagedStorage) FreeStorage();
            else SetHandle(IntPtr.Zero);
        }

        return true;
    }
}
