namespace LadybugDb.Client;

/// <summary>Runtime configuration for opening a <see cref="LadybugDatabase"/>.</summary>
public sealed record LadybugConfig
{
    /// <summary>Max buffer pool size in bytes. 0 selects the engine default.</summary>
    public ulong BufferPoolSize { get; init; }

    /// <summary>Max threads used during query execution. 0 selects the engine default.</summary>
    public ulong MaxThreads { get; init; }

    /// <summary>Compress supported types on disk.</summary>
    public bool EnableCompression { get; init; } = true;

    /// <summary>Open read-only. No write transaction is permitted on the database.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Max database size in bytes. 0 selects the engine default.</summary>
    public ulong MaxDbSize { get; init; }
}
