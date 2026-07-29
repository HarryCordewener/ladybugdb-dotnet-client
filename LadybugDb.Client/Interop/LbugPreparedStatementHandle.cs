using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugPreparedStatementHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_connection_prepare</c> and takes ownership of the resulting
    /// <c>lbug_prepared_statement</c> storage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>lbug_prepared_statement</c> carries TWO pointer fields (<c>_prepared_statement</c> and
    /// <c>_bound_values</c>), not one - the storage size below comes from
    /// <c>sizeof(lbug_prepared_statement)</c>, never a hardcoded single-pointer size, or half the
    /// struct would be left unallocated for the native call to write into.
    /// </para>
    /// <para>
    /// Adopts unconditionally once <c>lbug_connection_prepare</c> has run, regardless of the
    /// returned <see cref="lbug_state"/> - same reasoning as <see cref="LbugQueryResultHandle.Execute"/>:
    /// a syntactically invalid Cypher statement is a normal "the prepared statement carries an
    /// error" outcome (checked separately via <c>lbug_prepared_statement_is_success</c> at the
    /// <see cref="LadybugDb.Client.LadybugConnection"/> layer, mirroring how a failed query still
    /// returns a destroy-safe <c>lbug_query_result</c>), not a case where <c>out_prepared_statement</c>
    /// was left untouched. The only path that must NOT adopt is one where the native call never ran
    /// at all - e.g. a lease throwing because an ancestor was concurrently disposed - which still
    /// routes through <see cref="LbugStructHandle.FreeUnowned"/> below.
    /// </para>
    /// <para>
    /// Also holds <paramref name="database"/> and <paramref name="connection"/> for this handle's
    /// ENTIRE remaining lifetime - not merely for the duration of this call - via
    /// <see cref="LbugStructHandle.AcquireParentHolds"/>, database outermost, same ordering as the
    /// temporary leases below: <c>lbug_connection_prepare</c> takes a connection pointer, and per
    /// <see cref="LbugQueryResultHandle.Execute"/>'s remarks a live connection handle alone does not
    /// protect against its ancestor database having been disposed out from under it - nor, without
    /// the long-lived hold, would either protect this statement's own eventual
    /// <c>lbug_prepared_statement_destroy</c> from running after either has already been destroyed.
    /// </para>
    /// </remarks>
    internal static unsafe LbugPreparedStatementHandle Prepare(
        LbugDatabaseHandle database, LbugConnectionHandle connection, sbyte* query, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_prepared_statement));
        var adopted = false;
        try
        {
            var prepared = (lbug_prepared_statement*)storage;
            var handle = new LbugPreparedStatementHandle();
            var parentsHeld = false;

            using (var dbLease = database.Acquire())
            using (var lease = connection.Acquire())
            {
                state = LbugNative.lbug_connection_prepare((lbug_connection*)lease.Pointer, query, prepared);

                // Take the long-lived holds this handle keeps for the rest of its life, still
                // inside the temporary leases above, which is what makes the attempt structurally
                // guaranteed to succeed here - see LbugQueryResultHandle.Execute's remarks and
                // LbugStructHandle.AcquireParentHolds.
                parentsHeld = handle.AcquireParentHolds(database, connection);
                if (!parentsHeld)
                {
                    // Not expected to be reachable given the above, but handled defensively
                    // anyway: lbug_connection_prepare already ran and left a destroy-safe
                    // statement behind (same unconditional-adopt reasoning as above) - destroy it
                    // now while the leases still guarantee it is safe to do so.
                    LbugNative.lbug_prepared_statement_destroy(prepared);
                }
            }

            if (!parentsHeld) throw new ObjectDisposedException(nameof(LadybugDatabase));

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
        var destroyed = true;
        try
        {
            LbugNative.lbug_prepared_statement_destroy((lbug_prepared_statement*)handle);
        }
        catch
        {
            // ReleaseHandle runs on the finalizer thread and must never throw; see
            // LbugDatabaseHandle.ReleaseHandle for the full rationale.
            destroyed = false;
        }
        finally
        {
            FreeStorage();
        }

        // Release the long-lived holds only AFTER the native destroy above - see
        // LbugConnectionHandle.ReleaseHandle and LbugStructHandle.ReleaseParentHolds.
        try
        {
            ReleaseParentHolds();
        }
        catch
        {
            destroyed = false;
        }

        return destroyed;
    }
}
