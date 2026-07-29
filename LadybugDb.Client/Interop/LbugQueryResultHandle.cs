using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

internal sealed class LbugQueryResultHandle : LbugStructHandle
{
    /// <summary>
    /// Runs <c>lbug_connection_query</c> and takes ownership of the resulting
    /// <c>lbug_query_result</c> storage.
    /// </summary>
    /// <remarks>
    /// <para>
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
    /// </para>
    /// <para>
    /// This also holds <paramref name="database"/> and <paramref name="connection"/> for the
    /// resulting handle's ENTIRE remaining lifetime - not merely for the duration of this call -
    /// via <see cref="LbugStructHandle.AcquireParentHolds"/>, database outermost, same ordering as
    /// the temporary leases below: a live connection handle only protects against the
    /// connection's own disposal; nothing about it protects against its ancestor database having
    /// been disposed out from under it, since the two are independent
    /// <see cref="System.Runtime.InteropServices.SafeHandle"/> instances with no relationship the
    /// runtime enforces on our behalf. Without holding both for this result's whole life, this
    /// result's own eventual <c>ReleaseHandle</c> - which for a DML result reaches into memory the
    /// database owns - could run after the database (or the connection) was already destroyed:
    /// observed directly as SIGSEGV, not merely a theoretical risk. See
    /// <see cref="LbugStructHandle.AcquireParentHolds"/> for the general mechanism this uses, and
    /// why the attempt is made while the temporary leases below are still active rather than
    /// after they are released.
    /// </para>
    /// </remarks>
    internal static unsafe LbugQueryResultHandle Execute(
        LbugDatabaseHandle database, LbugConnectionHandle connection, sbyte* query, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var result = (lbug_query_result*)storage;
            var handle = new LbugQueryResultHandle();
            var parentsHeld = false;

            using (var dbLease = database.Acquire())
            using (var connLease = connection.Acquire())
            {
                state = LbugNative.lbug_connection_query((lbug_connection*)connLease.Pointer, query, result);

                // Take the long-lived holds this handle keeps for the rest of its life, still
                // inside the temporary leases above, which is what makes the attempt structurally
                // guaranteed to succeed here (see LbugStructHandle.AcquireParentHolds's remarks):
                // dbLease/connLease's own outstanding references already keep both parents' counts
                // above zero.
                parentsHeld = handle.AcquireParentHolds(database, connection);
                if (!parentsHeld)
                {
                    // Not expected to be reachable given the above, but handled defensively
                    // anyway: the native call above still ran and left a destroy-safe result (see
                    // this method's remarks on unconditional adoption), so destroy it now while
                    // dbLease/connLease still guarantee it is safe to do so, rather than adopting
                    // a handle nothing would ever be able to safely release later.
                    LbugNative.lbug_query_result_destroy(result);
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

    /// <summary>
    /// Runs <c>lbug_connection_execute</c> against a prepared statement and takes ownership of the
    /// resulting <c>lbug_query_result</c> storage.
    /// </summary>
    /// <remarks>
    /// Same allocate-unowned/run-native-call/adopt-unconditionally shape as <see cref="Execute"/>,
    /// for the same reason: an execution that fails still leaves <c>out_query_result</c> in a
    /// destroy-safe state carrying a readable error message, exactly like a failed
    /// <c>lbug_connection_query</c>. Leases, and then long-livedly holds (see
    /// <see cref="Execute"/>'s remarks), <paramref name="database"/>, <paramref name="connection"/>,
    /// and <paramref name="statement"/> together - database outermost - because
    /// <c>lbug_connection_execute</c> dereferences both the connection and the prepared statement
    /// pointers, not just the statement's own storage, and this result's own eventual destroy can
    /// reach into memory all three transitively depend on.
    /// </remarks>
    internal static unsafe LbugQueryResultHandle ExecutePrepared(
        LbugDatabaseHandle database, LbugConnectionHandle connection, LbugPreparedStatementHandle statement, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var result = (lbug_query_result*)storage;
            var handle = new LbugQueryResultHandle();
            var parentsHeld = false;

            using (var dbLease = database.Acquire())
            using (var connLease = connection.Acquire())
            using (var stmtLease = statement.Acquire())
            {
                state = LbugNative.lbug_connection_execute(
                    (lbug_connection*)connLease.Pointer, (lbug_prepared_statement*)stmtLease.Pointer, result);

                // See Execute's remarks: taken while the temporary leases above are still active.
                parentsHeld = handle.AcquireParentHolds(database, connection, statement);
                if (!parentsHeld)
                {
                    LbugNative.lbug_query_result_destroy(result);
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

    /// <summary>
    /// Runs <c>lbug_query_result_get_next_query_result</c> and wraps the resulting
    /// <c>lbug_query_result</c> storage - the next statement's result, for a multi-statement
    /// script run through a single <see cref="LbugConnectionHandle"/> query.
    /// </summary>
    /// <remarks>
    /// <para>
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
    /// not an independent handle.
    /// </para>
    /// <para>
    /// This handle holds a long-lived reference on <paramref name="parent"/> for its own entire
    /// remaining lifetime - see <see cref="LbugStructHandle.AcquireParentHolds"/> - which is what
    /// makes that "dies with its parent" relationship actually true rather than merely intended: a
    /// live child now keeps its parent from being destroyed, chaining transitively all the way up
    /// to the original result <see cref="Execute"/>/<see cref="ExecutePrepared"/> returned (and,
    /// through that result's own holds, the connection and database beneath it), so a native call
    /// three results deep is always safe regardless of what the caller has already disposed. This
    /// supersedes the older approach of the caller (<see cref="LadybugDb.Client.LadybugQueryResult"/>'s
    /// <c>_root</c> field) manually re-leasing the ORIGINAL owning result around every native call
    /// on a descendant - that field remains for the "throw promptly on new work after dispose"
    /// behaviour <see cref="LbugStructHandle.Acquire"/>'s leases provide (see that type's remarks),
    /// but is no longer the only thing keeping a descendant's chain of ancestors alive.
    /// </para>
    /// </remarks>
    internal static unsafe LbugQueryResultHandle GetNextQueryResult(LbugQueryResultHandle parent, out lbug_state state)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_query_result));
        var adopted = false;
        try
        {
            var next = (lbug_query_result*)storage;
            var handle = new LbugQueryResultHandle();
            var parentHeld = false;

            using (var lease = parent.Acquire())
            {
                state = LbugNative.lbug_query_result_get_next_query_result((lbug_query_result*)lease.Pointer, next);
                if (state == lbug_state.LbugSuccess)
                {
                    // See this method's remarks: taken while the temporary lease above is still
                    // active. On failure here there is nothing further to destroy - this "next"
                    // result is a view the parent's own storage owns (see the remarks above), so
                    // declining to adopt is enough; the case is reported to the caller below.
                    parentHeld = handle.AcquireParentHolds(parent);
                }
            }

            if (state == lbug_state.LbugSuccess)
            {
                if (!parentHeld) throw new ObjectDisposedException(nameof(LadybugDatabase));

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
        var destroyed = true;
        try
        {
            LbugNative.lbug_query_result_destroy((lbug_query_result*)handle);
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

        // Release the long-lived holds only AFTER the native destroy above, not before - see
        // LbugConnectionHandle.ReleaseHandle and LbugStructHandle.ReleaseParentHolds for why the
        // ordering, not merely the holding, is what matters.
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
