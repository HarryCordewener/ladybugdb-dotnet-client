using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugPreparedStatementHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_connection_prepare</c> and takes ownership of the resulting
    /// <c>lbug_prepared_statement</c> storage.
    /// </summary>
    /// <remarks>
    /// <c>lbug_prepared_statement</c> carries TWO pointer fields (<c>_prepared_statement</c> and
    /// <c>_bound_values</c>), not one - the storage size below comes from
    /// <c>sizeof(lbug_prepared_statement)</c>, never a hardcoded single-pointer size, or half the
    /// struct would be left unallocated for the native call to write into.
    ///
    /// Adopts unconditionally once <c>lbug_connection_prepare</c> has run, regardless of the
    /// returned <see cref="lbug_state"/> - same reasoning as <see cref="LbugQueryResultHandle.Execute"/>:
    /// a syntactically invalid Cypher statement is a normal "the prepared statement carries an
    /// error" outcome (checked separately via <c>lbug_prepared_statement_is_success</c> at the
    /// <see cref="LadybugDb.Client.LadybugConnection"/> layer, mirroring how a failed query still
    /// returns a destroy-safe <c>lbug_query_result</c>), not a case where <c>out_prepared_statement</c>
    /// was left untouched. The only path that must NOT adopt is one where the native call never ran
    /// at all - e.g. a lease throwing because an ancestor was concurrently disposed - which still
    /// routes through <see cref="LbugStructHandle.FreeUnowned"/> below.
    ///
    /// Leases <paramref name="database"/> in addition to <paramref name="connection"/>, database
    /// outermost - <c>lbug_connection_prepare</c> takes a connection pointer, and per
    /// <see cref="LbugQueryResultHandle.Execute"/>'s remarks a live connection handle alone does not
    /// protect against its ancestor database having been disposed out from under it.
    /// </remarks>
    internal static unsafe LbugPreparedStatementHandle Prepare(
        LbugDatabaseHandle database, LbugConnectionHandle connection, sbyte* query, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_prepared_statement));
        var adopted = false;
        try
        {
            var prepared = (lbug_prepared_statement*)storage;
            using (var dbLease = database.Acquire())
            using (var lease = connection.Acquire())
            {
                state = LbugNative.lbug_connection_prepare((lbug_connection*)lease.Pointer, query, prepared);
            }

            var handle = new LbugPreparedStatementHandle();
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
            LbugNative.lbug_prepared_statement_destroy((lbug_prepared_statement*)handle);
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
