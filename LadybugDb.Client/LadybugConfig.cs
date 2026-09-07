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
    /// qualitative result: refusals climbing with the flag off, a handful of genuine row
    /// conflicts with it on (the engine then reports "Write-write conflict of updating the same
    /// row.", classified as the same retryable <see cref="LadybugWriteConflictException"/>).
    /// Treat the shape of the result (does throughput scale with concurrency) as the finding;
    /// treat the specific mutations/sec figures as this machine's, not a guarantee for yours.
    /// </para>
    /// </remarks>
    public bool EnableMultiWrites { get; init; }

    /// <summary>
    /// Whether the engine checkpoints automatically once the write-ahead log grows past
    /// <see cref="CheckpointThreshold"/>. Maps to <c>auto_checkpoint</c>; the engine default is
    /// <see langword="true"/>. A checkpoint blocks new writers and drains active ones for its
    /// duration, so a server that wants to choose its own quiet moment turns this off and issues
    /// <c>CHECKPOINT</c> itself.
    /// </summary>
    public bool AutoCheckpoint { get; init; } = true;

    /// <summary>
    /// The write-ahead log size, in bytes, past which an automatic checkpoint runs. Maps to
    /// <c>checkpoint_threshold</c>; <c>0</c> keeps the engine default (16 MiB). The readiness
    /// review measured a 100,000-object database growing from 102 MB to 434 MB across 250,000
    /// mutations, which is the kind of growth this threshold governs.
    /// </summary>
    public ulong CheckpointThreshold { get; init; }

    /// <summary>
    /// Whether the engine verifies page checksums. Maps to <c>enable_checksums</c>; the engine
    /// default is <see langword="true"/>.
    /// </summary>
    public bool EnableChecksums { get; init; } = true;

    /// <summary>
    /// Whether opening a database whose write-ahead log cannot be replayed throws instead of
    /// discarding the unreplayable tail. Maps to <c>throw_on_wal_replay_failure</c>; the engine
    /// default is <see langword="true"/>.
    /// </summary>
    public bool ThrowOnWalReplayFailure { get; init; } = true;
}
