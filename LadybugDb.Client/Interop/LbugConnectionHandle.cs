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
/// <see cref="_ownerDatabase"/> for as long as this connection has an open transaction (see
/// <see cref="MarkTransactionOpen"/>/<see cref="MarkTransactionClosed"/>) - not merely for the
/// duration of one call, unlike <see cref="LbugStructHandle.Acquire"/>. That lease makes it
/// impossible for the database's own <c>ReleaseHandle</c> to run - <c>SafeHandle</c> only invokes
/// it once every outstanding reference reaches zero - until this handle's own
/// <see cref="ReleaseHandle"/> has released it, REGARDLESS of which handle's finalizer the GC
/// happens to run first. Ordering is enforced by reference-counting, not by observed GC
/// behaviour.
/// </summary>
internal sealed class LbugConnectionHandle : LbugStructHandle
{
    /// <summary>
    /// This connection's owning database, retained (not merely leased per-call) specifically so
    /// <see cref="MarkTransactionOpen"/>/<see cref="MarkTransactionClosed"/> can bracket an open
    /// transaction with a long-lived <see cref="SafeHandle.DangerousAddRef(ref bool)"/> lease -
    /// see this class's remarks.
    /// </summary>
    private LbugDatabaseHandle? _ownerDatabase;

    /// <summary><see langword="true"/> between a successful <see cref="MarkTransactionOpen"/> and the matching <see cref="MarkTransactionClosed"/>, so <see cref="ReleaseHandle"/> knows whether it is still holding a database reference it must release.</summary>
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
    /// Begins holding a long-lived reference on the owning database for as long as this
    /// connection has an open transaction. Called by
    /// <see cref="LadybugConnection.BeginTransactionAsync"/>; not for direct use. See this
    /// class's remarks.
    /// </summary>
    internal void MarkTransactionOpen()
    {
        if (_databaseRefHeldForOpenTransaction || _ownerDatabase is null) return;
        var acquired = false;
        _ownerDatabase.DangerousAddRef(ref acquired);
        _databaseRefHeldForOpenTransaction = acquired;
    }

    /// <summary>
    /// Releases the reference <see cref="MarkTransactionOpen"/> took, once this connection no
    /// longer has an open transaction. Called by <see cref="LadybugConnection.OnTransactionCompleted"/>;
    /// not for direct use.
    /// </summary>
    internal void MarkTransactionClosed()
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
