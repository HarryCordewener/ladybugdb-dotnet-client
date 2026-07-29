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
    /// </remarks>
    internal static LadybugException Classify(string? message, string statement)
    {
        var isWriteConflict = message is not null
            && message.Contains("write transaction", StringComparison.OrdinalIgnoreCase);
        var normalized = string.IsNullOrEmpty(message) ? "Query failed." : message;

        return isWriteConflict
            ? new LadybugWriteConflictException(normalized, statement)
            : new LadybugException(normalized, statement);
    }
}
