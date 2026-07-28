using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugFlatTupleHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_query_result_get_next</c> and takes ownership of the resulting
    /// <c>lbug_flat_tuple</c> storage.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="LbugConnectionHandle.Open"/>: unlike <see cref="LbugQueryResultHandle.Execute"/>,
    /// nothing here documents that <c>lbug_flat_tuple_destroy</c> is safe to call on storage
    /// <c>lbug_query_result_get_next</c> did not successfully populate, so this only adopts when
    /// <paramref name="state"/> comes back <see cref="lbug_state.LbugSuccess"/>; a failure instead
    /// routes through <see cref="LbugStructHandle.FreeUnowned"/>, leaving the returned handle
    /// invalid (and safe to <c>Dispose</c> as a no-op).
    /// </remarks>
    internal static unsafe LbugFlatTupleHandle GetNext(LbugQueryResultHandle queryResult, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_flat_tuple));
        var adopted = false;
        try
        {
            var tuple = (lbug_flat_tuple*)storage;
            using (var lease = queryResult.Acquire())
            {
                state = LbugNative.lbug_query_result_get_next((lbug_query_result*)lease.Pointer, tuple);
            }

            var handle = new LbugFlatTupleHandle();
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
            LbugNative.lbug_flat_tuple_destroy((lbug_flat_tuple*)handle);
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
