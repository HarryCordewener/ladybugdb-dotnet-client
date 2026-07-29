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
    /// e.g. a lease throwing because an ancestor was concurrently disposed, which would leave
    /// storage the engine never touched; that case still routes through <c>FreeUnowned</c> below.
    ///
    /// This also leases <paramref name="database"/> in addition to <paramref name="connection"/>,
    /// and in that order (database outermost). A live connection handle only protects against the
    /// connection's own disposal; nothing about it protects against its ancestor database having
    /// been disposed out from under it, since the two are independent
    /// <see cref="System.Runtime.InteropServices.SafeHandle"/> instances with no relationship the
    /// runtime enforces on our behalf. Without this, a disposed database's freed storage is
    /// dereferenced by the native call - observed as a segfault, not a managed exception.
    /// </remarks>
    internal static unsafe LbugQueryResultHandle Execute(
        LbugDatabaseHandle database, LbugConnectionHandle connection, sbyte* query, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var result = (lbug_query_result*)storage;
            using (var dbLease = database.Acquire())
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

    /// <summary>
    /// Runs <c>lbug_query_result_get_next_query_result</c> and wraps the resulting
    /// <c>lbug_query_result</c> storage - the next statement's result, for a multi-statement
    /// script run through a single <see cref="LbugConnectionHandle"/> query.
    /// </summary>
    /// <remarks>
    /// Same allocate-unowned/run-native-call/adopt-only-on-success shape as
    /// <see cref="LbugFlatTupleHandle.GetNext"/>. What is different, and safety-critical, is
    /// ownership: verified empirically (not from documentation - the header only says "Returns
    /// the next query result"; the one textual hint is the <c>_is_owned_by_cpp</c> field the
    /// struct carries but the header never explains) via a standalone probe process run three
    /// times each way so a crash could only take that throwaway process down, never this one.
    /// A three-statement multi-result script showed the parent's own wrapper always comes back
    /// with <c>_is_owned_by_cpp == 0</c> (it owns and must free its native storage) while every
    /// result returned from this method comes back with <c>_is_owned_by_cpp == 1</c> (a view the
    /// engine still owns; <c>lbug_query_result_destroy</c> on it is a proven no-op). Concretely:
    /// destroying the parent first and then touching the child crashed the probe process with
    /// SIGSEGV (exit 139) on the very next native call against it - a real use-after-free, not a
    /// theoretical one - while destroying the child first and then continuing to use the parent
    /// completed cleanly every time (exit 0), including a further native call on the parent
    /// afterward. In short: the object this method returns is a child that dies with its parent,
    /// not an independent handle. Callers must therefore lease the ORIGINAL owning result (the
    /// one returned from <see cref="Execute"/>, propagated down through every subsequent call to
    /// this method, not just the immediate caller) for the duration of every native call this
    /// child's own handle makes afterward - see <see cref="LadybugDb.Client.LadybugQueryResult"/>'s
    /// <c>_root</c> field, which threads that reference through the whole chain for exactly this
    /// reason.
    /// </remarks>
    internal static unsafe LbugQueryResultHandle GetNextQueryResult(LbugQueryResultHandle parent, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var next = (lbug_query_result*)storage;
            using (var lease = parent.Acquire())
            {
                state = LbugNative.lbug_query_result_get_next_query_result((lbug_query_result*)lease.Pointer, next);
            }

            var handle = new LbugQueryResultHandle();
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
