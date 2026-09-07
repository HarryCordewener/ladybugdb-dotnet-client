namespace LadybugDb.Client;

/// <summary>
/// Maps a failed query's error message to the exception <see cref="LadybugConnection"/> throws.
/// Pulled out of <see cref="LadybugConnection"/> so this mapping - the one place a substring
/// match against the native engine's wording decides whether a caller sees a plain
/// <see cref="LadybugException"/> or the retryable <see cref="LadybugWriteConflictException"/> -
/// is unit-testable without a real engine. A prior version of this match matched only
/// <c>"one write transaction"</c> and shipped with no test exercising either branch.
/// </summary>
internal static class QueryFailureClassifier
{
    /// <summary>
    /// Classifies a query failure by its error message and returns the exception to throw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matches the broad substring <c>"write transaction"</c> rather than a longer phrase like
    /// <c>"one write transaction"</c> or <c>"new write transaction"</c>. The real engine (v0.18.3)
    /// reports the full message
    /// <c>"Cannot start a new write transaction in the system. Only one write transaction at a
    /// time is allowed in the system."</c> - confirmed against the real library, see
    /// <c>DatabaseLifecycleTests.ConcurrentWrite_ThrowsLadybugWriteConflictException</c> - which
    /// happens to contain both of those longer phrasings, so either would have worked today. But
    /// pinning to one specific longer phrasing is exactly the kind of brittleness that let this
    /// match silently rot before: it would break again if the engine's wording shifted by even one
    /// word (e.g. to "another write transaction is already in progress"). <c>"write transaction"</c>
    /// is present in every phrasing seen so far and is not part of any other error message this
    /// engine is known to emit.
    /// </para>
    /// <para>
    /// A second, unrelated wording covers the same retryable condition under
    /// <see cref="LadybugConfig.EnableMultiWrites"/>: <c>"Runtime exception: Write-write conflict
    /// of updating the same row."</c> There the engine admits concurrent writers and detects the
    /// collision at the row instead of at the writer slot, so the message never mentions a
    /// transaction at all. Matched on <c>"write-write conflict"</c>. Before this was added, that
    /// failure surfaced as a plain <see cref="LadybugException"/>, which a retry loop written to
    /// the documented contract treated as fatal - the benchmark workload's concurrent-writer
    /// section lost every writer that way.
    /// </para>
    /// </remarks>
    internal static LadybugException Classify(string? message, string statement)
    {
        // Two distinct engine wordings, one retryable condition. "write transaction" is the
        // single-writer refusal (the default mode); "write-write conflict" is what the engine says
        // instead under EnableMultiWrites, where two writers are admitted and collide on a row
        // ("Runtime exception: Write-write conflict of updating the same row.", observed against
        // v0.18.3 by the benchmark workload's concurrent-writer section). Both mean the same thing
        // to a caller: nothing was applied, retry the transaction.
        var isWriteConflict = message is not null
            && (message.Contains("write transaction", StringComparison.OrdinalIgnoreCase)
                || message.Contains("write-write conflict", StringComparison.OrdinalIgnoreCase));
        var normalized = string.IsNullOrEmpty(message) ? "Query failed." : message;

        return isWriteConflict
            ? new LadybugWriteConflictException(normalized, statement)
            : new LadybugException(normalized, statement);
    }
}
