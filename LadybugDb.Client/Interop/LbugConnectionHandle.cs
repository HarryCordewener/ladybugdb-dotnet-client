using System.Runtime.InteropServices;
using LadybugDb.Client.Native;

namespace LadybugDb.Client.Interop;

/// <summary>
/// <c>lbug_connection_destroy</c> auto-rolls-back any transaction still open on the connection
/// it destroys, and that auto-rollback needs the owning <see cref="LbugDatabaseHandle"/> to still
/// be alive - if the database was already destroyed, the native side calls
/// <c>std::terminate()</c> instead of raising anything catchable (verified directly against the
/// real engine). <see cref="LadybugConnection"/>/<see cref="LadybugDatabase"/> close out any open
/// transaction explicitly before either's own <c>Dispose</c>/<c>DisposeAsync</c> can reach this
/// handle's release, which covers every path that goes through those managed wrappers. It does
/// NOT cover a caller abandoning <c>LadybugDatabase</c>/<c>LadybugConnection</c>/
/// <c>LadybugTransaction</c> without disposing anything: only the underlying <c>SafeHandle</c>s
/// have finalizers (the managed wrappers do not), so in that case the ONLY thing that ever runs
/// is this handle's own <see cref="ReleaseHandle"/> and <see cref="LbugDatabaseHandle"/>'s -
/// independently, in an order the CLR does not guarantee (an object reachable only through
/// another finalizable object is not guaranteed to outlive it during finalization). This type
/// closes that gap by holding its own <see cref="SafeHandle.DangerousAddRef(ref bool)"/> lease on
/// <see cref="_ownerDatabase"/> for as long as this connection has an open (or being-opened)
/// transaction (see <see cref="TryAcquireDatabaseHoldForTransaction"/>/
/// <see cref="ReleaseDatabaseHoldForTransaction"/>) - not merely for the duration of one call,
/// unlike <see cref="LbugStructHandle.Acquire"/>. That lease makes it impossible for the
/// database's own <c>ReleaseHandle</c> to run - <c>SafeHandle</c> only invokes it once every
/// outstanding reference reaches zero - until this handle's own <see cref="ReleaseHandle"/> has
/// released it, REGARDLESS of which handle's finalizer the GC happens to run first, or of a
/// concurrent <c>Dispose</c> racing a concurrent transaction begin. Ordering is enforced by
/// reference-counting, not by observed GC or thread-scheduling behaviour.
/// </summary>
/// <remarks>
/// <b>The hold must be acquired BEFORE the engine-level <c>BEGIN TRANSACTION</c> is issued, not
/// after it succeeds.</b> A second, live-concurrency crash (distinct from the GC-abandonment one
/// above, found reviewing this same class) proved that the boundary matters: the engine considers
/// a transaction open the instant its own <c>BEGIN TRANSACTION</c> call returns success - before
/// <em>any</em> C# bookkeeping runs, including the <c>await using</c> that disposes that call's
/// own query result inside <c>LadybugTransaction.BeginAsync</c>. If the hold were taken only
/// after that call returns (as an earlier version of this fix did), a concurrent
/// <see cref="LadybugDatabase.Dispose"/> could complete FULLY inside that window - nothing is
/// holding the database open yet - freeing its native memory while the engine still has a live,
/// completely untracked open transaction on this connection. Reproduced 4/4 as a segfault
/// (SIGSEGV), not the catchable <c>std::terminate()</c> case above; verified fixed by moving the
/// acquire to <em>before</em> <see cref="LadybugConnection.BeginTransactionAsync"/> issues
/// anything to the engine at all, releasing it immediately if the attempt itself then fails (see
/// that method).
/// </remarks>
internal sealed class LbugConnectionHandle : LbugStructHandle
{
    /// <summary>
    /// This connection's owning database, retained (not merely leased per-call) specifically so
    /// <see cref="TryAcquireDatabaseHoldForTransaction"/>/<see cref="ReleaseDatabaseHoldForTransaction"/>
    /// can bracket a transaction attempt (and, once it succeeds, the resulting open transaction)
    /// with a long-lived <see cref="SafeHandle.DangerousAddRef(ref bool)"/> lease - see this
    /// class's remarks.
    /// </summary>
    private LbugDatabaseHandle? _ownerDatabase;

    /// <summary><see langword="true"/> between a successful <see cref="TryAcquireDatabaseHoldForTransaction"/> and the matching <see cref="ReleaseDatabaseHoldForTransaction"/>, so <see cref="ReleaseHandle"/> knows whether it is still holding a database reference it must release.</summary>
    private bool _databaseRefHeldForOpenTransaction;

    internal static unsafe LbugConnectionHandle Open(LbugDatabaseHandle database)
    {
        var storage = AllocateUnowned((nuint)sizeof(lbug_connection));
        var adopted = false;
        try
        {
            var conn = (lbug_connection*)storage;

            lbug_state state;
            using (var lease = database.Acquire())
            {
                state = LbugNative.lbug_connection_init((lbug_database*)lease.Pointer, conn);
            }

            if (state != lbug_state.LbugSuccess)
            {
                var detail = NativeString.TakeOwnershipOrNull(LbugNative.lbug_get_last_error());
                var message = detail is null
                    ? "Failed to open a LadybugDB connection."
                    : $"Failed to open a LadybugDB connection: {detail}";
                throw new LadybugException(message);
            }

            var handle = new LbugConnectionHandle { _ownerDatabase = database };
            // See LbugDatabaseHandle.Open: set before Adopt so a failure here biases toward a
            // leak (storage never freed) rather than a double free (freed here, then again from
            // a handle that already believes it owns it).
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
    /// Begins holding a long-lived reference on the owning database for the duration of a
    /// <c>BEGIN TRANSACTION</c> attempt, and (if it succeeds) for as long as the resulting
    /// transaction stays open afterward. Called by
    /// <see cref="LadybugConnection.BeginTransactionAsync"/> BEFORE it issues anything to the
    /// engine - see this class's remarks for why the ordering, not just the holding, is what
    /// matters here. Not for direct use.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> if the owning database is already fully closed - the caller must
    /// not proceed to issue <c>BEGIN TRANSACTION</c> in that case, since there would be nothing
    /// left for it to safely run against.
    /// </returns>
    internal bool TryAcquireDatabaseHoldForTransaction()
    {
        if (_databaseRefHeldForOpenTransaction) return true;
        if (_ownerDatabase is null) return false;
        var acquired = false;
        _ownerDatabase.DangerousAddRef(ref acquired);
        _databaseRefHeldForOpenTransaction = acquired;
        return acquired;
    }

    /// <summary>
    /// Releases the reference <see cref="TryAcquireDatabaseHoldForTransaction"/> took - either
    /// because the <c>BEGIN TRANSACTION</c> attempt it was guarding failed outright (called from
    /// <see cref="LadybugConnection.BeginTransactionAsync"/>'s own failure path), or because the
    /// transaction it protected has since committed, rolled back, or otherwise closed (called
    /// from <see cref="LadybugConnection.OnTransactionCompleted"/>). Not for direct use.
    /// </summary>
    internal void ReleaseDatabaseHoldForTransaction()
    {
        if (!_databaseRefHeldForOpenTransaction) return;
        _databaseRefHeldForOpenTransaction = false;
        _ownerDatabase?.DangerousRelease();
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

        // Release AFTER the native destroy above, not before: this is what guarantees the
        // database was still alive for it - regardless of whether this ReleaseHandle is running
        // from an explicit Dispose or, if this connection was abandoned without one, from this
        // handle's own finalizer racing arbitrarily against the database handle's finalizer. See
        // this class's remarks. Never allowed to throw out of here either.
        if (_databaseRefHeldForOpenTransaction)
        {
            _databaseRefHeldForOpenTransaction = false;
            try
            {
                _ownerDatabase?.DangerousRelease();
            }
            catch
            {
                destroyed = false;
            }
        }

        return destroyed;
    }
}
