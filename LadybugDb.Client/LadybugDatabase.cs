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
    /// Serializes write transactions. LadybugDB permits exactly one write transaction at a
    /// time and raises rather than queueing, so the client holds this rather than letting
    /// callers collide. Read paths do not take it.
    /// </summary>
    internal SemaphoreSlim WriteLock { get; } = new(1, 1);

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

    public void Dispose()
    {
        _handle.Dispose();
        WriteLock.Dispose();
    }
}
