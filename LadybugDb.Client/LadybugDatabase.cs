using LadybugDb.Client.Interop;
using LadybugDb.Client.Native;

namespace LadybugDb.Client;

/// <summary>
/// An embedded LadybugDB database. Opening is a local file operation, so this type is
/// constructed and disposed synchronously; connections and results are async-disposable.
/// </summary>
public sealed class LadybugDatabase : IDisposable
{
    private readonly LbugDatabaseHandle _handle;

    /// <summary>
    /// Reserved for Milestone 2, which will use this to serialize write transactions: LadybugDB
    /// permits exactly one write transaction at a time and raises rather than queueing, so the
    /// client is meant to hold this rather than let callers collide. Not wired up yet - nothing
    /// currently acquires it, so concurrent writers demonstrably do collide today (see
    /// <see cref="LadybugWriteConflictException"/>, which exists precisely because they can).
    /// </summary>
    internal SemaphoreSlim WriteLock { get; } = new(1, 1);

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
        return native;
    }

    private static byte ToNativeBool(bool value) => value ? (byte)1 : (byte)0;

    /// <summary>
    /// Closes the database. Connections, results, and other objects opened from it should not be
    /// used afterward; disposing them first is still the intended order. Doing so anyway is
    /// always memory-safe - it never corrupts state or crashes the process - but it is not
    /// guaranteed to throw <see cref="ObjectDisposedException"/> immediately. <c>Dispose</c>
    /// releases this object's own baseline reference on the underlying handle; the handle is only
    /// actually closed once every outstanding lease on it has also been released (see
    /// <see cref="Interop.LbugStructHandle.Acquire"/>), so a call already in flight on another
    /// thread when <c>Dispose</c> runs may still complete normally instead of throwing, and a
    /// burst of concurrent calls can keep succeeding for some time afterward while leases keep
    /// draining. A call that starts after the handle has fully closed throws
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        _handle.Dispose();
        WriteLock.Dispose();
    }
}
