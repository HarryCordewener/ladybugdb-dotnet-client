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
        _connection.OnTransactionCompleted(this);
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
        _connection.OnTransactionCompleted(this);
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
            _connection.OnTransactionCompleted(this);
        }
    }

    /// <summary>
    /// Closes this transaction out synchronously - rolling it back if it is not already
    /// completed - without ever letting the failure surface. Called by
    /// <see cref="LadybugConnection"/> and <see cref="LadybugDatabase"/>, never directly by user
    /// code, from their own <c>Dispose</c>/<c>DisposeAsync</c> paths when this transaction might
    /// still be open at that point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists at all, not just <see cref="DisposeAsync"/>.</b> The native
    /// <c>lbug_connection_destroy</c> auto-rolls-back any transaction still open on that
    /// connection as part of destroying it - reasonable on its own, since a connection going away
    /// with an open transaction has to be resolved somehow. That auto-rollback needs the database
    /// the transaction belongs to to still be alive. If the database was destroyed FIRST (an
    /// ordering this library has otherwise always treated as safe - see
    /// <see cref="LadybugDatabase.Dispose"/> - because every other native call in this library
    /// leases the ancestor's handle and throws a catchable <see cref="ObjectDisposedException"/>
    /// instead of touching freed memory), that auto-rollback cannot do that, and - verified
    /// directly against the real engine, reproducibly, 100% of attempts - calls
    /// <c>std::terminate()</c> instead of raising anything catchable. A C++ exception unwinding
    /// out of a P/Invoke call is undefined behaviour the CLR does not intercept; the process is
    /// killed (SIGABRT) before any C# <c>catch</c>, including this type's own in
    /// <see cref="DisposeAsync"/>, ever runs.
    /// </para>
    /// <para>
    /// <b>The invariant this method exists to guarantee: <c>lbug_connection_destroy</c> must
    /// never be invoked while this transaction is still open.</b> Both
    /// <see cref="LadybugConnection.DisposeAsync"/> (a connection disposed directly, transaction
    /// never touched) and <see cref="LadybugDatabase.Dispose"/> (the database disposed while some
    /// connection it owns still has one open - the crash-report scenario) call this method first,
    /// synchronously, while the pieces they still individually control - respectively, this
    /// connection's own handle, and the database's handle - are still guaranteed usable. That
    /// closes the transaction out at the ENGINE level (a real <c>ROLLBACK</c> reaches the engine
    /// successfully) before either dispose path lets <c>lbug_connection_destroy</c> run, so its
    /// internal auto-rollback always finds nothing left to do - not merely "nothing this managed
    /// wrapper knows about", but nothing open at all.
    /// </para>
    /// <para>
    /// Synchronous, not <c>async</c>: <see cref="LadybugConnection"/>'s query methods are
    /// documented to always complete synchronously today (embedded engine, CPU/disk-bound work,
    /// no genuine offload yet), so retrieving <see cref="ValueTask{TResult}.Result"/> directly off
    /// an already-completed <see cref="ValueTask{TResult}"/> here does not block - it is exactly
    /// what <c>await</c>ing the same, already-finished task would do, without forcing this method
    /// (and its two callers' own <c>Dispose</c>/<c>DisposeAsync</c>, both intentionally
    /// synchronous call sites) to become <c>async</c> for a genuine yield that never happens.
    /// </para>
    /// </remarks>
    internal void EnsureClosedForDispose()
    {
        if (_completed) return;

        try
        {
            var result = _connection.QueryAsync("ROLLBACK").Result;
            result.DisposeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Swallowed - see remarks above: called from a Dispose path that must not throw,
            // and there is nothing a caller could usefully do differently even if this
            // surfaced. If this failed because the database is already gone, there is nothing
            // left for the native auto-rollback to conflict with either - the transaction was
            // never going to survive this connection's destruction regardless.
        }
        finally
        {
            _completed = true;
            _connection.OnTransactionCompleted(this);
        }
    }
}
