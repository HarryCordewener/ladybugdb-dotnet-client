using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// An embedded LadybugDB database. Opening is a local file operation, so this type is
/// constructed and disposed synchronously; connections and results are async-disposable.
/// </summary>
/// <remarks>
/// <para>
/// This client does not serialize write transactions on the caller's behalf, and holds no
/// lock of its own around them. Whether concurrent writers can proceed is entirely
/// <see cref="LadybugConfig.EnableMultiWrites"/>'s call - see its remarks for the measurement
/// that settled this. With the flag off (the default), concurrent write transactions from
/// separate connections raise <see cref="LadybugWriteConflictException"/> straight from the
/// engine; with it on, they do not. Either way, that is the engine's behaviour to arbitrate,
/// not this client's - an earlier design reserved a <c>SemaphoreSlim</c> here specifically to
/// take on that job client-side, but measurement showed the flag already does it, so the lock
/// was removed rather than adding a second, redundant serialization point.
/// </para>
/// <para>
/// Safe to abandon without disposing anything, including with an open <see cref="LadybugTransaction"/>
/// still on one of its connections - not just to dispose out of order. Neither this type nor
/// <see cref="LadybugConnection"/> has a finalizer of its own; only their underlying native
/// handles do, and those two handles' finalizers would otherwise run independently, in whichever
/// order the GC happens to pick. <see cref="Interop.LbugConnectionHandle"/> closes that gap by
/// holding a reference-counted lease on its owning database's handle for the connection's ENTIRE
/// lifetime - not merely while a transaction happens to be open on it - which is what makes the
/// ordering safe by construction rather than by observed GC behaviour, regardless of whether a
/// transaction is involved at all. See that type's remarks (and
/// <see cref="Interop.LbugStructHandle.AcquireParentHolds"/>, the general mechanism every native
/// child handle in this library now uses for the same reason) for the full explanation and why
/// relying on the observed order alone was not good enough.
/// </para>
/// <para>
/// This includes racing <see cref="Dispose"/> against a concurrent
/// <see cref="LadybugConnection.BeginTransactionAsync"/> on one of this database's connections,
/// not just against an already-open transaction. Because a connection's lease on its owning
/// database is taken once, at <see cref="ConnectAsync"/> time, and held for the
/// connection's whole life, it is already in place well before any later
/// <c>BEGIN TRANSACTION</c> is issued - so there is no window for a concurrent
/// <see cref="Dispose"/> to race into, even though the engine considers a transaction open the
/// instant its own <c>BEGIN TRANSACTION</c> call returns, earlier than any of this library's own
/// bookkeeping would otherwise run. See <see cref="Interop.LbugConnectionHandle"/>'s remarks.
/// </para>
/// </remarks>
public sealed class LadybugDatabase : IDisposable
{
    private readonly LbugDatabaseHandle _handle;

    /// <summary>
    /// Every <see cref="LadybugConnection"/> opened from this database that currently has an
    /// active <see cref="LadybugTransaction"/> on it. Populated by
    /// <see cref="TrackTransactionOpened"/>/<see cref="TrackTransactionClosed"/>, which
    /// <see cref="LadybugConnection"/> calls as transactions begin and end. Existing solely so
    /// <see cref="Dispose"/> can find and force-close any of them before releasing this
    /// database's own handle - see <see cref="Dispose"/> and
    /// <see cref="LadybugTransaction.EnsureClosedForDispose"/> for why that ordering matters.
    /// </summary>
    private readonly HashSet<LadybugConnection> _connectionsWithOpenTransactions = [];

    /// <summary>Guards <see cref="_connectionsWithOpenTransactions"/> against concurrent connections opening or closing transactions, and against a concurrent <see cref="Dispose"/>.</summary>
    private readonly Lock _transactionTrackingGate = new();

    /// <summary>Opens (creating if necessary) the LadybugDB database at <paramref name="path"/>.</summary>
    /// <param name="path">Filesystem path to the database directory.</param>
    /// <param name="config">Runtime configuration. <see langword="null"/> selects engine defaults.</param>
    /// <exception cref="LadybugException">The engine failed to open the database.</exception>
    public LadybugDatabase(string path, LadybugConfig? config = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _handle = LbugDatabaseHandle.Open(path, BuildConfig(config ?? new LadybugConfig()));
    }

    internal LbugDatabaseHandle Handle => _handle;

    /// <summary>
    /// Opens a connection. Multiple connections may share one database.
    /// </summary>
    /// <remarks>
    /// No <c>IsClosed</c> pre-check here: <see cref="LbugConnectionHandle.Open"/> leases this
    /// database's handle internally and that lease already throws
    /// <see cref="ObjectDisposedException"/> if the database has been disposed. A separate
    /// check-then-call here would just reintroduce the TOCTOU window leases exist to close.
    /// </remarks>
    public ValueTask<LadybugConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new LadybugConnection(this, LbugConnectionHandle.Open(_handle)));
    }

    private static unsafe lbug_system_config BuildConfig(LadybugConfig config)
    {
        var native = LbugNative.lbug_default_system_config();
        if (config.BufferPoolSize != 0) native.buffer_pool_size = config.BufferPoolSize;
        if (config.MaxThreads != 0) native.max_num_threads = config.MaxThreads;
        if (config.MaxDbSize != 0) native.max_db_size = config.MaxDbSize;
        native.enable_compression = ToNativeBool(config.EnableCompression);
        native.read_only = ToNativeBool(config.ReadOnly);
        native.enable_multi_writes = ToNativeBool(config.EnableMultiWrites);
        return native;
    }

    private static byte ToNativeBool(bool value) => value ? (byte)1 : (byte)0;

    /// <summary>
    /// Records that <paramref name="connection"/> now has an active transaction, so
    /// <see cref="Dispose"/> knows to close it out first if this database is disposed before the
    /// caller does. Called by <see cref="LadybugConnection.BeginTransactionAsync"/>. Not for
    /// direct use.
    /// </summary>
    internal void TrackTransactionOpened(LadybugConnection connection)
    {
        lock (_transactionTrackingGate) _connectionsWithOpenTransactions.Add(connection);
    }

    /// <summary>
    /// Undoes <see cref="TrackTransactionOpened"/> once <paramref name="connection"/>'s
    /// transaction has committed, rolled back, or otherwise closed. Called by
    /// <see cref="LadybugConnection.OnTransactionCompleted"/>. Not for direct use.
    /// </summary>
    internal void TrackTransactionClosed(LadybugConnection connection)
    {
        lock (_transactionTrackingGate) _connectionsWithOpenTransactions.Remove(connection);
    }

    /// <summary>
    /// Closes the database. Connections, results, and other objects opened from it should not be
    /// used afterward; disposing them first is still the intended order. Doing so anyway is
    /// always memory-safe - it never corrupts state or crashes the process. <c>Dispose</c> rejects
    /// any NEW call against this database immediately, throwing <see cref="ObjectDisposedException"/>
    /// - but the underlying native storage is only actually destroyed once every OTHER thing still
    /// depending on it (a call already in flight on another thread, or a still-undisposed child
    /// object such as a connection or query result - see
    /// <see cref="Interop.LbugStructHandle.AcquireParentHolds"/>) has finished with it. A call
    /// already in flight on another thread when <c>Dispose</c> runs may therefore still complete
    /// normally instead of throwing, and a burst of concurrent calls can keep succeeding for some
    /// time afterward while those finish draining - but a call that STARTS after this method
    /// returns always throws <see cref="ObjectDisposedException"/>, regardless of what else is
    /// still keeping this database's native storage alive underneath.
    /// </summary>
    /// <remarks>
    /// Before releasing this database's own handle, forces every connection that still has an
    /// open <see cref="LadybugTransaction"/> to roll it back first, while this handle is still
    /// usable - see <see cref="LadybugTransaction.EnsureClosedForDispose"/> for exactly why that
    /// ordering, and not simply leaving the transaction for the connection's own eventual
    /// disposal to discover, is required: a transaction still open when the underlying
    /// <c>lbug_connection_destroy</c> runs triggers the engine's own internal auto-rollback, which
    /// needs a live database and calls <c>std::terminate()</c> - killing the process, not merely
    /// throwing - if this database was destroyed first. Closing every open transaction out here
    /// keeps the "disposing out of order is always memory-safe" guarantee above true for
    /// transactions too, not just for the plain query/result objects it originally covered.
    /// Each connection's own long-lived hold on this database (see
    /// <see cref="Interop.LbugConnectionHandle"/>'s remarks) independently guarantees this
    /// database's native storage cannot actually be freed before that connection's own
    /// <c>lbug_connection_destroy</c> has run, so <c>std::terminate()</c> is no longer reachable
    /// even without the eager rollback below - but rolling every open transaction back here, right
    /// away, is still worth doing on its own merits: it resolves the transaction at the engine
    /// level as soon as the database is disposed, rather than leaving it open (holding whatever
    /// locks the engine takes for an open write transaction) until the connection is itself
    /// disposed or GC'd, which could be arbitrarily later.
    /// </remarks>
    public void Dispose()
    {
        LadybugConnection[] connectionsToClose;
        lock (_transactionTrackingGate)
        {
            connectionsToClose = [.. _connectionsWithOpenTransactions];
            _connectionsWithOpenTransactions.Clear();
        }

        foreach (var connection in connectionsToClose)
            connection.EnsureNoOpenTransactionForDispose();

        _handle.Dispose();
    }
}
