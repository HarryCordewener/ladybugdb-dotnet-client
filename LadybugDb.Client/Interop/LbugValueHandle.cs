using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugValueHandle : LbugStructHandle
{
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
            FreeStorage();
        }

        return true;
    }
}
