using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// <c>lbug_connection_destroy</c> can dereference its owning database's storage - most visibly, it
/// auto-rolls-back any transaction still open on the connection it destroys, and that auto-rollback
/// needs the owning <see cref="LbugDatabaseHandle"/> to still be alive, or the native side calls
/// <c>std::terminate()</c> instead of raising anything catchable (verified directly against the
/// real engine). This handle closes that gap the general way every other <c>LbugStructHandle</c>
/// with a native parent does (see <see cref="LbugStructHandle.AcquireParentHolds"/>): from the
/// moment <see cref="Open"/> successfully adopts this connection's storage, it holds its own
/// <see cref="System.Runtime.InteropServices.SafeHandle.DangerousAddRef(ref bool)"/> lease on
/// <see cref="LbugDatabaseHandle"/> for this connection's ENTIRE remaining lifetime - not merely
/// while a transaction happens to be open on it, and not merely for the duration of one call, unlike
/// <see cref="LbugStructHandle.Acquire"/>. That lease makes it impossible for the database's own
/// <c>ReleaseHandle</c> to run - <c>SafeHandle</c> only invokes it once every outstanding reference
/// reaches zero - until this handle's own <see cref="ReleaseHandle"/> has released it, which happens
/// only AFTER <c>lbug_connection_destroy</c> has already run. That ordering holds REGARDLESS of
/// which handle's finalizer the GC happens to run first, or of a concurrent <c>Dispose</c> racing a
/// concurrent transaction begin, or of whether a transaction is open on this connection at all: it
/// is enforced by reference-counting, not by observed GC or thread-scheduling behaviour, or by any
/// bookkeeping scoped to transactions specifically.
/// </summary>
/// <remarks>
/// <para>
/// <b>This used to be a transaction-only special case; it no longer is.</b> An earlier version of
/// this type took the long-lived database hold only while a transaction was open (or being opened),
/// tracked by a dedicated <see cref="System.Threading.Interlocked"/> counter, on the theory that
/// every OTHER connection operation already "captures its product into a self-protecting
/// <c>SafeHandle</c>" and therefore needed no such hold. That reasoning was wrong: a self-protecting
/// child <c>SafeHandle</c> (a query result, a prepared statement) only guards its own storage
/// against ITS OWN disposal - nothing about it makes the DATABASE outlive it, and a query result's
/// <c>ReleaseHandle</c> reaching into database-owned memory (a DML result's factorized table, for
/// example) with no database lease at all is exactly as unsafe as the transaction-only case this
/// type used to guard against alone. Every child handle needing a lifetime reference on its parent
/// is the general rule, not a property specific to transactions - see
/// <see cref="LbugQueryResultHandle"/> and <see cref="LbugPreparedStatementHandle"/>, which now use
/// the same <see cref="LbugStructHandle.AcquireParentHolds"/> mechanism this type does, and
/// <see cref="LbugStructHandle.AcquireParentHolds"/> itself for the general mechanism. Because a
/// connection now holds its database for its whole life unconditionally, <c>BeginTransactionAsync</c>
/// no longer needs (and no longer has) any hold logic of its own - the general rule already covers
/// it by the time a transaction begins, since the connection was opened, and started holding its
/// database, before any transaction could exist.
/// </para>
/// <para>
/// <b>The hold is taken while the native <c>lbug_connection_init</c> call's own short-lived database
/// lease is still active, not after.</b> That lease already guarantees the database's own reference
/// count is at least 1 at the moment the hold is attempted, which - per
/// <see cref="LbugStructHandle.AcquireParentHolds"/>'s remarks - makes the attempt itself
/// structurally guaranteed to succeed there. <see cref="Open"/> still checks the result and handles
/// failure defensively (destroying the just-inited connection immediately, while the temporary
/// lease still guarantees the database is safe to call into, then throwing
/// <see cref="ObjectDisposedException"/> instead of returning a handle nothing would ever be able to
/// safely release later) for the same reason <see cref="LbugStructHandle.AcquireParentHolds"/>
/// itself keeps its own per-attempt handling: not because this path is expected to trigger given
/// today's call shape, but because nothing about the general contract guarantees it never will.
/// </para>
/// </remarks>
internal sealed class LbugConnectionHandle : LbugStructHandle
{
    internal static unsafe LbugConnectionHandle Open(LbugDatabaseHandle database)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_connection));
        var adopted = false;
        try
        {
            var conn = (lbug_connection*)storage;
            var handle = new LbugConnectionHandle();
            var parentHeld = false;

            using (var dbLease = database.Acquire())
            {
                var state = LbugNative.lbug_connection_init((lbug_database*)dbLease.Pointer, conn);
                if (state != lbug_state.LbugSuccess)
                {
                    var detail = NativeString.TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
                    var message = detail is null
                        ? "Failed to open a LadybugDB connection."
                        : $"Failed to open a LadybugDB connection: {detail}";
                    throw new LadybugException(message);
                }

                // Take the hold this connection keeps on its owning database for its whole
                // remaining life - see this class's remarks - still inside dbLease above, which
                // is what makes the attempt structurally guaranteed to succeed here (see
                // LbugStructHandle.AcquireParentHolds's remarks): dbLease's own outstanding
                // reference already keeps the database's count above zero.
                parentHeld = handle.AcquireParentHolds(database);
                if (!parentHeld)
                {
                    // Not expected to be reachable given the above, but handled defensively
                    // anyway: lbug_connection_init already ran and left a real connection behind
                    // - destroy it now, while dbLease still guarantees it is safe to do so, rather
                    // than adopting a handle whose own eventual destroy would have nothing keeping
                    // the database alive for it.
                    LbugNative.lbug_connection_destroy(conn);
                }
            }

            if (!parentHeld) throw new ObjectDisposedException(nameof(LadybugDatabase));

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
        var destroyed = true;
        try
        {
            LbugNative.lbug_connection_destroy((lbug_connection*)handle);
        }
        catch
        {
            // See LbugDatabaseHandle.ReleaseHandle: this runs on the finalizer thread and must
            // never throw, however the native entry point resolves.
            destroyed = false;
        }
        finally
        {
            FreeStorage();
        }

        // Release the hold on the owning database only AFTER the native destroy above, not
        // before - this is what guarantees the database was still alive for
        // lbug_connection_destroy's own internal auto-rollback of any transaction still open on
        // this connection, regardless of whether this ReleaseHandle is running from an explicit
        // Dispose or (if this connection was abandoned without one) from this handle's own
        // finalizer racing arbitrarily against the database handle's finalizer. See this class's
        // remarks and LbugStructHandle.ReleaseParentHolds.
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
