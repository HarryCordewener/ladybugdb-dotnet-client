namespace LadybugDb.Client;

/// <summary>An error reported by the LadybugDB engine.</summary>
public class LadybugException : Exception
{
    /// <summary>Creates an exception reported by the engine, optionally attributed to the Cypher statement that caused it.</summary>
    /// <param name="message">The engine's error message.</param>
    /// <param name="statement">The Cypher statement that produced the error, when one is known.</param>
    public LadybugException(string message, string? statement = null)
        : base(statement is null ? message : $"{message}{Environment.NewLine}Statement: {statement}")
        => Statement = statement;

    /// <summary>Wraps an underlying exception (for example a marshalling failure) with no known statement.</summary>
    /// <param name="message">A description of what failed.</param>
    /// <param name="innerException">The underlying exception.</param>
    public LadybugException(string message, Exception innerException)
        : base(message, innerException) => Statement = null;

    /// <summary>The Cypher statement that produced this error, when one is known.</summary>
    public string? Statement { get; }
}

/// <summary>
/// Thrown when the engine refuses a write because another write transaction is active.
/// LadybugDB permits exactly one write transaction at a time and rejects rather than queueing,
/// so this is expected under contention and is safe to retry.
/// </summary>
public sealed class LadybugWriteConflictException : LadybugException
{
    /// <summary>Creates a write-conflict exception, optionally attributed to the Cypher statement that was rejected.</summary>
    /// <param name="message">The engine's error message.</param>
    /// <param name="statement">The Cypher statement that was rejected, when one is known.</param>
    public LadybugWriteConflictException(string message, string? statement = null)
        : base(message, statement) { }
}
