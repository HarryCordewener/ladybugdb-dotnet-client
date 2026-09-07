using System.Text;
namespace LadybugDb.Client;

/// <summary>The transaction-control effect a Cypher statement has on its connection.</summary>
internal enum TransactionEffect
{
    /// <summary>Not transaction control, or not in a form this client recognizes.</summary>
    None,

    /// <summary>Opens a transaction: <c>BEGIN TRANSACTION</c>, optionally <c>READ ONLY</c>.</summary>
    Begin,

    /// <summary>Closes a transaction: <c>COMMIT</c> or <c>ROLLBACK</c>.</summary>
    End,
}

/// <summary>
/// Recognizes the transaction-control statements a caller can hand to
/// <see cref="LadybugConnection.QueryAsync(string, System.Threading.CancellationToken)"/> as raw
/// Cypher, so the connection can keep its own transaction bookkeeping accurate instead of being
/// blind to them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The engine's rejection of a nested <c>BEGIN TRANSACTION</c> does not
/// merely fail the second call - it tears down the transaction that was already open, and the writes
/// inside it are lost with no error at write time. The <c>COMMIT</c> that follows reports
/// "No active transaction for COMMIT." and the rows are simply gone. Reproduced directly, not
/// assumed. The C API exposes no way to ask the engine whether a transaction is open, so the only
/// defense is for the client to never send that nested <c>BEGIN</c> - which requires noticing the
/// transactions a caller opens behind the client's back.
/// </para>
/// <para>
/// <b>Recognition is deliberately conservative, and the failure mode is one-directional.</b>
/// A statement is classified only when its entire normalized text is exactly a transaction-control
/// statement. Anything else - a multi-statement script, a form carrying a comment, an unrecognized
/// spelling - classifies as <see cref="TransactionEffect.None"/> and is passed through untouched.
/// That means this can fail to notice a transaction, leaving the caller in the same manual regime as
/// before; it cannot invent one that isn't there. False negatives cost nothing that was not already
/// the case, whereas a false positive would refuse a legitimate query, so the trade is taken
/// knowingly in that direction. In particular <c>CREATE (n {s: 'BEGIN TRANSACTION'})</c> is not
/// classified, because a string literal is not the whole statement.
/// </para>
/// <para>
/// The accepted spellings were measured against the engine rather than assumed:
/// <c>BEGIN TRANSACTION</c> and <c>BEGIN TRANSACTION READ ONLY</c> are accepted, in any case, with
/// arbitrary internal whitespace and an optional trailing semicolon. <c>BEGIN</c> alone and
/// <c>BEGIN TRANSACTION READ WRITE</c> are parser rejections, so neither is recognized here.
/// </para>
/// </remarks>
internal static class TransactionStatement
{
    /// <summary>
    /// Classifies <paramref name="cypher"/>'s transaction-control effect.
    /// </summary>
    /// <param name="cypher">The statement as the caller wrote it.</param>
    /// <returns>
    /// <see cref="TransactionEffect.Begin"/>, <see cref="TransactionEffect.End"/>, or
    /// <see cref="TransactionEffect.None"/> when the statement is not recognized as transaction
    /// control - see this class's remarks for why that case is passed through rather than guessed at.
    /// </returns>
    internal static TransactionEffect Classify(string? cypher)
    {
        if (string.IsNullOrWhiteSpace(cypher))
        {
            return TransactionEffect.None;
        }

        // Nearly every statement is neither BEGIN, COMMIT nor ROLLBACK, and this runs on every
        // QueryAsync. Decide that from the first keyword before normalizing anything: a statement
        // that does not start with one of the three words is a definite None with no allocation,
        // and only a candidate pays for the normalized exact comparison below (which still rejects
        // "COMMITTED" or "BEGIN TRANSACTION; CREATE ..."). Measured before this check: 118 ns and
        // 528 B per typical MATCH statement (LadybugDb.Client.Benchmarks, ClassifierBenchmarks).
        var trimmedStart = cypher.AsSpan().TrimStart();
        if (!trimmedStart.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
            && !trimmedStart.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            && !trimmedStart.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            return TransactionEffect.None;
        }

        var normalized = Normalize(cypher);

        // Ordered longest-first so READ ONLY is not shadowed by the bare form.
        if (normalized.Equals("BEGIN TRANSACTION READ ONLY", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase))
        {
            return TransactionEffect.Begin;
        }

        if (normalized.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            return TransactionEffect.End;
        }

        return TransactionEffect.None;
    }

    /// <summary>
    /// Trims the statement, drops a single trailing semicolon, and collapses every internal run of
    /// whitespace to one space, so the comparisons above cover the spellings the engine accepts
    /// without a comparison per spelling.
    /// </summary>
    private static string Normalize(string cypher)
    {
        var trimmed = cypher.AsSpan().Trim();

        if (trimmed.Length > 0 && trimmed[^1] == ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        // Only transaction-control statements survive the comparisons above, and those are short,
        // so building a string here costs nothing measurable next to a query round trip.
        var builder = new StringBuilder(trimmed.Length);
        var inWhitespace = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && builder.Length > 0)
            {
                builder.Append(' ');
            }

            inWhitespace = false;
            builder.Append(c);
        }

        return builder.ToString();
    }
}
