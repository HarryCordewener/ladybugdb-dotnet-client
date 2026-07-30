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
/// <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/>. This type exists to make that lifecycle hard to
/// misuse - commit or rollback exactly once, and an undisposed-without-either transaction rolls
/// back automatically - not because the engine hands back some native transaction handle
/// underneath it. Nothing stops a caller from issuing <c>BEGIN TRANSACTION</c>/<c>COMMIT</c>/
/// <c>ROLLBACK</c> directly through <see cref="LadybugConnection.QueryAsync(string, CancellationToken)"/> instead; this
/// type is a convenience over that, not a different code path.
/// </para>
/// <para>
/// Every statement run against <see cref="LadybugConnection"/> while a transaction is open -
/// not just ones issued through this type - participates in it, because the transaction lives
/// on the connection itself (that is what <c>BEGIN TRANSACTION</c> means to the engine), not on
/// this managed wrapper. This wrapper only tracks whether <em>this</em> transaction has already
/// been completed; a second, unrelated <see cref="LadybugConnection.BeginTransactionAsync"/>
/// call on the same connection while one is already open is rejected client-side, with an
/// <see cref="InvalidOperationException"/>, by <see cref="LadybugConnection.BeginTransactionAsync"/>
/// itself before any <c>BEGIN TRANSACTION</c> reaches the engine - see that method's remarks for
/// why never sending it is the only way to keep the original transaction usable.
/// </para>
/// </remarks>
public sealed class LadybugTransaction : IAsyncDisposable
{
    private readonly LadybugConnection _connection;

    /// <summary>
    /// 0 while open, 1 once completed. An <c>int</c>, not a <c>bool</c>, specifically so the
    /// open-to-completed transition can go through <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
    /// instead of a plain check-then-set - see <see cref="TryClaimCompletion"/> for why that
    /// matters: <see cref="LadybugConnection.DisposeAsync"/> and
    /// <see cref="LadybugDatabase.Dispose"/> can both call this transaction's close-out path for
    /// the same still-open transaction from different threads (a database disposed on one thread
    /// concurrently with the connection it owns being disposed on another - not a contrived
    /// case, e.g. two independent shutdown paths racing), and a plain <c>if (_completed) return;
    /// _completed = true;</c> has a window where both threads read <see langword="false"/>
    /// before either writes <see langword="true"/>, letting both issue a native call against the
    /// same connection concurrently. Measured directly: instrumented and forced with a
    /// <see cref="System.Threading.Barrier"/>, that window was hit in every single contended
    /// iteration attempted before this fix existed.
    /// </summary>
    private int _completed;

    /// <summary>
    /// Test-only diagnostic: the number of callers that ever successfully won
    /// <see cref="TryClaimCompletion"/>'s race for this transaction. Structurally always exactly
    /// 0 or 1 given <see cref="Interlocked.CompareExchange(ref int, int, int)"/>'s own atomicity
    /// guarantee - not something production code reads - but exposed so
    /// <c>TransactionConcurrentDisposalTests</c> can directly confirm "exactly one native
    /// ROLLBACK, ever, no matter how many threads race to close this transaction out" rather than
    /// inferring it indirectly from on-disk state, which the fix-round-2 investigation found does
    /// not reliably differ between the racy and fixed versions of this code (both can leave
    /// correct on-disk state most of the time even when two threads did both reach the native
    /// call).
    /// </summary>
    internal int CompletionClaimCount => _completionClaimCount;

    private int _completionClaimCount;

    private LadybugTransaction(LadybugConnection connection) => _connection = connection;

    /// <summary>
    /// Atomically claims this transaction's open-to-completed transition. Returns
    /// <see langword="true"/> for exactly one caller, ever, across however many threads race to
    /// call this concurrently - every other caller (including ones already inside
    /// <see cref="CommitAsync"/>, <see cref="RollbackAsync"/>, <see cref="DisposeAsync"/>, or
    /// <see cref="EnsureClosedForDispose"/> at the same moment) gets <see langword="false"/> and
    /// must not attempt the corresponding native call. See <see cref="_completed"/> for why a
    /// plain check-then-set is not good enough here.
    /// </summary>
    private bool TryClaimCompletion()
    {
        var won = Interlocked.CompareExchange(ref _completed, 1, 0) == 0;
        if (won) Interlocked.Increment(ref _completionClaimCount);
        return won;
    }

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
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    /// <summary>Commits the transaction by issuing <c>COMMIT</c>.</summary>
    /// <param name="cancellationToken">Forwarded to the underlying <c>COMMIT</c> query.</param>
    /// <exception cref="InvalidOperationException">
    /// This transaction was already committed or rolled back - including by a concurrent
    /// <see cref="DisposeAsync"/>/<see cref="EnsureClosedForDispose"/> that won the race to close
    /// it out first; see <see cref="_completed"/>.
    /// </exception>
    /// <remarks>
    /// A failed <c>COMMIT</c> is terminal, not retryable: the claim <see cref="TryClaimCompletion"/>
    /// won above is never given back, regardless of whether the native call below throws. An earlier
    /// version did give it back on failure, specifically so a caller could retry or fall back to
    /// <see cref="RollbackAsync"/> - but that re-opened the exact race this type exists to close: a
    /// concurrent <see cref="DisposeAsync"/>/<see cref="EnsureClosedForDispose"/> could then win the
    /// now-reopened claim, issue its own native call against the same connection while this method's
    /// caller was still observing the first failure, and clear <see cref="_completed"/> out from
    /// under a transaction whose completion attempt had already reached the engine. A <c>COMMIT</c>
    /// that reached the engine and failed leaves the engine's own transaction state not something
    /// this wrapper can safely guess at; treating it as still-open is what let two native calls race
    /// the same connection. Callers that need to retry must open a new transaction via
    /// <see cref="LadybugConnection.BeginTransactionAsync"/>.
    /// </remarks>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!TryClaimCompletion()) throw AlreadyCompleted();

        try
        {
            await using (var _ = await _connection.QueryAsync("COMMIT", cancellationToken))
            {
            }
        }
        finally
        {
            // Runs whether the COMMIT above succeeded or threw - see the remarks above for why a
            // failed attempt must still be treated as this transaction's final word, exactly like
            // DisposeAsync/EnsureClosedForDispose already do.
            _connection.OnTransactionCompleted(this);
        }
    }

    /// <summary>Rolls back the transaction by issuing <c>ROLLBACK</c>.</summary>
    /// <param name="cancellationToken">Forwarded to the underlying <c>ROLLBACK</c> query.</param>
    /// <exception cref="InvalidOperationException">
    /// This transaction was already committed or rolled back - including by a concurrent
    /// <see cref="DisposeAsync"/>/<see cref="EnsureClosedForDispose"/> that won the race to close
    /// it out first; see <see cref="_completed"/>.
    /// </exception>
    /// <remarks>A failed <c>ROLLBACK</c> is terminal too - see <see cref="CommitAsync"/>'s remarks, which apply identically here.</remarks>
    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (!TryClaimCompletion()) throw AlreadyCompleted();

        try
        {
            await using (var _ = await _connection.QueryAsync("ROLLBACK", cancellationToken))
            {
            }
        }
        finally
        {
            // See CommitAsync's identical finally/remarks.
            _connection.OnTransactionCompleted(this);
        }
    }

    private static InvalidOperationException AlreadyCompleted() => new(
        "This transaction has already been completed (committed or rolled back). " +
        "Start a new one with BeginTransactionAsync for further work.");

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
        if (!TryClaimCompletion()) return;

        try
        {
            await using var _ = await _connection.QueryAsync("ROLLBACK");
        }
        catch
        {
            // Swallowed - see remarks above. Unlike CommitAsync/RollbackAsync, the claim is NOT
            // given back on failure here: disposal must not leave this transaction in a state
            // where something could still try to commit or roll it back again.
        }
        finally
        {
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
    /// <para>
    /// <b>Callable concurrently from both callers at once - by design, not by luck.</b>
    /// <see cref="LadybugConnection.DisposeAsync"/> and <see cref="LadybugDatabase.Dispose"/> can
    /// legitimately race: a database disposed on one thread while the connection it owns is
    /// disposed on another (two independent shutdown paths, not a contrived scenario) can both
    /// reach this method for the SAME still-open transaction at the same time. <see cref="TryClaimCompletion"/>
    /// - not a plain <c>if (_completed) return;</c> - is what makes that safe: exactly one caller
    /// ever proceeds to issue the native <c>ROLLBACK</c>, and every other concurrent caller
    /// (here, or in <see cref="CommitAsync"/>/<see cref="RollbackAsync"/>/<see cref="DisposeAsync"/>)
    /// returns immediately instead. Without this, two threads could both observe "not completed
    /// yet" and both issue a native call against the same connection concurrently - measured
    /// directly (forced with a <see cref="System.Threading.Barrier"/>) to happen on effectively
    /// every contended attempt before this method used <see cref="TryClaimCompletion"/>, and
    /// two concurrent native calls on one connection with no engine-side lock between them is
    /// exactly the kind of thing that can work by chance for a long time and then not.
    /// </para>
    /// </remarks>
    internal void EnsureClosedForDispose()
    {
        if (!TryClaimCompletion()) return;

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
            _connection.OnTransactionCompleted(this);
        }
    }
}
