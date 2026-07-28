using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugQueryResultHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_connection_query</c> and takes ownership of the resulting
    /// <c>lbug_query_result</c> storage.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="LbugConnectionHandle.Open"/>'s allocate-unowned/run-native-call/adopt
    /// shape, but deliberately diverges from it on one point: <c>Open</c> only adopts when
    /// <c>state == LbugSuccess</c>, while this adopts unconditionally once
    /// <c>lbug_connection_query</c> has run, regardless of the returned <see cref="lbug_state"/>.
    /// That is a considered choice, not an oversight - <c>Open</c>'s storage (a bare
    /// <c>lbug_database</c>/<c>lbug_connection</c>) is meaningless on init failure, so there is
    /// nothing for its matching <c>*_destroy</c> to do and adopting would just risk calling it on
    /// storage the engine never finished building. A query failure is different: the C header
    /// does not document it explicitly, but observed behavior (a failed query still returns a
    /// result carrying a readable error message via <c>lbug_query_result_get_error_message</c>,
    /// and this project's design notes record the same) implies <c>out_query_result</c> is always
    /// left in a state <c>lbug_query_result_destroy</c> is safe - and needs - to be called on:
    /// either a real result, or one capturing the error. Skipping adoption on failure would leak
    /// that captured state instead of freeing it. This is still an assumption against an
    /// undocumented C contract, not a proven guarantee - if a future engine version left
    /// <c>out_query_result</c> unpopulated on some failure path, destroying it here would be
    /// unsafe. The only path that must NOT adopt is one where the native call never ran at all,
    /// e.g. the connection lease throwing because the connection was concurrently disposed, which
    /// would leave storage the engine never touched; that case still routes through
    /// <c>FreeUnowned</c> below.
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
