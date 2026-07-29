using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugLogicalTypeHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_value_get_data_type</c> and takes ownership of the resulting
    /// <c>lbug_logical_type</c> storage.
    /// </summary>
    /// <remarks>
    /// <paramref name="value"/> is a raw pointer, not a handle: this is always called from inside
    /// a scope that already holds a lease covering it (see <see cref="LadybugDb.Client.ValueReader.Read(lbug_value*)"/>),
    /// so a second lease here would be redundant, not safer.
    ///
    /// <c>lbug_value_get_data_type</c> returns <c>void</c> - there is no <see cref="lbug_state"/>
    /// to check. Unlike <see cref="LbugValueHandle.GetValue"/> or <see cref="LbugFlatTupleHandle.GetNext"/>,
    /// which only adopt on <see cref="lbug_state.LbugSuccess"/>, this adopts unconditionally once
    /// the native call has run - the same reasoning <see cref="LbugQueryResultHandle.Execute"/>
    /// documents for its own unconditional adopt: with no distinct failure signal, treating the
    /// call as always having produced storage <c>lbug_data_type_destroy</c> is safe to call on is
    /// the only available option.
    /// </remarks>
    internal static unsafe LbugLogicalTypeHandle GetDataType(lbug_value* value)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_logical_type));
        var adopted = false;
        try
        {
            var type = (lbug_logical_type*)storage;
            LbugNative.lbug_value_get_data_type(value, type);

            var handle = new LbugLogicalTypeHandle();
            // See LbugDatabaseHandle.Open: set before Adopt so a failure here biases toward a
            // leak (storage never freed) rather than a double free.
            adopted = true;
            handle.Adopt(storage);
            return handle;
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
            LbugNative.lbug_data_type_destroy((lbug_logical_type*)handle);
        }
        catch
        {
            // ReleaseHandle runs on the finalizer thread and must never throw; see
            // LbugDatabaseHandle.ReleaseHandle for the full rationale.
            return false;
        }
        finally
        {
            FreeStorage();
        }

        return true;
    }
}
