namespace LadybugDb.Client;

/// <summary>An error reported by the LadybugDB engine.</summary>
public class LadybugException : Exception
{
    public LadybugException(string message, string? statement = null)
        : base(statement is null ? message : $"{message}{Environment.NewLine}Statement: {statement}")
        => Statement = statement;

    /// <summary>Wraps an underlying exception (for example a marshalling failure) with no known statement.</summary>
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
    public LadybugWriteConflictException(string message, string? statement = null)
        : base(message, statement) { }
}
