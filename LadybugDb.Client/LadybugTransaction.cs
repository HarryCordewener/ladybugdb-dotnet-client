namespace LadybugDb.Client;

/// <summary>
/// A transaction opened on a <see cref="LadybugConnection"/> via
/// <see cref="LadybugConnection.BeginTransactionAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a native primitive.</b> The C API exposes no transaction functions at all -
/// <see cref="LadybugConnection.BeginTransactionAsync"/> issues the Cypher statement
/// <c>BEGIN TRANSACTION</c>, <see cref="CommitAsync"/> issues <c>COMMIT</c>, and
/// <see cref="RollbackAsync"/> issues <c>ROLLBACK</c>, all through the exact same query path as
/// <see cref="LadybugConnection.QueryAsync"/>. This type exists to make that lifecycle hard to
/// misuse - commit or rollback exactly once, and an undisposed-without-either transaction rolls
/// back automatically - not because the engine hands back some native transaction handle
/// underneath it. Nothing stops a caller from issuing <c>BEGIN TRANSACTION</c>/<c>COMMIT</c>/
/// <c>ROLLBACK</c> directly through <see cref="LadybugConnection.QueryAsync"/> instead; this
/// type is a convenience over that, not a different code path.
/// </para>
/// <para>
/// Every statement run against <see cref="LadybugConnection"/> while a transaction is open -
/// not just ones issued through this type - participates in it, because the transaction lives
/// on the connection itself (that is what <c>BEGIN TRANSACTION</c> means to the engine), not on
/// this managed wrapper. This wrapper only tracks whether <em>this</em> transaction has already
/// been completed, so a second, unrelated <see cref="LadybugConnection.BeginTransactionAsync"/>
/// call on the same connection while one is already open is the engine's error to raise, not
/// this type's.
/// </para>
/// </remarks>
public sealed class LadybugTransaction : IAsyncDisposable
{
    private readonly LadybugConnection _connection;
    private bool _completed;

    private LadybugTransaction(LadybugConnection connection) => _connection = connection;

    /// <summary>
    /// Issues <c>BEGIN TRANSACTION</c> on <paramref name="connection"/> and returns the
    /// resulting <see cref="LadybugTransaction"/>. Called by
    /// <see cref="LadybugConnection.BeginTransactionAsync"/>; not exposed directly, since a
    /// transaction only ever makes sense already begun.
    /// </summary>
    internal static async ValueTask<LadybugTransaction> BeginAsync(
        LadybugConnection connection, CancellationToken cancellationToken)
    {
        await using (var _ = await connection.QueryAsync("BEGIN TRANSACTION", cancellationToken))
        {
        }

        return new LadybugTransaction(connection);
    }

    /// <summary>
    /// <see langword="true"/> once <see cref="CommitAsync"/> or <see cref="RollbackAsync"/> has
    /// run on this transaction - successfully, or via the automatic rollback
    /// <see cref="DisposeAsync"/> performs when neither ran first.
    /// </summary>
    public bool IsCompleted => _completed;

    /// <summary>Commits the transaction by issuing <c>COMMIT</c>.</summary>
    /// <param name="cancellationToken">Forwarded to the underlying <c>COMMIT</c> query.</param>
    /// <exception cref="InvalidOperationException">
    /// This transaction was already committed or rolled back.
    /// </exception>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await using (var _ = await _connection.QueryAsync("COMMIT", cancellationToken))
        {
        }

        _completed = true;
    }

    /// <summary>Rolls back the transaction by issuing <c>ROLLBACK</c>.</summary>
    /// <param name="cancellationToken">Forwarded to the underlying <c>ROLLBACK</c> query.</param>
    /// <exception cref="InvalidOperationException">
    /// This transaction was already committed or rolled back.
    /// </exception>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        await using (var _ = await _connection.QueryAsync("ROLLBACK", cancellationToken))
        {
        }

        _completed = true;
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException(
                "This transaction has already been completed (committed or rolled back). " +
                "Start a new one with BeginTransactionAsync for further work.");
    }

    /// <summary>
    /// Rolls back the transaction if neither <see cref="CommitAsync"/> nor
    /// <see cref="RollbackAsync"/> has run yet. Otherwise a no-op.
    /// </summary>
    /// <remarks>
    /// Never throws, by design - exactly like every <c>ReleaseHandle</c> override in
    /// <see cref="Interop.LbugStructHandle"/> for the same reason: a <c>Dispose</c>/<c>DisposeAsync</c>
    /// path that can throw is unsafe to
    /// call from a <c>finally</c> or a <c>using</c> block unwinding from some other exception, and
    /// callers reasonably rely on disposal being exactly that safe. The rollback issued here can
    /// itself fail - the connection may already be closed, or the database it belongs to disposed
    /// - and there is nothing a caller could usefully do differently in response from inside a
    /// <c>DisposeAsync</c>, so the failure is swallowed rather than surfaced. The transaction is
    /// still marked completed either way: a rollback that failed to run is not a transaction this
    /// type should let anyone try to commit or roll back again.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_completed) return;

        try
        {
            await using var _ = await _connection.QueryAsync("ROLLBACK");
        }
        catch
        {
            // Swallowed - see remarks above.
        }
        finally
        {
            _completed = true;
        }
    }
}
