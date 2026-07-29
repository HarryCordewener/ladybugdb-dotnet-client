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

    /// <summary>Maps to the engine's <c>enable_multi_writes</c> setting.</summary>
    /// <remarks>
    /// <para>
    /// Measured directly against the real engine: with this <see langword="false"/> (the
    /// default), LadybugDB permits exactly one write transaction at a time and raises rather
    /// than queueing - concurrent writers from separate connections observably collide, and
    /// <see cref="LadybugWriteConflictException"/> exists precisely because they do. Across
    /// three consecutive 1/2/4/8-concurrent-writer runs at this setting, conflicts climbed with
    /// writer count (0 / ~2,700 / ~8,000 / ~18,000 over a 3-second window) while throughput
    /// stayed flat (roughly 2,400-2,800 mutations/sec regardless of writer count).
    /// </para>
    /// <para>
    /// With this <see langword="true"/>, the same workload produced <b>zero</b>
    /// <see cref="LadybugWriteConflictException"/>s at any writer count across all three runs,
    /// and throughput scaled up with concurrency instead of staying flat (roughly 2,600/sec at
    /// one writer, rising to 3,500-3,800/sec at four to eight). The flag genuinely lifts the
    /// one-write-transaction-at-a-time restriction; it is not a no-op. Because of that, this
    /// client does not serialize writers itself - see <see cref="LadybugDatabase"/>'s remarks.
    /// </para>
    /// <para>
    /// The specific numbers above are this machine's, not a portable benchmark result - an
    /// independent spot-check on different hardware/load saw materially different absolute
    /// throughput (602 to 1,248 mut/s, versus roughly 2,600 to 3,900 here) but the identical
    /// qualitative result: conflicts present and climbing with the flag off, zero with it on.
    /// Treat the shape of the result (does the flag eliminate conflicts, does throughput scale
    /// with concurrency) as the finding; treat the specific mutations/sec figures as this
    /// machine's, not a guarantee for yours.
    /// </para>
    /// </remarks>
    public bool EnableMultiWrites { get; init; }
}
