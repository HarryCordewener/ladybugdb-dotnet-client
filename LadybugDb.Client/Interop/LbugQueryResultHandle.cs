using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugQueryResultHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_connection_query</c> and takes ownership of the resulting
    /// <c>lbug_query_result</c> storage.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="LbugConnectionHandle.Open"/>: storage is allocated unowned, the native
    /// call runs against it, and only once that call has actually completed - whether the query
    /// itself succeeded or failed - does this adopt the storage so <see cref="ReleaseHandle"/>
    /// will destroy it. A query failure (bad Cypher, a runtime error) still leaves
    /// <c>out_query_result</c> populated with a struct <c>lbug_query_result_destroy</c> is safe
    /// to call on - it carries the captured error message - so it is adopted just the same. The
    /// only path that must NOT adopt is one where the native call never ran at all, e.g. the
    /// connection lease throwing because the connection was concurrently disposed, which would
    /// leave storage the engine never touched.
    /// </remarks>
    internal static unsafe LbugQueryResultHandle Execute(
        LbugConnectionHandle connection, sbyte* query, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var result = (lbug_query_result*)storage;
            using (var lease = connection.Acquire())
            {
                state = LbugNative.lbug_connection_query((lbug_connection*)lease.Pointer, query, result);
            }

            var handle = new LbugQueryResultHandle();
            // See LbugDatabaseHandle.Open: set before Adopt so a failure inside Adopt itself
            // biases toward a leak (storage never freed) rather than a double free.
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
            LbugNative.lbug_query_result_destroy((lbug_query_result*)handle);
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
